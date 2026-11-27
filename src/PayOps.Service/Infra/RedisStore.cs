using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayOps.Core;
using PayOps.Core.Routing;
using StackExchange.Redis;

namespace PayOps.Service.Infra;

/// <summary>当前进程（副本）的身份信息。</summary>
public sealed class InstanceInfo
{
    public InstanceInfo(IOptions<PayOpsOptions> options)
    {
        Host = Environment.MachineName;
        InstanceId = $"{Host}-{Guid.NewGuid().ToString("N")[..6]}";
        Roles = options.Value.RoleList();
        Version = typeof(InstanceInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
    }

    public string InstanceId { get; }
    public string Host { get; }
    public string[] Roles { get; }
    public string Version { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
}

public sealed record NodeInfo(string InstanceId, string Host, string[] Roles, int WorkerId, string Version, DateTime StartedAt, DateTime LastSeen);

/// <summary>
/// Redis 封装：分布式锁、防重放、通道熔断、日限额计数、节点注册、配置变更广播。
/// 路由相关读取在 Redis 故障时"失败放行"（返回空状态），保证支付主链路不被缓存层拖垮。
/// </summary>
public sealed class RedisStore(IConnectionMultiplexer mux, IOptions<PayOpsOptions> options, ILogger<RedisStore> logger)
{
    private const string ReleaseScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
    private const string RenewScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('pexpire', KEYS[1], ARGV[2]) else return 0 end";
    private readonly PayOpsOptions _options = options.Value;

    private IDatabase Database => mux.GetDatabase();

    public string Key(string name) => _options.Redis.KeyPrefix + name;

    public bool IsConnected => mux.IsConnected;

    public async Task<TimeSpan> PingAsync() => await Database.PingAsync();

    // ---------------- 分布式锁 ----------------

    public sealed class LockHandle(RedisStore? store, string key, string token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (store is null) return;
            try
            {
                await store.Database.ScriptEvaluateAsync(ReleaseScript, [key], [token]);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                store.LogDegraded("释放锁", ex);
            }
        }
    }

    /// <summary>获取锁；被占用返回 null。Redis 不可用时放行（由数据库唯一键兜底）。</summary>
    public async Task<LockHandle?> TryLockAsync(string name, TimeSpan ttl, TimeSpan? wait = null)
    {
        var key = Key("lock:" + name);
        var token = Guid.NewGuid().ToString("N");
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.Zero);
        try
        {
            while (true)
            {
                if (await Database.StringSetAsync(key, token, ttl, When.NotExists)) return new LockHandle(this, key, token);
                if (DateTime.UtcNow >= deadline) return null;
                await Task.Delay(50);
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogDegraded("加锁", ex);
            return new LockHandle(null, key, token);
        }
    }

    /// <summary>一次性占位（防重放）；已存在返回 false。Redis 不可用时放行。</summary>
    public async Task<bool> SetOnceAsync(string name, TimeSpan ttl)
    {
        try
        {
            return await Database.StringSetAsync(Key(name), "1", ttl, When.NotExists);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogDegraded("防重放", ex);
            return true;
        }
    }

    // ---------------- 熔断 ----------------

    public async Task RecordFailureAsync(long bindingId)
    {
        try
        {
            var failKey = Key($"breaker:fail:{bindingId}");
            var count = await Database.StringIncrementAsync(failKey);
            if (count == 1) await Database.KeyExpireAsync(failKey, TimeSpan.FromSeconds(_options.Breaker.WindowSeconds));
            if (count >= _options.Breaker.FailureThreshold)
            {
                await Database.StringSetAsync(Key($"breaker:open:{bindingId}"), "1", TimeSpan.FromSeconds(_options.Breaker.OpenSeconds));
                logger.LogWarning("通道绑定 {BindingId} 连续失败 {Count} 次，熔断 {Seconds}s", bindingId, count, _options.Breaker.OpenSeconds);
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogDegraded("记录通道失败", ex);
        }
    }

    public async Task RecordSuccessAsync(long bindingId)
    {
        try
        {
            await Database.KeyDeleteAsync(Key($"breaker:fail:{bindingId}"), CommandFlags.FireAndForget);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogDegraded("记录通道成功", ex);
        }
    }

    public Task ResetBreakerAsync(long bindingId) =>
        Database.KeyDeleteAsync([Key($"breaker:fail:{bindingId}"), Key($"breaker:open:{bindingId}")]);

    // ---------------- 日限额计数 ----------------

    public async Task AddQuotaAsync(long bindingId, long amount, string day)
    {
        if (bindingId <= 0) return;
        try
        {
            var key = Key($"quota:{day}:{bindingId}");
            var batch = Database.CreateBatch();
            var t1 = batch.HashIncrementAsync(key, "amount", amount);
            var t2 = batch.HashIncrementAsync(key, "count", 1);
            var t3 = batch.KeyExpireAsync(key, TimeSpan.FromHours(48));
            batch.Execute();
            await Task.WhenAll(t1, t2, t3);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogDegraded("日限额计数", ex);
        }
    }

    public async Task<Dictionary<long, RouteRuntime>> GetRuntimeAsync(IReadOnlyCollection<long> bindingIds, string day)
    {
        var result = new Dictionary<long, RouteRuntime>(bindingIds.Count);
        if (bindingIds.Count == 0) return result;
        try
        {
            var batch = Database.CreateBatch();
            var tasks = bindingIds.Distinct().Select(id => (
                Id: id,
                Quota: batch.HashGetAsync(Key($"quota:{day}:{id}"), ["amount", "count"]),
                Open: batch.KeyExistsAsync(Key($"breaker:open:{id}")),
                Fail: batch.StringGetAsync(Key($"breaker:fail:{id}")))).ToList();
            batch.Execute();
            foreach (var t in tasks)
            {
                var quota = await t.Quota;
                result[t.Id] = new RouteRuntime(
                    quota[0].HasValue ? (long)quota[0] : 0,
                    quota[1].HasValue ? (int)quota[1] : 0,
                    await t.Open,
                    (await t.Fail) is { HasValue: true } f ? (int)f : 0);
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogDegraded("读取通道运行时状态", ex);
        }
        return result;
    }

    // ---------------- 节点号租约与节点注册 ----------------

    public async Task<int?> AcquireWorkerIdAsync(string instanceId, TimeSpan ttl)
    {
        var start = Random.Shared.Next(1000);
        for (var i = 0; i < 1000; i++)
        {
            var id = (start + i) % 1000;
            if (await Database.StringSetAsync(Key($"worker:{id}"), instanceId, ttl, When.NotExists)) return id;
        }
        return null;
    }

    public async Task<bool> RenewWorkerIdAsync(int workerId, string instanceId, TimeSpan ttl) =>
        (long)await Database.ScriptEvaluateAsync(RenewScript, [Key($"worker:{workerId}")], [instanceId, (long)ttl.TotalMilliseconds]) == 1;

    public Task ReleaseWorkerIdAsync(int workerId, string instanceId) =>
        Database.ScriptEvaluateAsync(ReleaseScript, [Key($"worker:{workerId}")], [instanceId]);

    public Task HeartbeatAsync(NodeInfo node) =>
        Database.HashSetAsync(Key("nodes"), node.InstanceId, JsonSerializer.Serialize(node));

    public Task RemoveNodeAsync(string instanceId) => Database.HashDeleteAsync(Key("nodes"), instanceId);

    public async Task<List<NodeInfo>> GetNodesAsync(TimeSpan staleAfter)
    {
        var nodes = new List<NodeInfo>();
        foreach (var entry in await Database.HashGetAllAsync(Key("nodes")))
        {
            var node = JsonSerializer.Deserialize<NodeInfo>((string)entry.Value!);
            if (node is null || DateTime.UtcNow - node.LastSeen > staleAfter)
                await Database.HashDeleteAsync(Key("nodes"), entry.Name, CommandFlags.FireAndForget);
            else
                nodes.Add(node);
        }
        return nodes.OrderBy(n => n.StartedAt).ToList();
    }

    // ---------------- 配置变更广播 ----------------

    private RedisChannel ConfigChannel => RedisChannel.Literal(Key("config:changed"));

    public async Task PublishConfigChangedAsync()
    {
        try
        {
            await mux.GetSubscriber().PublishAsync(ConfigChannel, DateTime.UtcNow.Ticks.ToString());
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogDegraded("广播配置变更", ex);
        }
    }

    public Task SubscribeConfigChangedAsync(Action handler) =>
        mux.GetSubscriber().SubscribeAsync(ConfigChannel, (_, _) => handler());

    private void LogDegraded(string what, Exception ex) =>
        logger.LogWarning("Redis 不可用，{What}降级放行：{Message}", what, ex.Message);
}
