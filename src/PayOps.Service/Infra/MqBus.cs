using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayOps.Core;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace PayOps.Service.Infra;

/// <summary>
/// RabbitMQ 封装。拓扑：
///   {prefix}.direct (direct)  → 队列 {prefix}.notify（业务通知投递）、{prefix}.check（订单/退款巡检）
///   {prefix}.events (topic)   → 领域事件（pay.success / refund.success / refund.failed），供内部系统自行绑定队列订阅
/// 消息只携带任务主键，数据库是唯一事实来源：消息丢失或重复都由数据库租约重新派发与幂等处理兜底。
/// </summary>
public sealed class MqBus(IOptions<PayOpsOptions> options, InstanceInfo instance, ILogger<MqBus> logger) : IAsyncDisposable
{
    public const string NotifyQueue = "notify";
    public const string CheckQueue = "check";

    private readonly RabbitMqOptions _options = options.Value.RabbitMq;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _publishChannel;
    private long _lastFailureTicks;

    private string DirectExchange => _options.Prefix + ".direct";
    public string EventExchange => _options.Prefix + ".events";
    public string QueueName(string shortName) => $"{_options.Prefix}.{shortName}";

    public bool IsConnected => _connection is { IsOpen: true };

    private async Task<IConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true }) return _connection;
        // 快速失败：MQ 刚连接失败时不让每个请求都再等一次连接超时，支付主链路靠数据库发件箱兜底
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastFailureTicks) < TimeSpan.FromSeconds(5).Ticks)
            throw new InvalidOperationException("RabbitMQ 暂不可用");
        await _connectLock.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;
            // 连接对象存在但已断开：客户端正在后台自动恢复，此时不重复建连
            if (_connection is not null) throw new InvalidOperationException("RabbitMQ 连接恢复中");

            var factory = new ConnectionFactory
            {
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
                ClientProvidedName = "payops:" + instance.InstanceId,
            };
            var endpoints = _options.Hosts
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(AmqpTcpEndpoint.Parse)
                .ToList();
            var connection = await factory.CreateConnectionAsync(endpoints, ct);
            try
            {
                await DeclareTopologyAsync(connection, ct);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
            _connection = connection;
            logger.LogInformation("RabbitMQ 已连接：{Endpoint}", connection.Endpoint);
            return connection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task DeclareTopologyAsync(IConnection connection, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DirectExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(EventExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        var args = new Dictionary<string, object?> { ["x-queue-type"] = _options.QueueType };
        foreach (var queue in new[] { NotifyQueue, CheckQueue })
        {
            await channel.QueueDeclareAsync(QueueName(queue), durable: true, exclusive: false, autoDelete: false, arguments: args, cancellationToken: ct);
            await channel.QueueBindAsync(QueueName(queue), DirectExchange, queue, cancellationToken: ct);
        }
    }

    public Task EnsureConnectedAsync(CancellationToken ct) => GetConnectionAsync(ct);

    public Task PublishAsync(string queue, string body, CancellationToken ct = default) =>
        PublishCoreAsync(DirectExchange, queue, body, ct);

    public Task PublishEventAsync(string routingKey, string body, CancellationToken ct = default) =>
        PublishCoreAsync(EventExchange, routingKey, body, ct);

    private async Task PublishCoreAsync(string exchange, string routingKey, string body, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(ct);
        await _publishLock.WaitAsync(ct);
        try
        {
            if (_publishChannel is not { IsOpen: true })
            {
                if (_publishChannel is not null) await _publishChannel.DisposeAsync();
                _publishChannel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
            }
            var props = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = Guid.NewGuid().ToString("N"),
                Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            };
            // 开启了发布确认跟踪：await 返回即 broker 已确认落盘
            await _publishChannel.BasicPublishAsync(exchange, routingKey, false, props, Encoding.UTF8.GetBytes(body), ct);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    /// <summary>
    /// 订阅队列。处理器抛异常时仍然 ack：任务状态留在数据库，租约到期后会被重新派发，避免毒消息在队列里空转。
    /// </summary>
    public async Task<IChannel> ConsumeAsync(string queue, ushort prefetch, Func<string, CancellationToken, Task> handler, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(ct);
        var channel = await connection.CreateChannelAsync(new CreateChannelOptions(false, false, consumerDispatchConcurrency: prefetch), ct);
        await channel.BasicQosAsync(0, prefetch, false, ct);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var body = Encoding.UTF8.GetString(ea.Body.Span);
            try
            {
                await handler(body, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // 停机中：不 ack，连接关闭后由 broker 重新投递给其他副本
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "处理队列 {Queue} 消息失败：{Body}", queue, body);
            }
            try
            {
                await channel.BasicAckAsync(ea.DeliveryTag, false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning("ack 失败（消息将被重新投递）：{Message}", ex.Message);
            }
        };
        await channel.BasicConsumeAsync(QueueName(queue), autoAck: false, consumer: consumer, cancellationToken: ct);
        return channel;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_publishChannel is not null) await _publishChannel.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug("关闭 RabbitMQ 连接时出错：{Message}", ex.Message);
        }
    }
}
