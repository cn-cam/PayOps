using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PayOps.Core;

namespace PayOps.Service.Infra;

/// <summary>
/// 每个副本都会运行：
/// 1. 通过 Redis 租约独占一个节点号（0~999），供订单号生成使用，租约丢失自动重新申请；
/// 2. 周期性上报心跳，管理后台据此展示在线副本；
/// 3. 订阅配置变更广播，及时失效本地配置快照。
/// </summary>
public sealed class NodeService(
    RedisStore redis,
    InstanceInfo instance,
    OrderNoGenerator orderNo,
    ConfigCache cache,
    ILogger<NodeService> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private int? _workerId;
    private bool _subscribed;

    public int WorkerId => _workerId ?? orderNo.WorkerId;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // 启动阶段同步完成一次，确保开始接单前已拿到节点号
        orderNo.WorkerId = Random.Shared.Next(1000);
        await TickAsync();
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await TickAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TickAsync()
    {
        try
        {
            if (_workerId is { } id && !await redis.RenewWorkerIdAsync(id, instance.InstanceId, LeaseTtl))
            {
                logger.LogWarning("节点号 {WorkerId} 租约丢失，重新申请", id);
                _workerId = null;
            }
            if (_workerId is null)
            {
                _workerId = await redis.AcquireWorkerIdAsync(instance.InstanceId, LeaseTtl);
                if (_workerId is { } acquired)
                {
                    orderNo.WorkerId = acquired;
                    logger.LogInformation("实例 {Instance} 获得节点号 {WorkerId}", instance.InstanceId, acquired);
                }
            }
            if (!_subscribed)
            {
                await redis.SubscribeConfigChangedAsync(cache.Invalidate);
                _subscribed = true;
            }
            await redis.HeartbeatAsync(new NodeInfo(instance.InstanceId, instance.Host, instance.Roles, WorkerId, instance.Version, instance.StartedAt, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            // Redis 不可用时沿用当前（或随机）节点号；订单号唯一性仍由数据库唯一键兜底
            logger.LogWarning("节点心跳失败：{Message}", ex.Message);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await redis.RemoveNodeAsync(instance.InstanceId);
            if (_workerId is { } id) await redis.ReleaseWorkerIdAsync(id, instance.InstanceId);
        }
        catch (Exception ex)
        {
            logger.LogDebug("注销节点失败：{Message}", ex.Message);
        }
    }
}
