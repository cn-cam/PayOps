using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Stores;

/// <summary>小米联运 SDK 支付：无服务端预下单，支付结果以小米服务端 GET 回调为准（HMAC-SHA1）。</summary>
public sealed class XiaomiProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    private const string QueryUrl = "https://mis.migc.xiaomi.com/api/biz/service/queryOrder.do";
    private const string SubscribeUrl = "https://mis.migc.xiaomi.com/api/biz/service/querySubscribe.do";
    private const string UnsubscribeUrl = "https://mis.migc.xiaomi.com/api/biz/service/unSubscribe.do";

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "xiaomi",
        Name = "小米应用商店支付",
        Category = "store",
        Methods = [new("xiaomi_sdk", "小米SDK支付")],
        Capabilities = new() { Query = true, Subscription = true, SubscriptionCancel = true, SubscriptionChange = true },
        ConfigFields =
        [
            new() { Key = "appId", Label = "AppID", Required = true },
            new() { Key = "appSecret", Label = "AppSecret", Type = "password", Required = true, Secret = true },
        ],
        NotifyNote = "小米的回调地址不随订单传递，必须把本通道的回调地址配置到小米开放平台（HTTPS）。主动查单需要下单时通过 openId 传入小米 uid。",
    };

    public override Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct)
    {
        if (ctx.Subscribe && ctx.ProductId.Length == 0) throw BadRequest("小米订阅下单必须传 productId（计费代码）");
        return Task.FromResult(new PayCredential
        {
            Type = CredentialTypes.SdkParams,
            Data =
            {
                ["cpOrderId"] = ctx.OrderNo,
                ["cpUserInfo"] = ctx.OrderNo,
                ["amount"] = ctx.Amount,
                ["productCode"] = ctx.ProductId,
                ["productName"] = ctx.Subject,
                // 订阅首购：客户端改用 miSubscribe（MiBuyInfo: cpOrderId / productCode / quantity=1）
                ["subscription"] = ctx.Subscribe,
                ["clientApi"] = ctx.Subscribe ? "miSubscribe" : "miUniPay",
            },
        });
    }

    /// <summary>小米订阅标识：用户 + 商品（同一订阅群组内一个用户只能持有一个商品）。</summary>
    public static string SubscriptionKey(string uid, string productCode) => $"{uid}:{productCode}";

    /// <summary>查询订阅状态（线上口径）。</summary>
    public override async Task<SubscriptionState> QuerySubscriptionAsync(SubscriptionContext ctx, CancellationToken ct)
    {
        var (uid, productCode) = SplitKey(ctx.UpstreamSubId);
        var p = new Dictionary<string, string> { ["appId"] = ctx.Config.Require("appId"), ["uid"] = uid, ["productCode"] = productCode };
        var url = $"{SubscribeUrl}?{string.Join('&', p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"))}&signature={Sign(p, ctx.Config.Require("appSecret"))}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var (_, text) = await SendAsync(req, ct);
        var json = ParseJson(text, Meta.Name);
        var status = json.Str("subStatus");
        var active = status is "SUBSCRIBE_PERIOD" or "SUBSCRIBE_PERIOD_GRACE";
        return new SubscriptionState
        {
            Known = true,
            Active = active,
            Revoked = status is "SUBSCRIBE_TERMINATE" or "SUBSCRIBE_ONLY_CONTRACT_TERMINATE",
            AutoRenew = active,
            ProductId = productCode,
            Message = status,
        };
    }

    /// <summary>小米解约：异步生效，最终状态以签约回调为准。</summary>
    public override async Task CancelSubscriptionAsync(SubscriptionContext ctx, CancellationToken ct)
    {
        var (uid, productCode) = SplitKey(ctx.UpstreamSubId);
        var p = new Dictionary<string, string> { ["appId"] = ctx.Config.Require("appId"), ["uid"] = uid, ["productCode"] = productCode };
        var url = $"{UnsubscribeUrl}?{string.Join('&', p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"))}&signature={Sign(p, ctx.Config.Require("appSecret"))}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        var (_, text) = await SendAsync(req, ct);
        var json = ParseJson(text, Meta.Name);
        if (json.Long("errcode") != 200) throw Upstream("XIAOMI_" + json.Str("errcode"), $"小米解约失败：{json.Str("errMsg")}");
    }

    private static (string Uid, string ProductCode) SplitKey(string key)
    {
        var idx = key.IndexOf(':');
        return idx <= 0 ? (key, "") : (key[..idx], key[(idx + 1)..]);
    }

    public override async Task<QueryResult> QueryAsync(OrderContext ctx, CancellationToken ct)
    {
        if (ctx.BuyerId.Length == 0) return new QueryResult { State = UpstreamState.Unknown, Message = "缺少小米 uid，无法主动查单" };
        var p = new Dictionary<string, string>
        {
            ["appId"] = ctx.Config.Require("appId"),
            ["cpOrderId"] = ctx.OrderNo,
            ["uid"] = ctx.BuyerId,
        };
        var url = $"{QueryUrl}?{string.Join('&', p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"))}&signature={Sign(p, ctx.Config.Require("appSecret"))}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var (_, text) = await SendAsync(req, ct);
        var json = ParseJson(text, Meta.Name);
        var paid = json.Str("orderStatus") == "TRADE_SUCCESS" && json.Str("cpOrderId") == ctx.OrderNo;
        return new QueryResult
        {
            State = paid ? UpstreamState.Paid : UpstreamState.NotPaid,
            UpstreamTradeNo = json.Str("orderId") is { Length: > 0 } id ? id : null,
            Amount = json.Long("payFee"),
            PaidAt = TimeUtil.ParseBeijing(json.Str("payTime"), "yyyy-MM-dd HH:mm:ss"),
            BuyerId = json.Str("uid"),
            Message = json.Str("orderStatus") + json.Str("errMsg"),
        };
    }

    public override Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var q = ctx.Query.Count > 0 ? ctx.Query : ctx.Form;
        string V(string k) => q.TryGetValue(k, out var v) ? v : "";
        var expected = Sign(q, ctx.Config.Require("appSecret"));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(V("signature").ToLowerInvariant())))
            return Task.FromResult(NotifyParseResult.Fail("验签失败"));
        if (V("appId") != ctx.Config.Require("appId")) return Task.FromResult(NotifyParseResult.Fail("appId 不匹配"));

        // 签约/解约回调（与支付回调共用同一地址，靠 subStatus 区分）
        if (V("subStatus").Length > 0)
        {
            var subStatus = V("subStatus");
            var info = new SubscriptionInfo { UpstreamSubId = SubscriptionKey(V("uid"), V("productCode")), ProductId = V("productCode") };
            return subStatus switch
            {
                "SUBSCRIBE_PERIOD" => Task.FromResult(NotifyParseResult.Of(new NotifyEvent
                {
                    Type = NotifyEventType.Subscription, SubKind = SubscriptionEventKind.AutoRenewOn, Subscription = info,
                })),
                "SUBSCRIBE_TERMINATE" or "SUBSCRIBE_ONLY_CONTRACT_TERMINATE" => Task.FromResult(NotifyParseResult.Of(new NotifyEvent
                {
                    Type = NotifyEventType.Subscription, SubKind = SubscriptionEventKind.AutoRenewOff, Subscription = info,
                })),
                _ => Task.FromResult(NotifyParseResult.Ignore($"忽略签约状态 {subStatus}")),
            };
        }

        if (V("orderStatus") != "TRADE_SUCCESS") return Task.FromResult(NotifyParseResult.Ignore($"订单状态 {V("orderStatus")}"));

        var amount = long.TryParse(V("payFee"), out var fee) ? fee + (long.TryParse(V("partnerGiftConsume"), out var gift) ? gift : 0) : (long?)null;
        var paidAt = TimeUtil.ParseBeijing(V("payTime"), "yyyy-MM-dd HH:mm:ss");
        var deductType = V("deductType");
        var productCode = V("productCode");

        // 服务端代扣（deductType=1）：续费扣款，不带 cpOrderId，按"用户 + 商品"定位订阅。
        // 用户主动支付（deductType=0）：可能是普通道具，也可能是订阅首购——把订阅信息一并带上，
        // 由服务层根据订单类型（仅订阅首购订单）决定是否建订阅，避免把普通支付误判为订阅。
        var subscription = productCode.Length > 0
            ? new SubscriptionInfo
            {
                UpstreamSubId = SubscriptionKey(V("uid"), productCode),
                ProductId = productCode,
                PeriodStart = paidAt,
                Amount = amount,
            }
            : null;

        if (deductType == "1")
        {
            return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
            {
                Type = NotifyEventType.Subscription,
                SubKind = SubscriptionEventKind.Charged,
                OrderNo = "",
                UpstreamTradeNo = V("orderId"),
                Amount = amount,
                PaidAt = paidAt,
                BuyerId = V("uid"),
                ProductId = productCode,
                Subscription = subscription,
            }));
        }

        return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
        {
            Type = NotifyEventType.Paid,
            OrderNo = V("cpOrderId"),
            UpstreamTradeNo = V("orderId"),
            Amount = amount,
            PaidAt = paidAt,
            BuyerId = V("uid"),
            ProductId = productCode,
            Subscription = subscription,
        }));
    }
    /// <summary>
    /// 订阅首购的购物车判断：小米签约回调不带 cpOrderId，只能靠支付回调里的商品代码识别。
    /// 这里用一个进程内集合登记本副本下单过的订阅商品，避免把普通道具支付误判为订阅扣款。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> subs = new();

    public static void RememberSubscriptionProduct(string productCode) => subs[productCode] = true;

    /// <summary>非空参数（除 signature）按 key 升序拼 k=v&amp;…，以 AppSecret 做 HMAC-SHA1，输出小写十六进制。</summary>
    public static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string appSecret)
    {
        var content = string.Join('&', parameters
            .Where(kv => kv.Key != "signature" && !string.IsNullOrEmpty(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        return Convert.ToHexStringLower(HMACSHA1.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(content)));
    }

    public override NotifyReply BuildReply(NotifyOutcome outcome, string message)
    {
        var code = outcome switch
        {
            NotifyOutcome.Success => 200,
            NotifyOutcome.SignError => 1525,
            NotifyOutcome.OrderNotFound => 1506,
            NotifyOutcome.AmountMismatch => 3515,
            _ => 3515,
        };
        return NotifyReply.Json(JsonSerializer.Serialize(new { errcode = code, errMsg = outcome == NotifyOutcome.Success ? "success" : message }));
    }
}
