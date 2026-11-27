using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Apple;

/// <summary>
/// 苹果应用内购买（StoreKit 2 + App Store Server API + Server Notifications V2），支持一次性商品与自动续费订阅。
/// 订单关联：下单返回由订单号可逆编码而成的 appAccountToken（UUID），客户端购买时传入；
/// 核验与通知中解码即可还原订单号。订阅以 originalTransactionId 作为订阅标识，每期扣款有独立的 transactionId。
/// 所有 JWS 均校验 x5c 证书链到 Apple Root CA - G3。苹果没有服务端退订接口，退订只能由用户在系统订阅管理中操作。
/// </summary>
public sealed class AppleProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    private const string ProductionHost = "https://api.storekit.itunes.apple.com";
    private const string SandboxHost = "https://api.storekit-sandbox.itunes.apple.com";
    private const string SubscriptionType = "Auto-Renewable Subscription";

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "apple",
        Name = "苹果应用内购买",
        Category = "store",
        Methods = [new("apple_iap", "Apple IAP")],
        Capabilities = new() { ReceiptVerify = true, Subscription = true, SubscriptionChange = true },
        ConfigFields =
        [
            new() { Key = "bundleId", Label = "Bundle ID", Required = true, Placeholder = "com.example.app" },
            new() { Key = "issuerId", Label = "Issuer ID", Required = true, Help = "App Store Connect → 用户和访问 → 集成 → App 内购买项目" },
            new() { Key = "keyId", Label = "密钥ID", Required = true },
            new() { Key = "privateKey", Label = "API私钥(.p8)", Type = "textarea", Required = true, Secret = true, Help = "App 内购买项目密钥文件内容" },
            new()
            {
                Key = "environment", Label = "环境", Type = "select", DefaultValue = "Auto",
                Options = [new("自动（先正式后沙盒）", "Auto"), new("仅正式", "Production"), new("仅沙盒", "Sandbox")],
                Help = "正式上线后建议选「仅正式」，避免沙盒凭证在生产发货",
            },
        ],
        NotifyNote = "请把回调地址配置到 App Store Connect「App 信息 → App Store 服务器通知」（选择 Version 2）。客户端购买时须把下单返回的 appAccountToken 传入 purchase(options:)。订阅的续订、退订、到期全部由服务器通知驱动。",
    };

    protected override string? ValidateKeys(PassageConfig config)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(config.Require("privateKey"));
        return null;
    }

    public override Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct)
    {
        if (ctx.ProductId.Length == 0) throw BadRequest("Apple IAP下单必须传 productId");
        return Task.FromResult(new PayCredential
        {
            Type = CredentialTypes.Iap,
            Data =
            {
                ["orderNo"] = ctx.OrderNo, ["productId"] = ctx.ProductId,
                ["appAccountToken"] = AppleToken.Encode(ctx.OrderNo),
                ["subscription"] = ctx.Subscribe,
            },
        });
    }

    public override async Task<QueryResult> VerifyReceiptAsync(ReceiptContext ctx, CancellationToken ct)
    {
        var transactionId = ctx.Receipt.GetValueOrDefault("transactionId", "");
        if (transactionId.Length == 0 && ctx.Receipt.GetValueOrDefault("signedTransaction", "") is { Length: > 0 } jws)
            transactionId = AppleJws.DecodePayloadUnverified(jws).Str("transactionId");
        if (transactionId.Length == 0 || !transactionId.All(char.IsAsciiLetterOrDigit))
            throw BadRequest("缺少有效的 transactionId（或 signedTransaction）");

        // 一律回源 App Store Server API，不直接信任客户端上传的 JWS
        var body = await GetAsync(ctx.Config, $"/inApps/v1/transactions/{transactionId}", ct);
        return ToQueryResult(ctx.Config, AppleJws.VerifyAndDecode(body.Str("signedTransactionInfo")));
    }

    /// <summary>查询订阅现状（Get All Subscription Statuses）。status：1 生效 2 过期 3 扣款重试 4 宽限期 5 撤销。</summary>
    public override async Task<SubscriptionState> QuerySubscriptionAsync(SubscriptionContext ctx, CancellationToken ct)
    {
        if (!ctx.UpstreamSubId.All(char.IsAsciiLetterOrDigit)) throw BadRequest("订阅标识无效");
        var body = await GetAsync(ctx.Config, $"/inApps/v1/subscriptions/{ctx.UpstreamSubId}", ct);
        if (!body.TryGetProperty("data", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return new SubscriptionState { Known = false, Message = "App Store 未返回订阅数据" };

        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("lastTransactions", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in list.EnumerateArray())
            {
                if (item.Str("originalTransactionId") != ctx.UpstreamSubId) continue;
                var status = item.Long("status") ?? 0;
                var tx = AppleJws.VerifyAndDecode(item.Str("signedTransactionInfo"));
                var autoRenew = false;
                if (item.Str("signedRenewalInfo") is { Length: > 0 } renewal)
                    autoRenew = AppleJws.VerifyAndDecode(renewal).Long("autoRenewStatus") == 1;
                return new SubscriptionState
                {
                    Known = true,
                    // 宽限期内仍应提供服务；扣款重试期（3）按未生效处理，扣款恢复后会经 DID_RENEW 重新激活
                    Active = status is 1 or 4,
                    Revoked = status == 5,
                    AutoRenew = autoRenew,
                    PeriodStart = Ms(tx, "purchaseDate"),
                    ExpiresAt = Ms(tx, "expiresDate"),
                    LatestTradeNo = tx.Str("transactionId") is { Length: > 0 } id ? id : null,
                    LatestAmount = Price(tx),
                    LatestPaidAt = Ms(tx, "purchaseDate"),
                    Message = $"status={status}",
                };
            }
        }
        return new SubscriptionState { Known = false, Message = "App Store 返回的数据中没有该订阅" };
    }

    public override Task CancelSubscriptionAsync(SubscriptionContext ctx, CancellationToken ct) =>
        throw new ProviderException("NOT_SUPPORTED",
            "苹果不提供服务端退订接口：请在 App 内调用 AppStore.showManageSubscriptions，或引导用户到「设置 → Apple 账户 → 订阅」退订；退订结果会经服务器通知自动同步",
            requestFault: true);

    public override Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var signedPayload = ParseJson(ctx.Body, Meta.Name).Str("signedPayload");
        if (signedPayload.Length == 0) return Task.FromResult(NotifyParseResult.Fail("缺少 signedPayload"));

        JsonElement payload;
        try
        {
            payload = AppleJws.VerifyAndDecode(signedPayload);
        }
        catch (ProviderException ex)
        {
            return Task.FromResult(NotifyParseResult.Fail(ex.Message));
        }

        var type = payload.Str("notificationType");
        var subtype = payload.Str("subtype");
        if (type is not ("ONE_TIME_CHARGE" or "REFUND" or "SUBSCRIBED" or "DID_RENEW" or "DID_CHANGE_RENEWAL_STATUS" or "DID_CHANGE_RENEWAL_PREF"
            or "EXPIRED" or "GRACE_PERIOD_EXPIRED" or "REVOKE"))
            return Task.FromResult(NotifyParseResult.Ignore($"忽略通知类型 {type}"));
        var data = payload.Obj("data");
        if (data is null) return Task.FromResult(NotifyParseResult.Ignore("缺少 data"));
        if (data.Value.Str("bundleId") != ctx.Config.Require("bundleId"))
            return Task.FromResult(NotifyParseResult.Fail("bundleId 不匹配"));
        if (!EnvironmentAllowed(ctx.Config, data.Value.Str("environment")))
            return Task.FromResult(NotifyParseResult.Ignore($"当前通道不接受 {data.Value.Str("environment")} 环境的通知"));

        QueryResult q;
        try
        {
            q = ToQueryResult(ctx.Config, AppleJws.VerifyAndDecode(data.Value.Str("signedTransactionInfo")));
        }
        catch (ProviderException ex)
        {
            return Task.FromResult(NotifyParseResult.Fail(ex.Message));
        }

        var result = new NotifyParseResult { Verified = true };
        var isSubscription = q.Subscription is not null;
        // 续订信息里的下一期商品用于降级预约；解析失败不影响主流程
        var nextProductId = "";
        var autoRenew = true;
        if (data.Value.Str("signedRenewalInfo") is { Length: > 0 } renewalJws)
        {
            try
            {
                var renewal = AppleJws.VerifyAndDecode(renewalJws);
                nextProductId = renewal.Str("autoRenewProductId");
                autoRenew = (renewal.Long("autoRenewStatus") ?? 1) == 1;
            }
            catch (ProviderException)
            {
            }
        }
        switch (type)
        {
            case "ONE_TIME_CHARGE":
                if (string.IsNullOrEmpty(q.BoundOrderNo))
                    return Task.FromResult(NotifyParseResult.Ignore("交易未携带 appAccountToken，等待客户端提交凭证核验"));
                result.Events.Add(new NotifyEvent
                {
                    Type = NotifyEventType.Paid, OrderNo = q.BoundOrderNo, UpstreamTradeNo = q.UpstreamTradeNo,
                    PaidAt = q.PaidAt, ProductId = q.ProductId,
                });
                break;

            case "SUBSCRIBED" or "DID_RENEW" when isSubscription:
                // 首购与续订统一上报为"一次成功扣款"，由服务层按订阅是否已存在区分
                var productId = q.ProductId ?? "";
                result.Events.Add(new NotifyEvent
                {
                    Type = NotifyEventType.Subscription,
                    SubKind = SubscriptionEventKind.Charged,
                    OrderNo = q.BoundOrderNo is { } bound && !bound.StartsWith("apple-token:", StringComparison.Ordinal) ? bound : "",
                    UpstreamTradeNo = q.UpstreamTradeNo,
                    PaidAt = q.PaidAt,
                    ProductId = productId,
                    Subscription = new SubscriptionInfo
                    {
                        UpstreamSubId = q.Subscription!.UpstreamSubId,
                        PeriodStart = q.Subscription.PeriodStart,
                        ExpiresAt = q.Subscription.ExpiresAt,
                        AutoRenew = autoRenew,
                        Amount = q.Subscription.Amount,
                        ProductId = productId,
                        NextProductId = nextProductId == productId ? "" : nextProductId,
                    },
                });
                break;

            case "DID_CHANGE_RENEWAL_STATUS" when isSubscription:
                if (subtype is not ("AUTO_RENEW_DISABLED" or "AUTO_RENEW_ENABLED"))
                    return Task.FromResult(NotifyParseResult.Ignore($"忽略续订状态子类型 {subtype}"));
                result.Events.Add(SubscriptionEvent(
                    subtype == "AUTO_RENEW_DISABLED" ? SubscriptionEventKind.AutoRenewOff : SubscriptionEventKind.AutoRenewOn, q));
                break;

            case "DID_CHANGE_RENEWAL_PREF" when isSubscription:
                // UPGRADE：立即生效，本期商品已变；DOWNGRADE：下一期生效；无 subtype：撤销降级
                if (subtype == "UPGRADE")
                {
                    result.Events.Add(SubscriptionEvent(SubscriptionEventKind.Upgraded, q));
                }
                else
                {
                    result.Events.Add(new NotifyEvent
                    {
                        Type = NotifyEventType.Subscription,
                        SubKind = SubscriptionEventKind.Downgraded,
                        UpstreamTradeNo = q.UpstreamTradeNo,
                        ProductId = q.ProductId ?? "",
                        Subscription = new SubscriptionInfo
                        {
                            UpstreamSubId = q.Subscription!.UpstreamSubId,
                            // 撤销降级时把预约商品清空，回落到本期商品
                            NextProductId = subtype == "DOWNGRADE" ? nextProductId : "",
                            ProductId = q.ProductId ?? "",
                            ExpiresAt = q.Subscription.ExpiresAt,
                            AutoRenew = autoRenew,
                        },
                    });
                }
                break;

            case "EXPIRED" or "GRACE_PERIOD_EXPIRED" when isSubscription:
                result.Events.Add(SubscriptionEvent(SubscriptionEventKind.Expired, q));
                break;

            case "REVOKE" when isSubscription:
                result.Events.Add(SubscriptionEvent(SubscriptionEventKind.Revoked, q));
                break;

            case "REFUND":
                // 订阅的每期扣款对应不同订单（首购单 / 续费单），因此退款一律按上游交易号反查订单；
                // 一次性商品优先用 appAccountToken 还原的订单号
                result.Events.Add(new NotifyEvent
                {
                    Type = NotifyEventType.Refund,
                    OrderNo = isSubscription ? "" : q.BoundOrderNo ?? "",
                    UpstreamTradeNo = q.UpstreamTradeNo,
                    UpstreamRefundNo = "APPLE-" + q.UpstreamTradeNo,
                    Message = "App Store 侧退款",
                });
                // 退的是仍在有效期内的那一期：订阅随之撤销
                if (isSubscription && q.Subscription!.ExpiresAt > DateTime.UtcNow)
                    result.Events.Add(SubscriptionEvent(SubscriptionEventKind.Revoked, q));
                break;

            default:
                return Task.FromResult(NotifyParseResult.Ignore($"通知 {type} 与交易类型不匹配，已忽略"));
        }
        return Task.FromResult(result);
    }

    private static NotifyEvent SubscriptionEvent(SubscriptionEventKind kind, QueryResult q) => new()
    {
        Type = NotifyEventType.Subscription,
        SubKind = kind,
        OrderNo = q.BoundOrderNo is { } bound && !bound.StartsWith("apple-token:", StringComparison.Ordinal) ? bound : "",
        UpstreamTradeNo = q.UpstreamTradeNo,
        PaidAt = q.PaidAt,
        ProductId = q.ProductId,
        Subscription = q.Subscription,
    };

    public override NotifyReply BuildReply(NotifyOutcome outcome, string message) =>
        outcome == NotifyOutcome.Success ? NotifyReply.Text("") : NotifyReply.Text(message, 500);

    private static bool EnvironmentAllowed(PassageConfig cfg, string environment) =>
        cfg.Get("environment", "Auto") switch
        {
            "Production" => environment == "Production",
            "Sandbox" => environment == "Sandbox",
            _ => true,
        };

    private static DateTime? Ms(JsonElement tx, string name) => tx.Long(name) is { } t and > 0 ? TimeUtil.FromUnixMs(t) : null;

    /// <summary>人民币价格（分）：price 为千分位单位，仅用于记录，不参与校验。</summary>
    private static long? Price(JsonElement tx) => tx.Str("currency") == "CNY" && tx.Long("price") is { } p ? p / 10 : null;

    private static QueryResult ToQueryResult(PassageConfig cfg, JsonElement tx)
    {
        if (tx.Str("bundleId") != cfg.Require("bundleId"))
            throw new ProviderException("RECEIPT_INVALID", "交易的 bundleId 与通道配置不一致", requestFault: true);
        if (!EnvironmentAllowed(cfg, tx.Str("environment")))
            throw new ProviderException("RECEIPT_INVALID", $"当前通道不接受 {tx.Str("environment")} 环境的交易", requestFault: true);

        var revoked = tx.Long("revocationDate") is > 0;
        var token = tx.Str("appAccountToken");
        var originalId = tx.Str("originalTransactionId");
        return new QueryResult
        {
            State = revoked ? UpstreamState.Refunded : UpstreamState.Paid,
            UpstreamTradeNo = tx.Str("transactionId") is { Length: > 0 } id ? id : null,
            // 苹果价格由商品档位决定且随店面币种变化，不参与金额校验
            Amount = null,
            PaidAt = Ms(tx, "purchaseDate"),
            BoundOrderNo = token.Length > 0 ? AppleToken.Decode(token) ?? "apple-token:" + token : null,
            ProductId = tx.Str("productId"),
            Message = revoked ? "已撤销/退款" : "已购买",
            Subscription = tx.Str("type") == SubscriptionType && originalId.Length > 0
                ? new SubscriptionInfo
                {
                    UpstreamSubId = originalId,
                    PeriodStart = Ms(tx, "purchaseDate"),
                    ExpiresAt = Ms(tx, "expiresDate"),
                    AutoRenew = true,
                    Amount = Price(tx),
                }
                : null,
        };
    }

    /// <summary>调用 App Store Server API：按环境配置依次尝试正式与沙盒。</summary>
    private async Task<JsonElement> GetAsync(PassageConfig cfg, string path, CancellationToken ct)
    {
        var hosts = cfg.Get("environment", "Auto") switch
        {
            "Production" => new[] { ProductionHost },
            "Sandbox" => new[] { SandboxHost },
            _ => new[] { ProductionHost, SandboxHost },
        };
        var jwt = BuildApiToken(cfg);
        var lastError = "";
        foreach (var host in hosts)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, host + path);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + jwt);
            var (status, text) = await SendAsync(req, ct);
            if (status == 200) return ParseJson(text, Meta.Name);
            lastError = $"HTTP {status} {Truncate(text, 200)}";
            // 404（交易不在该环境）或 401（应用尚未在正式环境发布）时尝试下一个环境
            if (status is not (404 or 401)) break;
        }
        throw new ProviderException("APPLE_API", $"请求 App Store Server API 失败：{lastError}", requestFault: true);
    }

    public static string BuildApiToken(PassageConfig cfg)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = JsonSerializer.Serialize(new { alg = "ES256", kid = cfg.Require("keyId"), typ = "JWT" });
        var payload = JsonSerializer.Serialize(new
        {
            iss = cfg.Require("issuerId"),
            iat = now,
            exp = now + 1200,
            aud = "appstoreconnect-v1",
            bid = cfg.Require("bundleId"),
        });
        var signingInput = KeyUtil.Base64Url(Encoding.UTF8.GetBytes(header)) + "." + KeyUtil.Base64Url(Encoding.UTF8.GetBytes(payload));
        using var key = ECDsa.Create();
        key.ImportFromPem(cfg.Require("privateKey"));
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return signingInput + "." + KeyUtil.Base64Url(signature);
    }
}

/// <summary>订单号与 appAccountToken(UUID) 的可逆编码。</summary>
public static class AppleToken
{
    // 32 个十六进制位中，第 12 位固定为版本号 4，第 16 位固定为变体 8，其余 30 位承载数据：
    // 前缀字符(2 位 hex) + 数字串，不足以 f 填充。
    public static string Encode(string orderNo)
    {
        if (orderNo.Length is < 2 or > 29 || !orderNo.Skip(1).All(char.IsAsciiDigit) || orderNo[0] > 0x7f)
            throw new ArgumentException("订单号格式不支持编码为 appAccountToken", nameof(orderNo));
        var data = (((int)orderNo[0]).ToString("x2") + orderNo[1..]).PadRight(30, 'f');
        var hex = data[..12] + "4" + data[12..15] + "8" + data[15..];
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    public static string? Decode(string token)
    {
        var hex = token.Replace("-", "").ToLowerInvariant();
        if (hex.Length != 32 || hex[12] != '4' || hex[16] != '8') return null;
        var data = (hex[..12] + hex[13..16] + hex[17..]).TrimEnd('f');
        if (data.Length < 3 || !data.Skip(2).All(char.IsAsciiDigit)) return null;
        try
        {
            return (char)Convert.ToInt32(data[..2], 16) + data[2..];
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public static class AppleJws
{
    /// <summary>Apple Root CA - G3（SHA-256 指纹 63343ABF…653E9179）。</summary>
    public const string RootCaG3 =
        "MIICQzCCAcmgAwIBAgIILcX8iNLFS5UwCgYIKoZIzj0EAwMwZzEbMBkGA1UEAwwSQXBwbGUgUm9vdCBDQSAtIEczMSYwJAYDVQQLDB1BcHBsZSBDZXJ0aWZpY2F0aW9uIEF1dGhvcml0eTETMBEGA1UECgwKQXBwbGUgSW5jLjELMAkGA1UEBhMCVVMwHhcNMTQwNDMwMTgxOTA2WhcNMzkwNDMwMTgxOTA2WjBnMRswGQYDVQQDDBJBcHBsZSBSb290IENBIC0gRzMxJjAkBgNVBAsMHUFwcGxlIENlcnRpZmljYXRpb24gQXV0aG9yaXR5MRMwEQYDVQQKDApBcHBsZSBJbmMuMQswCQYDVQQGEwJVUzB2MBAGByqGSM49AgEGBSuBBAAiA2IABJjpLz1AcqTtkyJygRMc3RCV8cWjTnHcFBbZDuWmBSp3ZHtfTjjTuxxEtX/1H7YyYl3J6YRbTzBPEVoA/VhYDKX1DyxNB0cTddqXl5dvMVztK517IDvYuVTZXpmkOlEKMaNCMEAwHQYDVR0OBBYEFLuw3qFYM4iapIqZ3r6966/ayySrMA8GA1UdEwEB/wQFMAMBAf8wDgYDVR0PAQH/BAQDAgEGMAoGCCqGSM49BAMDA2gAMGUCMQCD6cHEFl4aXTQY2e3v9GwOAEZLuN+yRhHFD/3meoyhpmvOwgPUnPWTxnS4at+qIxUCMG1mihDK1A3UT82NQz60imOlM27jbdoXt2QfyFMm+YhidDkLF1vLUagM6BgD56KyKA==";

    public const string RootCaG3Sha256 = "63343ABFB89A6A03EBB57E9B3F5FA7BE7C4F5C756F3017B3A8C488C3653E9179";

    private const string LeafOid = "1.2.840.113635.100.6.11.1";
    private const string IntermediateOid = "1.2.840.113635.100.6.2.1";

    public static JsonElement DecodePayloadUnverified(string jws)
    {
        var parts = jws.Split('.');
        if (parts.Length != 3) throw Invalid("JWS 格式不正确");
        try
        {
            using var doc = JsonDocument.Parse(KeyUtil.FromBase64Url(parts[1]));
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw Invalid("JWS 载荷无法解析");
        }
    }

    /// <summary>校验 x5c 证书链（锚定 Apple Root CA - G3）与 ES256 签名后返回载荷。</summary>
    public static JsonElement VerifyAndDecode(string jws)
    {
        var parts = jws.Split('.');
        if (parts.Length != 3) throw Invalid("JWS 格式不正确");

        List<X509Certificate2> certs;
        try
        {
            using var header = JsonDocument.Parse(KeyUtil.FromBase64Url(parts[0]));
            if (header.RootElement.Str("alg") != "ES256") throw Invalid("JWS 算法不是 ES256");
            if (!header.RootElement.TryGetProperty("x5c", out var x5c) || x5c.ValueKind != JsonValueKind.Array || x5c.GetArrayLength() < 2)
                throw Invalid("JWS 缺少 x5c 证书链");
            certs = x5c.EnumerateArray()
                .Select(c => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(c.GetString() ?? "")))
                .ToList();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            throw Invalid("JWS 头部无法解析");
        }

        try
        {
            var leaf = certs[0];
            var intermediate = certs[1];
            if (leaf.Extensions[LeafOid] is null || intermediate.Extensions[IntermediateOid] is null)
                throw Invalid("JWS 证书不是 App Store 签发");

            using var root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(RootCaG3));
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.ExtraStore.Add(intermediate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (!chain.Build(leaf)) throw Invalid("JWS 证书链校验失败");

            using var key = leaf.GetECDsaPublicKey() ?? throw Invalid("JWS 证书缺少 ECDSA 公钥");
            var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!key.VerifyData(signingInput, KeyUtil.FromBase64Url(parts[2]), HashAlgorithmName.SHA256))
                throw Invalid("JWS 签名校验失败");
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw Invalid("JWS 校验异常：" + ex.Message);
        }
        finally
        {
            foreach (var c in certs) c.Dispose();
        }

        return DecodePayloadUnverified(jws);
    }

    private static ProviderException Invalid(string message) => new("RECEIPT_INVALID", message, requestFault: true);
}
