using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Honor;

/// <summary>
/// 荣耀应用内支付（Honor IAP）。与华为类似：客户端 SDK 支付后提交 purchaseToken 核验；
/// 服务端回调仅作为触发器，一律回源调用 verifyToken 取得带签名的购买数据。
/// </summary>
public sealed class HonorProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    private const string DefaultTokenUrl = "https://hnoauth-login-drcn.cloud.honor.com/oauth2/v3/token";
    private const string DefaultApiUrl = "https://iap-api-drcn.cloud.honor.com";
    private static readonly ConcurrentDictionary<string, (string Token, DateTime ExpireAt)> Tokens = new();

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "honor",
        Name = "荣耀应用内支付",
        Category = "store",
        Methods = [new("honor_iap", "荣耀IAP")],
        Capabilities = new() { ReceiptVerify = true },
        ConfigFields =
        [
            new() { Key = "appId", Label = "应用AppID", Required = true },
            new() { Key = "appSecret", Label = "应用密钥", Type = "password", Required = true, Secret = true },
            new() { Key = "publicKey", Label = "IAP公钥", Type = "textarea", Required = true, Help = "荣耀开发者服务平台「应用内支付」中的支付公钥（Base64）" },
            new() { Key = "apiUrl", Label = "IAP服务地址", DefaultValue = DefaultApiUrl },
            new() { Key = "tokenUrl", Label = "Token地址", DefaultValue = DefaultTokenUrl },
        ],
        NotifyNote = "请把回调地址配置到荣耀开发者服务平台「应用内支付 → 支付结果回调」。客户端创建购买时须把下单返回的 bizOrderNo / developerPayload 原样传入。",
    };

    protected override string? ValidateKeys(PassageConfig config)
    {
        using var pub = KeyUtil.LoadRsaPublicKey(config.Require("publicKey"));
        return null;
    }

    public override Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct)
    {
        if (ctx.ProductId.Length == 0) throw BadRequest("荣耀IAP下单必须传 productId");
        return Task.FromResult(new PayCredential
        {
            Type = CredentialTypes.Iap,
            Data =
            {
                ["orderNo"] = ctx.OrderNo, ["productId"] = ctx.ProductId,
                ["bizOrderNo"] = ctx.OrderNo, ["developerPayload"] = ctx.OrderNo,
            },
        });
    }

    public override async Task<QueryResult> VerifyReceiptAsync(ReceiptContext ctx, CancellationToken ct)
    {
        var token = ctx.Receipt.GetValueOrDefault("purchaseToken", "");
        if (token.Length == 0) throw BadRequest("缺少 purchaseToken");
        return await VerifyTokenAsync(ctx.Config, token, ct);
    }

    public override async Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var root = ParseJson(ctx.Body, Meta.Name);
        var eventCode = root.Long("eventCode");
        if (eventCode is not (1001 or 1002)) return NotifyParseResult.Ignore($"忽略事件 {eventCode}");
        var dataText = root.Str("data");
        if (dataText.Length == 0) return NotifyParseResult.Ignore("缺少 data");
        var purchaseToken = ParseJson(dataText, Meta.Name).Str("purchaseToken");
        if (purchaseToken.Length == 0) return NotifyParseResult.Ignore("缺少 purchaseToken");

        var q = await VerifyTokenAsync(ctx.Config, purchaseToken, ct);
        if (eventCode == 1002 || q.State == UpstreamState.Refunded)
        {
            return NotifyParseResult.Of(new NotifyEvent
            {
                Type = NotifyEventType.Refund,
                OrderNo = q.BoundOrderNo ?? "",
                UpstreamTradeNo = q.UpstreamTradeNo,
                UpstreamRefundNo = "HONOR-" + q.UpstreamTradeNo,
                Message = "荣耀侧退款",
            });
        }
        if (q.State != UpstreamState.Paid) return NotifyParseResult.Ignore($"购买状态 {q.Message}");
        if (string.IsNullOrEmpty(q.BoundOrderNo)) return NotifyParseResult.Ignore("购买数据未携带 bizOrderNo / developerPayload，无法关联订单");
        return NotifyParseResult.Of(new NotifyEvent
        {
            Type = NotifyEventType.Paid,
            OrderNo = q.BoundOrderNo,
            UpstreamTradeNo = q.UpstreamTradeNo,
            PaidAt = q.PaidAt,
            ProductId = q.ProductId,
        });
    }

    public override NotifyReply BuildReply(NotifyOutcome outcome, string message) =>
        outcome == NotifyOutcome.Success
            ? NotifyReply.Json("{\"code\":0,\"message\":\"success\"}")
            : NotifyReply.Json(JsonSerializer.Serialize(new { code = 1, message }), 500);

    private async Task<QueryResult> VerifyTokenAsync(PassageConfig cfg, string purchaseToken, CancellationToken ct)
    {
        var url = cfg.Get("apiUrl", DefaultApiUrl).TrimEnd('/') + "/iap/server/verifyToken";
        var (status, text) = await PostVerifyAsync(cfg, url, purchaseToken, refreshToken: false, ct);
        if (status == 401) (status, text) = await PostVerifyAsync(cfg, url, purchaseToken, refreshToken: true, ct);
        var json = ParseJson(text, Meta.Name);
        var data = json.Obj("data");
        if (status is < 200 or >= 300 || json.Long("code") != 0 || data is null)
            throw new ProviderException("HONOR_" + json.Str("code"), $"荣耀凭证核验失败：{json.Str("message")}", requestFault: true);
        return ParsePurchaseData(cfg.Require("publicKey"), data.Value.Str("purchaseProductInfo"), data.Value.Str("dataSig"));
    }

    public static QueryResult ParsePurchaseData(string publicKey, string info, string signature)
    {
        if (!KeyUtil.RsaVerify(publicKey, info, signature, HashAlgorithmName.SHA256))
            throw new ProviderException("RECEIPT_SIGN_ERROR", "荣耀购买数据签名校验失败", requestFault: true);

        using var doc = JsonDocument.Parse(info);
        var d = doc.RootElement;
        var purchaseState = d.Long("purchaseState");
        var bound = d.Str("bizOrderNo") is { Length: > 0 } biz ? biz : d.Str("developerPayload");
        return new QueryResult
        {
            State = purchaseState switch
            {
                0 => UpstreamState.Paid,
                1 or 5 => UpstreamState.Refunded,
                2 => UpstreamState.Failed,
                _ => UpstreamState.NotPaid,
            },
            UpstreamTradeNo = d.Str("orderId") is { Length: > 0 } o ? o : null,
            // 荣耀明确不建议用实付金额比对（优惠券场景会不一致），不参与金额校验
            Amount = null,
            PaidAt = d.Long("purchaseTime") is { } t and > 0 ? TimeUtil.FromUnixMs(t) : null,
            BoundOrderNo = bound.Length > 0 ? bound : null,
            ProductId = d.Str("productId"),
            Message = $"purchaseState={purchaseState}",
        };
    }

    private async Task<(int Status, string Body)> PostVerifyAsync(PassageConfig cfg, string url, string purchaseToken, bool refreshToken, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(cfg, refreshToken, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("access-token", accessToken);
        req.Headers.TryAddWithoutValidation("x-iap-appid", cfg.Require("appId"));
        req.Headers.TryAddWithoutValidation("purchaseToken", purchaseToken);
        var (status, text) = await SendAsync(req, ct);
        return (status, text.Length == 0 ? "{}" : text);
    }

    private async Task<string> GetAccessTokenAsync(PassageConfig cfg, bool forceRefresh, CancellationToken ct)
    {
        var appId = cfg.Require("appId");
        if (!forceRefresh && Tokens.TryGetValue(appId, out var cached) && cached.ExpireAt > DateTime.UtcNow) return cached.Token;

        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Get("tokenUrl", DefaultTokenUrl))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = appId,
                ["client_secret"] = cfg.Require("appSecret"),
            }),
        };
        var (_, text) = await SendAsync(req, ct);
        var json = ParseJson(text, Meta.Name);
        var token = json.Str("access_token");
        if (token.Length == 0) throw Upstream("HONOR_TOKEN", $"获取荣耀 AccessToken 失败：{Truncate(text, 200)}");
        var ttl = Math.Max(60, (json.Long("expires_in") ?? 3600) - 300);
        Tokens[appId] = (token, DateTime.UtcNow.AddSeconds(ttl));
        return token;
    }
}
