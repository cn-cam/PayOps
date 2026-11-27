using Dapper;
using Microsoft.Extensions.Logging;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Core.Providers;
using PayOps.Service.Data;
using PayOps.Service.Infra;

namespace PayOps.Service.Pay;

/// <summary>
/// 自动续费订阅。
/// - 首购：业务以 subscribe=true 下单 → 客户端在商店完成订阅 → 凭证核验或商店通知入账，同一事务内建立订阅；
/// - 续订：商店每期扣款成功的通知生成一笔"续费单"（生来已支付）并顺延订阅周期，业务方收到 orderType=2 的支付通知；
/// - 退订 / 恢复续订 / 到期 / 撤销：以商店通知为准，条件更新保证每次状态变化只通知业务一次；
///   支持服务端退订的类型还可以通过接口主动退订；
/// - 巡检：到期后若没等到通知，主动向商店查询确认是续上了还是过期了。
/// </summary>
public sealed class SubscriptionService(
    Db db,
    ConfigCache cache,
    OrderService orders,
    NotifyService notify,
    IPayProviderRegistry providers,
    ILogger<SubscriptionService> logger)
{
    private static readonly TimeSpan CheckLease = TimeSpan.FromSeconds(120);
    /// <summary>到期后多久仍未续上就判定过期：能主动查询的类型给足扣款重试期，查不了的类型只等 1 天。</summary>
    private static readonly TimeSpan GraceKnown = TimeSpan.FromDays(16);
    private static readonly TimeSpan GraceUnknown = TimeSpan.FromDays(1);

    /// <summary>下次巡检时间：到期后 10 分钟（留出商店通知到达的时间）。</summary>
    public static DateTime? NextCheck(DateTime? expiresAt, DateTime now) =>
        expiresAt is null ? now.AddDays(1) : (expiresAt.Value > now ? expiresAt.Value : now).AddMinutes(10);

    public Task<PaySubscription?> FindAsync(string subNo) =>
        db.FirstOrDefaultAsync<PaySubscription>("SELECT * FROM pay_subscription WHERE sub_no = @subNo", new { subNo });

    public Task<PaySubscription?> FindByUpstreamAsync(string provider, string upstreamSubId) =>
        db.FirstOrDefaultAsync<PaySubscription>(
            "SELECT * FROM pay_subscription WHERE provider = @provider AND upstream_sub_id = @upstreamSubId", new { provider, upstreamSubId });

    public async Task<PaySubscription> RequireAsync(long appId, string? subNo, string? orderNo, string? mchOrderNo)
    {
        PaySubscription? sub;
        if (!string.IsNullOrWhiteSpace(subNo))
        {
            sub = await FindAsync(subNo.Trim());
        }
        else
        {
            var order = await orders.RequireAsync(appId, orderNo, mchOrderNo);
            sub = order.SubNo.Length == 0 ? null : await FindAsync(order.SubNo);
        }
        if (sub is null || sub.AppId != appId) throw new PayException(ErrorCodes.NotFound, "订阅不存在（订单尚未支付或不是订阅订单）");
        return sub;
    }

    // ------------------------------------------------------------------ 上游事件

    /// <summary>处理上游回调里的订阅事件。</summary>
    public async Task<(NotifyOutcome Outcome, string Message)> HandleEventAsync(NotifyEvent e, string provider, long passageId)
    {
        if (e.Subscription is not { } info || info.UpstreamSubId.Length == 0) return (NotifyOutcome.Error, "订阅事件缺少订阅标识");
        var sub = await FindByUpstreamAsync(provider, info.UpstreamSubId);

        if (e.SubKind == SubscriptionEventKind.Charged)
        {
            if (sub is null) return await HandleInitialAsync(e, info, passageId);
            if (sub.PassageId != passageId) return (NotifyOutcome.OrderNotFound, "订阅不属于该通道");
            var outcome = await orders.RecordRenewalAsync(sub,
                new RenewalCharge(e.UpstreamTradeNo ?? "", e.Amount ?? info.Amount, e.PaidAt, info.PeriodStart, info.ExpiresAt, e.BuyerId, info.Token), "notify");
            return outcome switch
            {
                RenewalOutcome.Renewed => (NotifyOutcome.Success, $"订阅 {sub.SubNo} 续费成功"),
                RenewalOutcome.Duplicate => (NotifyOutcome.Success, "重复通知，该笔扣款已入账"),
                _ => (NotifyOutcome.Error, "续费事件缺少上游交易号"),
            };
        }

        if (sub is null) return (NotifyOutcome.Success, "订阅不存在，忽略");
        if (sub.PassageId != passageId) return (NotifyOutcome.OrderNotFound, "订阅不属于该通道");
        var changed = await ApplyStateAsync(sub, e.SubKind, info.ExpiresAt);
        return (NotifyOutcome.Success, changed ? $"订阅 {sub.SubNo} 状态已更新：{Describe(e.SubKind)}" : "订阅状态无变化");
    }

    private async Task<(NotifyOutcome, string)> HandleInitialAsync(NotifyEvent e, SubscriptionInfo info, long passageId)
    {
        if (e.OrderNo.Length == 0) return (NotifyOutcome.Success, "订阅首购未关联订单，等待客户端提交凭证核验");
        var order = await orders.FindAsync(e.OrderNo);
        if (order is null) return (NotifyOutcome.OrderNotFound, $"订单 {e.OrderNo} 不存在");
        if (order.PassageId != passageId) return (NotifyOutcome.OrderNotFound, "订单不属于该通道");
        if (!string.IsNullOrEmpty(e.ProductId) && order.ProductId.Length > 0 && e.ProductId != order.ProductId)
            return (NotifyOutcome.AmountMismatch, "商品与订单不一致");

        var outcome = await orders.HandlePaidAsync(order, new PaidInfo(e.UpstreamTradeNo, e.Amount, e.PaidAt, e.BuyerId, info), "notify");
        return outcome switch
        {
            PaidOutcome.Success => (NotifyOutcome.Success, "订阅首购已入账"),
            PaidOutcome.AlreadyPaid => (NotifyOutcome.Success, "重复通知，订单已支付"),
            PaidOutcome.AmountMismatch => (NotifyOutcome.AmountMismatch, "金额与订单不一致"),
            PaidOutcome.TradeReused => (NotifyOutcome.Error, "上游交易号已被其他订单使用"),
            _ => (NotifyOutcome.Error, "订单已由其他交易支付"),
        };
    }

    /// <summary>
    /// 应用一次状态变化。每种变化都是带前置条件的更新，只有真正发生变化时才写业务通知（同一事务）。
    /// </summary>
    public async Task<bool> ApplyStateAsync(PaySubscription sub, SubscriptionEventKind kind, DateTime? expiresAt = null)
    {
        var now = DateTime.UtcNow;
        var (sql, eventName) = kind switch
        {
            SubscriptionEventKind.AutoRenewOff =>
                ("UPDATE pay_subscription SET auto_renew = 0, cancel_at = @now, expires_at = COALESCE(@expiresAt, expires_at), updated_at = @now WHERE id = @Id AND status = 1 AND auto_renew = 1", "canceled"),
            SubscriptionEventKind.AutoRenewOn =>
                ("UPDATE pay_subscription SET auto_renew = 1, cancel_at = NULL, expires_at = COALESCE(@expiresAt, expires_at), updated_at = @now WHERE id = @Id AND status = 1 AND auto_renew = 0", "resumed"),
            SubscriptionEventKind.Expired =>
                ("UPDATE pay_subscription SET status = 2, auto_renew = 0, next_check_at = NULL, updated_at = @now WHERE id = @Id AND status = 1", "expired"),
            SubscriptionEventKind.Revoked =>
                ("UPDATE pay_subscription SET status = 3, auto_renew = 0, next_check_at = NULL, updated_at = @now WHERE id = @Id AND status <> 3", "revoked"),
            _ => ("", ""),
        };
        if (sql.Length == 0) return false;

        var url = await notify.ResolveUrlAsync(sub.AppId, sub.NotifyUrl);
        var taskId = await db.TransactionAsync(async (conn, tx) =>
        {
            if (await conn.ExecuteAsync(sql, new { sub.Id, now, expiresAt }, tx) == 0) return -1L;
            var latest = await conn.QueryFirstAsync<PaySubscription>("SELECT * FROM pay_subscription WHERE id = @Id", new { sub.Id }, tx);
            Copy(latest, sub);
            var bizNo = $"{sub.SubNo}-{eventName[..2].ToUpperInvariant()}-{new DateTimeOffset(now).ToUnixTimeMilliseconds():x}";
            return await notify.InsertTaskAsync(conn, tx, sub.AppId, sub.AppCode, NotifyType.Subscription, bizNo, url, NotifyService.BuildSubscriptionPayload(sub, eventName));
        });
        if (taskId < 0) return false;

        logger.LogInformation("订阅 {SubNo} {Change}，到期时间 {ExpiresAt:u}", sub.SubNo, Describe(kind), sub.ExpiresAt);
        await notify.EnqueueAsync(taskId);
        return true;
    }

    private static void Copy(PaySubscription from, PaySubscription to)
    {
        to.Status = from.Status;
        to.AutoRenew = from.AutoRenew;
        to.ExpiresAt = from.ExpiresAt;
        to.PeriodStart = from.PeriodStart;
        to.CancelAt = from.CancelAt;
        to.RenewCount = from.RenewCount;
        to.LastOrderNo = from.LastOrderNo;
        to.NextCheckAt = from.NextCheckAt;
        to.UpdatedAt = from.UpdatedAt;
    }

    private static string Describe(SubscriptionEventKind kind) => kind switch
    {
        SubscriptionEventKind.AutoRenewOff => "已退订（当前周期结束后失效）",
        SubscriptionEventKind.AutoRenewOn => "已恢复续订",
        SubscriptionEventKind.Expired => "已到期",
        SubscriptionEventKind.Revoked => "已撤销",
        _ => kind.ToString(),
    };

    // ------------------------------------------------------------------ 主动操作

    private async Task<(IPayProvider Provider, SubscriptionContext Context)> ContextAsync(PaySubscription sub)
    {
        var provider = providers.Get(sub.Provider);
        var passage = await cache.GetPassageAsync(sub.PassageId) ?? throw new PayException(ErrorCodes.PassageError, "订阅所属通道不存在");
        return (provider, new SubscriptionContext
        {
            PassageId = sub.PassageId,
            Config = passage.Config,
            UpstreamSubId = sub.UpstreamSubId,
            Token = sub.UpstreamToken,
            ProductId = sub.ProductId,
        });
    }

    /// <summary>服务端退订：停止自动续订，当前周期仍然有效。</summary>
    public async Task<PaySubscription> CancelAsync(PaySubscription sub, CancellationToken ct)
    {
        if (sub.Status != SubscriptionStatus.Active) throw new PayException(ErrorCodes.OrderStateError, "订阅已失效，无需退订");
        if (!sub.AutoRenew) return sub;
        var (provider, context) = await ContextAsync(sub);
        if (!provider.Meta.Capabilities.SubscriptionCancel)
            throw new PayException(ErrorCodes.NotSupported, $"{provider.Meta.Name}不支持服务端退订，请引导用户在商店的订阅管理页退订；退订结果会通过通知自动同步");
        try
        {
            await provider.CancelSubscriptionAsync(context, ct);
        }
        catch (ProviderException ex)
        {
            throw new PayException(ErrorCodes.PassageError, "退订失败：" + ex.Message);
        }
        await ApplyStateAsync(sub, SubscriptionEventKind.AutoRenewOff);
        return sub;
    }

    /// <summary>向上游查询订阅现状并同步：补记漏掉的续费、同步续订开关、确认过期/撤销。</summary>
    public async Task<SyncResult> SyncAsync(PaySubscription sub, CancellationToken ct) => (await SyncCoreAsync(sub, ct)).Result;

    /// <returns>Known = 上游是否给出了确定结论（决定到期后的宽限时长）。</returns>
    private async Task<(SyncResult Result, bool Known)> SyncCoreAsync(PaySubscription sub, CancellationToken ct)
    {
        var (provider, context) = await ContextAsync(sub);
        SubscriptionState state;
        try
        {
            state = await provider.QuerySubscriptionAsync(context, ct);
        }
        catch (ProviderException ex)
        {
            return (new SyncResult((int)sub.Status, false, "查询上游失败：" + ex.Message), true);
        }
        if (!state.Known) return (new SyncResult((int)sub.Status, false, state.Message.Length > 0 ? state.Message : "该通道不支持主动查询订阅状态"), false);

        var changed = false;
        if (state.Revoked)
        {
            changed = await ApplyStateAsync(sub, SubscriptionEventKind.Revoked);
            return (new SyncResult((int)sub.Status, changed, "上游已撤销该订阅"), true);
        }

        if (state.Active && !string.IsNullOrEmpty(state.LatestTradeNo))
        {
            var outcome = await orders.RecordRenewalAsync(sub,
                new RenewalCharge(state.LatestTradeNo, state.LatestAmount, state.LatestPaidAt, state.PeriodStart, state.ExpiresAt, "", ""), "sync");
            changed |= outcome == RenewalOutcome.Renewed;
        }

        if (state.Active)
        {
            if (sub.Status == SubscriptionStatus.Active && state.AutoRenew != sub.AutoRenew)
                changed |= await ApplyStateAsync(sub, state.AutoRenew ? SubscriptionEventKind.AutoRenewOn : SubscriptionEventKind.AutoRenewOff, state.ExpiresAt);
        }
        else if (sub.Status == SubscriptionStatus.Active)
        {
            changed |= await ApplyStateAsync(sub, SubscriptionEventKind.Expired);
        }

        return (new SyncResult((int)sub.Status, changed, changed ? "已按上游状态同步" : "与上游一致，无变化"), true);
    }

    // ------------------------------------------------------------------ 巡检

    public Task<List<string>> ClaimDueChecksAsync(int batchSize) =>
        db.TransactionAsync(async (conn, tx) =>
        {
            var due = (await conn.QueryAsync<string>(
                "SELECT sub_no FROM pay_subscription WHERE status = 1 AND next_check_at <= UTC_TIMESTAMP(3) ORDER BY next_check_at LIMIT @batchSize FOR UPDATE SKIP LOCKED",
                new { batchSize }, tx)).AsList();
            if (due.Count > 0)
                await conn.ExecuteAsync(
                    "UPDATE pay_subscription SET next_check_at = DATE_ADD(UTC_TIMESTAMP(3), INTERVAL @lease SECOND) WHERE sub_no IN @due",
                    new { lease = (int)CheckLease.TotalSeconds, due }, tx);
            return due;
        });

    /// <summary>巡检消费：订阅到期后仍未收到续订/过期通知时，主动确认。</summary>
    public async Task CheckAsync(string subNo, CancellationToken ct)
    {
        var sub = await FindAsync(subNo);
        if (sub is null || sub.Status != SubscriptionStatus.Active) return;

        var (_, queryable) = await SyncCoreAsync(sub, ct);
        sub = await FindAsync(subNo);
        if (sub is null || sub.Status != SubscriptionStatus.Active) return;

        var now = DateTime.UtcNow;
        if (sub.ExpiresAt is { } expires && expires <= now && now - expires > (queryable ? GraceKnown : GraceUnknown))
        {
            await ApplyStateAsync(sub, SubscriptionEventKind.Expired);
            return;
        }

        var next = sub.ExpiresAt is { } e && e > now ? e.AddMinutes(10) : now.AddHours(1);
        await db.ExecuteAsync(
            "UPDATE pay_subscription SET next_check_at = @next, check_count = check_count + 1, updated_at = UTC_TIMESTAMP(3) WHERE id = @Id AND status = 1",
            new { sub.Id, next });
    }
}
