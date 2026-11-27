using System.Data;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Core.Security;
using PayOps.Service.Data;
using PayOps.Service.Infra;

namespace PayOps.Service.Pay;

/// <summary>
/// 业务通知（事务发件箱）：
/// 1. 订单/退款状态变更与通知任务写入同一个数据库事务；
/// 2. 提交后立即把任务 ID 发到 MQ；发布失败或消息丢失时，由派发任务按 next_time 租约重新扫描派发；
/// 3. 消费者先把领域事件发布到 events 交换机，再 HTTP 回调业务方，失败按退避间隔重试。
/// 语义为"至少一次"，业务方必须按订单号幂等。
/// </summary>
public sealed class NotifyService(
    Db db,
    MqBus mq,
    ConfigCache cache,
    IHttpClientFactory httpFactory,
    IOptions<PayOpsOptions> options,
    ILogger<NotifyService> logger)
{
    public const string HttpClientName = "payops-notify";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(120);
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly NotifyOptions _options = options.Value.Notify;

    public static string BuildPayPayload(PayOrder o) => JsonSerializer.Serialize(new
    {
        type = "pay",
        appId = o.AppCode,
        orderNo = o.OrderNo,
        mchOrderNo = o.MchOrderNo,
        channel = o.ChannelCode,
        provider = o.Provider,
        payMethod = o.PayMethod,
        amount = o.Amount,
        currency = o.Currency,
        status = (int)o.Status,
        upstreamTradeNo = o.UpstreamTradeNo ?? "",
        userId = o.UserId,
        productId = o.ProductId,
        attach = o.Attach,
        paidAt = o.PaidAt,
    }, PayloadJson);

    public static string BuildRefundPayload(PayRefund r, PayOrder o) => JsonSerializer.Serialize(new
    {
        type = "refund",
        appId = o.AppCode,
        orderNo = o.OrderNo,
        mchOrderNo = o.MchOrderNo,
        refundNo = r.RefundNo,
        mchRefundNo = r.MchRefundNo,
        channel = o.ChannelCode,
        provider = o.Provider,
        amount = o.Amount,
        refundAmount = r.Amount,
        refundStatus = (int)r.Status,
        source = r.Source,
        reason = r.Reason,
        errorMsg = r.ErrorMsg,
        attach = o.Attach,
        refundedAt = r.SuccessAt,
    }, PayloadJson);

    /// <summary>在业务事务内写入通知任务（同一业务单号幂等）。返回任务 ID，已存在时返回 0。</summary>
    public async Task<long> InsertTaskAsync(IDbConnection conn, IDbTransaction tx, long appId, string appCode, NotifyType type, string bizNo, string url, string payload)
    {
        var now = DateTime.UtcNow;
        var rows = await conn.ExecuteAsync(
            """
            INSERT IGNORE INTO pay_notify_task
              (app_id, app_code, type, biz_no, url, payload, status, attempts, max_attempts, next_time, created_at, updated_at)
            VALUES (@appId, @appCode, @type, @bizNo, @url, @payload, 0, 0, @max, @next, @now, @now)
            """,
            new { appId, appCode, type, bizNo, url, payload, max = _options.RetryIntervals.Length + 1, next = now + Lease, now }, tx);
        return rows == 0 ? 0 : await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()", transaction: tx);
    }

    /// <summary>事务提交后立即派发；失败则把 next_time 拨回当前，交给派发任务尽快重试。</summary>
    public async Task EnqueueAsync(long taskId)
    {
        if (taskId <= 0) return;
        try
        {
            // 限时发布：MQ 卡顿不能拖慢上游回调的应答
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await mq.PublishAsync(MqBus.NotifyQueue, taskId.ToString(), cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("通知任务 {TaskId} 即时派发失败，转由派发任务重试：{Message}", taskId, ex.Message);
            try
            {
                await db.ExecuteAsync("UPDATE pay_notify_task SET next_time = UTC_TIMESTAMP(3) WHERE id = @taskId AND status = 0", new { taskId });
            }
            catch (Exception inner)
            {
                logger.LogWarning("重置通知任务 {TaskId} 失败：{Message}", taskId, inner.Message);
            }
        }
    }

    /// <summary>派发任务：领取到期任务并续租，再逐个发布到 MQ。SKIP LOCKED 保证多副本互不阻塞、互不重复。</summary>
    public async Task<int> DispatchDueAsync(int batchSize, CancellationToken ct)
    {
        var ids = await db.TransactionAsync(async (conn, tx) =>
        {
            var due = (await conn.QueryAsync<long>(
                "SELECT id FROM pay_notify_task WHERE status = 0 AND next_time <= UTC_TIMESTAMP(3) ORDER BY next_time LIMIT @batchSize FOR UPDATE SKIP LOCKED",
                new { batchSize }, tx)).AsList();
            if (due.Count > 0)
                await conn.ExecuteAsync(
                    "UPDATE pay_notify_task SET next_time = DATE_ADD(UTC_TIMESTAMP(3), INTERVAL @lease SECOND) WHERE id IN @due",
                    new { lease = (int)Lease.TotalSeconds, due }, tx);
            return due;
        });
        foreach (var id in ids) await mq.PublishAsync(MqBus.NotifyQueue, id.ToString(), ct);
        return ids.Count;
    }

    /// <summary>消费者：执行一次通知。</summary>
    public async Task ExecuteAsync(long taskId, CancellationToken ct)
    {
        var task = await db.GetAsync<PayNotifyTask>(taskId);
        if (task is null || task.Status != NotifyStatus.Pending) return;

        // 领域事件先于 HTTP 回调发布；"已发布"标记随本次执行的结果一并落库，少一次写入。
        // 两者之间进程崩溃只会导致事件重复发布，符合"至少一次"语义。
        if (!task.EventPublished) await mq.PublishEventAsync(EventRoutingKey(task), task.Payload, ct);

        if (string.IsNullOrWhiteSpace(task.Url))
        {
            await db.ExecuteAsync(
                "UPDATE pay_notify_task SET status = 1, event_published = 1, last_response = '未配置通知地址，仅发布 MQ 事件', last_time = @now, updated_at = @now WHERE id = @taskId AND status = 0",
                new { taskId, now = DateTime.UtcNow });
            return;
        }

        var app = await cache.GetAppAsync(task.AppId);
        var (httpStatus, response, ok) = app is null
            ? (0, "应用不存在", false)
            : await PostAsync(task, app.Secret, ct);

        var now = DateTime.UtcNow;
        var attempts = task.Attempts + 1;
        if (ok)
        {
            await db.ExecuteAsync(
                "UPDATE pay_notify_task SET status = 1, event_published = 1, attempts = @attempts, last_http_status = @httpStatus, last_response = @response, last_time = @now, updated_at = @now WHERE id = @taskId AND status = 0",
                new { taskId, attempts, httpStatus, response, now });
            return;
        }

        var exhausted = attempts >= task.MaxAttempts;
        var delay = _options.RetryIntervals.Length == 0 ? 60 : _options.RetryIntervals[Math.Min(attempts - 1, _options.RetryIntervals.Length - 1)];
        await db.ExecuteAsync(
            "UPDATE pay_notify_task SET status = @status, event_published = 1, attempts = @attempts, next_time = @next, last_http_status = @httpStatus, last_response = @response, last_time = @now, updated_at = @now WHERE id = @taskId AND status = 0",
            new { taskId, status = exhausted ? NotifyStatus.Failed : NotifyStatus.Pending, attempts, next = now.AddSeconds(delay), httpStatus, response, now });
        logger.LogWarning("业务通知失败 {BizNo} 第 {Attempts}/{Max} 次：HTTP {Status} {Response}", task.BizNo, attempts, task.MaxAttempts, httpStatus, response);
    }

    private static string EventRoutingKey(PayNotifyTask task)
    {
        if (task.Type == NotifyType.Pay) return "pay.success";
        return task.Payload.Contains("\"refundStatus\":1", StringComparison.Ordinal) ? "refund.success" : "refund.failed";
    }

    private async Task<(int Status, string Response, bool Ok)> PostAsync(PayNotifyTask task, string secret, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(task.Url);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = ApiSigner.NewNonce();
            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(task.Payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation(ApiSigner.HeaderAppId, task.AppCode);
            req.Headers.TryAddWithoutValidation(ApiSigner.HeaderTimestamp, timestamp);
            req.Headers.TryAddWithoutValidation(ApiSigner.HeaderNonce, nonce);
            req.Headers.TryAddWithoutValidation(ApiSigner.HeaderSign, ApiSigner.Sign(secret, "POST", uri.PathAndQuery, timestamp, nonce, task.Payload));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var resp = await httpFactory.CreateClient(HttpClientName).SendAsync(req, cts.Token);
            var body = (await resp.Content.ReadAsStringAsync(cts.Token)).Trim();
            var ok = resp.IsSuccessStatusCode && body.Equals("success", StringComparison.OrdinalIgnoreCase);
            return ((int)resp.StatusCode, body.Length > 500 ? body[..500] : body, ok);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (0, ex is OperationCanceledException ? "请求超时" : ex.Message.Length > 500 ? ex.Message[..500] : ex.Message, false);
        }
    }

    /// <summary>后台手动重发：重置为待通知并立即派发。</summary>
    public async Task RetryAsync(long taskId)
    {
        var rows = await db.ExecuteAsync(
            "UPDATE pay_notify_task SET status = 0, attempts = 0, next_time = @next, updated_at = @now WHERE id = @taskId",
            new { taskId, next = DateTime.UtcNow + Lease, now = DateTime.UtcNow });
        if (rows == 0) throw new PayException(ErrorCodes.NotFound, "通知任务不存在");
        await EnqueueAsync(taskId);
    }

    /// <summary>重新通知某笔已支付订单（任务不存在则补建）。</summary>
    public async Task RenotifyOrderAsync(PayOrder order)
    {
        if (order.Status != OrderStatus.Paid) throw new PayException(ErrorCodes.OrderStateError, "仅支付成功的订单可重发通知");
        var taskId = await db.ScalarAsync<long?>("SELECT id FROM pay_notify_task WHERE type = 1 AND biz_no = @no", new { no = order.OrderNo });
        if (taskId is null)
        {
            var url = await ResolveUrlAsync(order.AppId, order.NotifyUrl);
            taskId = await db.TransactionAsync((conn, tx) =>
                InsertTaskAsync(conn, tx, order.AppId, order.AppCode, NotifyType.Pay, order.OrderNo, url, BuildPayPayload(order)));
            await EnqueueAsync(taskId.Value);
            return;
        }
        await RetryAsync(taskId.Value);
    }

    public async Task<string> ResolveUrlAsync(long appId, string preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        return (await cache.GetAppAsync(appId))?.App.NotifyUrl ?? "";
    }
}
