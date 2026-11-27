using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Alipay;
using PayOps.Providers.Common;
using PayOps.Providers.Douyin;
using PayOps.Providers.Stores;
using PayOps.Providers.Wechat;

namespace PayOps.Tests;

/// <summary>签约代扣（微信 v2 / 支付宝协议 / 抖音免密）、资金账单与手续费解析的单元测试。不访问真实上游。</summary>
public class DeductAndFundBillTests
{
    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("测试不应访问网络");
    }

    private static readonly IHttpClientFactory Http = new NoHttp();

    private static PassageConfig Config(params (string Key, string Value)[] items) => new(items.ToDictionary(i => i.Key, i => i.Value));

    private static NotifyContext Notify(PassageConfig config, string body) => new()
    {
        PassageId = 1,
        Config = config,
        HttpMethod = "POST",
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Query = new Dictionary<string, string>(),
        Form = new Dictionary<string, string>(),
        Body = body,
    };

    // ---------------------------------------------------------------- 微信 v2

    [Fact]
    public void Wechat_v2_signature_and_xml_round_trip()
    {
        var p = new Dictionary<string, string>
        {
            ["appid"] = "wx1", ["mch_id"] = "1900000001", ["nonce_str"] = "n", ["contract_id"] = "C1", ["version"] = "1.0",
        };
        var sign = WechatV2.Sign(p, "v2key");
        Assert.Equal(32, sign.Length);
        Assert.Equal(sign, sign.ToUpperInvariant());   // 微信 v2 签名为大写
        p["sign"] = sign;
        Assert.True(WechatV2.Verify(p, "v2key"));
        p["contract_id"] = "C2";
        Assert.False(WechatV2.Verify(p, "v2key"));
        Assert.True(WechatV2.Verify(p, "wrong") == false);

        var xml = WechatV2.ToXml(p);
        var parsed = WechatV2.ParseXml(xml);
        Assert.Equal("C2", parsed["contract_id"]);
        Assert.Equal(sign, parsed["sign"]);
        Assert.Contains("SUCCESS", WechatV2.SuccessReply());
    }

    [Fact]
    public async Task Wechat_v2_contract_and_deduct_notifications_are_parsed()
    {
        var cfg = Config(("appId", "wx1"), ("mchId", "1900000001"), ("serialNo", "S"), ("privateKey", "x"),
            ("apiV3Key", "0123456789abcdef0123456789abcdef"), ("platformPublicKey", "x"), ("apiV2Key", "v2key"), ("planId", "101"));
        var provider = new WechatProvider(Http);

        Dictionary<string, string> Signed(params (string K, string V)[] items)
        {
            var d = items.ToDictionary(i => i.K, i => i.V, StringComparer.Ordinal);
            d["sign"] = WechatV2.Sign(d, "v2key");
            return d;
        }

        // 签约通知
        var signXml = WechatV2.ToXml(Signed(("return_code", "SUCCESS"), ("result_code", "SUCCESS"), ("mch_id", "1900000001"),
            ("contract_code", "P1"), ("contract_id", "C1"), ("plan_id", "101"), ("openid", "oABC"), ("change_type", "ADD"),
            ("operate_time", "1795752000"), ("request_serial", "P1")));
        var signed = Assert.Single((await provider.ParseNotifyAsync(Notify(cfg, signXml), default)).Events);
        Assert.Equal((NotifyEventType.Agreement, "P1", "C1", true), (signed.Type, signed.OrderNo, signed.Agreement!.AgreementId, signed.Agreement.Signed));

        // 解约通知
        var deleteXml = WechatV2.ToXml(Signed(("return_code", "SUCCESS"), ("result_code", "SUCCESS"), ("mch_id", "1900000001"),
            ("contract_id", "C1"), ("change_type", "DELETE"), ("contract_termination_mode", "2")));
        var deleted = Assert.Single((await provider.ParseNotifyAsync(Notify(cfg, deleteXml), default)).Events);
        Assert.False(deleted.Agreement!.Signed);

        // 扣款结果通知
        var deductXml = WechatV2.ToXml(Signed(("return_code", "SUCCESS"), ("result_code", "SUCCESS"), ("mch_id", "1900000001"),
            ("appid", "wx1"), ("openid", "oABC"), ("trade_state", "SUCCESS"), ("total_fee", "1800"), ("transaction_id", "T1"),
            ("out_trade_no", "P2"), ("time_end", "20261127120000"), ("contract_id", "C1")));
        var deducted = Assert.Single((await provider.ParseNotifyAsync(Notify(cfg, deductXml), default)).Events);
        Assert.Equal((NotifyEventType.Subscription, SubscriptionEventKind.Charged, "T1", 1800L, "C1"),
            (deducted.Type, deducted.SubKind, deducted.UpstreamTradeNo, deducted.Amount, deducted.Subscription!.UpstreamSubId));

        // 篡改报文必须验签失败
        var tampered = deductXml.Replace("1800", "1");
        Assert.False((await provider.ParseNotifyAsync(Notify(cfg, tampered), default)).Verified);
    }

    [Fact]
    public void Wechat_fund_bill_splits_income_expense_refund_and_fee()
    {
        const string csv =
            "记账时间,微信支付业务单号,资金流水单号,业务名称,业务类型,收支类型,收支金额(元),账户结余(元),资金变更提交申请人,备注,业务凭证号\n" +
            "`2026-11-26 10:00:00,`4200001,`F1,`交易,`交易,`收入,`100.00,`100.00,`,`订单,`4200001\n" +
            "`2026-11-26 10:00:01,`4200001,`F2,`交易,`扣除交易手续费,`支出,`0.60,`99.40,`,`手续费,`\n" +
            "`2026-11-26 11:00:00,`4200001,`F3,`退款,`退款,`支出,`30.00,`69.40,`,`退款,`5030001\n" +
            "`2026-11-26 11:00:01,`4200001,`F4,`退款,`退还交易手续费,`收入,`0.18,`69.58,`,`退手续费,`\n" +
            "资金流水总笔数,收入笔数,收入金额,支出笔数,支出金额\n" +
            "`4,`2,`100.18,`2,`30.60\n";

        var summary = WechatProvider.ParseFundBill(csv);
        Assert.Equal((100_18L, 30_60L, 30_00L, 0_42L), (summary.Income, summary.Expense, summary.Refund, summary.Fee));
    }

    // ---------------------------------------------------------------- 支付宝

    [Fact]
    public async Task Alipay_agreement_sign_url_carries_required_params()
    {
        using var rsa = RSA.Create(2048);
        var cfg = Config(("appId", "2021000000000001"), ("privateKey", rsa.ExportPkcs8PrivateKeyPem()),
            ("alipayPublicKey", rsa.ExportSubjectPublicKeyInfoPem()), ("signScene", "INDUSTRY|DIGITAL_MEDIA"));
        var provider = new AlipayProvider(Http);
        var credential = await provider.CreateAsync(new CreateContext
        {
            PassageId = 1, Config = cfg, OrderNo = "P1", Method = "alipay_agreement", Amount = 1800, Subject = "会员",
            NotifyUrl = "https://x/api/notify/alipay/1", ExpireAt = DateTime.UtcNow.AddDays(1), SignAgreement = true,
        }, default);

        Assert.Equal(CredentialTypes.RedirectUrl, credential.Type);
        var pairs = ((string)credential.Data["url"]!).Split('?')[1].Split('&')
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("alipay.user.agreement.page.sign", pairs["method"]);
        using var biz = JsonDocument.Parse(pairs["biz_content"]);
        Assert.Equal("GENERAL_WITHHOLDING", biz.RootElement.GetProperty("product_code").GetString());
        Assert.Equal("GENERAL_WITHHOLDING_P", biz.RootElement.GetProperty("personal_product_code").GetString());
        Assert.Equal("INDUSTRY|DIGITAL_MEDIA", biz.RootElement.GetProperty("sign_scene").GetString());
        Assert.Equal("P1", biz.RootElement.GetProperty("external_agreement_no").GetString());
        Assert.Equal("18.00", biz.RootElement.GetProperty("period_rule_params").GetProperty("single_amount").GetString());
        var content = AlipayProvider.SignContent(new SortedDictionary<string, string>(pairs, StringComparer.Ordinal));
        Assert.True(KeyUtil.RsaVerify(rsa.ExportSubjectPublicKeyInfoPem(), content, pairs["sign"], HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task Alipay_agreement_notify_is_verified_and_mapped()
    {
        using var alipayKey = RSA.Create(2048);
        var cfg = Config(("appId", "2021000000000001"), ("privateKey", alipayKey.ExportPkcs8PrivateKeyPem()),
            ("alipayPublicKey", alipayKey.ExportSubjectPublicKeyInfoPem()));
        var form = new Dictionary<string, string>
        {
            ["notify_type"] = "dut_user_sign", ["notify_id"] = "1", ["app_id"] = "2021000000000001",
            ["agreement_no"] = "2021000000000001A1", ["external_agreement_no"] = "P1", ["status"] = "NORMAL",
            ["sign_time"] = "2026-11-27 12:00:00", ["alipay_open_id"] = "open1", ["sign_type"] = "RSA2",
        };
        var content = string.Join('&', form.Where(kv => kv.Key != "sign_type").OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        form["sign"] = Convert.ToBase64String(alipayKey.SignData(Encoding.UTF8.GetBytes(content), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var ctx = new NotifyContext
        {
            PassageId = 1, Config = cfg, HttpMethod = "POST", Headers = new Dictionary<string, string>(),
            Query = new Dictionary<string, string>(), Form = form, Body = "",
        };
        var e = Assert.Single((await new AlipayProvider(Http).ParseNotifyAsync(ctx, default)).Events);
        Assert.Equal((NotifyEventType.Agreement, "P1", "2021000000000001A1", "open1"), (e.Type, e.OrderNo, e.Agreement!.AgreementId, e.Agreement.PayerId));
        Assert.Equal(new DateTime(2026, 11, 27, 4, 0, 0, DateTimeKind.Utc), e.Agreement.SignedAt);
    }

    // ---------------------------------------------------------------- 抖音

    [Fact]
    public void Douyin_fund_bill_parses_income_expense_and_fee()
    {
        const string csv =
            "记账时间,商户编号,账户类型,余额类型,资金流水单号,商户订单号,抖音支付订单号,业务名称,业务类型,收支类型,收支金额（元）,账户余额（元）,资金变更提交申请人,备注\n" +
            "`2026-11-26 10:00:00,`6000000000000001,`基本户,`可用余额,`F1,`P1,`T1,`交易,`交易,`收入,`100,`100,`,`\n" +
            "`2026-11-26 10:00:01,`6000000000000001,`基本户,`可用余额,`F2,`P1,`T1,`交易,`扣除交易手续费,`支出,`0.6,`99.4,`,`\n" +
            "`2026-11-26 11:00:00,`6000000000000001,`基本户,`可用余额,`F3,`P1,`T1,`退款,`退款,`支出,`30,`69.4,`,`\n" +
            "资金流水总笔数,收入合计笔数,收入合计金额,支出合计笔数,支出合计金额\n" +
            "`3,`1,`100,`2,`30.6\n";

        var summary = DouyinProvider.ParseFundBill(csv);
        Assert.Equal((10_000L, 3_060L, 3_000L, 60L), (summary.Income, summary.Expense, summary.Refund, summary.Fee));
    }

    [Fact]
    public async Task Douyin_contract_notify_maps_to_agreement_event()
    {
        using var platform = RSA.Create(2048);
        using var merchant = RSA.Create(2048);
        const string apiKey = "0123456789abcdef0123456789abcdef";
        var cfg = Config(("appId", "aw1"), ("mchId", "6000000000000001"), ("serialNo", "S"),
            ("privateKey", merchant.ExportPkcs8PrivateKeyPem()), ("apiKey", apiKey), ("platformCert", platform.ExportSubjectPublicKeyInfoPem()));

        var contract = JsonSerializer.Serialize(new
        {
            mchid = "6000000000000001", out_contract_code = "P1", plan_id = "101", openid = "open1",
            change_type = "ADD", operate_time = 1795752000, contract_id = "C1", request_serial = "1795752000",
        });
        var nonce = "abcdefghijkl";
        var cipher = new byte[Encoding.UTF8.GetByteCount(contract)];
        var tag = new byte[16];
        using (var aes = new AesGcm(Encoding.UTF8.GetBytes(apiKey), 16))
            aes.Encrypt(Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(contract), cipher, tag);
        var body = JsonSerializer.Serialize(new
        {
            event_type = "CONTRACTADD.SUCCESS",
            resource = new { original_type = "contract", algorithm = "AEAD-AES-256-GCM", ciphertext = Convert.ToBase64String([.. cipher, .. tag]), associated_data = "", nonce },
        });
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = Convert.ToBase64String(platform.SignData(Encoding.UTF8.GetBytes($"{ts}\nn1\n{body}\n"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var ctx = new NotifyContext
        {
            PassageId = 1, Config = cfg, HttpMethod = "POST", Body = body,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Douyinpay-Timestamp"] = ts, ["Douyinpay-Nonce"] = "n1", ["Douyinpay-Signature"] = signature, ["Douyinpay-Serial"] = "763CC8F6EF3A8802",
            },
            Query = new Dictionary<string, string>(), Form = new Dictionary<string, string>(),
        };
        var e = Assert.Single((await new DouyinProvider(Http).ParseNotifyAsync(ctx, default)).Events);
        Assert.Equal((NotifyEventType.Agreement, "P1", "C1", true), (e.Type, e.OrderNo, e.Agreement!.AgreementId, e.Agreement.Signed));
    }

    private static Dictionary<string, string> ParseQuery(string query) => query.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2))
        .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    // ---------------------------------------------------------------- 小米订阅

    [Fact]
    public async Task Xiaomi_subscription_callbacks_and_deduct_are_recognised()
    {
        var cfg = Config(("appId", "2882"), ("appSecret", "SECRET"));
        var provider = new XiaomiProvider(Http);
        string Signed(params (string K, string V)[] items)
        {
            var d = items.ToDictionary(i => i.K, i => i.V);
            var content = string.Join('&', d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
            d["signature"] = Convert.ToHexStringLower(HMACSHA1.HashData("SECRET"u8, Encoding.UTF8.GetBytes(content)));
            return string.Join('&', d.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        }

        NotifyContext Ctx(string query) => new()
        {
            PassageId = 1, Config = cfg, HttpMethod = "GET", Body = "", Headers = new Dictionary<string, string>(),
            Form = new Dictionary<string, string>(), Query = ParseQuery(query),
        };

        // 签约回调
        var signQuery = Signed(("appId", "2882"), ("uid", "100010"), ("productCode", "vip_month"), ("subStatus", "SUBSCRIBE_PERIOD"));
        var signed = Assert.Single((await provider.ParseNotifyAsync(Ctx(signQuery), default)).Events);
        Assert.Equal((NotifyEventType.Subscription, SubscriptionEventKind.AutoRenewOn, "100010:vip_month"),
            (signed.Type, signed.SubKind, signed.Subscription!.UpstreamSubId));

        // 服务端代扣（deductType=1，没有 cpOrderId）
        var deductQuery = Signed(("appId", "2882"), ("uid", "100010"), ("orderId", "MI1"), ("orderStatus", "TRADE_SUCCESS"),
            ("payFee", "1800"), ("productCode", "vip_month"), ("payTime", "2026-11-27 12:00:00"), ("deductType", "1"));
        var renewal = Assert.Single((await provider.ParseNotifyAsync(Ctx(deductQuery), default)).Events);
        Assert.Equal((NotifyEventType.Subscription, SubscriptionEventKind.Charged, 1800L, ""),
            (renewal.Type, renewal.SubKind, renewal.Amount, renewal.OrderNo));

        // 用户主动支付（deductType=0，带 cpOrderId）：按普通支付处理，但同时带上订阅信息供首购建订阅
        var firstQuery = Signed(("appId", "2882"), ("uid", "100010"), ("cpOrderId", "P9"), ("orderId", "MI2"),
            ("orderStatus", "TRADE_SUCCESS"), ("payFee", "1800"), ("productCode", "vip_month"),
            ("payTime", "2026-11-27 12:00:00"), ("deductType", "0"));
        var first = Assert.Single((await provider.ParseNotifyAsync(Ctx(firstQuery), default)).Events);
        Assert.Equal((NotifyEventType.Paid, "P9", "100010:vip_month"), (first.Type, first.OrderNo, first.Subscription!.UpstreamSubId));

        Assert.Equal("100010:vip_month", XiaomiProvider.SubscriptionKey("100010", "vip_month"));
    }
}
