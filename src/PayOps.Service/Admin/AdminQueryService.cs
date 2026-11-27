using Microsoft.Extensions.Options;
using PayOps.Core;
using PayOps.Core.Providers;
using PayOps.Service.Data;

namespace PayOps.Service.Admin;

/// <summary>后台查询：订单、退款、通知任务、上游回调日志与仪表盘统计。</summary>
public sealed class AdminQueryService(Db db, IPayProviderRegistry providers, IOptions<PayOpsOptions> options)
{
    private const string OrderSelect =
        "o.id, o.order_no, o.mch_order_no, o.app_id, o.app_code, o.channel_id, o.channel_code, o.passage_id, p.name AS passage_name, o.provider, o.pay_method, o.subject, o.amount, o.refund_amount, o.currency, o.status, o.upstream_trade_no, o.user_id, o.created_at, o.paid_at, o.expire_at";

    private const string OrderDetailSelect = OrderSelect +
        ", o.body, o.product_id, o.client_ip, o.buyer_id, o.notify_url, o.return_url, o.attach, o.credential, o.error_code, o.error_msg, o.closed_at, o.check_count, o.next_check_at, o.updated_at, o.binding_id";

    private const string OrderFrom = "pay_order o LEFT JOIN pay_passage p ON p.id = o.passage_id";

    private const string RefundSelect =
        "id, refund_no, order_no, mch_refund_no, app_id, app_code, passage_id, provider, amount, reason, status, upstream_refund_no, error_msg, source, created_at, success_at";

    private const string NotifyTaskSelect =
        "id, app_id, app_code, type, biz_no, url, payload, status, attempts, max_attempts, next_time, last_http_status, last_response, last_time, created_at";

    private const string UpstreamSelect = "id, provider, passage_id, kind, order_no, verified, result, request_body, client_ip, created_at";

    private readonly int _offsetHours = options.Value.TimeZoneOffsetHours;

    // ------------------------------------------------------------------ 订单

    public Task<PageResult<OrderItem>> ListOrdersAsync(OrderQuery q)
    {
        var where = new SqlWhere()
            .Add("o.order_no = {0}", q.OrderNo)
            .Add("o.mch_order_no = {0}", q.MchOrderNo)
            .Add("o.upstream_trade_no = {0}", q.UpstreamTradeNo)
            .Add("o.app_id = {0}", q.AppId)
            .Add("o.channel_id = {0}", q.ChannelId)
            .Add("o.passage_id = {0}", q.PassageId)
            .Add("o.provider = {0}", q.Provider)
            .Add("o.status = {0}", q.Status)
            .Add("o.user_id = {0}", q.UserId)
            .Add("o.created_at >= {0}", q.StartUtc)
            .Add("o.created_at < {0}", q.EndUtc);
        return db.PageAsync<OrderItem>(OrderSelect, OrderFrom, where, "o.id DESC", q.Page, q.PageSize);
    }

    public async Task<OrderDetail> GetOrderAsync(string orderNo)
    {
        var detail = await db.FirstOrDefaultAsync<OrderDetail>($"SELECT {OrderDetailSelect} FROM {OrderFrom} WHERE o.order_no = @orderNo", new { orderNo })
                     ?? throw new PayException(ErrorCodes.OrderNotFound, "订单不存在");
        detail.Refunds = await db.QueryAsync<RefundItem>($"SELECT {RefundSelect} FROM pay_refund WHERE order_no = @orderNo ORDER BY id DESC", new { orderNo });
        var bizNos = detail.Refunds.Select(r => r.RefundNo).Append(orderNo).ToList();
        detail.NotifyTasks = await db.QueryAsync<NotifyTaskItem>($"SELECT {NotifyTaskSelect} FROM pay_notify_task WHERE biz_no IN @bizNos ORDER BY id DESC", new { bizNos });
        detail.UpstreamNotifies = await db.QueryAsync<UpstreamNotifyItem>($"SELECT {UpstreamSelect} FROM pay_upstream_notify WHERE order_no = @orderNo ORDER BY id DESC LIMIT 50", new { orderNo });
        return detail;
    }

    // ------------------------------------------------------------------ 退款 / 通知 / 回调日志

    public Task<PageResult<RefundItem>> ListRefundsAsync(RefundQuery q)
    {
        var where = new SqlWhere()
            .Add("refund_no = {0}", q.RefundNo)
            .Add("order_no = {0}", q.OrderNo)
            .Add("mch_refund_no = {0}", q.MchRefundNo)
            .Add("app_id = {0}", q.AppId)
            .Add("status = {0}", q.Status)
            .Add("created_at >= {0}", q.StartUtc)
            .Add("created_at < {0}", q.EndUtc);
        return db.PageAsync<RefundItem>(RefundSelect, "pay_refund", where, "id DESC", q.Page, q.PageSize);
    }

    public Task<PageResult<NotifyTaskItem>> ListNotifyTasksAsync(NotifyTaskQuery q)
    {
        var where = new SqlWhere()
            .Add("biz_no = {0}", q.BizNo)
            .Add("app_id = {0}", q.AppId)
            .Add("status = {0}", q.Status)
            .Add("type = {0}", q.Type)
            .Add("created_at >= {0}", q.StartUtc)
            .Add("created_at < {0}", q.EndUtc);
        return db.PageAsync<NotifyTaskItem>(NotifyTaskSelect, "pay_notify_task", where, "id DESC", q.Page, q.PageSize);
    }

    public Task<PageResult<UpstreamNotifyItem>> ListUpstreamNotifiesAsync(UpstreamNotifyQuery q)
    {
        var where = new SqlWhere()
            .Add("provider = {0}", q.Provider)
            .Add("order_no = {0}", q.OrderNo)
            .Add("passage_id = {0}", q.PassageId)
            .Add("verified = {0}", q.Verified)
            .Add("created_at >= {0}", q.StartUtc)
            .Add("created_at < {0}", q.EndUtc);
        return db.PageAsync<UpstreamNotifyItem>(UpstreamSelect, "pay_upstream_notify", where, "id DESC", q.Page, q.PageSize);
    }

    // ------------------------------------------------------------------ 仪表盘

    /// <summary>业务时区某天 0 点对应的 UTC 时间；dayOffset=0 为今天。</summary>
    private DateTime DayStartUtc(int dayOffset)
    {
        var local = DateTime.UtcNow.AddHours(_offsetHours).Date.AddDays(dayOffset);
        return DateTime.SpecifyKind(local.AddHours(-_offsetHours), DateTimeKind.Utc);
    }

    public async Task<DashboardSummary> SummaryAsync()
    {
        var today = DayStartUtc(0);
        return new DashboardSummary
        {
            Today = await StatAsync(today, today.AddDays(1)),
            Yesterday = await StatAsync(today.AddDays(-1), today),
            PendingNotify = await db.ScalarAsync<long>("SELECT COUNT(*) FROM pay_notify_task WHERE status = 0"),
            FailedNotify = await db.ScalarAsync<long>("SELECT COUNT(*) FROM pay_notify_task WHERE status = 2"),
            ProcessingRefund = await db.ScalarAsync<long>("SELECT COUNT(*) FROM pay_refund WHERE status = 0"),
        };
    }

    private async Task<DashboardStat> StatAsync(DateTime start, DateTime end)
    {
        var stat = await db.FirstOrDefaultAsync<DashboardStat>(
            """
            SELECT COUNT(*) AS order_count,
                   COALESCE(SUM(status = 1), 0) AS paid_count,
                   COALESCE(SUM(IF(status = 1, amount, 0)), 0) AS paid_amount
            FROM pay_order WHERE created_at >= @start AND created_at < @end
            """, new { start, end }) ?? new DashboardStat();
        stat.RefundAmount = await db.ScalarAsync<long>(
            "SELECT COALESCE(SUM(amount), 0) FROM pay_refund WHERE status = 1 AND success_at >= @start AND success_at < @end", new { start, end });
        stat.SuccessRate = Rate(stat.PaidCount, stat.OrderCount);
        return stat;
    }

    public async Task<List<TrendPoint>> TrendAsync(int days)
    {
        days = Math.Clamp(days <= 0 ? 7 : days, 1, 90);
        var start = DayStartUtc(-(days - 1));
        var rows = await db.QueryAsync<TrendPoint>(
            """
            SELECT DATE_FORMAT(DATE_ADD(created_at, INTERVAL @offset HOUR), '%Y-%m-%d') AS date,
                   COUNT(*) AS order_count,
                   COALESCE(SUM(status = 1), 0) AS paid_count,
                   COALESCE(SUM(IF(status = 1, amount, 0)), 0) AS paid_amount
            FROM pay_order WHERE created_at >= @start
            GROUP BY date
            """, new { start, offset = _offsetHours });
        var map = rows.ToDictionary(r => r.Date);
        var firstDay = DateTime.UtcNow.AddHours(_offsetHours).Date.AddDays(-(days - 1));
        return Enumerable.Range(0, days)
            .Select(i => firstDay.AddDays(i).ToString("yyyy-MM-dd"))
            .Select(d => map.GetValueOrDefault(d) ?? new TrendPoint { Date = d })
            .ToList();
    }

    public async Task<List<DistributionItem>> DistributionAsync(string? dimension, int days)
    {
        days = Math.Clamp(days <= 0 ? 1 : days, 1, 90);
        var start = DayStartUtc(-(days - 1));
        var (key, name, join) = dimension switch
        {
            "passage" => ("o.passage_id", "COALESCE(p.name, '未路由')", "LEFT JOIN pay_passage p ON p.id = o.passage_id"),
            "provider" => ("o.provider", "o.provider", ""),
            _ => ("o.channel_id", "CONCAT(o.app_code, ' / ', COALESCE(c.name, o.channel_code))", "LEFT JOIN pay_channel c ON c.id = o.channel_id"),
        };
        var rows = await db.QueryAsync<DistributionItem>(
            $"""
             SELECT CAST({key} AS CHAR) AS `key`, MAX({name}) AS name,
                    COUNT(*) AS order_count,
                    COALESCE(SUM(o.status = 1), 0) AS paid_count,
                    COALESCE(SUM(IF(o.status = 1, o.amount, 0)), 0) AS paid_amount
             FROM pay_order o {join}
             WHERE o.created_at >= @start
             GROUP BY {key}
             ORDER BY paid_amount DESC, order_count DESC
             LIMIT 50
             """, new { start });
        foreach (var r in rows)
        {
            r.SuccessRate = Rate(r.PaidCount, r.OrderCount);
            if (dimension == "provider") r.Name = providers.Find(r.Key)?.Meta.Name ?? r.Key;
        }
        return rows;
    }

    private static double Rate(long paid, long total) => total == 0 ? 0 : Math.Round(paid / (double)total, 4);
}
