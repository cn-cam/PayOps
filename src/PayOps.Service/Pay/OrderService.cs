using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Core.Providers;
using PayOps.Core.Routing;
using PayOps.Service.Data;
using PayOps.Service.Infra;

namespace PayOps.Service.Pay;

public sealed class CreateOrderRequest
{
    public string Channel { get; set; } = "";
    public string PayMethod { get; set; } = "";
    public string MchOrderNo { get; set; } = "";
    public long Amount { get; set; }
    public string Currency { get; set; } = "CNY";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ClientIp { get; set; } = "";
    /// <summary>微信 openid / 小米 uid 等上游用户标识。</summary>
    public string OpenId { get; set; } = "";
    /// <summary>应用商店内购商品 ID。</summary>
    public string ProductId { get; set; } = "";
    public string NotifyUrl { get; set; } = "";
    public string ReturnUrl { get; set; } = "";
    public string Attach { get; set; } = "";
    public int ExpireSeconds { get; set; }
    /// <summary>自动续费订阅首购：仅支持订阅的支付类型可用（应用商店内购）。</summary>
    public bool Subscribe { get; set; }
    public Dictionary<string, string>? Extra { get; set; }
}

public sealed class OrderResult
{
    public string OrderNo { get; set; } = "";
    public string MchOrderNo { get; set; } = "";
    public int Status { get; set; }
    public long Amount { get; set; }
    public string Provider { get; set; } = "";
    public string PayMethod { get; set; } = "";
    public int OrderType { get; set; }
    public long PassageId { get; set; }
    public string PassageName { get; set; } = "";
    public DateTime ExpireAt { get; set; }
    public JsonElement? Credential { get; set; }
}

/// <param name="Subscription">非空表示这笔成功扣款属于自动续费订阅（首购），入账时同时建立订阅。</param>
public sealed record PaidInfo(string? UpstreamTradeNo, long? Amount, DateTime? PaidAt, string BuyerId, SubscriptionInfo? Subscription = null);

/// <summary>一次订阅续费扣款。</summary>
public sealed record RenewalCharge(string TradeNo, long? Amount, DateTime? PaidAt, DateTime? PeriodStart, DateTime? ExpiresAt, string BuyerId, string Token);

public enum RenewalOutcome { Renewed, Duplicate, Invalid }

public enum PaidOutcome { Success, AlreadyPaid, OrderNotFound, AmountMismatch, TradeReused, Conflict }

public sealed record SyncResult(int Status, bool Changed, string Message);

public sealed class OrderService(
    Db db,
    ConfigCache cache,
    RedisStore redis,
    RouteService router,
    NotifyService notify,
    IPayProviderRegistry providers,
    OrderNoGenerator orderNo,
    IOptions<PayOpsOptions> options,
    ILogger<OrderService> logger)
{
    private static readonly TimeSpan CheckLease = TimeSpan.FromSeconds(120);
    private readonly PayOpsOptions _options = options.Value;

    public string UpstreamNotifyUrl(string provider, long passageId, string kind = "") =>
        $"{_options.PublicBaseUrl.TrimEnd('/')}/api/notify/{provider}/{passageId}{(kind.Length > 0 ? "/" + kind : "")}";

    public Task<PayOrder?> FindAsync(string orderNo) =>
        db.FirstOrDefaultAsync<PayOrder>("SELECT * FROM pay_order WHERE order_no = @orderNo", new { orderNo });

    public Task<PayOrder?> FindByMchOrderNoAsync(long appId, string mchOrderNo) =>
        db.FirstOrDefaultAsync<PayOrder>("SELECT * FROM pay_order WHERE app_id = @appId AND mch_order_no = @mchOrderNo", new { appId, mchOrderNo });

    public async Task<PayOrder> RequireAsync(long appId, string? orderNo, string? mchOrderNo)
    {
        PayOrder? order = null;
        if (!string.IsNullOrWhiteSpace(orderNo)) order = await FindAsync(orderNo.Trim());
        else if (!string.IsNullOrWhiteSpace(mchOrderNo)) order = await FindByMchOrderNoAsync(appId, mchOrderNo.Trim());
        else throw new PayException(ErrorCodes.InvalidParam, "orderNo 与 mchOrderNo 至少传一个");
        if (order is null || order.AppId != appId) throw new PayException(ErrorCodes.OrderNotFound, "订单不存在");
        return order;
    }

    // ------------------------------------------------------------------ 下单

    public async Task<OrderResult> CreateAsync(PayApp app, CreateOrderRequest req, CancellationToken ct)
    {
        Validate(req);
        var provider = providers.FindByMethod(req.PayMethod)
                       ?? throw new PayException(ErrorCodes.InvalidParam, $"不支持的支付方式：{req.PayMethod}");
        if (req.Subscribe && !provider.Meta.Capabilities.Subscription)
            throw new PayException(ErrorCodes.NotSupported, $"{provider.Meta.Name}不支持自动续费订阅");
        var channel = await cache.GetChannelAsync(app.Id, req.Channel);
        if (channel is null || channel.Status != 1)
            throw new PayException(ErrorCodes.ChannelNotFound, $"渠道不存在或已停用：{req.Channel}");

        await using var lockHandle = await redis.TryLockAsync($"create:{app.Id}:{req.MchOrderNo}", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3))
                                     ?? throw new PayException(ErrorCodes.Busy, "相同订单号的请求正在处理中，请稍后重试");

        var existing = await FindByMchOrderNoAsync(app.Id, req.MchOrderNo);
        if (existing is not null && existing.Status != OrderStatus.Failed) return await ReuseAsync(existing, req, channel);

        var route = await router.RouteAsync(channel.Id, provider.Meta.Code, req.PayMethod, req.Amount);
        if (route.Ordered.Count == 0) throw NoPassage(route, provider, req);

        var now = DateTime.UtcNow;
        var expireSeconds = req.ExpireSeconds > 0 ? Math.Clamp(req.ExpireSeconds, 60, 7 * 86400) : _options.Order.DefaultExpireSeconds;
        var order = new PayOrder
        {
            OrderNo = orderNo.Next('P'),
            AppId = app.Id,
            AppCode = app.AppCode,
            MchOrderNo = req.MchOrderNo,
            ChannelId = channel.Id,
            ChannelCode = channel.Code,
            Provider = provider.Meta.Code,
            PayMethod = req.PayMethod,
            OrderType = req.Subscribe ? OrderType.SubscriptionInitial : OrderType.OneTime,
            Subject = req.Subject,
            Body = req.Body,
            Amount = req.Amount,
            Currency = req.Currency,
            Status = OrderStatus.Pending,
            UserId = req.UserId,
            BuyerId = req.OpenId,
            ProductId = req.ProductId,
            ClientIp = req.ClientIp,
            NotifyUrl = req.NotifyUrl,
            ReturnUrl = req.ReturnUrl,
            Attach = req.Attach,
            ExpireAt = now.AddSeconds(expireSeconds),
            NextCheckAt = now.AddSeconds(expireSeconds),
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (existing is not null)
        {
            // 上次下单全部通道失败：沿用该行重新发起，换新平台单号避免上游单号冲突
            order.Id = existing.Id;
            await db.ExecuteAsync(
                """
                UPDATE pay_order SET order_no=@OrderNo, channel_id=@ChannelId, channel_code=@ChannelCode, passage_id=0, binding_id=0,
                  provider=@Provider, pay_method=@PayMethod, order_type=@OrderType, sub_no='', subject=@Subject, body=@Body, amount=@Amount, currency=@Currency, status=0,
                  user_id=@UserId, buyer_id=@BuyerId, product_id=@ProductId, client_ip=@ClientIp, notify_url=@NotifyUrl, return_url=@ReturnUrl,
                  attach=@Attach, credential=NULL, error_code='', error_msg='', expire_at=@ExpireAt, next_check_at=@NextCheckAt,
                  check_count=0, created_at=@CreatedAt, updated_at=@UpdatedAt
                WHERE id=@Id AND status=3
                """, order);
        }
        else
        {
            try
            {
                order.Id = await db.InsertAsync(order);
            }
            catch (MySqlException ex) when (Db.IsDuplicateKey(ex))
            {
                // 并发兜底（Redis 锁降级时）：以数据库唯一键为准
                var dup = await FindByMchOrderNoAsync(app.Id, req.MchOrderNo) ?? throw new PayException(ErrorCodes.Busy, "系统繁忙，请重试");
                return await ReuseAsync(dup, req, channel);
            }
        }

        return await CreateUpstreamAsync(order, req, provider, route.Ordered, ct);
    }

    private async Task<OrderResult> CreateUpstreamAsync(PayOrder order, CreateOrderRequest req, IPayProvider provider, List<RouteCandidate> candidates, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var candidate in candidates.Take(Math.Max(1, _options.Order.MaxRouteAttempts)))
        {
            var passage = await cache.GetPassageAsync(candidate.PassageId);
            if (passage is null) continue;
            try
            {
                var credential = await provider.CreateAsync(new CreateContext
                {
                    PassageId = candidate.PassageId,
                    Config = passage.Config,
                    OrderNo = order.OrderNo,
                    Method = order.PayMethod,
                    Amount = order.Amount,
                    Currency = order.Currency,
                    Subject = order.Subject,
                    Body = order.Body,
                    ClientIp = order.ClientIp,
                    OpenId = req.OpenId,
                    UserId = order.UserId,
                    ProductId = order.ProductId,
                    Attach = order.Attach,
                    ReturnUrl = order.ReturnUrl,
                    NotifyUrl = UpstreamNotifyUrl(provider.Meta.Code, candidate.PassageId),
                    ExpireAt = order.ExpireAt,
                    Subscribe = order.OrderType == OrderType.SubscriptionInitial,
                    Extra = req.Extra ?? [],
                }, ct);

                order.PassageId = candidate.PassageId;
                order.BindingId = candidate.BindingId;
                order.Credential = JsonSerializer.Serialize(credential, JsonDefaults.Web);
                order.NextCheckAt = NextCheck(order, provider, 0);
                await db.ExecuteAsync(
                    "UPDATE pay_order SET passage_id=@PassageId, binding_id=@BindingId, credential=@Credential, next_check_at=@NextCheckAt, updated_at=UTC_TIMESTAMP(3) WHERE id=@Id AND status=0",
                    order);
                await redis.RecordSuccessAsync(candidate.BindingId);
                return ToResult(order, candidate.PassageName);
            }
            catch (ProviderException ex) when (ex.RequestFault)
            {
                await FailAsync(order, ex.Code, ex.Message);
                throw new PayException(ErrorCodes.InvalidParam, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                await redis.RecordFailureAsync(candidate.BindingId);
                logger.LogWarning("订单 {OrderNo} 通道 {Passage}({PassageId}) 下单失败，尝试下一个通道：{Message}",
                    order.OrderNo, candidate.PassageName, candidate.PassageId, ex.Message);
            }
        }

        var message = last?.Message ?? "没有可用通道";
        await FailAsync(order, (last as ProviderException)?.Code ?? ErrorCodes.PassageError, message);
        throw new PayException(ErrorCodes.PassageError, "通道下单失败：" + message);
    }

    private Task FailAsync(PayOrder order, string code, string message) =>
        db.ExecuteAsync(
            "UPDATE pay_order SET status=3, error_code=@code, error_msg=@message, next_check_at=NULL, updated_at=UTC_TIMESTAMP(3) WHERE id=@Id AND status=0",
            new { order.Id, code, message = message.Length > 500 ? message[..500] : message });

    private async Task<OrderResult> ReuseAsync(PayOrder existing, CreateOrderRequest req, PayChannel channel)
    {
        switch (existing.Status)
        {
            case OrderStatus.Paid:
                throw new PayException(ErrorCodes.OrderPaid, "该业务订单已支付");
            case OrderStatus.Closed:
                throw new PayException(ErrorCodes.OrderExists, "该业务订单号已关闭，请更换 mchOrderNo 重新下单");
        }
        var same = existing.Amount == req.Amount && existing.PayMethod == req.PayMethod && existing.ChannelId == channel.Id
                   && (existing.OrderType == OrderType.SubscriptionInitial) == req.Subscribe;
        if (!same || existing.PassageId == 0 || string.IsNullOrEmpty(existing.Credential) || existing.ExpireAt <= DateTime.UtcNow)
            throw new PayException(ErrorCodes.OrderExists, "业务订单号已存在（参数不一致或已过期）；每次支付尝试请使用新的 mchOrderNo");
        var passage = await cache.GetPassageAsync(existing.PassageId);
        return ToResult(existing, passage?.Passage.Name ?? "");
    }

    private static PayException NoPassage(RouteResult route, IPayProvider provider, CreateOrderRequest req)
    {
        var reasons = route.Evaluations
            .Where(e => e.Candidate.Provider.Equals(provider.Meta.Code, StringComparison.OrdinalIgnoreCase))
            .Select(e => $"{e.Candidate.PassageName}:{e.Reason}")
            .ToList();
        var detail = reasons.Count == 0 ? $"渠道 {req.Channel} 未配置{provider.Meta.Name}通道" : string.Join("；", reasons);
        return new PayException(ErrorCodes.NoAvailablePassage, "没有可用的支付通道（" + detail + "）");
    }

    private static void Validate(CreateOrderRequest req)
    {
        req.Channel = req.Channel.Trim();
        req.PayMethod = req.PayMethod.Trim();
        req.MchOrderNo = req.MchOrderNo.Trim();
        req.Subject = req.Subject.Trim();
        if (req.Channel.Length == 0) throw Invalid("channel 不能为空");
        if (req.PayMethod.Length == 0) throw Invalid("payMethod 不能为空");
        if (req.MchOrderNo.Length is 0 or > 64) throw Invalid("mchOrderNo 长度需在 1~64 之间");
        if (req.Amount is <= 0 or > 100_000_000_00) throw Invalid("amount 必须为正整数（分）");
        if (req.Subject.Length is 0 or > 128) throw Invalid("subject 长度需在 1~128 之间");
        if (req.Body.Length > 255 || req.Attach.Length > 512 || req.NotifyUrl.Length > 512 || req.ReturnUrl.Length > 512)
            throw Invalid("body / attach / notifyUrl / returnUrl 超长");
        if (req.NotifyUrl.Length > 0 && !IsHttpUrl(req.NotifyUrl)) throw Invalid("notifyUrl 必须是 http(s) 地址");
        if (string.IsNullOrWhiteSpace(req.Currency)) req.Currency = "CNY";

        static PayException Invalid(string m) => new(ErrorCodes.InvalidParam, m);
    }

    public static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    public static OrderResult ToResult(PayOrder o, string passageName) => new()
    {
        OrderNo = o.OrderNo,
        MchOrderNo = o.MchOrderNo,
        Status = (int)o.Status,
        Amount = o.Amount,
        Provider = o.Provider,
        PayMethod = o.PayMethod,
        OrderType = (int)o.OrderType,
        PassageId = o.PassageId,
        PassageName = passageName,
        ExpireAt = o.ExpireAt,
        Credential = string.IsNullOrEmpty(o.Credential) ? null : JsonSerializer.Deserialize<JsonElement>(o.Credential),
    };

    private DateTime NextCheck(PayOrder order, IPayProvider provider, int checkCount)
    {
        if (!provider.Meta.Capabilities.Query) return order.ExpireAt;
        var intervals = _options.Order.CheckIntervals;
        var seconds = intervals.Length == 0 ? 60 : intervals[Math.Min(checkCount, intervals.Length - 1)];
        var next = DateTime.UtcNow.AddSeconds(seconds);
        return next < order.ExpireAt ? next : order.ExpireAt;
    }

    // ------------------------------------------------------------------ 支付成功

    /// <summary>
    /// 支付成功入口（上游回调 / 主动查单 / 凭证核验共用）。
    /// 幂等：条件更新 status&lt;&gt;1 只会成功一次；(provider, upstream_trade_no) 唯一键防止同一上游交易被用于多笔订单。
    /// 订单状态变更与业务通知任务在同一事务内写入。
    /// </summary>
    public async Task<PaidOutcome> HandlePaidAsync(string orderNoValue, PaidInfo info, string source)
    {
        var order = await FindAsync(orderNoValue);
        if (order is null) return PaidOutcome.OrderNotFound;
        return await HandlePaidAsync(order, info, source);
    }

    public async Task<PaidOutcome> HandlePaidAsync(PayOrder order, PaidInfo info, string source)
    {
        if (info.Amount is { } amount && amount != order.Amount)
        {
            logger.LogError("订单 {OrderNo} 金额不一致：订单 {Expected} 分，上游 {Actual} 分（来源 {Source}）", order.OrderNo, order.Amount, amount, source);
            return PaidOutcome.AmountMismatch;
        }
        if (order.Status == OrderStatus.Paid) return SameTrade(order, info) ? PaidOutcome.AlreadyPaid : PaidOutcome.Conflict;

        var now = DateTime.UtcNow;
        var tradeNo = string.IsNullOrWhiteSpace(info.UpstreamTradeNo) ? null : info.UpstreamTradeNo;
        var url = await notify.ResolveUrlAsync(order.AppId, order.NotifyUrl);
        long taskId;
        try
        {
            taskId = await db.TransactionAsync(async (conn, tx) =>
            {
                var rows = await conn.ExecuteAsync(
                    """
                    UPDATE pay_order SET status=1, upstream_trade_no=@tradeNo, paid_at=@paidAt,
                      buyer_id=IF(@buyerId='', buyer_id, @buyerId), next_check_at=NULL, error_code='', error_msg='', updated_at=@now
                    WHERE id=@Id AND status<>1
                    """,
                    new { order.Id, tradeNo, paidAt = info.PaidAt ?? now, buyerId = info.BuyerId, now }, tx);
                if (rows == 0) return -1L;

                var wasClosed = order.Status != OrderStatus.Pending;
                order.Status = OrderStatus.Paid;
                order.UpstreamTradeNo = tradeNo;
                order.PaidAt = info.PaidAt ?? now;
                if (wasClosed) logger.LogWarning("订单 {OrderNo} 在关闭/失败后收到支付成功，已按成功处理", order.OrderNo);

                // 订阅首购：以上游为准——只要这笔扣款属于自动续费订阅，就在同一事务内建立（或续上）订阅
                PaySubscription? sub = null;
                if (info.Subscription is { } s) sub = await AttachSubscriptionAsync(conn, tx, order, s, now);
                return await notify.InsertTaskAsync(conn, tx, order.AppId, order.AppCode, NotifyType.Pay, order.OrderNo, url, NotifyService.BuildPayPayload(order, sub));
            });
        }
        catch (MySqlException ex) when (Db.IsDuplicateKey(ex))
        {
            logger.LogError("上游交易号 {TradeNo} 已被其他订单使用，拒绝为订单 {OrderNo} 入账（来源 {Source}）", tradeNo, order.OrderNo, source);
            return PaidOutcome.TradeReused;
        }

        if (taskId < 0)
        {
            var latest = await FindAsync(order.OrderNo);
            return latest is not null && SameTrade(latest, info) ? PaidOutcome.AlreadyPaid : PaidOutcome.Conflict;
        }

        logger.LogInformation("订单 {OrderNo} 支付成功，金额 {Amount} 分，通道 {PassageId}，来源 {Source}", order.OrderNo, order.Amount, order.PassageId, source);
        await redis.AddQuotaAsync(order.BindingId, order.Amount, router.BusinessDay);
        await notify.EnqueueAsync(taskId);
        return PaidOutcome.Success;
    }

    /// <summary>
    /// 为订阅首购订单建立订阅。若上游订阅标识已存在（例如过期后重新订阅仍沿用同一标识），则把本单挂到原订阅上并顺延周期。
    /// </summary>
    private async Task<PaySubscription> AttachSubscriptionAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, PayOrder order, SubscriptionInfo s, DateTime now)
    {
        var sub = new PaySubscription
        {
            SubNo = orderNo.Next('S'),
            AppId = order.AppId,
            AppCode = order.AppCode,
            ChannelId = order.ChannelId,
            ChannelCode = order.ChannelCode,
            PassageId = order.PassageId,
            Provider = order.Provider,
            ProductId = order.ProductId,
            UserId = order.UserId,
            UpstreamSubId = s.UpstreamSubId,
            UpstreamToken = s.Token,
            Status = SubscriptionStatus.Active,
            AutoRenew = s.AutoRenew,
            Amount = order.Amount,
            PeriodStart = s.PeriodStart ?? order.PaidAt ?? now,
            ExpiresAt = s.ExpiresAt,
            FirstOrderNo = order.OrderNo,
            LastOrderNo = order.OrderNo,
            NotifyUrl = order.NotifyUrl,
            Attach = order.Attach,
            NextCheckAt = SubscriptionService.NextCheck(s.ExpiresAt, now),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var existing = await conn.QueryFirstOrDefaultAsync<PaySubscription>(
            "SELECT * FROM pay_subscription WHERE provider = @Provider AND upstream_sub_id = @UpstreamSubId FOR UPDATE", sub, tx);
        if (existing is null)
        {
            sub.Id = await Db.InsertAsync(conn, tx, sub);
        }
        else
        {
            await conn.ExecuteAsync(
                """
                UPDATE pay_subscription SET status = 1, auto_renew = @AutoRenew, amount = @Amount, period_start = @PeriodStart,
                  expires_at = COALESCE(@ExpiresAt, expires_at), cancel_at = NULL, renew_count = renew_count + 1, last_order_no = @LastOrderNo,
                  upstream_token = IF(@UpstreamToken = '', upstream_token, @UpstreamToken), next_check_at = @NextCheckAt, check_count = 0, updated_at = @UpdatedAt
                WHERE id = @Id
                """,
                new { existing.Id, sub.AutoRenew, sub.Amount, sub.PeriodStart, sub.ExpiresAt, sub.LastOrderNo, sub.UpstreamToken, sub.NextCheckAt, sub.UpdatedAt }, tx);
            existing.Status = SubscriptionStatus.Active;
            existing.AutoRenew = sub.AutoRenew;
            existing.ExpiresAt = sub.ExpiresAt ?? existing.ExpiresAt;
            existing.PeriodStart = sub.PeriodStart;
            existing.LastOrderNo = order.OrderNo;
            existing.RenewCount++;
            sub = existing;
        }

        order.OrderType = OrderType.SubscriptionInitial;
        order.SubNo = sub.SubNo;
        await conn.ExecuteAsync("UPDATE pay_order SET order_type = 1, sub_no = @SubNo WHERE id = @Id", new { sub.SubNo, order.Id }, tx);
        return sub;
    }

    /// <summary>
    /// 记录一次订阅续费：续费单由上游扣款事件生成，生来即为已支付（不经过下单与路由）。
    /// (provider, upstream_trade_no) 唯一键保证同一笔扣款的重复通知只入账一次；
    /// 续费单、订阅周期顺延、业务通知任务在同一事务内写入。
    /// </summary>
    public async Task<RenewalOutcome> RecordRenewalAsync(PaySubscription sub, RenewalCharge charge, string source)
    {
        if (string.IsNullOrWhiteSpace(charge.TradeNo)) return RenewalOutcome.Invalid;
        if (await db.ScalarAsync<long?>(
                "SELECT id FROM pay_order WHERE provider = @Provider AND upstream_trade_no = @TradeNo", new { sub.Provider, charge.TradeNo }) is not null)
            return RenewalOutcome.Duplicate;

        var now = DateTime.UtcNow;
        var first = await FindAsync(sub.FirstOrderNo);
        var mchOrderNo = "RENEW-" + charge.TradeNo;
        var order = new PayOrder
        {
            OrderNo = orderNo.Next('P'),
            AppId = sub.AppId,
            AppCode = sub.AppCode,
            MchOrderNo = mchOrderNo.Length > 64 ? mchOrderNo[..64] : mchOrderNo,
            ChannelId = sub.ChannelId,
            ChannelCode = sub.ChannelCode,
            PassageId = sub.PassageId,
            BindingId = first?.BindingId ?? 0,
            Provider = sub.Provider,
            PayMethod = first?.PayMethod ?? "",
            OrderType = OrderType.SubscriptionRenewal,
            SubNo = sub.SubNo,
            Subject = first?.Subject ?? sub.ProductId,
            Body = "订阅续费",
            Amount = charge.Amount ?? sub.Amount,
            Currency = first?.Currency ?? "CNY",
            Status = OrderStatus.Paid,
            UpstreamTradeNo = charge.TradeNo,
            UserId = sub.UserId,
            BuyerId = charge.BuyerId,
            ProductId = sub.ProductId,
            NotifyUrl = sub.NotifyUrl,
            Attach = sub.Attach,
            ExpireAt = now,
            PaidAt = charge.PaidAt ?? now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var url = await notify.ResolveUrlAsync(sub.AppId, sub.NotifyUrl);

        long taskId;
        try
        {
            taskId = await db.TransactionAsync(async (conn, tx) =>
            {
                order.Id = await Db.InsertAsync(conn, tx, order);
                var periodStart = charge.PeriodStart ?? order.PaidAt;
                await conn.ExecuteAsync(
                    """
                    UPDATE pay_subscription SET status = 1, auto_renew = 1, cancel_at = NULL, amount = @Amount, period_start = @periodStart,
                      expires_at = COALESCE(@ExpiresAt, expires_at), renew_count = renew_count + 1, last_order_no = @OrderNo,
                      upstream_token = IF(@Token = '', upstream_token, @Token), next_check_at = @next, check_count = 0, updated_at = @now
                    WHERE id = @Id
                    """,
                    new { sub.Id, order.Amount, periodStart, charge.ExpiresAt, order.OrderNo, charge.Token, next = SubscriptionService.NextCheck(charge.ExpiresAt ?? sub.ExpiresAt, now), now }, tx);
                sub.Status = SubscriptionStatus.Active;
                sub.AutoRenew = true;
                sub.CancelAt = null;
                sub.PeriodStart = periodStart;
                sub.ExpiresAt = charge.ExpiresAt ?? sub.ExpiresAt;
                sub.RenewCount++;
                sub.LastOrderNo = order.OrderNo;
                return await notify.InsertTaskAsync(conn, tx, order.AppId, order.AppCode, NotifyType.Pay, order.OrderNo, url, NotifyService.BuildPayPayload(order, sub));
            });
        }
        catch (MySqlException ex) when (Db.IsDuplicateKey(ex))
        {
            return RenewalOutcome.Duplicate;
        }

        logger.LogInformation("订阅 {SubNo} 续费成功：续费单 {OrderNo}，金额 {Amount} 分，到期 {ExpiresAt:u}，来源 {Source}", sub.SubNo, order.OrderNo, order.Amount, sub.ExpiresAt, source);
        await notify.EnqueueAsync(taskId);
        return RenewalOutcome.Renewed;
    }

    private static bool SameTrade(PayOrder order, PaidInfo info) =>
        string.IsNullOrWhiteSpace(info.UpstreamTradeNo) || string.IsNullOrEmpty(order.UpstreamTradeNo) || order.UpstreamTradeNo == info.UpstreamTradeNo;

    // ------------------------------------------------------------------ 凭证核验（应用商店内购）

    public async Task<PayOrder> VerifyReceiptAsync(PayOrder order, IReadOnlyDictionary<string, string> receipt, CancellationToken ct)
    {
        if (order.Status == OrderStatus.Paid) return order;
        var provider = providers.Get(order.Provider);
        if (!provider.Meta.Capabilities.ReceiptVerify)
            throw new PayException(ErrorCodes.NotSupported, $"{provider.Meta.Name}不需要凭证核验，支付结果以服务端回调为准");
        var passage = await cache.GetPassageAsync(order.PassageId) ?? throw new PayException(ErrorCodes.PassageError, "订单通道不存在");

        QueryResult q;
        try
        {
            q = await provider.VerifyReceiptAsync(new ReceiptContext
            {
                PassageId = order.PassageId,
                Config = passage.Config,
                OrderNo = order.OrderNo,
                ProductId = order.ProductId,
                Subscribe = order.OrderType == OrderType.SubscriptionInitial,
                Receipt = receipt,
            }, ct);
        }
        catch (ProviderException ex)
        {
            throw new PayException(ErrorCodes.ReceiptInvalid, ex.Message);
        }

        if (q.State != UpstreamState.Paid) throw new PayException(ErrorCodes.ReceiptInvalid, $"凭证未处于已支付状态：{q.Message}");
        if (q.BoundOrderNo is not null && q.BoundOrderNo != order.OrderNo)
            throw new PayException(ErrorCodes.ReceiptInvalid, "凭证不属于该订单");
        if (order.ProductId.Length > 0 && !string.IsNullOrEmpty(q.ProductId) && q.ProductId != order.ProductId)
            throw new PayException(ErrorCodes.ReceiptInvalid, "凭证中的商品与订单不一致");
        if (string.IsNullOrEmpty(q.UpstreamTradeNo))
            throw new PayException(ErrorCodes.ReceiptInvalid, "凭证缺少上游交易号");

        var outcome = await HandlePaidAsync(order, new PaidInfo(q.UpstreamTradeNo, q.Amount, q.PaidAt, q.BuyerId, q.Subscription), "receipt");
        return outcome switch
        {
            PaidOutcome.Success or PaidOutcome.AlreadyPaid => (await FindAsync(order.OrderNo))!,
            PaidOutcome.TradeReused => throw new PayException(ErrorCodes.ReceiptInvalid, "该凭证已被其他订单使用"),
            PaidOutcome.AmountMismatch => throw new PayException(ErrorCodes.ReceiptInvalid, "凭证金额与订单金额不一致"),
            _ => throw new PayException(ErrorCodes.ReceiptInvalid, "凭证与订单已有支付记录冲突"),
        };
    }

    // ------------------------------------------------------------------ 查单 / 关单 / 巡检

    private async Task<QueryResult?> QueryUpstreamAsync(PayOrder order, CancellationToken ct)
    {
        if (order.PassageId == 0) return null;
        var provider = providers.Find(order.Provider);
        if (provider is null || !provider.Meta.Capabilities.Query) return null;
        var passage = await cache.GetPassageAsync(order.PassageId);
        if (passage is null) return null;
        return await provider.QueryAsync(ToContext(order, passage), ct);
    }

    private static OrderContext ToContext(PayOrder order, PassageEntry passage) => new()
    {
        PassageId = order.PassageId,
        Config = passage.Config,
        OrderNo = order.OrderNo,
        Method = order.PayMethod,
        Amount = order.Amount,
        UpstreamTradeNo = order.UpstreamTradeNo,
        UserId = order.UserId,
        BuyerId = order.BuyerId,
        ProductId = order.ProductId,
    };

    /// <summary>主动查询上游并同步状态（补单）。</summary>
    public async Task<SyncResult> SyncAsync(PayOrder order, CancellationToken ct)
    {
        if (order.Status == OrderStatus.Paid) return new SyncResult((int)order.Status, false, "订单已是支付成功");
        QueryResult? q;
        try
        {
            q = await QueryUpstreamAsync(order, ct);
        }
        catch (ProviderException ex)
        {
            return new SyncResult((int)order.Status, false, "查询上游失败：" + ex.Message);
        }
        if (q is null) return new SyncResult((int)order.Status, false, "该通道不支持主动查单");
        if (q.State != UpstreamState.Paid) return new SyncResult((int)order.Status, false, $"上游状态：{Describe(q)}");

        var outcome = await HandlePaidAsync(order, new PaidInfo(q.UpstreamTradeNo, q.Amount, q.PaidAt, q.BuyerId), "sync");
        return outcome switch
        {
            PaidOutcome.Success => new SyncResult((int)OrderStatus.Paid, true, "上游已支付，已补单成功"),
            PaidOutcome.AlreadyPaid => new SyncResult((int)OrderStatus.Paid, false, "订单已是支付成功"),
            PaidOutcome.AmountMismatch => new SyncResult((int)order.Status, false, "上游金额与订单不一致，未入账"),
            _ => new SyncResult((int)order.Status, false, "入账失败：" + outcome),
        };
    }

    private static string Describe(QueryResult q) =>
        (q.State switch
        {
            UpstreamState.NotPaid => "未支付",
            UpstreamState.Closed => "已关闭",
            UpstreamState.Failed => "支付失败",
            UpstreamState.Refunded => "已退款",
            _ => "未知",
        }) + (q.Message.Length > 0 ? $"（{q.Message}）" : "");

    public async Task CloseAsync(PayOrder order, string reason, CancellationToken ct)
    {
        if (order.Status == OrderStatus.Closed) return;
        if (order.Status != OrderStatus.Pending) throw new PayException(ErrorCodes.OrderStateError, "仅待支付订单可关闭");

        var provider = providers.Find(order.Provider);
        var passage = order.PassageId > 0 ? await cache.GetPassageAsync(order.PassageId) : null;
        if (provider is not null && passage is not null)
        {
            try
            {
                // 关单前先查一次，避免把用户刚付完的订单关掉
                if (provider.Meta.Capabilities.Query)
                {
                    var q = await provider.QueryAsync(ToContext(order, passage), ct);
                    if (q.State == UpstreamState.Paid)
                    {
                        var outcome = await HandlePaidAsync(order, new PaidInfo(q.UpstreamTradeNo, q.Amount, q.PaidAt, q.BuyerId), "close-check");
                        if (outcome is PaidOutcome.Success or PaidOutcome.AlreadyPaid)
                            throw new PayException(ErrorCodes.OrderPaid, "订单已支付，无法关闭");
                    }
                }
                if (provider.Meta.Capabilities.Close) await provider.CloseAsync(ToContext(order, passage), ct);
            }
            catch (ProviderException ex)
            {
                // 上游关单失败不阻塞本地关闭：若之后仍收到支付成功回调，订单会被重新置为成功
                logger.LogWarning("订单 {OrderNo} 上游关单失败：{Message}", order.OrderNo, ex.Message);
            }
        }

        await db.ExecuteAsync(
            "UPDATE pay_order SET status=2, closed_at=UTC_TIMESTAMP(3), next_check_at=NULL, error_msg=@reason, updated_at=UTC_TIMESTAMP(3) WHERE id=@Id AND status=0",
            new { order.Id, reason });
    }

    /// <summary>巡检派发：领取到期的待支付订单并续租，返回订单号列表。</summary>
    public Task<List<string>> ClaimDueChecksAsync(int batchSize) =>
        db.TransactionAsync(async (conn, tx) =>
        {
            var due = (await conn.QueryAsync<string>(
                "SELECT order_no FROM pay_order WHERE status = 0 AND next_check_at <= UTC_TIMESTAMP(3) ORDER BY next_check_at LIMIT @batchSize FOR UPDATE SKIP LOCKED",
                new { batchSize }, tx)).AsList();
            if (due.Count > 0)
                await conn.ExecuteAsync(
                    "UPDATE pay_order SET next_check_at = DATE_ADD(UTC_TIMESTAMP(3), INTERVAL @lease SECOND) WHERE order_no IN @due",
                    new { lease = (int)CheckLease.TotalSeconds, due }, tx);
            return due;
        });

    /// <summary>巡检消费：掉单补偿（主动查单）与超时关单。</summary>
    public async Task CheckAsync(string orderNoValue, CancellationToken ct)
    {
        var order = await FindAsync(orderNoValue);
        if (order is null || order.Status != OrderStatus.Pending) return;
        var provider = providers.Find(order.Provider);

        try
        {
            var q = await QueryUpstreamAsync(order, ct);
            if (q?.State == UpstreamState.Paid)
            {
                var outcome = await HandlePaidAsync(order, new PaidInfo(q.UpstreamTradeNo, q.Amount, q.PaidAt, q.BuyerId), "patrol");
                if (outcome is PaidOutcome.Success or PaidOutcome.AlreadyPaid) return;
            }
        }
        catch (ProviderException ex)
        {
            logger.LogWarning("订单 {OrderNo} 巡检查单失败：{Message}", order.OrderNo, ex.Message);
        }

        if (DateTime.UtcNow >= order.ExpireAt)
        {
            try
            {
                await CloseAsync(order, "超时未支付，系统关闭", ct);
            }
            catch (PayException ex) when (ex.Code == ErrorCodes.OrderPaid)
            {
            }
            return;
        }

        var next = provider is null ? order.ExpireAt : NextCheck(order, provider, order.CheckCount + 1);
        await db.ExecuteAsync(
            "UPDATE pay_order SET next_check_at=@next, check_count=check_count+1, updated_at=UTC_TIMESTAMP(3) WHERE id=@Id AND status=0",
            new { order.Id, next });
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
