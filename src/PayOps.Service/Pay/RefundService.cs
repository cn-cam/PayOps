using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Core.Providers;
using PayOps.Service.Data;
using PayOps.Service.Infra;

namespace PayOps.Service.Pay;

public sealed class RefundRequest
{
    public string OrderNo { get; set; } = "";
    public string MchOrderNo { get; set; } = "";
    public string MchRefundNo { get; set; } = "";
    public long Amount { get; set; }
    public string Reason { get; set; } = "";
    public string NotifyUrl { get; set; } = "";
}

public sealed class RefundService(
    Db db,
    ConfigCache cache,
    RedisStore redis,
    NotifyService notify,
    OrderService orders,
    IPayProviderRegistry providers,
    OrderNoGenerator orderNo,
    IOptions<PayOpsOptions> options,
    ILogger<RefundService> logger)
{
    private static readonly TimeSpan CheckLease = TimeSpan.FromSeconds(120);
    private readonly PayOpsOptions _options = options.Value;

    public Task<PayRefund?> FindAsync(string refundNo) =>
        db.FirstOrDefaultAsync<PayRefund>("SELECT * FROM pay_refund WHERE refund_no = @refundNo", new { refundNo });

    public Task<PayRefund?> FindByMchRefundNoAsync(long appId, string mchRefundNo) =>
        db.FirstOrDefaultAsync<PayRefund>("SELECT * FROM pay_refund WHERE app_id = @appId AND mch_refund_no = @mchRefundNo", new { appId, mchRefundNo });

    public async Task<PayRefund> RequireAsync(long appId, string? refundNo, string? mchRefundNo)
    {
        PayRefund? refund = null;
        if (!string.IsNullOrWhiteSpace(refundNo)) refund = await FindAsync(refundNo.Trim());
        else if (!string.IsNullOrWhiteSpace(mchRefundNo)) refund = await FindByMchRefundNoAsync(appId, mchRefundNo.Trim());
        else throw new PayException(ErrorCodes.InvalidParam, "refundNo 与 mchRefundNo 至少传一个");
        if (refund is null || refund.AppId != appId) throw new PayException(ErrorCodes.RefundNotFound, "退款单不存在");
        return refund;
    }

    /// <summary>
    /// 发起退款。可退余额通过单条条件更新原子占用（refund_amount + 本次 &lt;= amount），
    /// 多副本并发下也不会超退；上游明确失败时释放占用。
    /// </summary>
    public async Task<PayRefund> CreateAsync(PayOrder order, RefundRequest req, string source, CancellationToken ct)
    {
        req.MchRefundNo = req.MchRefundNo.Trim();
        if (req.MchRefundNo.Length is 0 or > 64) throw new PayException(ErrorCodes.InvalidParam, "mchRefundNo 长度需在 1~64 之间");
        if (req.Amount <= 0) throw new PayException(ErrorCodes.InvalidParam, "退款金额必须为正整数（分）");
        if (req.Reason.Length > 255) throw new PayException(ErrorCodes.InvalidParam, "reason 超长");
        if (req.NotifyUrl.Length > 0 && !OrderService.IsHttpUrl(req.NotifyUrl)) throw new PayException(ErrorCodes.InvalidParam, "notifyUrl 必须是 http(s) 地址");
        if (order.Status != OrderStatus.Paid) throw new PayException(ErrorCodes.OrderStateError, "仅支付成功的订单可退款");

        var provider = providers.Get(order.Provider);
        if (!provider.Meta.Capabilities.Refund)
            throw new PayException(ErrorCodes.NotSupported, $"{provider.Meta.Name}不支持接口退款，请在对应平台后台处理");
        var passage = await cache.GetPassageAsync(order.PassageId) ?? throw new PayException(ErrorCodes.PassageError, "订单通道不存在");

        await using var lockHandle = await redis.TryLockAsync($"refund:{order.AppId}:{req.MchRefundNo}", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3))
                                     ?? throw new PayException(ErrorCodes.Busy, "相同退款单号的请求正在处理中，请稍后重试");

        var existing = await FindByMchRefundNoAsync(order.AppId, req.MchRefundNo);
        if (existing is not null) return Idempotent(existing, order, req);

        var now = DateTime.UtcNow;
        var refund = new PayRefund
        {
            RefundNo = orderNo.Next('R'),
            OrderNo = order.OrderNo,
            AppId = order.AppId,
            AppCode = order.AppCode,
            MchRefundNo = req.MchRefundNo,
            PassageId = order.PassageId,
            Provider = order.Provider,
            Amount = req.Amount,
            Reason = req.Reason,
            Status = RefundStatus.Processing,
            NotifyUrl = req.NotifyUrl,
            Source = source,
            NextCheckAt = now.AddSeconds(30),
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            await db.TransactionAsync(async (conn, tx) =>
            {
                var rows = await conn.ExecuteAsync(
                    "UPDATE pay_order SET refund_amount = refund_amount + @amount, updated_at = @now WHERE id = @Id AND status = 1 AND refund_amount + @amount <= amount",
                    new { order.Id, amount = req.Amount, now }, tx);
                if (rows == 0) throw new PayException(ErrorCodes.RefundExceeded, "退款金额超过订单可退余额");
                refund.Id = await Db.InsertAsync(conn, tx, refund);
                return 0;
            });
        }
        catch (MySqlException ex) when (Db.IsDuplicateKey(ex))
        {
            var dup = await FindByMchRefundNoAsync(order.AppId, req.MchRefundNo) ?? throw new PayException(ErrorCodes.Busy, "系统繁忙，请重试");
            return Idempotent(dup, order, req);
        }

        try
        {
            var result = await provider.RefundAsync(ToContext(refund, order, passage), ct);
            await ApplyAsync(refund, result);
        }
        catch (ProviderException ex) when (ex.Code == "UPSTREAM_UNREACHABLE")
        {
            // 结果未知：保持处理中，由巡检任务查询上游确认，不能贸然释放可退额度
            logger.LogWarning("退款 {RefundNo} 请求上游结果未知，等待巡检确认：{Message}", refund.RefundNo, ex.Message);
        }
        catch (ProviderException ex)
        {
            await MarkFailedAsync(refund, ex.Message);
        }
        return (await FindAsync(refund.RefundNo))!;
    }

    private static PayRefund Idempotent(PayRefund existing, PayOrder order, RefundRequest req)
    {
        if (existing.OrderNo != order.OrderNo || existing.Amount != req.Amount)
            throw new PayException(ErrorCodes.Conflict, "退款单号已存在且参数不一致");
        return existing;
    }

    private RefundContext ToContext(PayRefund refund, PayOrder order, PassageEntry passage) => new()
    {
        PassageId = order.PassageId,
        Config = passage.Config,
        OrderNo = order.OrderNo,
        Method = order.PayMethod,
        UpstreamTradeNo = order.UpstreamTradeNo,
        RefundNo = refund.RefundNo,
        OrderAmount = order.Amount,
        RefundAmount = refund.Amount,
        Reason = refund.Reason,
        UpstreamRefundNo = refund.UpstreamRefundNo,
        NotifyUrl = orders.UpstreamNotifyUrl(order.Provider, order.PassageId, "refund"),
    };

    private async Task<bool> ApplyAsync(PayRefund refund, RefundResult result)
    {
        switch (result.State)
        {
            case UpstreamRefundState.Success:
                return await MarkSuccessAsync(refund, result.UpstreamRefundNo);
            case UpstreamRefundState.Failed:
                return await MarkFailedAsync(refund, result.Message.Length > 0 ? result.Message : "上游退款失败");
            default:
                if (!string.IsNullOrEmpty(result.UpstreamRefundNo))
                    await db.ExecuteAsync("UPDATE pay_refund SET upstream_refund_no = @no WHERE id = @Id AND status = 0", new { no = result.UpstreamRefundNo, refund.Id });
                return false;
        }
    }

    public async Task<bool> MarkSuccessAsync(PayRefund refund, string? upstreamRefundNo)
    {
        var order = await orders.FindAsync(refund.OrderNo);
        if (order is null) return false;
        var now = DateTime.UtcNow;
        var url = await notify.ResolveUrlAsync(refund.AppId, refund.NotifyUrl.Length > 0 ? refund.NotifyUrl : order.NotifyUrl);
        var taskId = await db.TransactionAsync(async (conn, tx) =>
        {
            var rows = await conn.ExecuteAsync(
                "UPDATE pay_refund SET status = 1, upstream_refund_no = COALESCE(@upstreamRefundNo, upstream_refund_no), success_at = @now, next_check_at = NULL, updated_at = @now WHERE id = @Id AND status = 0",
                new { refund.Id, upstreamRefundNo, now }, tx);
            if (rows == 0) return -1L;
            refund.Status = RefundStatus.Success;
            refund.SuccessAt = now;
            return await notify.InsertTaskAsync(conn, tx, refund.AppId, refund.AppCode, NotifyType.Refund, refund.RefundNo, url, NotifyService.BuildRefundPayload(refund, order));
        });
        if (taskId < 0) return false;
        logger.LogInformation("退款 {RefundNo} 成功，金额 {Amount} 分", refund.RefundNo, refund.Amount);
        await notify.EnqueueAsync(taskId);
        return true;
    }

    public async Task<bool> MarkFailedAsync(PayRefund refund, string message)
    {
        var order = await orders.FindAsync(refund.OrderNo);
        if (order is null) return false;
        var now = DateTime.UtcNow;
        message = message.Length > 500 ? message[..500] : message;
        var url = await notify.ResolveUrlAsync(refund.AppId, refund.NotifyUrl.Length > 0 ? refund.NotifyUrl : order.NotifyUrl);
        var taskId = await db.TransactionAsync(async (conn, tx) =>
        {
            var rows = await conn.ExecuteAsync(
                "UPDATE pay_refund SET status = 2, error_msg = @message, next_check_at = NULL, updated_at = @now WHERE id = @Id AND status = 0",
                new { refund.Id, message, now }, tx);
            if (rows == 0) return -1L;
            // 释放占用的可退额度
            await conn.ExecuteAsync(
                "UPDATE pay_order SET refund_amount = GREATEST(refund_amount - @amount, 0), updated_at = @now WHERE order_no = @OrderNo",
                new { amount = refund.Amount, now, refund.OrderNo }, tx);
            refund.Status = RefundStatus.Failed;
            refund.ErrorMsg = message;
            return await notify.InsertTaskAsync(conn, tx, refund.AppId, refund.AppCode, NotifyType.Refund, refund.RefundNo, url, NotifyService.BuildRefundPayload(refund, order));
        });
        if (taskId < 0) return false;
        logger.LogWarning("退款 {RefundNo} 失败：{Message}", refund.RefundNo, message);
        await notify.EnqueueAsync(taskId);
        return true;
    }

    /// <summary>主动查询上游退款状态并同步。</summary>
    public async Task<SyncResult> SyncAsync(PayRefund refund, CancellationToken ct)
    {
        if (refund.Status != RefundStatus.Processing) return new SyncResult((int)refund.Status, false, "退款单已是终态");
        var order = await orders.FindAsync(refund.OrderNo);
        var provider = providers.Find(refund.Provider);
        var passage = await cache.GetPassageAsync(refund.PassageId);
        if (order is null || provider is null || passage is null) return new SyncResult((int)refund.Status, false, "订单或通道不存在");
        try
        {
            var result = await provider.QueryRefundAsync(ToContext(refund, order, passage), ct);
            var changed = await ApplyAsync(refund, result);
            return new SyncResult((int)refund.Status, changed, changed ? "已同步为终态" : "上游仍在处理中");
        }
        catch (ProviderException ex)
        {
            return new SyncResult((int)refund.Status, false, "查询上游失败：" + ex.Message);
        }
    }

    public Task<List<string>> ClaimDueChecksAsync(int batchSize) =>
        db.TransactionAsync(async (conn, tx) =>
        {
            var due = (await conn.QueryAsync<string>(
                "SELECT refund_no FROM pay_refund WHERE status = 0 AND next_check_at <= UTC_TIMESTAMP(3) ORDER BY next_check_at LIMIT @batchSize FOR UPDATE SKIP LOCKED",
                new { batchSize }, tx)).AsList();
            if (due.Count > 0)
                await conn.ExecuteAsync(
                    "UPDATE pay_refund SET next_check_at = DATE_ADD(UTC_TIMESTAMP(3), INTERVAL @lease SECOND) WHERE refund_no IN @due",
                    new { lease = (int)CheckLease.TotalSeconds, due }, tx);
            return due;
        });

    /// <summary>巡检消费：查询处理中的退款；超过最大次数后停止自动巡检，保持处理中等待人工核实（不自动释放额度）。</summary>
    public async Task CheckAsync(string refundNo, CancellationToken ct)
    {
        var refund = await FindAsync(refundNo);
        if (refund is null || refund.Status != RefundStatus.Processing) return;
        var result = await SyncAsync(refund, ct);
        if (result.Changed) return;

        var count = refund.CheckCount + 1;
        DateTime? next = count >= _options.Order.RefundCheckMaxCount ? null : DateTime.UtcNow.AddSeconds(Math.Min(60 * count, 1800));
        if (next is null) logger.LogError("退款 {RefundNo} 巡检 {Count} 次仍未到终态，已停止自动巡检，请人工核实", refundNo, count);
        await db.ExecuteAsync(
            "UPDATE pay_refund SET next_check_at = @next, check_count = @count, updated_at = UTC_TIMESTAMP(3) WHERE id = @Id AND status = 0",
            new { refund.Id, next, count });
    }

    /// <summary>处理上游回调中的退款事件。返回 false 表示找不到对应单据。</summary>
    public async Task<bool> HandleUpstreamEventAsync(NotifyEvent e, string provider, long passageId)
    {
        if (!string.IsNullOrEmpty(e.RefundNo))
        {
            var refund = await FindAsync(e.RefundNo);
            if (refund is null || refund.PassageId != passageId) return false;
            if (e.RefundSuccess) await MarkSuccessAsync(refund, e.UpstreamRefundNo);
            else await MarkFailedAsync(refund, e.Message.Length > 0 ? e.Message : "上游退款失败");
            return true;
        }

        // 商店侧发起的退款（用户向应用商店申请）：本地没有退款单，按上游事件补建
        if (!e.RefundSuccess) return true;
        var order = !string.IsNullOrEmpty(e.OrderNo)
            ? await orders.FindAsync(e.OrderNo)
            : await db.FirstOrDefaultAsync<PayOrder>(
                "SELECT * FROM pay_order WHERE provider = @provider AND upstream_trade_no = @tradeNo", new { provider, tradeNo = e.UpstreamTradeNo });
        if (order is null || order.PassageId != passageId) return false;
        if (order.Status != OrderStatus.Paid) return true;

        var mchRefundNo = string.IsNullOrEmpty(e.UpstreamRefundNo) ? "UP-" + order.OrderNo : e.UpstreamRefundNo;
        if (await FindByMchRefundNoAsync(order.AppId, mchRefundNo) is not null) return true;

        var now = DateTime.UtcNow;
        var amount = Math.Min(e.RefundAmount ?? order.Amount - order.RefundAmount, order.Amount - order.RefundAmount);
        if (amount <= 0) return true;
        var refundRow = new PayRefund
        {
            RefundNo = orderNo.Next('R'),
            OrderNo = order.OrderNo,
            AppId = order.AppId,
            AppCode = order.AppCode,
            MchRefundNo = mchRefundNo,
            PassageId = order.PassageId,
            Provider = order.Provider,
            Amount = amount,
            Reason = e.Message.Length > 0 ? e.Message : "上游发起退款",
            Status = RefundStatus.Success,
            UpstreamRefundNo = e.UpstreamRefundNo,
            Source = "upstream",
            SuccessAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var url = await notify.ResolveUrlAsync(order.AppId, order.NotifyUrl);
        try
        {
            var taskId = await db.TransactionAsync(async (conn, tx) =>
            {
                var rows = await conn.ExecuteAsync(
                    "UPDATE pay_order SET refund_amount = refund_amount + @amount, updated_at = @now WHERE id = @Id AND refund_amount + @amount <= amount",
                    new { order.Id, amount, now }, tx);
                if (rows == 0) return -1L;
                refundRow.Id = await Db.InsertAsync(conn, tx, refundRow);
                return await notify.InsertTaskAsync(conn, tx, order.AppId, order.AppCode, NotifyType.Refund, refundRow.RefundNo, url, NotifyService.BuildRefundPayload(refundRow, order));
            });
            if (taskId > 0)
            {
                logger.LogWarning("订单 {OrderNo} 收到上游侧退款 {Amount} 分，已补建退款单 {RefundNo}", order.OrderNo, amount, refundRow.RefundNo);
                await notify.EnqueueAsync(taskId);
            }
        }
        catch (MySqlException ex) when (Db.IsDuplicateKey(ex))
        {
            // 并发重复回调
        }
        return true;
    }
}
