using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Huawei;

/// <summary>
/// 华为应用内支付（HMS Core IAP，消耗型/非消耗型商品）。
/// 流程：下单返回 developerPayload（本系统订单号）→ 客户端 SDK 支付 → 提交 purchaseToken 核验，
/// 同时接收华为「订单关键事件通知 v2」。通知本身不带签名，仅作为触发器，一律回源调用 Order 服务核验。
/// </summary>
public sealed class HuaweiProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    private const string TokenUrl = "https://oauth-login.cloud.huawei.com/oauth2/v3/token";
    private const string DefaultOrdersUrl = "https://orders-drcn.iap.cloud.huawei.com.cn";
    private static readonly ConcurrentDictionary<string, (string Token, DateTime ExpireAt)> Tokens = new();

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "huawei",
        Name = "华为应用内支付",
        Category = "store",
        Methods = [new("huawei_iap", "华为IAP")],
        Capabilities = new() { ReceiptVerify = true },
        ConfigFields =
        [
            new() { Key = "appId", Label = "应用AppID", Required = true, Help = "AppGallery Connect 中的 APP ID（OAuth client_id）" },
            new() { Key = "appSecret", Label = "应用密钥", Type = "password", Required = true, Secret = true, Help = "AppGallery Connect 中的 APP SECRET" },
            new() { Key = "publicKey", Label = "IAP公钥", Type = "textarea", Required = true, Help = "「应用内支付服务」页面的支付公钥（Base64），用于校验购买数据签名" },
            new() { Key = "ordersUrl", Label = "Order服务地址", DefaultValue = DefaultOrdersUrl, Help = "中国站默认即可；海外站点请替换为对应地址" },
        ],
        NotifyNote = "请把回调地址配置到 AppGallery Connect「应用内支付服务 → 订单关键事件通知」。客户端创建购买时须把下单返回的 developerPayload 原样传入。",
    };

    protected override string? ValidateKeys(PassageConfig config)
    {
        using var pub = KeyUtil.LoadRsaPublicKey(config.Require("publicKey"));
        return null;
    }

    public override Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct)
    {
        if (ctx.ProductId.Length == 0) throw BadRequest("华为IAP下单必须传 productId");
        return Task.FromResult(new PayCredential
        {
            Type = CredentialTypes.Iap,
            Data = { ["orderNo"] = ctx.OrderNo, ["productId"] = ctx.ProductId, ["developerPayload"] = ctx.OrderNo },
        });
    }

    public override async Task<QueryResult> VerifyReceiptAsync(ReceiptContext ctx, CancellationToken ct)
    {
        var token = ctx.Receipt.GetValueOrDefault("purchaseToken", "");
        if (token.Length == 0) throw BadRequest("缺少 purchaseToken");
        var productId = ctx.Receipt.GetValueOrDefault("productId", "") is { Length: > 0 } p ? p : ctx.ProductId;
        return await VerifyTokenAsync(ctx.Config, token, productId, ct);
    }

    public override async Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var root = ParseJson(ctx.Body, Meta.Name);
        if (root.Str("eventType") != "ORDER") return NotifyParseResult.Ignore($"忽略事件 {root.Str("eventType")}");
        var n = root.Obj("orderNotification");
        if (n is null) return NotifyParseResult.Ignore("缺少 orderNotification");
        var purchaseToken = n.Value.Str("purchaseToken");
        var productId = n.Value.Str("productId");
        var type = n.Value.Long("notificationType");
        if (purchaseToken.Length == 0) return NotifyParseResult.Ignore("缺少 purchaseToken");

        // 通知不带签名：回源核验后才信任
        var q = await VerifyTokenAsync(ctx.Config, purchaseToken, productId, ct);
        if (string.IsNullOrEmpty(q.BoundOrderNo)) return NotifyParseResult.Ignore("购买数据未携带 developerPayload，无法关联订单");

        if (type == 2 || q.State == UpstreamState.Refunded)
        {
            return NotifyParseResult.Of(new NotifyEvent
            {
                Type = NotifyEventType.Refund,
                OrderNo = q.BoundOrderNo,
                UpstreamTradeNo = q.UpstreamTradeNo,
                UpstreamRefundNo = "HW-" + q.UpstreamTradeNo,
                Message = "华为侧退款",
            });
        }
        if (q.State != UpstreamState.Paid) return NotifyParseResult.Ignore($"购买状态 {q.Message}");
        return NotifyParseResult.Of(new NotifyEvent
        {
            Type = NotifyEventType.Paid,
            OrderNo = q.BoundOrderNo,
            UpstreamTradeNo = q.UpstreamTradeNo,
            Amount = q.Amount,
            PaidAt = q.PaidAt,
            ProductId = q.ProductId,
        });
    }

    public override NotifyReply BuildReply(NotifyOutcome outcome, string message) =>
        outcome == NotifyOutcome.Success ? NotifyReply.Text("") : NotifyReply.Text(message, 500);

    private async Task<QueryResult> VerifyTokenAsync(PassageConfig cfg, string purchaseToken, string productId, CancellationToken ct)
    {
        var url = cfg.Get("ordersUrl", DefaultOrdersUrl).TrimEnd('/') + "/applications/purchases/tokens/verify";
        var payload = JsonSerializer.Serialize(new { purchaseToken, productId });

        var (status, text) = await PostVerifyAsync(cfg, url, payload, refreshToken: false, ct);
        if (status == 401) (status, text) = await PostVerifyAsync(cfg, url, payload, refreshToken: true, ct);
        var json = ParseJson(text, Meta.Name);
        if (status is < 200 or >= 300 || json.Str("responseCode") != "0")
            throw new ProviderException("HUAWEI_" + json.Str("responseCode"), $"华为凭证核验失败：{json.Str("responseMessage")}", requestFault: true);

        var data = json.Str("purchaseTokenData");
        return ParsePurchaseData(cfg.Require("publicKey"), data, json.Str("dataSignature"), json.Str("signatureAlgorithm"));
    }

    public static QueryResult ParsePurchaseData(string publicKey, string data, string signature, string algorithm)
    {
        var padding = algorithm.Contains("PSS", StringComparison.OrdinalIgnoreCase) ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
        if (!KeyUtil.RsaVerify(publicKey, data, signature, HashAlgorithmName.SHA256, padding))
            throw new ProviderException("RECEIPT_SIGN_ERROR", "华为购买数据签名校验失败", requestFault: true);

        using var doc = JsonDocument.Parse(data);
        var d = doc.RootElement;
        var purchaseState = d.Long("purchaseState");
        return new QueryResult
        {
            State = purchaseState switch
            {
                0 => UpstreamState.Paid,
                1 => UpstreamState.Closed,
                2 => UpstreamState.Refunded,
                _ => UpstreamState.NotPaid,
            },
            UpstreamTradeNo = d.Str("orderId") is { Length: > 0 } o ? o : null,
            Amount = d.Str("currency") == "CNY" ? d.Long("price") : null,
            PaidAt = d.Long("purchaseTime") is { } t and > 0 ? TimeUtil.FromUnixMs(t) : null,
            BoundOrderNo = d.Str("developerPayload") is { Length: > 0 } payload ? payload : null,
            ProductId = d.Str("productId"),
            Message = $"purchaseState={purchaseState}",
        };
    }

    private async Task<(int Status, string Body)> PostVerifyAsync(PassageConfig cfg, string url, string payload, bool refreshToken, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(cfg, refreshToken, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("APPAT:" + accessToken)));
        var (status, text) = await SendAsync(req, ct);
        return (status, text.Length == 0 ? "{}" : text);
    }

    private async Task<string> GetAccessTokenAsync(PassageConfig cfg, bool forceRefresh, CancellationToken ct)
    {
        var appId = cfg.Require("appId");
        if (!forceRefresh && Tokens.TryGetValue(appId, out var cached) && cached.ExpireAt > DateTime.UtcNow) return cached.Token;

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
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
        if (token.Length == 0) throw Upstream("HUAWEI_TOKEN", $"获取华为 AccessToken 失败：{Truncate(text, 200)}");
        var ttl = Math.Max(60, (json.Long("expires_in") ?? 3600) - 300);
        Tokens[appId] = (token, DateTime.UtcNow.AddSeconds(ttl));
        return token;
    }
}
