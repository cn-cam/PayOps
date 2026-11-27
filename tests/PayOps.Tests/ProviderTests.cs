using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Alipay;
using PayOps.Providers.Apple;
using PayOps.Providers.Common;
using PayOps.Providers.Douyin;
using PayOps.Providers.Honor;
using PayOps.Providers.Huawei;
using PayOps.Providers.Mock;
using PayOps.Providers.Stores;
using PayOps.Providers.Wechat;

namespace PayOps.Tests;

/// <summary>
/// 各支付类型的验签/解析自洽性测试：用测试内生成的密钥构造"上游回调"，
/// 验证能正确通过；任何篡改都必须被拒绝。不访问真实上游。
/// </summary>
public class ProviderTests
{
    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("测试不应访问网络");
    }

    private static readonly IHttpClientFactory Http = new NoHttp();

    private static PassageConfig Config(params (string Key, string Value)[] items) =>
        new(items.ToDictionary(i => i.Key, i => i.Value));

    private static NotifyContext Notify(PassageConfig config, string body = "", Dictionary<string, string>? headers = null,
        Dictionary<string, string>? form = null, Dictionary<string, string>? query = null, string method = "POST") => new()
    {
        PassageId = 1,
        Config = config,
        HttpMethod = method,
        Headers = new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase),
        Query = query ?? [],
        Form = form ?? [],
        Body = body,
    };

    private static CreateContext Create(PassageConfig config, string method, string orderNo = "P261127120000001000001") => new()
    {
        PassageId = 1,
        Config = config,
        OrderNo = orderNo,
        Method = method,
        Amount = 600,
        Subject = "测试商品",
        ProductId = "coin_60",
        NotifyUrl = "https://pay.example.com/api/notify/x/1",
        ExpireAt = DateTime.UtcNow.AddMinutes(30),
    };

    private static (string PrivatePem, string PublicPem, RSA Key) NewRsa(int bits = 2048)
    {
        var rsa = RSA.Create(bits);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem(), rsa);
    }

    // ---------------------------------------------------------------- 微信

    [Fact]
    public async Task Wechat_notify_is_verified_decrypted_and_parsed()
    {
        var (_, platformPublic, platformKey) = NewRsa();
        var (merchantPrivate, _, _) = NewRsa();
        const string apiV3Key = "0123456789abcdef0123456789abcdef";
        var config = Config(("appId", "wx1"), ("mchId", "1900000001"), ("serialNo", "S1"), ("privateKey", merchantPrivate),
            ("apiV3Key", apiV3Key), ("platformPublicKey", platformPublic));
        var provider = new WechatProvider(Http);
        Assert.Null(provider.ValidateConfig(config));

        var resource = JsonSerializer.Serialize(new
        {
            mchid = "1900000001", out_trade_no = "P1", transaction_id = "4200001", trade_state = "SUCCESS",
            success_time = "2026-11-27T12:00:00+08:00", amount = new { total = 600 }, payer = new { openid = "oABC" },
        });
        var nonce = "abcdefghijkl";
        var cipher = new byte[Encoding.UTF8.GetByteCount(resource)];
        var tag = new byte[16];
        using (var aes = new AesGcm(Encoding.UTF8.GetBytes(apiV3Key), 16))
            aes.Encrypt(Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(resource), cipher, tag, Encoding.UTF8.GetBytes("transaction"));
        var body = JsonSerializer.Serialize(new
        {
            event_type = "TRANSACTION.SUCCESS",
            resource = new { algorithm = "AEAD_AES_256_GCM", ciphertext = Convert.ToBase64String([.. cipher, .. tag]), associated_data = "transaction", nonce },
        });
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = Convert.ToBase64String(platformKey.SignData(Encoding.UTF8.GetBytes($"{ts}\nn1\n{body}\n"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var headers = new Dictionary<string, string> { ["Wechatpay-Timestamp"] = ts, ["Wechatpay-Nonce"] = "n1", ["Wechatpay-Signature"] = signature, ["Wechatpay-Serial"] = "X" };

        var ok = await provider.ParseNotifyAsync(Notify(config, body, headers), default);
        Assert.True(ok.Verified);
        var e = Assert.Single(ok.Events);
        Assert.Equal((NotifyEventType.Paid, "P1", "4200001", 600L, "oABC"), (e.Type, e.OrderNo, e.UpstreamTradeNo, e.Amount, e.BuyerId));
        Assert.Equal(new DateTime(2026, 11, 27, 4, 0, 0, DateTimeKind.Utc), e.PaidAt);

        var tampered = await provider.ParseNotifyAsync(Notify(config, body.Replace("TRANSACTION.SUCCESS", "TRANSACTION.SUCCESS "), headers), default);
        Assert.False(tampered.Verified);

        headers["Wechatpay-Timestamp"] = (long.Parse(ts) - 3600).ToString();
        Assert.False((await provider.ParseNotifyAsync(Notify(config, body, headers), default)).Verified);
    }

    [Fact]
    public void Wechat_request_authorization_is_verifiable_with_merchant_public_key()
    {
        var (privatePem, publicPem, _) = NewRsa();
        var auth = WechatProvider.BuildAuthorization("1900000001", "SERIAL", privatePem, "POST", "/v3/pay/transactions/app", "{}", "1700000000", "nonce");
        var signature = auth.Split("signature=\"")[1].Split('"')[0];
        Assert.True(KeyUtil.RsaVerify(publicPem, "POST\n/v3/pay/transactions/app\n1700000000\nnonce\n{}\n", signature, HashAlgorithmName.SHA256));
        Assert.Contains("mchid=\"1900000001\"", auth);
        Assert.Contains("serial_no=\"SERIAL\"", auth);
    }

    // ---------------------------------------------------------------- 支付宝

    [Fact]
    public async Task Alipay_app_order_string_is_signed_and_notify_is_verified()
    {
        var (appPrivate, appPublic, _) = NewRsa();
        var (_, alipayPublic, alipayKey) = NewRsa();
        var config = Config(("appId", "2021000000000001"), ("privateKey", appPrivate), ("alipayPublicKey", alipayPublic));
        var provider = new AlipayProvider(Http);

        var credential = await provider.CreateAsync(Create(config, "alipay_app"), default);
        Assert.Equal(CredentialTypes.OrderString, credential.Type);
        var pairs = ((string)credential.Data["orderString"]!).Split('&')
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("alipay.trade.app.pay", pairs["method"]);
        Assert.Contains("\"total_amount\":\"6.00\"", pairs["biz_content"]);
        var content = AlipayProvider.SignContent(new SortedDictionary<string, string>(pairs, StringComparer.Ordinal));
        Assert.True(KeyUtil.RsaVerify(appPublic, content, pairs["sign"], HashAlgorithmName.SHA256));

        var form = new Dictionary<string, string>
        {
            ["app_id"] = "2021000000000001", ["out_trade_no"] = "P1", ["trade_no"] = "2026112722001", ["trade_status"] = "TRADE_SUCCESS",
            ["total_amount"] = "6.00", ["gmt_payment"] = "2026-11-27 12:00:00", ["buyer_id"] = "2088001", ["sign_type"] = "RSA2",
        };
        var signContent = string.Join('&', form.Where(kv => kv.Key != "sign_type").OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        form["sign"] = Convert.ToBase64String(alipayKey.SignData(Encoding.UTF8.GetBytes(signContent), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var ok = await provider.ParseNotifyAsync(Notify(config, form: form), default);
        var e = Assert.Single(ok.Events);
        Assert.Equal(("P1", "2026112722001", 600L), (e.OrderNo, e.UpstreamTradeNo, e.Amount));

        form["total_amount"] = "0.01";
        Assert.False((await provider.ParseNotifyAsync(Notify(config, form: form), default)).Verified);
    }

    // ---------------------------------------------------------------- 抖音

    [Fact]
    public async Task Douyin_signatures_follow_documented_rules()
    {
        // 请求签名：值（不含 app_id / sign）+ SALT 排序后以 & 连接取 MD5
        var body = new Dictionary<string, object> { ["app_id"] = "tt1", ["out_order_no"] = "P1", ["total_amount"] = 600L, ["subject"] = "s", ["body"] = "b", ["valid_time"] = 1800L };
        var expected = KeyUtil.Md5Hex(string.Join('&', new[] { "P1", "600", "s", "b", "1800", "SALT" }.Order(StringComparer.Ordinal)));
        Assert.Equal(expected, DouyinProvider.RequestSign(body, "SALT"));

        var config = Config(("appId", "tt1"), ("salt", "SALT"), ("token", "TOKEN"));
        var msg = JsonSerializer.Serialize(new { appid = "tt1", cp_orderno = "P1", total_amount = 600, status = "SUCCESS", order_id = "N7000", paid_at = 1795752000 });
        var sorted = new[] { "TOKEN", "1795752000", "797", msg };
        Array.Sort(sorted, StringComparer.Ordinal);
        var signature = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(string.Concat(sorted))));
        var callback = JsonSerializer.Serialize(new { timestamp = 1795752000, nonce = "797", msg, type = "payment", msg_signature = signature });

        var provider = new DouyinProvider(Http);
        var ok = await provider.ParseNotifyAsync(Notify(config, callback), default);
        var e = Assert.Single(ok.Events);
        Assert.Equal(("P1", "N7000", 600L), (e.OrderNo, e.UpstreamTradeNo, e.Amount));

        Assert.False((await provider.ParseNotifyAsync(Notify(config, callback.Replace("797", "798")), default)).Verified);
    }

    // ---------------------------------------------------------------- OPPO / vivo / 小米

    [Fact]
    public async Task Oppo_notify_uses_fixed_order_sha1_rsa()
    {
        var (_, publicPem, key) = NewRsa();
        var config = Config(("publicKey", publicPem));
        var form = new Dictionary<string, string>
        {
            ["notifyId"] = "GC2026", ["partnerOrder"] = "P1", ["productName"] = "金币", ["productDesc"] = "60 金币",
            ["price"] = "600", ["count"] = "1", ["attach"] = "P1", ["userId"] = "u9",
        };
        var baseString = OppoProvider.BaseString("GC2026", "P1", "金币", "60 金币", "600", "1", "P1");
        form["sign"] = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(baseString), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));

        var provider = new OppoProvider(Http);
        var e = Assert.Single((await provider.ParseNotifyAsync(Notify(config, form: form), default)).Events);
        Assert.Equal(("P1", "GC2026", 600L), (e.OrderNo, e.UpstreamTradeNo, e.Amount));
        Assert.Equal("result=OK&resultMsg=成功", provider.BuildReply(NotifyOutcome.Success, "").Body);

        form["price"] = "1";
        Assert.False((await provider.ParseNotifyAsync(Notify(config, form: form), default)).Verified);

        // 官方统一公钥必须是可加载的 RSA 公钥
        using var official = KeyUtil.LoadRsaPublicKey(OppoProvider.DefaultPublicKey);
        Assert.Equal(1024, official.KeySize);
    }

    [Fact]
    public async Task Vivo_signature_and_notify_round_trip()
    {
        var config = Config(("appId", "100"), ("cpId", "cp1"), ("appKey", "KEY"));
        var provider = new VivoProvider(Http);

        var credential = await provider.CreateAsync(Create(config, "vivo_sdk"), default);
        var signed = credential.Data.Where(kv => kv.Key != "vivoSignature").ToDictionary(kv => kv.Key, kv => (string)kv.Value!);
        var manual = string.Join('&', signed.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")) + "&" + KeyUtil.Md5Hex("KEY");
        Assert.Equal(KeyUtil.Md5Hex(manual), credential.Data["vivoSignature"]);
        Assert.Equal("600", credential.Data["orderAmount"]);

        var form = new Dictionary<string, string>
        {
            ["respCode"] = "200", ["respMsg"] = "交易成功", ["signMethod"] = "MD5", ["tradeType"] = "01", ["tradeStatus"] = "0000",
            ["cpId"] = "cp1", ["appId"] = "100", ["uid"] = "u1", ["cpOrderNumber"] = "P1", ["orderNumber"] = "V2026",
            ["orderAmount"] = "600", ["extInfo"] = "", ["payTime"] = "20261127120000",
        };
        form["signature"] = VivoProvider.Sign(form, "KEY").ToUpperInvariant();
        var e = Assert.Single((await provider.ParseNotifyAsync(Notify(config, form: form), default)).Events);
        Assert.Equal(("P1", "V2026", 600L), (e.OrderNo, e.UpstreamTradeNo, e.Amount));

        form["orderAmount"] = "1";
        Assert.False((await provider.ParseNotifyAsync(Notify(config, form: form), default)).Verified);
    }

    [Fact]
    public async Task Xiaomi_notify_hmac_sha1_and_error_codes()
    {
        var config = Config(("appId", "2882"), ("appSecret", "SECRET"));
        var query = new Dictionary<string, string>
        {
            ["appId"] = "2882", ["cpOrderId"] = "P1", ["cpUserInfo"] = "P1", ["uid"] = "100010", ["orderId"] = "2026MI",
            ["orderStatus"] = "TRADE_SUCCESS", ["payFee"] = "500", ["partnerGiftConsume"] = "100", ["productCode"] = "coin_60",
            ["productName"] = "金币", ["productCount"] = "1", ["payTime"] = "2026-11-27 12:00:00",
        };
        var content = string.Join('&', query.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        query["signature"] = Convert.ToHexStringLower(HMACSHA1.HashData("SECRET"u8, Encoding.UTF8.GetBytes(content)));

        var provider = new XiaomiProvider(Http);
        var e = Assert.Single((await provider.ParseNotifyAsync(Notify(config, query: query, method: "GET"), default)).Events);
        Assert.Equal(("P1", "2026MI", 600L, "100010"), (e.OrderNo, e.UpstreamTradeNo, e.Amount, e.BuyerId));

        Assert.Contains("\"errcode\":200", provider.BuildReply(NotifyOutcome.Success, "").Body);
        Assert.Contains("\"errcode\":1525", provider.BuildReply(NotifyOutcome.SignError, "x").Body);
        Assert.Contains("\"errcode\":1506", provider.BuildReply(NotifyOutcome.OrderNotFound, "x").Body);

        query["payFee"] = "1";
        Assert.False((await provider.ParseNotifyAsync(Notify(config, query: query, method: "GET"), default)).Verified);
    }

    // ---------------------------------------------------------------- 华为 / 荣耀

    [Theory]
    [InlineData("SHA256WithRSA")]
    [InlineData("SHA256WithRSA/PSS")]
    public void Huawei_purchase_data_signature_and_fields(string algorithm)
    {
        var (_, publicPem, key) = NewRsa();
        var data = JsonSerializer.Serialize(new
        {
            orderId = "HW001", productId = "coin_60", purchaseState = 0, developerPayload = "P1",
            price = 600, currency = "CNY", purchaseTime = 1795752000000,
        });
        var padding = algorithm.Contains("PSS") ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(data), HashAlgorithmName.SHA256, padding));

        var q = HuaweiProvider.ParsePurchaseData(publicPem, data, signature, algorithm);
        Assert.Equal((UpstreamState.Paid, "HW001", "P1", "coin_60", 600L), (q.State, q.UpstreamTradeNo, q.BoundOrderNo, q.ProductId, q.Amount));

        Assert.Throws<ProviderException>(() => HuaweiProvider.ParsePurchaseData(publicPem, data.Replace("P1", "P2"), signature, algorithm));
    }

    [Fact]
    public void Honor_purchase_data_prefers_biz_order_no_and_maps_refund()
    {
        var (_, publicPem, key) = NewRsa();
        string Sign(string s) => Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(s), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var paid = JsonSerializer.Serialize(new { orderId = "HN001", bizOrderNo = "P1", developerPayload = "other", productId = "coin_60", purchaseState = 0, price = "6.00" });
        var q = HonorProvider.ParsePurchaseData(publicPem, paid, Sign(paid));
        Assert.Equal((UpstreamState.Paid, "HN001", "P1"), (q.State, q.UpstreamTradeNo, q.BoundOrderNo));
        Assert.Null(q.Amount);

        var refunded = JsonSerializer.Serialize(new { orderId = "HN001", developerPayload = "P1", purchaseState = 1 });
        Assert.Equal(UpstreamState.Refunded, HonorProvider.ParsePurchaseData(publicPem, refunded, Sign(refunded)).State);
        Assert.Throws<ProviderException>(() => HonorProvider.ParsePurchaseData(publicPem, paid, Sign(refunded)));
    }

    // ---------------------------------------------------------------- 苹果

    [Theory]
    [InlineData("P261127120000001000001")]
    [InlineData("P1")]
    [InlineData("R2611271200000010000019999999")]
    public void Apple_account_token_is_a_reversible_uuid(string orderNo)
    {
        var token = AppleToken.Encode(orderNo);
        Assert.True(Guid.TryParseExact(token, "D", out _));
        Assert.Equal('4', token[14]);
        Assert.Equal(orderNo, AppleToken.Decode(token));
        Assert.Equal(orderNo, AppleToken.Decode(token.ToUpperInvariant()));
    }

    [Fact]
    public void Apple_account_token_rejects_foreign_uuids()
    {
        Assert.Null(AppleToken.Decode(Guid.NewGuid().ToString().Replace('4', '5')));
        Assert.Null(AppleToken.Decode("not-a-uuid"));
    }

    [Fact]
    public void Apple_root_certificate_matches_published_fingerprint()
    {
        var der = Convert.FromBase64String(AppleJws.RootCaG3);
        Assert.Equal(AppleJws.RootCaG3Sha256, Convert.ToHexString(SHA256.HashData(der)));
        using var cert = X509CertificateLoader.LoadCertificate(der);
        Assert.Contains("Apple Root CA - G3", cert.Subject);
    }

    [Fact]
    public void Apple_jws_signed_by_untrusted_chain_is_rejected()
    {
        // 自建一条"根 → 中间 → 叶子"证书链签发 JWS：签名本身有效，但不锚定 Apple 根证书，必须被拒绝
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootReq = new CertificateRequest("CN=Fake Root", rootKey, HashAlgorithmName.SHA256);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafReq = new CertificateRequest("CN=Fake Leaf", leafKey, HashAlgorithmName.SHA256);
        using var leaf = leafReq.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), [1, 2, 3, 4]);

        var header = KeyUtil.Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            alg = "ES256",
            x5c = new[] { Convert.ToBase64String(leaf.RawData), Convert.ToBase64String(root.RawData), Convert.ToBase64String(root.RawData) },
        })));
        var payload = KeyUtil.Base64Url(Encoding.UTF8.GetBytes("{\"transactionId\":\"1\",\"bundleId\":\"com.example\"}"));
        var signature = KeyUtil.Base64Url(leafKey.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256));
        var jws = $"{header}.{payload}.{signature}";

        Assert.Equal("1", AppleJws.DecodePayloadUnverified(jws).GetProperty("transactionId").GetString());
        Assert.Throws<ProviderException>(() => AppleJws.VerifyAndDecode(jws));
        Assert.Throws<ProviderException>(() => AppleJws.VerifyAndDecode("a.b"));
    }

    [Fact]
    public void Apple_api_token_is_es256_jwt_with_required_claims()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var config = Config(("bundleId", "com.example.app"), ("issuerId", "ISSUER"), ("keyId", "KEY1"), ("privateKey", key.ExportPkcs8PrivateKeyPem()));
        var parts = AppleProvider.BuildApiToken(config).Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), KeyUtil.FromBase64Url(parts[2]), HashAlgorithmName.SHA256));
        var header = JsonDocument.Parse(KeyUtil.FromBase64Url(parts[0])).RootElement;
        var claims = JsonDocument.Parse(KeyUtil.FromBase64Url(parts[1])).RootElement;
        Assert.Equal(("ES256", "KEY1"), (header.GetProperty("alg").GetString(), header.GetProperty("kid").GetString()));
        Assert.Equal(("ISSUER", "appstoreconnect-v1", "com.example.app"),
            (claims.GetProperty("iss").GetString(), claims.GetProperty("aud").GetString(), claims.GetProperty("bid").GetString()));
        Assert.InRange(claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64(), 1, 3600);
    }

    // ---------------------------------------------------------------- 通用

    [Fact]
    public async Task Mock_callback_round_trip_and_tamper_detection()
    {
        var config = Config(("secret", "s3cr3t"));
        var provider = new MockProvider(Http);
        var credential = await provider.CreateAsync(Create(config, "mock", "P9"), default);
        var body = (string)credential.Data["body"]!;

        var e = Assert.Single((await provider.ParseNotifyAsync(Notify(config, body), default)).Events);
        Assert.Equal(("P9", 600L), (e.OrderNo, e.Amount));
        Assert.False((await provider.ParseNotifyAsync(Notify(config, body.Replace("600", "1")), default)).Verified);
        Assert.False((await provider.ParseNotifyAsync(Notify(Config(("secret", "other")), body), default)).Verified);

        await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(Create(Config(("secret", "s"), ("failCreate", "true")), "mock"), default));
    }

    [Fact]
    public void Every_provider_declares_consistent_metadata()
    {
        IPayProvider[] all =
        [
            new WechatProvider(Http), new AlipayProvider(Http), new DouyinProvider(Http), new HuaweiProvider(Http), new HonorProvider(Http),
            new OppoProvider(Http), new VivoProvider(Http), new XiaomiProvider(Http), new AppleProvider(Http), new MockProvider(Http),
        ];
        Assert.Equal(all.Length, all.Select(p => p.Meta.Code).Distinct().Count());
        var methods = all.SelectMany(p => p.Meta.Methods.Select(m => m.Code)).ToList();
        Assert.Equal(methods.Count, methods.Distinct().Count());
        Assert.All(all, p =>
        {
            Assert.NotEmpty(p.Meta.Methods);
            Assert.All(p.Meta.Methods, m => Assert.StartsWith(p.Meta.Code, m.Code));
            Assert.Equal(p.Meta.ConfigFields.Count, p.Meta.ConfigFields.Select(f => f.Key).Distinct().Count());
            // 缺少必填项时必须给出明确提示，而不是抛异常
            if (p.Meta.ConfigFields.Any(f => f.Required)) Assert.NotNull(p.ValidateConfig(Config()));
        });
        foreach (var code in new[] { "wechat", "alipay", "douyin", "huawei", "oppo", "vivo", "xiaomi", "honor", "apple" })
            Assert.Contains(all, p => p.Meta.Code == code);
    }
}
