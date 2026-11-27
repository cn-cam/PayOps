using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Huawei;

/// <summary>
/// 华为应用内支付（HMS Core IAP）：消耗型/非消耗型商品（Order 服务）与自动续费订阅（Subscription 服务）。
/// 流程：下单返回 developerPayload（本系统订单号）→ 客户端 SDK 支付 → 提交 purchaseToken（订阅另需 subscriptionId）核验，
/// 同时接收「关键事件通知 v2」。通知只作为触发器，一律回源调用华为接口取带签名的购买数据后再处理。
/// 华为没有"订阅到期"通知，到期由巡检任务主动查询确认。
/// </summary>
public sealed class HuaweiProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    private const string TokenUrl = "https://oauth-login.cloud.huawei.com/oauth2/v3/token";
    private const string DefaultOrdersUrl = "https://orders-drcn.iap.cloud.huawei.com.cn";
    private const string DefaultSubscriptionUrl = "https://subscr-drcn.iap.cloud.huawei.com.cn";
    private static readonly ConcurrentDictionary<string, (string Token, DateTime ExpireAt)> Tokens = new();

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "huawei",
        Name = "华为应用内支付",
        Category = "store",
        Methods = [new("huawei_iap", "华为IAP")],
        Capabilities = new() { ReceiptVerify = true, Subscription = true, SubscriptionCancel = true, SubscriptionChange = true, SubscriptionPause = true },
        ConfigFields =
        [
            new() { Key = "appId", Label = "应用AppID", Required = true, Help = "AppGallery Connect 中的 APP ID（OAuth client_id）" },
            new() { Key = "appSecret", Label = "应用密钥", Type = "password", Required = true, Secret = true, Help = "AppGallery Connect 中的 APP SECRET" },
            new() { Key = "publicKey", Label = "IAP公钥", Type = "textarea", Required = true, Help = "「应用内支付服务」页面的支付公钥（Base64），用于校验购买数据签名" },
            new() { Key = "ordersUrl", Label = "Order服务地址", DefaultValue = DefaultOrdersUrl, Help = "中国站默认即可；海外站点请替换为对应地址" },
            new() { Key = "subscriptionUrl", Label = "Subscription服务地址", DefaultValue = DefaultSubscriptionUrl, Help = "订阅型商品使用；中国站默认即可" },
        ],
        NotifyNote = "请把回调地址配置到 AppGallery Connect「应用内支付服务 → 关键事件通知」（版本选 v2，订单与订阅事件共用）。客户端创建购买时须把下单返回的 developerPayload 原样传入；订阅核验需同时提交 purchaseToken 与 subscriptionId。",
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
            Data =
            {
                ["orderNo"] = ctx.OrderNo, ["productId"] = ctx.ProductId,
                ["developerPayload"] = ctx.OrderNo, ["subscription"] = ctx.Subscribe,
            },
        });
    }

    public override async Task<QueryResult> VerifyReceiptAsync(ReceiptContext ctx, CancellationToken ct)
    {
        var token = ctx.Receipt.GetValueOrDefault("purchaseToken", "");
        if (token.Length == 0) throw BadRequest("缺少 purchaseToken");
        var subscriptionId = ctx.Receipt.GetValueOrDefault("subscriptionId", "");
        if (subscriptionId.Length > 0) return await QuerySubscriptionDataAsync(ctx.Config, subscriptionId, token, ct);
        if (ctx.Subscribe) throw BadRequest("订阅订单核验需要同时提交 subscriptionId（来自客户端 InAppPurchaseData）");

        var productId = ctx.Receipt.GetValueOrDefault("productId", "") is { Length: > 0 } p ? p : ctx.ProductId;
        return await VerifyTokenAsync(ctx.Config, token, productId, ct);
    }

    // ------------------------------------------------------------------ 订阅

    public override async Task<SubscriptionState> QuerySubscriptionAsync(SubscriptionContext ctx, CancellationToken ct)
    {
        if (ctx.Token.Length == 0) return new SubscriptionState { Known = false, Message = "缺少 purchaseToken，无法查询" };
        var q = await QuerySubscriptionDataAsync(ctx.Config, ctx.UpstreamSubId, ctx.Token, ct);
        return new SubscriptionState
        {
            Known = true,
            Active = q.State == UpstreamState.Paid,
            Revoked = q.State == UpstreamState.Refunded,
            Paused = q.State == UpstreamState.Closed && q.Subscription is not null,
            AutoRenew = q.Subscription?.AutoRenew ?? false,
            ProductId = q.Subscription?.ProductId,
            PeriodStart = q.Subscription?.PeriodStart,
            ExpiresAt = q.Subscription?.ExpiresAt,
            LatestTradeNo = q.UpstreamTradeNo,
            LatestAmount = q.Subscription?.Amount,
            LatestPaidAt = q.PaidAt,
            Message = q.Message,
        };
    }

    /// <summary>停止订阅：只停止后续续订，不退款，当前周期仍有效。</summary>
    public override async Task CancelSubscriptionAsync(SubscriptionContext ctx, CancellationToken ct)
    {
        if (ctx.Token.Length == 0) throw BadRequest("缺少 purchaseToken，无法退订");
        var payload = JsonSerializer.Serialize(new { subscriptionId = ctx.UpstreamSubId, purchaseToken = ctx.Token });
        var json = await PostAsync(ctx.Config, SubscriptionUrl(ctx.Config) + "/sub/applications/v2/purchases/stop", payload, ct);
        if (json.Str("responseCode") != "0")
            throw Upstream("HUAWEI_" + json.Str("responseCode"), $"华为停止订阅失败：{json.Str("responseMessage")}");
    }

    private async Task<QueryResult> QuerySubscriptionDataAsync(PassageConfig cfg, string subscriptionId, string purchaseToken, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { subscriptionId, purchaseToken });
        var json = await PostAsync(cfg, SubscriptionUrl(cfg) + "/sub/applications/v2/purchases/get", payload, ct);
        if (json.Str("responseCode") != "0")
            throw new ProviderException("HUAWEI_" + json.Str("responseCode"), $"华为订阅核验失败：{json.Str("responseMessage")}", requestFault: true);
        return ParsePurchaseData(cfg.Require("publicKey"), json.Str("inappPurchaseData"), json.Str("dataSignature"), json.Str("signatureAlgorithm"));
    }

    private static string SubscriptionUrl(PassageConfig cfg) => cfg.Get("subscriptionUrl", DefaultSubscriptionUrl).TrimEnd('/');

    // ------------------------------------------------------------------ 回调

    public override async Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var root = ParseJson(ctx.Body, Meta.Name);
        return root.Str("eventType") switch
        {
            "ORDER" => await ParseOrderNotifyAsync(ctx.Config, root, ct),
            "SUBSCRIPTION" => await ParseSubscriptionNotifyAsync(ctx.Config, root, ct),
            var other => NotifyParseResult.Ignore($"忽略事件 {other}"),
        };
    }

    private async Task<NotifyParseResult> ParseOrderNotifyAsync(PassageConfig cfg, JsonElement root, CancellationToken ct)
    {
        var n = root.Obj("orderNotification");
        if (n is null) return NotifyParseResult.Ignore("缺少 orderNotification");
        var purchaseToken = n.Value.Str("purchaseToken");
        var productId = n.Value.Str("productId");
        var type = n.Value.Long("notificationType");
        if (purchaseToken.Length == 0) return NotifyParseResult.Ignore("缺少 purchaseToken");

        // 订单通知不带签名：回源核验后才信任
        var q = await VerifyTokenAsync(cfg, purchaseToken, productId, ct);
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

    /// <summary>
    /// 订阅通知。notificationType：0 首购、2 过期后自动续订成功、3 用户恢复订阅、7 正常续期扣款 → 扣款成功；
    /// 1 撤销（退款）；5 停止续订；6 恢复续订。其余（降级、暂停、延期等）忽略。
    /// </summary>
    private async Task<NotifyParseResult> ParseSubscriptionNotifyAsync(PassageConfig cfg, JsonElement root, CancellationToken ct)
    {
        var n = root.Obj("subNotification");
        if (n is null) return NotifyParseResult.Ignore("缺少 subNotification");
        var statusText = n.Value.Str("statusUpdateNotification");
        var algorithm = n.Value.Str("signatureAlgorithm");
        if (!KeyUtil.RsaVerify(cfg.Require("publicKey"), statusText, n.Value.Str("notificationSignature"), HashAlgorithmName.SHA256, Padding(algorithm)))
            return NotifyParseResult.Fail("订阅通知签名校验失败");

        var status = ParseJson(statusText, Meta.Name);
        var type = status.Long("notificationType");
        var subscriptionId = status.Str("subscriptionId");
        var purchaseToken = status.Str("purchaseToken");
        var kind = type switch
        {
            0 or 2 or 3 or 7 => SubscriptionEventKind.Charged,
            1 => SubscriptionEventKind.Revoked,
            5 => SubscriptionEventKind.AutoRenewOff,
            6 => SubscriptionEventKind.AutoRenewOn,
            // 4 降级/换购，下一期生效；9 账号冻结、10 暂停计划生效 → 按暂停处理
            4 => SubscriptionEventKind.Downgraded,
            9 or 10 => SubscriptionEventKind.Paused,
            _ => (SubscriptionEventKind)0,
        };
        if (kind == 0) return NotifyParseResult.Ignore($"忽略订阅通知类型 {type}");
        if (subscriptionId.Length == 0 || purchaseToken.Length == 0) return NotifyParseResult.Ignore("订阅通知缺少 subscriptionId / purchaseToken");

        // 通知只当触发器：以查询接口返回的带签名数据为准
        var q = await QuerySubscriptionDataAsync(cfg, subscriptionId, purchaseToken, ct);
        var info = q.Subscription ?? new SubscriptionInfo { UpstreamSubId = subscriptionId, Token = purchaseToken };
        var result = new NotifyParseResult { Verified = true };

        if (kind == SubscriptionEventKind.Charged)
        {
            if (q.State != UpstreamState.Paid) return NotifyParseResult.Ignore($"订阅当前无效：{q.Message}");
            result.Events.Add(new NotifyEvent
            {
                Type = NotifyEventType.Subscription, SubKind = kind,
                OrderNo = q.BoundOrderNo ?? "", UpstreamTradeNo = q.UpstreamTradeNo,
                PaidAt = q.PaidAt, ProductId = q.ProductId, Subscription = info,
            });
            return result;
        }

        if (kind == SubscriptionEventKind.Downgraded)
        {
            // 4：下一期改为 status.productId（换购时也可能是立即生效，以查询结果为准）
            result.Events.Add(new NotifyEvent
            {
                Type = NotifyEventType.Subscription, SubKind = kind, UpstreamTradeNo = q.UpstreamTradeNo,
                Subscription = new SubscriptionInfo
                {
                    UpstreamSubId = subscriptionId, Token = purchaseToken,
                    ProductId = q.ProductId ?? info.ProductId,
                    NextProductId = status.Str("productId"),
                    ExpiresAt = info.ExpiresAt, AutoRenew = info.AutoRenew, Amount = info.Amount,
                },
            });
            return result;
        }

        if (kind == SubscriptionEventKind.Revoked)
        {
            // 撤销 = 退款并立即终止：退款按通知里的订单号反查对应那一期的订单
            var refundedOrderId = status.Str("orderId") is { Length: > 0 } id ? id : q.UpstreamTradeNo;
            result.Events.Add(new NotifyEvent
            {
                Type = NotifyEventType.Refund, OrderNo = "", UpstreamTradeNo = refundedOrderId,
                UpstreamRefundNo = "HW-" + refundedOrderId, Message = "华为侧撤销订阅并退款",
            });
        }
        result.Events.Add(new NotifyEvent { Type = NotifyEventType.Subscription, SubKind = kind, UpstreamTradeNo = q.UpstreamTradeNo, Subscription = info });
        return result;
    }

    public override NotifyReply BuildReply(NotifyOutcome outcome, string message) =>
        outcome == NotifyOutcome.Success ? NotifyReply.Text("") : NotifyReply.Text(message, 500);

    // ------------------------------------------------------------------ 购买数据

    private async Task<QueryResult> VerifyTokenAsync(PassageConfig cfg, string purchaseToken, string productId, CancellationToken ct)
    {
        var url = cfg.Get("ordersUrl", DefaultOrdersUrl).TrimEnd('/') + "/applications/purchases/tokens/verify";
        var json = await PostAsync(cfg, url, JsonSerializer.Serialize(new { purchaseToken, productId }), ct);
        if (json.Str("responseCode") != "0")
            throw new ProviderException("HUAWEI_" + json.Str("responseCode"), $"华为凭证核验失败：{json.Str("responseMessage")}", requestFault: true);
        return ParsePurchaseData(cfg.Require("publicKey"), json.Str("purchaseTokenData"), json.Str("dataSignature"), json.Str("signatureAlgorithm"));
    }

    private static RSASignaturePadding Padding(string algorithm) =>
        algorithm.Contains("PSS", StringComparison.OrdinalIgnoreCase) ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;

    /// <summary>校验购买数据签名并解析。kind=2 为订阅：以 subIsvalid 判断是否有效，并给出订阅信息。</summary>
    public static QueryResult ParsePurchaseData(string publicKey, string data, string signature, string algorithm)
    {
        if (!KeyUtil.RsaVerify(publicKey, data, signature, HashAlgorithmName.SHA256, Padding(algorithm)))
            throw new ProviderException("RECEIPT_SIGN_ERROR", "华为购买数据签名校验失败", requestFault: true);

        using var doc = JsonDocument.Parse(data);
        var d = doc.RootElement;
        var purchaseState = d.Long("purchaseState");
        var isSubscription = d.Long("kind") == 2 && d.Str("subscriptionId").Length > 0;
        var price = d.Str("currency") == "CNY" ? d.Long("price") : null;

        UpstreamState state;
        if (isSubscription)
        {
            var valid = d.Str("subIsvalid").Equals("true", StringComparison.OrdinalIgnoreCase);
            state = purchaseState == 2 ? UpstreamState.Refunded : valid ? UpstreamState.Paid : UpstreamState.Closed;
        }
        else
        {
            state = purchaseState switch
            {
                0 => UpstreamState.Paid,
                1 => UpstreamState.Closed,
                2 => UpstreamState.Refunded,
                _ => UpstreamState.NotPaid,
            };
        }

        return new QueryResult
        {
            State = state,
            UpstreamTradeNo = d.Str("orderId") is { Length: > 0 } o ? o : null,
            // 订阅存在优惠期/试用期价格，不参与金额校验，仅记录
            Amount = isSubscription ? null : price,
            PaidAt = d.Long("purchaseTime") is { } t and > 0 ? TimeUtil.FromUnixMs(t) : null,
            BoundOrderNo = d.Str("developerPayload") is { Length: > 0 } payload ? payload : null,
            ProductId = d.Str("productId"),
            Message = $"purchaseState={purchaseState}" + (isSubscription ? $" subIsvalid={d.Str("subIsvalid")}" : ""),
            Subscription = isSubscription
                ? new SubscriptionInfo
                {
                    UpstreamSubId = d.Str("subscriptionId"),
                    Token = d.Str("purchaseToken"),
                    PeriodStart = d.Long("purchaseTime") is { } s and > 0 ? TimeUtil.FromUnixMs(s) : null,
                    ExpiresAt = d.Long("expirationDate") is { } e and > 0 ? TimeUtil.FromUnixMs(e) : null,
                    AutoRenew = d.Str("autoRenewing").Equals("true", StringComparison.OrdinalIgnoreCase) || d.Long("renewStatus") == 1,
                    Amount = price,
                }
                : null,
        };
    }

    // ------------------------------------------------------------------ HTTP

    /// <summary>调用华为 IAP 服务端接口（APPAT 基本认证）；401 时刷新 AccessToken 重试一次。</summary>
    private async Task<JsonElement> PostAsync(PassageConfig cfg, string url, string payload, CancellationToken ct)
    {
        var (status, text) = await PostOnceAsync(cfg, url, payload, refreshToken: false, ct);
        if (status == 401) (status, text) = await PostOnceAsync(cfg, url, payload, refreshToken: true, ct);
        var json = ParseJson(text, Meta.Name);
        if (status is < 200 or >= 300)
            throw Upstream("HUAWEI_HTTP_" + status, $"华为接口返回 HTTP {status}：{Truncate(text, 200)}");
        return json;
    }

    private async Task<(int Status, string Body)> PostOnceAsync(PassageConfig cfg, string url, string payload, bool refreshToken, CancellationToken ct)
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
