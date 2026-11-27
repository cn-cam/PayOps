using System.Text.Json;
using Microsoft.Extensions.Logging;
using PayOps.Core.Domain;
using PayOps.Core.Providers;
using PayOps.Core.Routing;
using PayOps.Core.Security;
using PayOps.Service.Data;

namespace PayOps.Service.Infra;

public sealed class PassageEntry(PayPassage passage, SecretProtector protector)
{
    private readonly Lazy<PassageConfig> _config = new(() => new PassageConfig(ConfigCache.DecryptConfig(protector, passage.ConfigCipher)));

    public PayPassage Passage { get; } = passage;
    public PassageConfig Config => _config.Value;
}

public sealed class AppEntry(PayApp app, SecretProtector protector)
{
    private readonly Lazy<string> _secret = new(() => protector.Unprotect(app.SecretCipher));

    public PayApp App { get; } = app;
    public string Secret => _secret.Value;
}

/// <summary>
/// 配置快照缓存：应用 / 渠道 / 通道 / 绑定都是小表，整体加载为不可变快照放在进程内存。
/// 刷新时机：TTL 到期，或任一副本修改配置后经 Redis 广播立即失效。
/// 数据库暂时不可用时继续使用旧快照，下单主链路不受影响。
/// </summary>
public sealed class ConfigCache(Db db, RedisStore redis, SecretProtector protector, ILogger<ConfigCache> logger)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile Snapshot? _snapshot;
    private long _expireTicks;

    private sealed class Snapshot
    {
        public required Dictionary<string, AppEntry> AppsByCode { get; init; }
        public required Dictionary<long, AppEntry> AppsById { get; init; }
        public required Dictionary<(long AppId, string Code), PayChannel> Channels { get; init; }
        public required Dictionary<long, PayChannel> ChannelsById { get; init; }
        public required Dictionary<long, PassageEntry> Passages { get; init; }
        public required ILookup<long, RouteCandidate> Bindings { get; init; }
    }

    public void Invalidate() => Interlocked.Exchange(ref _expireTicks, 0);

    /// <summary>本副本立即失效并广播给其他副本。</summary>
    public async Task InvalidateAllAsync()
    {
        Invalidate();
        await redis.PublishConfigChangedAsync();
    }

    private async ValueTask<Snapshot> GetAsync()
    {
        var current = _snapshot;
        if (current is not null && DateTime.UtcNow.Ticks < Interlocked.Read(ref _expireTicks)) return current;

        await _loadLock.WaitAsync();
        try
        {
            current = _snapshot;
            if (current is not null && DateTime.UtcNow.Ticks < Interlocked.Read(ref _expireTicks)) return current;
            try
            {
                current = await LoadAsync();
                _snapshot = current;
            }
            catch (Exception ex) when (current is not null)
            {
                logger.LogError(ex, "刷新配置快照失败，继续使用旧快照");
            }
            Interlocked.Exchange(ref _expireTicks, DateTime.UtcNow.Add(Ttl).Ticks);
            return current!;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<Snapshot> LoadAsync()
    {
        var apps = (await db.QueryAsync<PayApp>("SELECT * FROM pay_app")).Select(a => new AppEntry(a, protector)).ToList();
        var channels = await db.QueryAsync<PayChannel>("SELECT * FROM pay_channel");
        var passages = (await db.QueryAsync<PayPassage>("SELECT * FROM pay_passage")).ToDictionary(p => p.Id, p => new PassageEntry(p, protector));
        var bindings = await db.QueryAsync<PayChannelPassage>("SELECT * FROM pay_channel_passage");

        return new Snapshot
        {
            AppsByCode = apps.ToDictionary(a => a.App.AppCode, StringComparer.Ordinal),
            AppsById = apps.ToDictionary(a => a.App.Id),
            Channels = channels.ToDictionary(c => (c.AppId, c.Code)),
            ChannelsById = channels.ToDictionary(c => c.Id),
            Passages = passages,
            Bindings = bindings
                .Where(b => passages.ContainsKey(b.PassageId))
                .OrderBy(b => b.Priority).ThenBy(b => b.Id)
                .ToLookup(b => b.ChannelId, b => ToCandidate(b, passages[b.PassageId].Passage)),
        };
    }

    public static RouteCandidate ToCandidate(PayChannelPassage b, PayPassage passage) => new()
    {
        BindingId = b.Id,
        PassageId = b.PassageId,
        PassageName = passage.Name,
        Provider = passage.Provider,
        Methods = SplitMethods(b.Methods).ToHashSet(StringComparer.OrdinalIgnoreCase),
        Weight = b.Weight,
        Priority = b.Priority,
        MinAmount = b.MinAmount,
        MaxAmount = b.MaxAmount,
        DailyLimitAmount = b.DailyLimitAmount,
        DailyLimitCount = b.DailyLimitCount,
        StartTime = PassageRouter.ParseTime(b.StartTime),
        EndTime = PassageRouter.ParseTime(b.EndTime),
        Enabled = b.Status == 1,
        PassageEnabled = passage.Status == 1,
    };

    public static string[] SplitMethods(string methods) =>
        methods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static Dictionary<string, string> DecryptConfig(SecretProtector protector, string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(cipher)) ?? [];
    }

    public async ValueTask<AppEntry?> GetAppByCodeAsync(string appCode) => (await GetAsync()).AppsByCode.GetValueOrDefault(appCode);

    public async ValueTask<AppEntry?> GetAppAsync(long id) => (await GetAsync()).AppsById.GetValueOrDefault(id);

    public async ValueTask<PayChannel?> GetChannelAsync(long appId, string code) => (await GetAsync()).Channels.GetValueOrDefault((appId, code));

    public async ValueTask<PayChannel?> GetChannelAsync(long channelId) => (await GetAsync()).ChannelsById.GetValueOrDefault(channelId);

    public async ValueTask<PassageEntry?> GetPassageAsync(long passageId) => (await GetAsync()).Passages.GetValueOrDefault(passageId);

    public async ValueTask<IReadOnlyList<RouteCandidate>> GetBindingsAsync(long channelId) => (await GetAsync()).Bindings[channelId].ToList();
}
