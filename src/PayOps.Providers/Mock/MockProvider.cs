using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Mock;

/// <summary>
/// 演示/联调用的模拟通道：不请求任何上游。下单返回一份可直接 POST 到回调地址的模拟回调，
/// 可配置"下单失败"以演示多通道故障转移与熔断；同时模拟自动续费订阅事件与对账单。生产环境请勿绑定到正式渠道。
/// </summary>
public sealed class MockProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    public const string ProviderCode = "mock";

    public const string EventCharged = "charged";
    public const string EventCancel = "cancel";
    public const string EventResume = "resume";
    public const string EventExpire = "expire";
    public const string EventRevoke = "revoke";
    public const string EventUpgrade = "upgrade";
    public const string EventDowngrade = "downgrade";
    public const string EventPause = "pause";
    public const string EventWake = "wake";
    public const string EventSign = "sign";
    public const string EventUnsign = "unsign";

    public override ProviderMeta Meta { get; } = new()
    {
        Code = ProviderCode,
        Name = "模拟支付",
        Category = "mock",
        Methods = [new("mock", "模拟支付")],
        Capabilities = new() { Close = true, Refund = true, Subscription = true, SubscriptionCancel = true, SubscriptionChange = true, SubscriptionPause = true, Bill = true, FundBill = true, Deduct = true },
        ConfigFields =
        [
            new() { Key = "secret", Label = "回调签名密钥", Type = "password", Required = true, Secret = true, DefaultValue = "mock-secret", Help = "模拟上游回调的 HMAC 密钥" },
            new() { Key = "failCreate", Label = "模拟下单失败", Type = "switch", DefaultValue = "false", Help = "开启后该通道下单必定失败，用于演示故障转移与熔断" },
            new() { Key = "refundAsync", Label = "退款异步到账", Type = "switch", DefaultValue = "false", Help = "开启后退款先返回处理中，由后台查单任务置为成功" },
            new() { Key = "subPeriodMinutes", Label = "订阅周期（分钟）", Type = "number", DefaultValue = "43200", Help = "模拟订阅每期的时长，默认 30 天" },
            new() { Key = "billMismatch", Label = "模拟对账差异", Type = "switch", DefaultValue = "false", Help = "开启后模拟账单会故意漏一笔、改一笔金额、多一笔，用于演示对账差异" },
            new() { Key = "billFeeBp", Label = "模拟手续费率（万分比）", Type = "number", DefaultValue = "60", Help = "模拟账单里每笔的手续费 = 金额 × 该费率；与通道上配置的手续费率不一致即可演示手续费核对差异" },
            new() { Key = "deductFail", Label = "模拟代扣失败", Type = "switch", DefaultValue = "false", Help = "开启后代扣必定失败，用于演示扣款失败退避" },
        ],
        NotifyNote = "模拟通道无真实上游。可在后台订单详情点击「模拟支付」、在订阅详情模拟续费/退订/到期，或把下单凭据中的 body 以 POST 发送到 notifyUrl。",
    };

    public override Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct)
    {
        if (ctx.Config.GetBool("failCreate"))
            throw Upstream("MOCK_FAIL", "模拟通道：下单失败（failCreate 已开启）");

        var secret = ctx.Config.Require("secret");
        string body;
        if (ctx.SignAgreement)
        {
            // 模拟签约：客户端"完成签约"后把这份 body 回放即可
            body = BuildSubscriptionCallback(secret, EventSign, "MOCKAGREE" + ctx.OrderNo, ctx.OrderNo, "", 0, 0, ctx.ProductId);
        }
        else if (ctx.Subscribe)
        {
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(PeriodMinutes(ctx.Config)).ToUnixTimeSeconds();
            body = BuildSubscriptionCallback(secret, EventCharged, "MOCKSUB" + ctx.OrderNo, ctx.OrderNo, "MOCK" + ctx.OrderNo, ctx.Amount, expiresAt);
        }
        else
        {
            body = BuildCallbackBody(secret, ctx.OrderNo, ctx.Amount);
        }
        return Task.FromResult(new PayCredential
        {
            Type = CredentialTypes.Mock,
            Data = { ["notifyUrl"] = ctx.NotifyUrl, ["method"] = "POST", ["body"] = body, ["subscription"] = ctx.Subscribe },
        });
    }

    public static int PeriodMinutes(PassageConfig config) => Math.Max(1, config.GetInt("subPeriodMinutes", 43200));

    public static string BuildCallbackBody(string secret, string orderNo, long amount) =>
        BuildSubscriptionCallback(secret, "", "", orderNo, "MOCK" + orderNo, amount, 0);

    /// <summary>构造一份带签名的模拟回调；eventName 为空表示一次性支付成功。productId 用于演示升级/降级。</summary>
    public static string BuildSubscriptionCallback(string secret, string eventName, string subId, string orderNo, string tradeNo, long amount, long expiresAt, string productId = "") =>
        JsonSerializer.Serialize(new
        {
            orderNo,
            tradeNo,
            amount,
            @event = eventName,
            subId,
            expiresAt,
            productId,
            sign = Sign(secret, orderNo, tradeNo, amount, eventName, subId, expiresAt, productId),
        });

    public static string Sign(string secret, string orderNo, string tradeNo, long amount, string eventName = "", string subId = "", long expiresAt = 0, string productId = "") =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{orderNo}|{tradeNo}|{amount}|{eventName}|{subId}|{expiresAt}|{productId}")));

    public override Task<RefundResult> RefundAsync(RefundContext ctx, CancellationToken ct) =>
        Task.FromResult(new RefundResult
        {
            State = ctx.Config.GetBool("refundAsync") ? UpstreamRefundState.Processing : UpstreamRefundState.Success,
            UpstreamRefundNo = "MOCK" + ctx.RefundNo,
        });

    public override Task<RefundResult> QueryRefundAsync(RefundContext ctx, CancellationToken ct) =>
        Task.FromResult(new RefundResult { State = UpstreamRefundState.Success, UpstreamRefundNo = "MOCK" + ctx.RefundNo });

    /// <summary>模拟"服务端退订"：直接成功，由服务层把订阅置为不再续订。</summary>
    public override Task CancelSubscriptionAsync(SubscriptionContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <summary>模拟签约代扣：直接扣款成功。</summary>
    public override Task<DeductResult> DeductAsync(DeductContext ctx, CancellationToken ct) =>
        ctx.Config.GetBool("deductFail")
            ? throw Upstream("MOCK_DEDUCT_FAIL", "模拟代扣失败（deductFail 已开启）")
            : Task.FromResult(new DeductResult
        {
            Paid = true,
            UpstreamTradeNo = "MOCKDEDUCT" + ctx.OrderNo,
            PaidAt = DateTime.UtcNow,
            Message = "模拟代扣成功",
        });

    /// <summary>模拟解约：直接成功。</summary>
    public override Task CancelAgreementAsync(SubscriptionContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <summary>模拟资金账单：收入 = 支付，支出 = 退款 + 手续费。</summary>
    public override Task<FundBillSummary> DownloadFundBillAsync(BillContext ctx, CancellationToken ct)
    {
        var bp = FeeBp(ctx.Config);
        long income = 0, refund = 0, fee = 0;
        foreach (var item in ctx.LocalItems)
        {
            var f = item.Amount * bp / 10_000;
            if (item.Kind == BillItemKind.Pay) { income += item.Amount; fee += f; }
            else { refund += item.Amount; fee -= f; }
        }
        return Task.FromResult(new FundBillSummary { Income = income, Expense = refund + fee, Refund = refund, Fee = fee, Message = "模拟资金账单" });
    }

    private static int FeeBp(PassageConfig config) => Math.Max(0, config.GetInt("billFeeBp", 60));

    /// <summary>模拟账单：以本地成功记录为蓝本；开启 billMismatch 时故意制造三类差异。</summary>
    public override Task<List<BillItem>> DownloadBillAsync(BillContext ctx, CancellationToken ct)
    {
        var bp = FeeBp(ctx.Config);
        var items = ctx.LocalItems.ToList();
        items = [.. items.Select(i => new BillItem
        {
            Kind = i.Kind, OrderNo = i.OrderNo, RefundNo = i.RefundNo, UpstreamTradeNo = i.UpstreamTradeNo, Amount = i.Amount,
            // 手续费：支付为正、退款为负，与微信账单一致
            Fee = i.Amount * bp / 10_000 * (i.Kind == BillItemKind.Pay ? 1 : -1),
            Time = i.Time,
        })];
        if (ctx.Config.GetBool("billMismatch"))
        {
            var pays = items.Where(i => i.Kind == BillItemKind.Pay).ToList();
            if (pays.Count > 0) items.Remove(pays[0]);
            if (pays.Count > 1)
            {
                var idx = items.IndexOf(pays[1]);
                items[idx] = new BillItem { Kind = BillItemKind.Pay, OrderNo = pays[1].OrderNo, UpstreamTradeNo = pays[1].UpstreamTradeNo, Amount = pays[1].Amount + 1, Fee = pays[1].Fee };
            }
            items.Add(new BillItem { Kind = BillItemKind.Pay, OrderNo = "PUNKNOWN" + ctx.BillDate.ToString("yyyyMMdd"), UpstreamTradeNo = "MOCKUNKNOWN" + ctx.BillDate.ToString("yyyyMMdd"), Amount = 100, Fee = 0 });
        }
        return Task.FromResult(items);
    }

    public override Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var json = ParseJson(ctx.Body, Meta.Name);
        var orderNo = json.Str("orderNo");
        var tradeNo = json.Str("tradeNo");
        var amount = json.Long("amount") ?? -1;
        var eventName = json.Str("event");
        var subId = json.Str("subId");
        var expiresAt = json.Long("expiresAt") ?? 0;
        var productId = json.Str("productId");
        var expected = Sign(ctx.Config.Require("secret"), orderNo, tradeNo, amount, eventName, subId, expiresAt, productId);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(json.Str("sign"))))
            return Task.FromResult(NotifyParseResult.Fail("验签失败"));

        if (eventName is EventSign or EventUnsign)
        {
            return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
            {
                Type = NotifyEventType.Agreement,
                OrderNo = orderNo,
                Agreement = new AgreementInfo
                {
                    AgreementId = subId,
                    SignedAt = DateTime.UtcNow,
                    Signed = eventName == EventSign,
                    Message = eventName == EventSign ? "模拟签约成功" : "模拟解约",
                },
            }));
        }

        if (eventName.Length == 0)
        {
            return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
            {
                Type = NotifyEventType.Paid,
                OrderNo = orderNo,
                UpstreamTradeNo = tradeNo,
                Amount = amount,
                PaidAt = DateTime.UtcNow,
                BuyerId = "mock-buyer",
            }));
        }

        var kind = eventName switch
        {
            EventCharged => SubscriptionEventKind.Charged,
            EventCancel => SubscriptionEventKind.AutoRenewOff,
            EventResume => SubscriptionEventKind.AutoRenewOn,
            EventExpire => SubscriptionEventKind.Expired,
            EventRevoke => SubscriptionEventKind.Revoked,
            EventUpgrade => SubscriptionEventKind.Upgraded,
            EventDowngrade => SubscriptionEventKind.Downgraded,
            EventPause => SubscriptionEventKind.Paused,
            EventWake => SubscriptionEventKind.Resumed,
            _ => (SubscriptionEventKind)0,
        };
        if (kind == 0 || subId.Length == 0) return Task.FromResult(NotifyParseResult.Ignore($"未知的模拟事件 {eventName}"));

        return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
        {
            Type = NotifyEventType.Subscription,
            SubKind = kind,
            OrderNo = orderNo,
            UpstreamTradeNo = tradeNo,
            Amount = kind == SubscriptionEventKind.Charged ? amount : null,
            PaidAt = DateTime.UtcNow,
            BuyerId = "mock-buyer",
            Subscription = new SubscriptionInfo
            {
                UpstreamSubId = subId,
                PeriodStart = DateTime.UtcNow,
                ExpiresAt = expiresAt > 0 ? TimeUtil.FromUnixSeconds(expiresAt) : null,
                AutoRenew = kind != SubscriptionEventKind.AutoRenewOff,
                ProductId = productId.Length > 0 ? productId : "",
                // 降级事件里 productId 就是下一期生效的商品
                NextProductId = kind == SubscriptionEventKind.Downgraded ? productId : "",
            },
        }));
    }
}
