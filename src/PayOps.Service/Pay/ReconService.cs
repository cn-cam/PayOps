using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Core.Providers;
using PayOps.Service.Data;
using PayOps.Service.Infra;

namespace PayOps.Service.Pay;

/// <summary>
/// 对账：按"通道 × 账单日"下载上游账单，与本地成功的支付单、退款单逐笔勾对。
/// 差异三类：上游有本地无（或本地未成功，俗称掉单/长款）、本地有上游无（短款）、金额不一致。
/// 调度同样不选主：每个 Worker 定期 INSERT IGNORE 生成批次（唯一键去重），批次用 SKIP LOCKED + 租约领取后经 MQ 执行。
/// 对账只产出差异，不自动改账；掉单可在后台对订单执行"同步"补单。
/// </summary>
public sealed class ReconService(
    Db db,
    ConfigCache cache,
    IPayProviderRegistry providers,
    MqBus mq,
    IOptions<PayOpsOptions> options,
    ILogger<ReconService> logger)
{
    private const int MaxDiffRows = 5000;
    private static readonly TimeSpan RunLease = TimeSpan.FromMinutes(10);
    private readonly PayOpsOptions _options = options.Value;

    private DateTime BusinessNow => DateTime.UtcNow + _options.TimeZoneOffset;

    /// <summary>账单日（业务时区自然日）对应的 UTC 区间。</summary>
    private (DateTime Start, DateTime End) UtcRange(DateOnly date)
    {
        var start = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue) - _options.TimeZoneOffset, DateTimeKind.Utc);
        return (start, start.AddDays(1));
    }

    /// <summary>为昨天生成对账批次（幂等）。到达配置的小时后才生成，给上游留出出账时间。</summary>
    public async Task<int> ScheduleAsync()
    {
        if (!_options.Recon.Enabled || BusinessNow.Hour < _options.Recon.Hour) return 0;
        var billProviders = providers.All.Where(p => p.Meta.Capabilities.Bill).Select(p => p.Meta.Code).ToList();
        if (billProviders.Count == 0) return 0;
        var date = DateOnly.FromDateTime(BusinessNow.AddDays(-1)).ToString("yyyy-MM-dd");
        return await db.ExecuteAsync(
            """
            INSERT IGNORE INTO pay_recon_batch (passage_id, provider, bill_date, status, next_run_at, created_at, updated_at)
            SELECT id, provider, @date, 0, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            FROM pay_passage WHERE status = 1 AND provider IN @billProviders
            """, new { date, billProviders });
    }

    /// <summary>手动发起（或重跑）某通道某天的对账，立即派发执行。</summary>
    public async Task<long> TriggerAsync(long passageId, string billDate)
    {
        if (!DateOnly.TryParseExact(billDate, "yyyy-MM-dd", out var date)) throw new PayException(ErrorCodes.InvalidParam, "账单日格式应为 yyyy-MM-dd");
        if (date >= DateOnly.FromDateTime(BusinessNow)) throw new PayException(ErrorCodes.InvalidParam, "只能对今天之前的日期对账");
        var passage = await cache.GetPassageAsync(passageId) ?? throw new PayException(ErrorCodes.InvalidParam, "通道不存在");
        var provider = providers.Get(passage.Passage.Provider);
        if (!provider.Meta.Capabilities.Bill) throw new PayException(ErrorCodes.NotSupported, $"{provider.Meta.Name}不支持下载对账单");

        await db.ExecuteAsync(
            """
            INSERT INTO pay_recon_batch (passage_id, provider, bill_date, status, attempts, next_run_at, created_at, updated_at)
            VALUES (@passageId, @provider, @billDate, 0, 0, @lease, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))
            ON DUPLICATE KEY UPDATE status = 0, attempts = 0, error_msg = '', next_run_at = @lease, updated_at = UTC_TIMESTAMP(3)
            """, new { passageId, provider = provider.Meta.Code, billDate, lease = DateTime.UtcNow + RunLease });
        var id = await db.ScalarAsync<long>("SELECT id FROM pay_recon_batch WHERE passage_id = @passageId AND bill_date = @billDate", new { passageId, billDate });
        try
        {
            await mq.PublishAsync(MqBus.CheckQueue, "recon:" + id);
        }
        catch (Exception ex)
        {
            // 派发失败则把执行时间拨回当前，由派发任务接手
            logger.LogWarning("对账批次 {BatchId} 即时派发失败：{Message}", id, ex.Message);
            await db.ExecuteAsync("UPDATE pay_recon_batch SET next_run_at = UTC_TIMESTAMP(3) WHERE id = @id AND status = 0", new { id });
        }
        return id;
    }

    public Task<List<long>> ClaimDueAsync(int batchSize) =>
        db.TransactionAsync(async (conn, tx) =>
        {
            var due = (await conn.QueryAsync<long>(
                "SELECT id FROM pay_recon_batch WHERE status = 0 AND next_run_at <= UTC_TIMESTAMP(3) ORDER BY next_run_at LIMIT @batchSize FOR UPDATE SKIP LOCKED",
                new { batchSize }, tx)).AsList();
            if (due.Count > 0)
                await conn.ExecuteAsync(
                    "UPDATE pay_recon_batch SET next_run_at = DATE_ADD(UTC_TIMESTAMP(3), INTERVAL @lease SECOND) WHERE id IN @due",
                    new { lease = (int)RunLease.TotalSeconds, due }, tx);
            return due;
        });

    private sealed class LocalPay
    {
        public string OrderNo { get; set; } = "";
        public string? UpstreamTradeNo { get; set; }
        public long Amount { get; set; }
    }

    private sealed class LocalRefund
    {
        public string RefundNo { get; set; } = "";
        public string OrderNo { get; set; } = "";
        public string? UpstreamRefundNo { get; set; }
        public long Amount { get; set; }
    }

    private sealed class OrderBrief
    {
        public string OrderNo { get; set; } = "";
        public OrderStatus Status { get; set; }
        public long Amount { get; set; }
    }

    private sealed class RefundBrief
    {
        public string RefundNo { get; set; } = "";
        public RefundStatus Status { get; set; }
        public long Amount { get; set; }
    }

    /// <summary>执行一个批次：下载账单 → 勾对 → 落差异。可重复执行，每次都会重建该批次的差异。</summary>
    public async Task RunAsync(long batchId, CancellationToken ct)
    {
        var batch = await db.GetAsync<PayReconBatch>(batchId);
        if (batch is null || batch.Status != ReconStatus.Pending) return;
        if (!DateOnly.TryParseExact(batch.BillDate, "yyyy-MM-dd", out var date)) return;

        var passage = await cache.GetPassageAsync(batch.PassageId);
        var provider = providers.Find(batch.Provider);
        if (passage is null || provider is null)
        {
            await FailAsync(batch, "通道或支付类型不存在", final: true);
            return;
        }

        var (start, end) = UtcRange(date);
        var localPays = await db.QueryAsync<LocalPay>(
            "SELECT order_no, upstream_trade_no, amount FROM pay_order WHERE passage_id = @PassageId AND status = 1 AND paid_at >= @start AND paid_at < @end",
            new { batch.PassageId, start, end });
        var localRefunds = await db.QueryAsync<LocalRefund>(
            "SELECT refund_no, order_no, upstream_refund_no, amount FROM pay_refund WHERE passage_id = @PassageId AND status = 1 AND source <> 'upstream' AND success_at >= @start AND success_at < @end",
            new { batch.PassageId, start, end });

        List<BillItem> bill;
        try
        {
            bill = await provider.DownloadBillAsync(new BillContext
            {
                PassageId = batch.PassageId,
                Config = passage.Config,
                BillDate = date,
                LocalItems =
                [
                    .. localPays.Select(p => new BillItem { Kind = BillItemKind.Pay, OrderNo = p.OrderNo, UpstreamTradeNo = p.UpstreamTradeNo ?? "", Amount = p.Amount }),
                    .. localRefunds.Select(r => new BillItem { Kind = BillItemKind.Refund, OrderNo = r.OrderNo, RefundNo = r.RefundNo, UpstreamTradeNo = r.UpstreamRefundNo ?? "", Amount = r.Amount }),
                ],
            }, ct);
        }
        catch (Exception ex) when (ex is ProviderException or InvalidDataException or FormatException)
        {
            // 账单可能尚未生成：按间隔重试，超过次数才判失败
            await FailAsync(batch, ex.Message, final: batch.Attempts + 1 >= _options.Recon.MaxAttempts);
            return;
        }

        var diffs = new List<PayReconDiff>();
        PayReconDiff Diff(ReconBizType biz, ReconDiffType type, string orderNo, string refundNo, string tradeNo, long? local, long? upstream, string localStatus) => new()
        {
            BatchId = batch.Id, PassageId = batch.PassageId, Provider = batch.Provider, BillDate = batch.BillDate,
            BizType = biz, DiffType = type, OrderNo = Cut(orderNo, 64), RefundNo = Cut(refundNo, 64), UpstreamTradeNo = Cut(tradeNo, 128),
            LocalAmount = local, UpstreamAmount = upstream, LocalStatus = localStatus, CreatedAt = DateTime.UtcNow,
        };

        // ---------- 支付 ----------
        var billPays = bill.Where(i => i.Kind == BillItemKind.Pay).ToList();
        var localPayMap = localPays.GroupBy(p => p.OrderNo).ToDictionary(g => g.Key, g => g.First());
        var missing = billPays.Select(i => i.OrderNo).Where(no => !localPayMap.ContainsKey(no)).Distinct().ToList();
        // 账单里有、当日本地成功集里没有的单：可能是跨日入账（本地在相邻日成功），也可能是真掉单
        var elsewhere = missing.Count == 0
            ? []
            : (await db.QueryAsync<OrderBrief>("SELECT order_no, status, amount FROM pay_order WHERE order_no IN @missing", new { missing })).ToDictionary(o => o.OrderNo);
        var matchedPays = new HashSet<string>();
        foreach (var item in billPays)
        {
            if (localPayMap.TryGetValue(item.OrderNo, out var local))
            {
                matchedPays.Add(item.OrderNo);
                if (local.Amount != item.Amount)
                    diffs.Add(Diff(ReconBizType.Pay, ReconDiffType.AmountMismatch, item.OrderNo, "", item.UpstreamTradeNo, local.Amount, item.Amount, "支付成功"));
            }
            else if (elsewhere.TryGetValue(item.OrderNo, out var other))
            {
                if (other.Status != OrderStatus.Paid)
                    diffs.Add(Diff(ReconBizType.Pay, ReconDiffType.UpstreamOnly, item.OrderNo, "", item.UpstreamTradeNo, other.Amount, item.Amount, StatusText(other.Status)));
                else if (other.Amount != item.Amount)
                    diffs.Add(Diff(ReconBizType.Pay, ReconDiffType.AmountMismatch, item.OrderNo, "", item.UpstreamTradeNo, other.Amount, item.Amount, "支付成功（跨日）"));
            }
            else
            {
                diffs.Add(Diff(ReconBizType.Pay, ReconDiffType.UpstreamOnly, item.OrderNo, "", item.UpstreamTradeNo, null, item.Amount, "订单不存在"));
            }
        }
        foreach (var local in localPays.Where(p => !matchedPays.Contains(p.OrderNo)))
            diffs.Add(Diff(ReconBizType.Pay, ReconDiffType.LocalOnly, local.OrderNo, "", local.UpstreamTradeNo ?? "", local.Amount, null, "支付成功"));

        // ---------- 退款 ----------
        var billRefunds = bill.Where(i => i.Kind == BillItemKind.Refund).ToList();
        var localRefundMap = localRefunds.GroupBy(r => r.RefundNo).ToDictionary(g => g.Key, g => g.First());
        var missingRefunds = billRefunds.Select(i => i.RefundNo).Where(no => no.Length > 0 && !localRefundMap.ContainsKey(no)).Distinct().ToList();
        var refundsElsewhere = missingRefunds.Count == 0
            ? []
            : (await db.QueryAsync<RefundBrief>("SELECT refund_no, status, amount FROM pay_refund WHERE refund_no IN @missingRefunds", new { missingRefunds })).ToDictionary(r => r.RefundNo);
        var matchedRefunds = new HashSet<string>();
        foreach (var item in billRefunds)
        {
            if (localRefundMap.TryGetValue(item.RefundNo, out var local))
            {
                matchedRefunds.Add(item.RefundNo);
                if (local.Amount != item.Amount)
                    diffs.Add(Diff(ReconBizType.Refund, ReconDiffType.AmountMismatch, item.OrderNo, item.RefundNo, item.UpstreamTradeNo, local.Amount, item.Amount, "退款成功"));
            }
            else if (refundsElsewhere.TryGetValue(item.RefundNo, out var other))
            {
                if (other.Status != RefundStatus.Success)
                    diffs.Add(Diff(ReconBizType.Refund, ReconDiffType.UpstreamOnly, item.OrderNo, item.RefundNo, item.UpstreamTradeNo, other.Amount, item.Amount, other.Status == RefundStatus.Processing ? "退款处理中" : "退款失败"));
                else if (other.Amount != item.Amount)
                    diffs.Add(Diff(ReconBizType.Refund, ReconDiffType.AmountMismatch, item.OrderNo, item.RefundNo, item.UpstreamTradeNo, other.Amount, item.Amount, "退款成功（跨日）"));
            }
            else
            {
                diffs.Add(Diff(ReconBizType.Refund, ReconDiffType.UpstreamOnly, item.OrderNo, item.RefundNo, item.UpstreamTradeNo, null, item.Amount, "退款单不存在"));
            }
        }
        foreach (var local in localRefunds.Where(r => !matchedRefunds.Contains(r.RefundNo)))
            diffs.Add(Diff(ReconBizType.Refund, ReconDiffType.LocalOnly, local.OrderNo, local.RefundNo, local.UpstreamRefundNo ?? "", local.Amount, null, "退款成功"));

        var total = diffs.Count;
        if (total > MaxDiffRows) logger.LogError("对账批次 {BatchId} 差异 {Total} 条，超过上限，仅保存前 {Max} 条", batchId, total, MaxDiffRows);

        await db.TransactionAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync("DELETE FROM pay_recon_diff WHERE batch_id = @batchId", new { batchId }, tx);
            foreach (var diff in diffs.Take(MaxDiffRows)) await Db.InsertAsync(conn, tx, diff);
            await conn.ExecuteAsync(
                """
                UPDATE pay_recon_batch SET status = @status, upstream_pay_count = @upc, upstream_pay_amount = @upa,
                  upstream_refund_count = @urc, upstream_refund_amount = @ura, local_pay_count = @lpc, local_pay_amount = @lpa,
                  local_refund_count = @lrc, local_refund_amount = @lra, diff_count = @total, error_msg = '', attempts = attempts + 1,
                  next_run_at = NULL, finished_at = UTC_TIMESTAMP(3), updated_at = UTC_TIMESTAMP(3)
                WHERE id = @batchId
                """,
                new
                {
                    batchId, total,
                    status = total == 0 ? ReconStatus.Balanced : ReconStatus.HasDiff,
                    upc = billPays.Count, upa = billPays.Sum(i => i.Amount),
                    urc = billRefunds.Count, ura = billRefunds.Sum(i => i.Amount),
                    lpc = localPays.Count, lpa = localPays.Sum(p => p.Amount),
                    lrc = localRefunds.Count, lra = localRefunds.Sum(r => r.Amount),
                }, tx);
            return 0;
        });

        if (total == 0)
            logger.LogInformation("对账 {Provider} 通道 {PassageId} {Date}：平账，支付 {Pays} 笔，退款 {Refunds} 笔", batch.Provider, batch.PassageId, batch.BillDate, billPays.Count, billRefunds.Count);
        else
            logger.LogWarning("对账 {Provider} 通道 {PassageId} {Date}：发现 {Total} 条差异", batch.Provider, batch.PassageId, batch.BillDate, total);
    }

    private async Task FailAsync(PayReconBatch batch, string message, bool final)
    {
        message = Cut(message, 500);
        if (final)
        {
            logger.LogError("对账批次 {BatchId}（通道 {PassageId} {Date}）失败：{Message}", batch.Id, batch.PassageId, batch.BillDate, message);
            await db.ExecuteAsync(
                "UPDATE pay_recon_batch SET status = 3, attempts = attempts + 1, error_msg = @message, next_run_at = NULL, finished_at = UTC_TIMESTAMP(3), updated_at = UTC_TIMESTAMP(3) WHERE id = @Id",
                new { batch.Id, message });
            return;
        }
        logger.LogWarning("对账批次 {BatchId} 暂未成功，{Minutes} 分钟后重试：{Message}", batch.Id, _options.Recon.RetryMinutes, message);
        await db.ExecuteAsync(
            "UPDATE pay_recon_batch SET attempts = attempts + 1, error_msg = @message, next_run_at = @next, updated_at = UTC_TIMESTAMP(3) WHERE id = @Id AND status = 0",
            new { batch.Id, message, next = DateTime.UtcNow.AddMinutes(_options.Recon.RetryMinutes) });
    }

    /// <summary>把差异标记为已处理并写明处理说明。</summary>
    public async Task HandleDiffAsync(long diffId, string remark, string username)
    {
        if (string.IsNullOrWhiteSpace(remark)) throw new PayException(ErrorCodes.InvalidParam, "请填写处理说明");
        var rows = await db.ExecuteAsync(
            "UPDATE pay_recon_diff SET status = 1, remark = @remark, handled_by = @username, handled_at = UTC_TIMESTAMP(3) WHERE id = @diffId",
            new { diffId, remark = Cut(remark.Trim(), 255), username });
        if (rows == 0) throw new PayException(ErrorCodes.NotFound, "差异记录不存在");
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private static string StatusText(OrderStatus status) => status switch
    {
        OrderStatus.Pending => "待支付",
        OrderStatus.Closed => "已关闭",
        OrderStatus.Failed => "支付失败",
        _ => "支付成功",
    };
}
