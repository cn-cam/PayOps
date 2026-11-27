using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Honor;

/// <summary>
/// 荣耀应用内支付（Honor IAP）：一次性商品与订阅型商品（荣耀称"签约"，订阅标识为合约号 agreementNo）。
/// 客户端 SDK 支付后提交 purchaseToken 核验；服务端回调仅作为触发器，回源调用 verifyToken 取得带签名的购买数据。
/// 回调事件：1001 支付成功、1002 退款成功、2002 订阅首购、2003 订阅续费、2004 订阅升级、2001 解约。
/// 荣耀没有"恢复续订""到期"事件，到期由巡检任务主动查询确认。
/// </summary>
public sealed partial class HonorProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    private const string DefaultTokenUrl = "https://hnoauth-login-drcn.cloud.honor.com/oauth2/v3/token";
    private const string DefaultApiUrl = "https://iap-api-drcn.cloud.honor.com";
    private static readonly ConcurrentDictionary<string, (string Token, DateTime ExpireAt)> Tokens = new();

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex OffsetWithoutColon();

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "honor",
        Name = "荣耀应用内支付",
        Category = "store",
        Methods = [new("honor_iap", "荣耀IAP")],
        Capabilities = new() { ReceiptVerify = true, Subscription = true, SubscriptionCancel = true, SubscriptionChange = true },
        ConfigFields =
        [
            new() { Key = "appId", Label = "应用AppID", Required = true },
            new() { Key = "appSecret", Label = "应用密钥", Type = "password", Required = true, Secret = true },
            new() { Key = "publicKey", Label = "IAP公钥", Type = "textarea", Required = true, Help = "荣耀开发者服务平台「应用内支付」中的支付公钥（Base64）" },
            new() { Key = "apiUrl", Label = "IAP服务地址", DefaultValue = DefaultApiUrl },
            new() { Key = "tokenUrl", Label = "Token地址", DefaultValue = DefaultTokenUrl },
        ],
        NotifyNote = "请把回调地址配置到荣耀开发者服务平台「应用内支付 → 支付结果回调」。客户端创建购买时须把下单返回的 bizOrderNo / developerPayload 原样传入。订阅型商品在荣耀侧需单独申请开通。",
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
                ["subscription"] = ctx.Subscribe,
            },
        });
    }

    public override async Task<QueryResult> VerifyReceiptAsync(ReceiptContext ctx, CancellationToken ct)
    {
        var token = ctx.Receipt.GetValueOrDefault("purchaseToken", "");
        if (token.Length == 0) throw BadRequest("缺少 purchaseToken");
        return await VerifyTokenAsync(ctx.Config, token, ct);
    }

    // ------------------------------------------------------------------ 订阅

    public override async Task<SubscriptionState> QuerySubscriptionAsync(SubscriptionContext ctx, CancellationToken ct)
    {
        if (ctx.Token.Length == 0) return new SubscriptionState { Known = false, Message = "缺少 purchaseToken，无法查询" };
        var q = await VerifyTokenAsync(ctx.Config, ctx.Token, ct);
        if (q.Subscription is not { } s) return new SubscriptionState { Known = false, Message = "荣耀未返回订阅信息" };
        return new SubscriptionState
        {
            Known = true,
            // 解约后已付费周期内仍应有效：以周期结束时间为准
            Active = q.State == UpstreamState.Paid && (s.AutoRenew || s.ExpiresAt is null || s.ExpiresAt > DateTime.UtcNow),
            Revoked = q.State == UpstreamState.Refunded,
            Paused = !s.AutoRenew && s.ExpiresAt is { } expiry && expiry <= DateTime.UtcNow,
            AutoRenew = s.AutoRenew,
            ProductId = s.ProductId,
            PeriodStart = s.PeriodStart,
            ExpiresAt = s.ExpiresAt,
            LatestTradeNo = q.UpstreamTradeNo,
            LatestPaidAt = q.PaidAt,
            Message = q.Message,
        };
    }

    /// <summary>订阅型商品解约（cancelType 固定 11）。</summary>
    public override async Task CancelSubscriptionAsync(SubscriptionContext ctx, CancellationToken ct)
    {
        var url = ctx.Config.Get("apiUrl", DefaultApiUrl).TrimEnd('/') + "/iap/server/cancelSubscriptionProduct";
        var body = JsonSerializer.Serialize(new { agreementNo = ctx.UpstreamSubId, cancelType = 11 });
        var json = await PostAsync(ctx.Config, url, ctx.Token, body, ct);
        if (json.Long("code") != 0) throw Upstream("HONOR_" + json.Str("code"), $"荣耀解约失败：{json.Str("message")}");
    }

    // ------------------------------------------------------------------ 回调

    public override async Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var root = ParseJson(ctx.Body, Meta.Name);
        var eventCode = root.Long("eventCode");
        if (eventCode is not (1001 or 1002 or 2001 or 2002 or 2003 or 2004)) return NotifyParseResult.Ignore($"忽略事件 {eventCode}");
        var dataText = root.Str("data");
        if (dataText.Length == 0) return NotifyParseResult.Ignore("缺少 data");
        var data = ParseJson(dataText, Meta.Name);
        var purchaseToken = data.Str("purchaseToken");
        if (purchaseToken.Length == 0) return NotifyParseResult.Ignore("缺少 purchaseToken");

        // 回源取带签名的购买数据；订阅事件里的合约号、周期等字段优先用回源结果，缺失时才用（已验签的）回调数据补齐
        var q = await VerifyTokenAsync(ctx.Config, purchaseToken, ct);

        if (eventCode is 1001 or 1002)
        {
            if (eventCode == 1002 || q.State == UpstreamState.Refunded)
            {
                return NotifyParseResult.Of(new NotifyEvent
                {
                    Type = NotifyEventType.Refund,
                    OrderNo = q.Subscription is null ? q.BoundOrderNo ?? "" : "",
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

        var info = q.Subscription;
        if (info is null)
        {
            // 回源结果没有订阅对象时，只有回调自身验签通过才采信其中的合约号
            var signature = ctx.Headers.TryGetValue("signature", out var sig) ? sig : "";
            var agreementNo = data.Str("agreementNo");
            if (agreementNo.Length == 0 || !KeyUtil.RsaVerify(ctx.Config.Require("publicKey"), dataText, signature, HashAlgorithmName.SHA256))
                return NotifyParseResult.Ignore("订阅事件缺少可信的合约号");
            info = new SubscriptionInfo
            {
                UpstreamSubId = agreementNo,
                Token = purchaseToken,
                PeriodStart = ParseTime(data.Str("subStartTime")),
                ExpiresAt = ParseTime(data.Str("subEndTime")),
                AutoRenew = eventCode != 2001,
            };
        }

        if (eventCode == 2001)
            return NotifyParseResult.Of(new NotifyEvent { Type = NotifyEventType.Subscription, SubKind = SubscriptionEventKind.AutoRenewOff, Subscription = info });

        // 2004：订阅升级/换购，立即生效
        if (eventCode == 2004)
            return NotifyParseResult.Of(new NotifyEvent
            {
                Type = NotifyEventType.Subscription, SubKind = SubscriptionEventKind.Upgraded,
                UpstreamTradeNo = data.Str("orderId") is { Length: > 0 } upgradedNo ? upgradedNo : q.UpstreamTradeNo,
                ProductId = q.ProductId, Subscription = info,
            });

        if (q.State != UpstreamState.Paid) return NotifyParseResult.Ignore($"购买状态 {q.Message}");
        // 续费回调是否带有独立订单号官方未说明：优先取回调里的 orderId 作为本期交易号
        var tradeNo = data.Str("orderId") is { Length: > 0 } id ? id : q.UpstreamTradeNo;
        return NotifyParseResult.Of(new NotifyEvent
        {
            Type = NotifyEventType.Subscription,
            SubKind = SubscriptionEventKind.Charged,
            OrderNo = q.BoundOrderNo ?? "",
            UpstreamTradeNo = tradeNo,
            PaidAt = q.PaidAt,
            ProductId = q.ProductId,
            Subscription = info,
        });
    }

    public override NotifyReply BuildReply(NotifyOutcome outcome, string message) =>
        outcome == NotifyOutcome.Success
            ? NotifyReply.Json("{\"code\":0,\"message\":\"success\"}")
            : NotifyReply.Json(JsonSerializer.Serialize(new { code = 1, message }), 500);

    // ------------------------------------------------------------------ 购买数据

    private async Task<QueryResult> VerifyTokenAsync(PassageConfig cfg, string purchaseToken, CancellationToken ct)
    {
        var url = cfg.Get("apiUrl", DefaultApiUrl).TrimEnd('/') + "/iap/server/verifyToken";
        var json = await PostAsync(cfg, url, purchaseToken, "{}", ct);
        var data = json.Obj("data");
        if (json.Long("code") != 0 || data is null)
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
        var sub = d.Obj("subscription");
        var agreementNo = sub?.Str("agreementNo") ?? "";
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
            Subscription = d.Long("productType") == 2 && agreementNo.Length > 0
                ? new SubscriptionInfo
                {
                    UpstreamSubId = agreementNo,
                    Token = d.Str("purchaseToken"),
                    PeriodStart = ParseTime(sub!.Value.Str("subStartTime")),
                    ExpiresAt = ParseTime(sub.Value.Str("subEndTime")),
                    AutoRenew = sub.Value.Long("subscriptionState") == 1,
                    Amount = Money.FromYuan(d.Str("payMoney")),
                    ProductId = d.Str("productId"),
                    
                }
                : null,
        };
    }

    /// <summary>解析荣耀的 "yyyy-MM-dd HH:mm:ssZ" 时间（时区形如 +0800）；不带时区时按北京时间。</summary>
    public static DateTime? ParseTime(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return null;
        var normalized = OffsetWithoutColon().Replace(value, "$1:$2");
        if (normalized != value || value.EndsWith('Z'))
            return DateTimeOffset.TryParse(normalized, null, System.Globalization.DateTimeStyles.None, out var dto) ? dto.UtcDateTime : null;
        return TimeUtil.ParseBeijing(value, "yyyy-MM-dd HH:mm:ss");
    }

    // ------------------------------------------------------------------ HTTP

    private async Task<JsonElement> PostAsync(PassageConfig cfg, string url, string purchaseToken, string body, CancellationToken ct)
    {
        var (status, text) = await PostOnceAsync(cfg, url, purchaseToken, body, refreshToken: false, ct);
        if (status == 401) (status, text) = await PostOnceAsync(cfg, url, purchaseToken, body, refreshToken: true, ct);
        var json = ParseJson(text, Meta.Name);
        if (status is < 200 or >= 300) throw Upstream("HONOR_HTTP_" + status, $"荣耀接口返回 HTTP {status}：{Truncate(text, 200)}");
        return json;
    }

    private async Task<(int Status, string Body)> PostOnceAsync(PassageConfig cfg, string url, string purchaseToken, string body, bool refreshToken, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(cfg, refreshToken, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("access-token", accessToken);
        req.Headers.TryAddWithoutValidation("x-iap-appid", cfg.Require("appId"));
        if (purchaseToken.Length > 0) req.Headers.TryAddWithoutValidation("purchaseToken", purchaseToken);
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
