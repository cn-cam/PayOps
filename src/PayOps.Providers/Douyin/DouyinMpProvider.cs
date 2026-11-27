using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Douyin;

/// <summary>抖音小程序担保支付（ecpay，小程序内 tt.pay）。APP / H5 / PC 支付见 <see cref="DouyinProvider"/>。</summary>
public sealed class DouyinMpProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    private const string DefaultHost = "https://developer.toutiao.com";

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "douyinmp",
        Name = "抖音小程序支付",
        Category = "direct",
        Methods = [new("douyinmp_ecpay", "抖音小程序担保支付")],
        Capabilities = new() { Query = true, Refund = true },
        ConfigFields =
        [
            new() { Key = "appId", Label = "小程序AppID", Required = true, Placeholder = "tt..." },
            new() { Key = "salt", Label = "支付SALT", Type = "password", Required = true, Secret = true, Help = "小程序后台「支付设置」中的 SALT，用于请求签名" },
            new() { Key = "token", Label = "回调Token", Type = "password", Required = true, Secret = true, Help = "小程序后台「支付设置」中的 Token，用于回调验签（不是 SALT）" },
            new() { Key = "host", Label = "接口域名", DefaultValue = DefaultHost, Help = "沙箱：https://open-sandbox.douyin.com" },
        ],
        NotifyNote = "下单时自动携带回调地址（需 HTTPS 443 端口）；同时建议在小程序后台「支付设置」填写该地址作为兜底。当前实现为担保支付（ecpay），新接入的非生活服务类小程序若只能使用「通用交易系统」，需另行扩展。",
    };

    public override async Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct)
    {
        var validTime = (long)Math.Clamp((ctx.ExpireAt - DateTime.UtcNow).TotalSeconds, 300, 172800);
        var body = new Dictionary<string, object>
        {
            ["app_id"] = ctx.Config.Require("appId"),
            ["out_order_no"] = ctx.OrderNo,
            ["total_amount"] = ctx.Amount,
            ["subject"] = TruncateChars(ctx.Subject, 42),
            ["body"] = TruncateChars(ctx.Body.Length > 0 ? ctx.Body : ctx.Subject, 42),
            ["valid_time"] = validTime,
            ["notify_url"] = ctx.NotifyUrl,
        };
        if (ctx.Attach.Length > 0) body["cp_extra"] = ctx.Attach;
        var json = await PostAsync(ctx.Config, "/api/apps/ecpay/v1/create_order", body, ct);
        EnsureOk(json, "抖音下单失败");
        var data = json.Obj("data") ?? throw Upstream("UPSTREAM_BAD_RESPONSE", "抖音下单返回缺少 data");
        return new PayCredential
        {
            Type = CredentialTypes.SdkParams,
            Data = { ["order_id"] = data.Str("order_id"), ["order_token"] = data.Str("order_token") },
        };
    }

    public override async Task<QueryResult> QueryAsync(OrderContext ctx, CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["app_id"] = ctx.Config.Require("appId"), ["out_order_no"] = ctx.OrderNo };
        var json = await PostAsync(ctx.Config, "/api/apps/ecpay/v1/query_order", body, ct);
        if (json.Long("err_no") == 2000) return new QueryResult { State = UpstreamState.NotPaid, Message = "订单不存在" };
        EnsureOk(json, "抖音查单失败");
        var info = json.Obj("payment_info");
        var status = info?.Str("order_status") ?? "";
        return new QueryResult
        {
            State = status switch
            {
                "SUCCESS" => UpstreamState.Paid,
                "TIMEOUT" => UpstreamState.Closed,
                "FAIL" => UpstreamState.Failed,
                _ => UpstreamState.NotPaid,
            },
            UpstreamTradeNo = json.Str("order_id") is { Length: > 0 } id ? id : null,
            Amount = info?.Long("total_fee"),
            PaidAt = TimeUtil.ParseBeijing(info?.Str("pay_time") ?? "", "yyyy-MM-dd HH:mm:ss"),
            Message = status,
        };
    }

    public override async Task<RefundResult> RefundAsync(RefundContext ctx, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["app_id"] = ctx.Config.Require("appId"),
            ["out_order_no"] = ctx.OrderNo,
            ["out_refund_no"] = ctx.RefundNo,
            ["reason"] = TruncateChars(ctx.Reason.Length > 0 ? ctx.Reason : "退款", 100),
            ["refund_amount"] = ctx.RefundAmount,
            ["notify_url"] = ctx.NotifyUrl,
        };
        var json = await PostAsync(ctx.Config, "/api/apps/ecpay/v1/create_refund", body, ct);
        EnsureOk(json, "抖音退款失败");
        return new RefundResult { State = UpstreamRefundState.Processing, UpstreamRefundNo = json.Str("refund_no") is { Length: > 0 } r ? r : null };
    }

    public override async Task<RefundResult> QueryRefundAsync(RefundContext ctx, CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["app_id"] = ctx.Config.Require("appId"), ["out_refund_no"] = ctx.RefundNo };
        var json = await PostAsync(ctx.Config, "/api/apps/ecpay/v1/query_refund", body, ct);
        if (json.Long("err_no") == 3101) return new RefundResult { State = UpstreamRefundState.Processing, Message = "退款单不存在" };
        EnsureOk(json, "抖音退款查询失败");
        var info = json.Obj("refundInfo");
        var status = info?.Str("refund_status") ?? "";
        return new RefundResult
        {
            State = status switch
            {
                "SUCCESS" => UpstreamRefundState.Success,
                "FAIL" => UpstreamRefundState.Failed,
                _ => UpstreamRefundState.Processing,
            },
            UpstreamRefundNo = info?.Str("refund_no") is { Length: > 0 } r ? r : null,
            Message = status,
        };
    }

    public override Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var root = ParseJson(ctx.Body, Meta.Name);
        var msg = root.Str("msg");
        var expected = CallbackSignature(ctx.Config.Require("token"), root.Str("timestamp"), root.Str("nonce"), msg);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(root.Str("msg_signature"))))
            return Task.FromResult(NotifyParseResult.Fail("验签失败"));

        var data = ParseJson(msg, Meta.Name);
        if (data.Str("appid") is { Length: > 0 } appId && appId != ctx.Config.Require("appId"))
            return Task.FromResult(NotifyParseResult.Fail("appid 不匹配"));

        switch (root.Str("type"))
        {
            case "payment":
                if (data.Str("status") != "SUCCESS") return Task.FromResult(NotifyParseResult.Ignore("非成功状态"));
                return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
                {
                    Type = NotifyEventType.Paid,
                    OrderNo = data.Str("cp_orderno"),
                    UpstreamTradeNo = FirstNonEmpty(data.Str("order_id"), data.Str("channel_no")),
                    Amount = data.Long("total_amount"),
                    PaidAt = data.Long("paid_at") is { } paid and > 0 ? TimeUtil.FromUnixSeconds(paid) : null,
                }));
            case "refund":
                return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
                {
                    Type = NotifyEventType.Refund,
                    RefundNo = data.Str("cp_refundno"),
                    UpstreamRefundNo = data.Str("refund_no"),
                    RefundAmount = data.Long("refund_amount"),
                    RefundSuccess = data.Str("status") == "SUCCESS",
                    Message = data.Str("message"),
                }));
            default:
                return Task.FromResult(NotifyParseResult.Ignore($"忽略回调类型 {root.Str("type")}"));
        }
    }

    public override NotifyReply BuildReply(NotifyOutcome outcome, string message) =>
        outcome == NotifyOutcome.Success
            ? NotifyReply.Json("{\"err_no\":0,\"err_tips\":\"success\"}")
            : NotifyReply.Json(JsonSerializer.Serialize(new { err_no = 400, err_tips = message }));

    /// <summary>请求签名：除 sign/app_id/thirdparty_id 外所有值 + SALT，字典序排序后以 &amp; 连接取 MD5。</summary>
    public static string RequestSign(IReadOnlyDictionary<string, object> body, string salt)
    {
        var values = new List<string> { salt };
        foreach (var (k, v) in body)
        {
            if (k is "sign" or "app_id" or "thirdparty_id" or "other_settle_params") continue;
            var s = (v is string str ? str : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "").Trim();
            if (s.Length == 0 || s == "null") continue;
            values.Add(s);
        }
        values.Sort(StringComparer.Ordinal);
        return KeyUtil.Md5Hex(string.Join('&', values));
    }

    /// <summary>回调签名：[token, timestamp, nonce, msg] 字典序排序后直接拼接取 SHA1。</summary>
    public static string CallbackSignature(string token, string timestamp, string nonce, string msg)
    {
        var parts = new[] { token, timestamp, nonce, msg };
        Array.Sort(parts, StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(string.Concat(parts))));
    }

    private async Task<JsonElement> PostAsync(PassageConfig cfg, string path, Dictionary<string, object> body, CancellationToken ct)
    {
        body["sign"] = RequestSign(body, cfg.Require("salt"));
        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Get("host", DefaultHost).TrimEnd('/') + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        var (_, text) = await SendAsync(req, ct);
        return ParseJson(text, Meta.Name);
    }

    private static void EnsureOk(JsonElement json, string prefix)
    {
        var errNo = json.Long("err_no");
        if (errNo == 0) return;
        throw Upstream("DOUYIN_" + errNo, $"{prefix}：{json.Str("err_tips")}");
    }

    private static string FirstNonEmpty(string a, string b) => a.Length > 0 ? a : b;

    private static string TruncateChars(string s, int max) => s.Length <= max ? s : s[..max];
}
