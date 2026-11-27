using System.Security.Cryptography;
using System.Text;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Stores;

/// <summary>vivo 联运 SDK 支付：服务端生成 vivoSignature，支付结果以 vivo 服务端回调为准（MD5 签名）。</summary>
public sealed class VivoProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    private const string QueryUrl = "https://pay.vivo.com.cn/vcoin/queryv2";

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "vivo",
        Name = "vivo应用商店支付",
        Category = "store",
        Methods = [new("vivo_sdk", "vivo SDK支付")],
        Capabilities = new() { Query = true },
        ConfigFields =
        [
            new() { Key = "appId", Label = "AppID", Required = true },
            new() { Key = "cpId", Label = "CP-ID", Required = true },
            new() { Key = "appKey", Label = "支付AppKey", Type = "password", Required = true, Secret = true, Help = "vivo 开放平台的支付 App-key（不是登录密钥）" },
        ],
        NotifyNote = "回调地址随下单凭据返回（notifyUrl，已参与 vivoSignature 签名），由客户端 SDK 传给 vivo。主动查单接口按 vivo 服务端文档实现，首次联调时请核对。",
    };

    public override Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct)
    {
        var p = new Dictionary<string, string>
        {
            ["appId"] = ctx.Config.Require("appId"),
            ["cpOrderNumber"] = ctx.OrderNo,
            ["orderAmount"] = ctx.Amount.ToString(),
            ["productName"] = Truncate(ctx.Subject, 50),
            ["productDesc"] = Truncate(ctx.Body.Length > 0 ? ctx.Body : ctx.Subject, 100),
            ["notifyUrl"] = ctx.NotifyUrl,
            ["expireTime"] = TimeUtil.BeijingString(ctx.ExpireAt, "yyyyMMddHHmmss"),
            ["extInfo"] = ctx.OrderNo,
        };
        var credential = new PayCredential { Type = CredentialTypes.SdkParams };
        foreach (var (k, v) in p) credential.Data[k] = v;
        credential.Data["vivoSignature"] = Sign(p, ctx.Config.Require("appKey"));
        return Task.FromResult(credential);
    }

    public override async Task<QueryResult> QueryAsync(OrderContext ctx, CancellationToken ct)
    {
        var p = new Dictionary<string, string>
        {
            ["version"] = "1.0.0",
            ["cpId"] = ctx.Config.Require("cpId"),
            ["appId"] = ctx.Config.Require("appId"),
            ["cpOrderNumber"] = ctx.OrderNo,
            ["orderAmount"] = ctx.Amount.ToString(),
        };
        if (!string.IsNullOrEmpty(ctx.UpstreamTradeNo)) p["orderNumber"] = ctx.UpstreamTradeNo;
        p["signature"] = Sign(p, ctx.Config.Require("appKey"));
        p["signMethod"] = "MD5";

        using var req = new HttpRequestMessage(HttpMethod.Post, QueryUrl) { Content = new FormUrlEncodedContent(p) };
        var (_, text) = await SendAsync(req, ct);
        var json = ParseJson(text, Meta.Name);
        var paid = json.Str("respCode") == "200" && json.Str("tradeStatus") == "0000" && json.Str("cpOrderNumber") == ctx.OrderNo;
        return new QueryResult
        {
            State = paid ? UpstreamState.Paid : UpstreamState.NotPaid,
            UpstreamTradeNo = json.Str("orderNumber") is { Length: > 0 } n ? n : null,
            Amount = json.Long("orderAmount"),
            PaidAt = TimeUtil.ParseBeijing(json.Str("payTime"), "yyyyMMddHHmmss"),
            BuyerId = json.Str("uid"),
            Message = $"respCode={json.Str("respCode")} tradeStatus={json.Str("tradeStatus")}",
        };
    }

    public override Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var f = ctx.Form.Count > 0 ? ctx.Form : ctx.Query;
        string V(string k) => f.TryGetValue(k, out var v) ? v : "";
        var expected = Sign(f, ctx.Config.Require("appKey"));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(V("signature").ToLowerInvariant())))
            return Task.FromResult(NotifyParseResult.Fail("验签失败"));
        if (V("appId") != ctx.Config.Require("appId") || V("cpId") != ctx.Config.Require("cpId"))
            return Task.FromResult(NotifyParseResult.Fail("appId / cpId 不匹配"));
        if (V("respCode") != "200" || V("tradeStatus") != "0000")
            return Task.FromResult(NotifyParseResult.Ignore($"非成功状态 respCode={V("respCode")} tradeStatus={V("tradeStatus")}"));

        return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
        {
            Type = NotifyEventType.Paid,
            OrderNo = V("cpOrderNumber"),
            UpstreamTradeNo = V("orderNumber"),
            Amount = long.TryParse(V("orderAmount"), out var amount) ? amount : null,
            PaidAt = TimeUtil.ParseBeijing(V("payTime"), "yyyyMMddHHmmss"),
            BuyerId = V("uid"),
        }));
    }

    /// <summary>非空参数（除 signMethod/signature）按 key 升序拼 k=v&amp;…，末尾追加 &amp;md5(appKey)，整体取 MD5 小写。</summary>
    public static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string appKey)
    {
        var content = string.Join('&', parameters
            .Where(kv => kv.Key is not ("signMethod" or "signature") && !string.IsNullOrEmpty(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        return KeyUtil.Md5Hex(content + "&" + KeyUtil.Md5Hex(appKey));
    }
}
