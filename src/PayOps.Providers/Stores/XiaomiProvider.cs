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

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "xiaomi",
        Name = "小米应用商店支付",
        Category = "store",
        Methods = [new("xiaomi_sdk", "小米SDK支付")],
        Capabilities = new() { Query = true },
        ConfigFields =
        [
            new() { Key = "appId", Label = "AppID", Required = true },
            new() { Key = "appSecret", Label = "AppSecret", Type = "password", Required = true, Secret = true },
        ],
        NotifyNote = "小米的回调地址不随订单传递，必须把本通道的回调地址配置到小米开放平台（HTTPS）。主动查单需要下单时通过 openId 传入小米 uid。",
    };

    public override Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct) =>
        Task.FromResult(new PayCredential
        {
            Type = CredentialTypes.SdkParams,
            Data =
            {
                ["cpOrderId"] = ctx.OrderNo,
                ["cpUserInfo"] = ctx.OrderNo,
                ["amount"] = ctx.Amount,
                ["productCode"] = ctx.ProductId,
                ["productName"] = ctx.Subject,
            },
        });

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
        if (V("subStatus").Length > 0) return Task.FromResult(NotifyParseResult.Ignore("忽略签约回调"));
        if (V("orderStatus") != "TRADE_SUCCESS") return Task.FromResult(NotifyParseResult.Ignore($"订单状态 {V("orderStatus")}"));

        return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
        {
            Type = NotifyEventType.Paid,
            OrderNo = V("cpOrderId"),
            UpstreamTradeNo = V("orderId"),
            // 使用代金券时 payFee 为扣除后的实付，需加回 partnerGiftConsume 才等于订单金额
            Amount = long.TryParse(V("payFee"), out var fee) ? fee + (long.TryParse(V("partnerGiftConsume"), out var gift) ? gift : 0) : null,
            PaidAt = TimeUtil.ParseBeijing(V("payTime"), "yyyy-MM-dd HH:mm:ss"),
            BuyerId = V("uid"),
        }));
    }

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
