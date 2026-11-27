using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayOps.Core;
using PayOps.Service.Infra;
using PayOps.Service.Pay;
using RabbitMQ.Client;

namespace PayOps.Service.Jobs;

/// <summary>
/// 派发任务（Worker 角色）：周期性从数据库领取到期任务并发布到 MQ。
/// 多副本同时运行也安全：领取使用 FOR UPDATE SKIP LOCKED + 租约续期，各副本领到的任务互不重叠。
/// </summary>
public sealed class DispatchJob(IServiceProvider services, ILogger<DispatchJob> logger) : BackgroundService
{
    private const int BatchSize = 200;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        LoopAsync("notify-dispatch", TimeSpan.FromSeconds(2), DispatchNotifyAsync, stoppingToken),
        LoopAsync("order-check-dispatch", TimeSpan.FromSeconds(5), DispatchOrderChecksAsync, stoppingToken),
        LoopAsync("refund-check-dispatch", TimeSpan.FromSeconds(10), DispatchRefundChecksAsync, stoppingToken));

    private async Task LoopAsync(string name, TimeSpan interval, Func<CancellationToken, Task<int>> work, CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            var count = 0;
            try
            {
                count = await work(ct);
                if (count > 0) logger.LogDebug("{Job} 派发 {Count} 条", name, count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("{Job} 执行失败：{Message}", name, ex.Message);
            }

            if (count >= BatchSize) continue; // 还有积压，立即继续
            try
            {
                // 加一点抖动，避免多副本同时扫描
                await Task.Delay(interval + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<int> DispatchNotifyAsync(CancellationToken ct)
    {
        await services.GetRequiredService<MqBus>().EnsureConnectedAsync(ct);
        return await services.GetRequiredService<NotifyService>().DispatchDueAsync(BatchSize, ct);
    }

    private async Task<int> DispatchOrderChecksAsync(CancellationToken ct)
    {
        var mq = services.GetRequiredService<MqBus>();
        await mq.EnsureConnectedAsync(ct); // MQ 不可用时不领取，避免白白占用租约
        var due = await services.GetRequiredService<OrderService>().ClaimDueChecksAsync(BatchSize);
        foreach (var orderNo in due) await mq.PublishAsync(MqBus.CheckQueue, "order:" + orderNo, ct);
        return due.Count;
    }

    private async Task<int> DispatchRefundChecksAsync(CancellationToken ct)
    {
        var mq = services.GetRequiredService<MqBus>();
        await mq.EnsureConnectedAsync(ct);
        var due = await services.GetRequiredService<RefundService>().ClaimDueChecksAsync(BatchSize);
        foreach (var refundNo in due) await mq.PublishAsync(MqBus.CheckQueue, "refund:" + refundNo, ct);
        return due.Count;
    }
}

/// <summary>消费任务（Worker 角色）：竞争消费 notify / check 两个队列，副本越多吞吐越高。</summary>
public sealed class ConsumerJob(IServiceProvider services, IOptions<PayOpsOptions> options, ILogger<ConsumerJob> logger) : BackgroundService
{
    private readonly List<IChannel> _channels = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var mq = services.GetRequiredService<MqBus>();
        var mqOptions = options.Value.RabbitMq;

        // 首次连接失败时持续重试；连接建立后的断线由客户端自动恢复（含消费者重新订阅）
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _channels.Add(await mq.ConsumeAsync(MqBus.NotifyQueue, mqOptions.NotifyPrefetch, HandleNotifyAsync, stoppingToken));
                _channels.Add(await mq.ConsumeAsync(MqBus.CheckQueue, mqOptions.CheckPrefetch, HandleCheckAsync, stoppingToken));
                logger.LogInformation("MQ 消费者已启动");
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("启动 MQ 消费者失败，5 秒后重试：{Message}", ex.Message);
                await CloseChannelsAsync();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task HandleNotifyAsync(string body, CancellationToken ct) =>
        long.TryParse(body, out var taskId)
            ? services.GetRequiredService<NotifyService>().ExecuteAsync(taskId, ct)
            : Task.CompletedTask;

    private Task HandleCheckAsync(string body, CancellationToken ct)
    {
        if (body.StartsWith("order:", StringComparison.Ordinal))
            return services.GetRequiredService<OrderService>().CheckAsync(body[6..], ct);
        if (body.StartsWith("refund:", StringComparison.Ordinal))
            return services.GetRequiredService<RefundService>().CheckAsync(body[7..], ct);
        return Task.CompletedTask;
    }

    private async Task CloseChannelsAsync()
    {
        foreach (var channel in _channels)
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch (Exception)
            {
                // 连接已断开时关闭通道会失败，忽略
            }
        }
        _channels.Clear();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await CloseChannelsAsync();
    }
}
