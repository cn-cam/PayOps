using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Alipay;
using PayOps.Providers.Common;
using PayOps.Providers.Douyin;
using PayOps.Providers.Honor;
using PayOps.Providers.Huawei;
using PayOps.Providers.Mock;
using PayOps.Providers.Wechat;

namespace PayOps.Tests;

/// <summary>抖音支付（APP/H5/PC）、订阅事件解析、对账单解析的单元测试。不访问真实上游。</summary>
public class SubscriptionAndBillTests
{
    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("测试不应访问网络");
    }

    private static readonly IHttpClientFactory Http = new NoHttp();

    private static PassageConfig Config(params (string Key, string Value)[] items) => new(items.ToDictionary(i => i.Key, i => i.Value));

    private static NotifyContext Notify(PassageConfig config, string body, Dictionary<string, string>? headers = null) => new()
    {
        PassageId = 1,
        Config = config,
        HttpMethod = "POST",
        Headers = new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase),
        Query = new Dictionary<string, string>(),
        Form = new Dictionary<string, string>(),
        Body = body,
    };

    // ---------------------------------------------------------------- 抖音支付

    private const string DouyinApiKey = "0123456789abcdef0123456789abcdef";

    private static string DouyinEncrypt(string plain, string nonce, string aad)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Encoding.UTF8.GetBytes(DouyinApiKey), 16);
        aes.Encrypt(Encoding.UTF8.GetBytes(nonce), data, cipher, tag, aad.Length > 0 ? Encoding.UTF8.GetBytes(aad) : null);
        return Convert.ToBase64String([.. cipher, .. tag]);
    }

    [Fact]
    public void Douyin_request_authorization_and_app_sign_follow_the_documented_layout()
    {
        using var rsa = RSA.Create(2048);
        var privatePem = rsa.ExportPkcs8PrivateKeyPem();
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();

        var auth = DouyinProvider.BuildAuthorization("6000000000000001", "41A48FEF2A9F04C5", privatePem, "GET",
            "/v1/trade/transactions/out-trade-no/P1?mchid=6000000000000001", "", "1669879970", "nonce");
        Assert.StartsWith("mchid=\"6000000000000001\",nonce_str=\"nonce\",timestamp=\"1669879970\",serial_no=\"41A48FEF2A9F04C5\",signature=\"", auth);
        var signature = auth.Split("signature=\"")[1].TrimEnd('"');
        // GET 请求的报文主体为空串，签名串以 "\n\n" 结尾
        Assert.True(KeyUtil.RsaVerify(publicPem, "GET\n/v1/trade/transactions/out-trade-no/P1?mchid=6000000000000001\n1669879970\nnonce\n\n", signature, HashAlgorithmName.SHA256));

        var appSign = DouyinProvider.AppSign(privatePem, "dy8888", "1670748766", "NONCE", "dy123");
        Assert.True(KeyUtil.RsaVerify(publicPem, "dy8888\n1670748766\nNONCE\ndy123\n", appSign, HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task Douyin_payment_and_refund_notifications_are_verified_and_decrypted()
    {
        using var platform = RSA.Create(2048);
        using var merchant = RSA.Create(2048);
        var config = Config(("appId", "awz9"), ("mchId", "6020230307605084"), ("serialNo", "S1"),
            ("privateKey", merchant.ExportPkcs8PrivateKeyPem()), ("apiKey", DouyinApiKey), ("platformCert", platform.ExportSubjectPublicKeyInfoPem()));
        var provider = new DouyinProvider(Http);
        Assert.Null(provider.ValidateConfig(config));

        Dictionary<string, string> Sign(string body) => new()
        {
            ["Douyinpay-Timestamp"] = "1669879970",
            ["Douyinpay-Nonce"] = "n1",
            ["Douyinpay-Serial"] = "763CC8F6EF3A8802",
            ["Douyinpay-Signature"] = Convert.ToBase64String(platform.SignData(
                Encoding.UTF8.GetBytes(DouyinProvider.VerifyMessage("1669879970", "n1", body)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
        };

        // 支付成功：附加数据为空串（抖音的回调允许不带 AAD）
        var transaction = JsonSerializer.Serialize(new
        {
            mchid = "6020230307605084", appid = "awz9", out_trade_no = "P1", transaction_id = "TP2022113021504111301402044101",
            trade_type = "APP", trade_state = "SUCCESS", success_time = "2022-11-30T21:51:32+08:00",
            payer = new { openid = "0a74ba22" }, amount = new { total = 10000, payer_total = 9500, currency = "CNY" },
        });
        var payBody = JsonSerializer.Serialize(new
        {
            id = "DY-1", event_type = "TRANSACTION.SUCCESS", resource_type = "encrypt-resource",
            resource = new { original_type = "transaction", algorithm = "AEAD-AES-256-GCM", ciphertext = DouyinEncrypt(transaction, "ippNsxmNlvJm", ""), associated_data = "", nonce = "ippNsxmNlvJm" },
        });
        var paid = await provider.ParseNotifyAsync(Notify(config, payBody, Sign(payBody)), default);
        var e = Assert.Single(paid.Events);
        Assert.Equal((NotifyEventType.Paid, "P1", "TP2022113021504111301402044101", 10000L, "0a74ba22"), (e.Type, e.OrderNo, e.UpstreamTradeNo, e.Amount, e.BuyerId));
        Assert.Equal(new DateTime(2022, 11, 30, 13, 51, 32, DateTimeKind.Utc), e.PaidAt);

        // 退款成功：带附加数据
        var refund = JsonSerializer.Serialize(new
        {
            mchid = "6020230307605084", out_trade_no = "P1", out_refund_no = "R1", refund_id = "TP-REFUND-1", refund_status = "SUCCESS",
            amount = new { total = 10000, refund = 300 },
        });
        var refundBody = JsonSerializer.Serialize(new
        {
            event_type = "REFUND.SUCCESS",
            resource = new { original_type = "refund", algorithm = "AEAD-AES-256-GCM", ciphertext = DouyinEncrypt(refund, "abcdefghijkl", "refund"), associated_data = "refund", nonce = "abcdefghijkl" },
        });
        var refunded = Assert.Single((await provider.ParseNotifyAsync(Notify(config, refundBody, Sign(refundBody)), default)).Events);
        Assert.Equal((NotifyEventType.Refund, "R1", "TP-REFUND-1", 300L, true), (refunded.Type, refunded.RefundNo, refunded.UpstreamRefundNo, refunded.RefundAmount, refunded.RefundSuccess));

        // 报文被改动、或用其他商户号的数据：一律拒绝
        Assert.False((await provider.ParseNotifyAsync(Notify(config, payBody + " ", Sign(payBody)), default)).Verified);
        var other = Config(("appId", "awz9"), ("mchId", "another"), ("serialNo", "S1"), ("privateKey", merchant.ExportPkcs8PrivateKeyPem()),
            ("apiKey", DouyinApiKey), ("platformCert", platform.ExportSubjectPublicKeyInfoPem()));
        Assert.False((await provider.ParseNotifyAsync(Notify(other, payBody, Sign(payBody)), default)).Verified);

        Assert.Equal(200, provider.BuildReply(NotifyOutcome.Success, "").StatusCode);
        Assert.Contains("\"code\":\"FAIL\"", provider.BuildReply(NotifyOutcome.SignError, "sign error").Body);
    }

    [Fact]
    public void Douyin_bill_parses_official_sample_rows_and_stops_at_padded_summary()
    {
        const string csv =
            "交易时间,应用ID,商户号,设备号,抖音支付订单号,商户订单号,用户标识,交易类型,交易状态,付款银行,货币种类,订单应结金额,优惠券金额,抖音支付退款单号,商户退款单号,退款金额,优惠券退款金额,退款类型,退款状态,商品名称,附加数据,手续费,费率,订单金额,申请退款金额,费率备注,特约商户\r\n" +
            "`2024-06-05 15:54:02,`dyab8acb865bb11234,`6020240327624200,`3993887420188887,`TP2024060515535032263372943784,`20240605155465838617,`testxt08c,`JSAPI,`SUCCESS,`ABC_DEBIT,`CNY,`0.01,`0,`,`,`0,`0,`,`,`测试,`,`0,`,`0.01,`0,`,`6020240327000000\r\n" +
            "`2024-06-05 11:13:46,`dyab8acb865bb11234,`6020240327624200,`,`TP2024060511120313981941922298,`20240605111676203213,`,`JSAPI,`REFUND,`OTHERS,`CNY,`0,`0,`TP2024060511134237964021842298,`rf20240605111897191214,`0.1,`0,`ORIGINAL,`SUCCESS,`商户退款,`,`0,`0,`0,`0.1,`,\r\n" +
            "`2024-06-05 12:00:00,`dyab8acb865bb11234,`6020240327624200,`,`TP3,`P3,`,`APP,`REFUND,`OTHERS,`CNY,`0,`0,`TPR3,`R3,`5,`0,`ORIGINAL,`PROCESSING,`退款中,`,`0,`0,`0,`5,`,\r\n" +
            "总交易单数,应结订单总金额,退款总金额,优惠券退款总金额,手续费总金额,订单总金额,申请退款总金额,,,,,,,,,,,,,,,,,,,,\r\n" +
            "`56,`0.69,`0.01,`0,`0,`0.69,`0.01,,,,,,,,,,,,,,,,,,,,";

        var items = DouyinProvider.ParseBill(csv);
        Assert.Equal(2, items.Count);
        Assert.Equal((BillItemKind.Pay, "20240605155465838617", "TP2024060515535032263372943784", 1L), (items[0].Kind, items[0].OrderNo, items[0].UpstreamTradeNo, items[0].Amount));
        // 金额不一定是两位小数（0.1 元 = 10 分）；处理中的退款行不参与对账
        Assert.Equal((BillItemKind.Refund, "20240605111676203213", "rf20240605111897191214", 10L), (items[1].Kind, items[1].OrderNo, items[1].RefundNo, items[1].Amount));
    }

    // ---------------------------------------------------------------- 微信 / 支付宝账单

    [Fact]
    public void Wechat_bill_parses_all_type_rows_by_header_name()
    {
        const string csv =
            "\uFEFF交易时间,公众账号ID,商户号,特约商户号,设备号,微信订单号,商户订单号,用户标识,交易类型,交易状态,付款银行,货币种类,应结订单金额,代金券金额,微信退款单号,商户退款单号,退款金额,充值券退款金额,退款类型,退款状态,商品名称,商户数据包,手续费,费率,订单金额,申请退款金额,费率备注\n" +
            "`2026-11-26 10:00:00,`wx1,`1900000001,`0,`,`4200001,`P1,`oABC,`APP,`SUCCESS,`OTHERS,`CNY,`6.00,`0.00,`0,`0,`0.00,`0.00,`,`,`金币,`,`0.04,`0.60%,`6.00,`0.00,`\n" +
            "`2026-11-26 11:00:00,`wx1,`1900000001,`0,`,`4200001,`P1,`oABC,`APP,`REFUND,`OTHERS,`CNY,`0.00,`0.00,`5030001,`R1,`2.50,`0.00,`ORIGINAL,`SUCCESS,`金币,`,`-0.02,`0.60%,`0.00,`2.50,`\n" +
            "`2026-11-26 12:00:00,`wx1,`1900000001,`0,`,`4200002,`P2,`oABC,`MICROPAY,`REVOKED,`OTHERS,`CNY,`0.00,`0.00,`0,`0,`0.00,`0.00,`,`,`金币,`,`0.00,`0.60%,`1.00,`0.00,`\n" +
            "总交易单数,应结订单总金额,退款总金额,充值券退款总金额,手续费总金额,订单总金额,申请退款总金额\n" +
            "`3,`6.00,`2.50,`0.00,`0.02,`7.00,`2.50\n";

        var items = WechatProvider.ParseBill(csv);
        Assert.Equal(2, items.Count);
        Assert.Equal((BillItemKind.Pay, "P1", "4200001", 600L), (items[0].Kind, items[0].OrderNo, items[0].UpstreamTradeNo, items[0].Amount));
        Assert.Equal((BillItemKind.Refund, "P1", "R1", 250L), (items[1].Kind, items[1].OrderNo, items[1].RefundNo, items[1].Amount));
    }

    [Fact]
    public void Alipay_bill_is_extracted_from_gbk_zip_and_refund_rows_use_request_no()
    {
        const string detail =
            "#支付宝业务明细查询\r\n#账号：[2088000000000001]\r\n#起始日期：[2026年11月26日 00:00:00]   终止日期：[2026年11月27日 00:00:00]\r\n" +
            "#-----------------------------------------业务明细列表----------------------------------------\r\n" +
            "支付宝交易号,商户订单号,业务类型,商品名称,创建时间,完成时间,门店编号,门店名称,操作员,终端号,对方账户,订单金额（元）,商家实收（元）,支付宝红包（元）,集分宝（元）,支付宝优惠（元）,商家优惠（元）,券核销金额（元）,券名称,商家红包消费金额（元）,卡消费金额（元）,退款批次号/请求号,服务费（元）,分润（元）,备注\r\n" +
            "2026112622001\t,P1\t,交易\t,金币,2026-11-26 10:00:00,2026-11-26 10:00:05,,,,,*明(138****0000),6.00,6.00,0.00,0.00,0.00,0.00,0.00,,0.00,0.00,\t,-0.04,0.00,\r\n" +
            "2026112622001\t,P1\t,退款\t,金币,2026-11-26 10:00:00,2026-11-26 11:00:00,,,,,*明(138****0000),-2.50,-2.50,0.00,0.00,0.00,0.00,0.00,,0.00,0.00,R1\t,0.02,0.00,\r\n" +
            "#-----------------------------------------业务明细列表结束------------------------------------\r\n" +
            "#交易合计：1笔，商家实收共6.00元，商家优惠共0.00元\r\n#退款合计：1笔，商家实收退款共-2.50元，商家优惠退款共0.00元\r\n";

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true, gbk))
        {
            using (var s = zip.CreateEntry("2088000000000001_20261126_业务明细(汇总).csv").Open()) s.Write(gbk.GetBytes("#汇总\r\n"));
            using (var s = zip.CreateEntry("2088000000000001_20261126_业务明细.csv").Open()) s.Write(gbk.GetBytes(detail));
        }

        var items = AlipayProvider.ParseBill(AlipayProvider.ExtractDetailCsv(buffer.ToArray()));
        Assert.Equal(2, items.Count);
        Assert.Equal((BillItemKind.Pay, "P1", "2026112622001", 600L), (items[0].Kind, items[0].OrderNo, items[0].UpstreamTradeNo, items[0].Amount));
        // 退款行金额为负数，取绝对值；商户退款请求号在"退款批次号/请求号"列
        Assert.Equal((BillItemKind.Refund, "P1", "R1", 250L), (items[1].Kind, items[1].OrderNo, items[1].RefundNo, items[1].Amount));
    }

    [Fact]
    public void Bill_text_decoding_falls_back_to_gbk_and_gunzip_is_transparent()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbkBytes = Encoding.GetEncoding(936).GetBytes("交易时间,商户订单号");
        Assert.Equal("交易时间,商户订单号", BillCsv.Decode(gbkBytes));
        Assert.Equal("交易时间,商户订单号", BillCsv.Decode(Encoding.UTF8.GetBytes("交易时间,商户订单号")));

        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true)) gz.Write("abc"u8);
        Assert.Equal("abc"u8.ToArray(), BillCsv.Gunzip(buffer.ToArray()));
        Assert.Equal("abc"u8.ToArray(), BillCsv.Gunzip("abc"u8.ToArray()));
        Assert.Equal(["a,b", "c\"d", ""], BillCsv.SplitLine("\"a,b\",\"c\"\"d\","));
    }

    // ---------------------------------------------------------------- 订阅

    [Fact]
    public void Huawei_subscription_purchase_data_yields_subscription_info()
    {
        using var rsa = RSA.Create(2048);
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        string Sign(string s) => Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(s), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

        var active = JsonSerializer.Serialize(new
        {
            kind = 2, subscriptionId = "SUB-1", purchaseToken = "TOKEN-1", orderId = "HW-ORDER-2", lastOrderId = "HW-ORDER-2",
            productId = "vip_month", developerPayload = "P1", purchaseState = 0, subIsvalid = true, autoRenewing = true, renewStatus = 1,
            price = 1800, currency = "CNY", purchaseTime = 1795752000000, expirationDate = 1798344000000,
        });
        var q = HuaweiProvider.ParsePurchaseData(publicPem, active, Sign(active), "SHA256WithRSA/PSS");
        Assert.Equal((UpstreamState.Paid, "HW-ORDER-2", "P1"), (q.State, q.UpstreamTradeNo, q.BoundOrderNo));
        Assert.Null(q.Amount); // 订阅存在优惠价，不参与金额校验
        Assert.NotNull(q.Subscription);
        Assert.Equal(("SUB-1", "TOKEN-1", true, 1800L), (q.Subscription!.UpstreamSubId, q.Subscription.Token, q.Subscription.AutoRenew, q.Subscription.Amount));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1798344000000).UtcDateTime, q.Subscription.ExpiresAt);

        // 用户已停止续订但仍在有效期：仍有效，autoRenew=false
        var stopped = active.Replace("\"autoRenewing\":true", "\"autoRenewing\":false").Replace("\"renewStatus\":1", "\"renewStatus\":0");
        var s = HuaweiProvider.ParsePurchaseData(publicPem, stopped, Sign(stopped), "SHA256WithRSA/PSS");
        Assert.Equal((UpstreamState.Paid, false), (s.State, s.Subscription!.AutoRenew));

        // 已过期
        var expired = active.Replace("\"subIsvalid\":true", "\"subIsvalid\":false");
        Assert.Equal(UpstreamState.Closed, HuaweiProvider.ParsePurchaseData(publicPem, expired, Sign(expired), "SHA256WithRSA/PSS").State);
    }

    [Fact]
    public void Honor_subscription_info_uses_agreement_no_and_parses_offset_times()
    {
        using var rsa = RSA.Create(2048);
        var info = JsonSerializer.Serialize(new
        {
            orderId = "HN-1", bizOrderNo = "P1", productId = "vip_month", productType = 2, purchaseState = 0, purchaseToken = "TOKEN", payMoney = "18.00",
            subscription = new { subscriptionState = 1, agreementNo = "AG-1", subStartTime = "2026-11-27 12:00:00+0800", subEndTime = "2026-12-27 12:00:00+0800" },
        });
        var signature = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(info), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var q = HonorProvider.ParsePurchaseData(rsa.ExportSubjectPublicKeyInfoPem(), info, signature);
        Assert.Equal(("AG-1", "TOKEN", true, 1800L), (q.Subscription!.UpstreamSubId, q.Subscription.Token, q.Subscription.AutoRenew, q.Subscription.Amount));
        Assert.Equal(new DateTime(2026, 12, 27, 4, 0, 0, DateTimeKind.Utc), q.Subscription.ExpiresAt);

        Assert.Equal(new DateTime(2026, 11, 27, 4, 0, 0, DateTimeKind.Utc), HonorProvider.ParseTime("2026-11-27 12:00:00"));
        Assert.Null(HonorProvider.ParseTime(""));
    }

    [Fact]
    public async Task Mock_subscription_callbacks_cover_charge_cancel_resume_expire_revoke()
    {
        var config = Config(("secret", "s3cr3t"), ("subPeriodMinutes", "60"));
        var provider = new MockProvider(Http);

        var credential = await provider.CreateAsync(new CreateContext
        {
            PassageId = 1, Config = config, OrderNo = "P9", Method = "mock", Amount = 1800, Subject = "月卡",
            NotifyUrl = "https://x/api/notify/mock/1", ExpireAt = DateTime.UtcNow.AddMinutes(30), Subscribe = true,
        }, default);
        Assert.Equal(true, credential.Data["subscription"]);
        var first = Assert.Single((await provider.ParseNotifyAsync(Notify(config, (string)credential.Data["body"]!), default)).Events);
        Assert.Equal((NotifyEventType.Subscription, SubscriptionEventKind.Charged, "P9", "MOCKSUBP9", 1800L), (first.Type, first.SubKind, first.OrderNo, first.Subscription!.UpstreamSubId, first.Amount));
        Assert.InRange((first.Subscription.ExpiresAt!.Value - DateTime.UtcNow).TotalMinutes, 58, 61);

        foreach (var (name, kind) in new[]
                 {
                     (MockProvider.EventCancel, SubscriptionEventKind.AutoRenewOff), (MockProvider.EventResume, SubscriptionEventKind.AutoRenewOn),
                     (MockProvider.EventExpire, SubscriptionEventKind.Expired), (MockProvider.EventRevoke, SubscriptionEventKind.Revoked),
                 })
        {
            var body = MockProvider.BuildSubscriptionCallback("s3cr3t", name, "MOCKSUBP9", "", "", 0, 0);
            var e = Assert.Single((await provider.ParseNotifyAsync(Notify(config, body), default)).Events);
            Assert.Equal((NotifyEventType.Subscription, kind), (e.Type, e.SubKind));
            // 事件名参与签名：把退订改成续费必须验签失败
            Assert.False((await provider.ParseNotifyAsync(Notify(config, body.Replace($"\"{name}\"", "\"charged\"")), default)).Verified);
        }
    }

    [Fact]
    public async Task Mock_bill_echoes_local_items_and_can_inject_three_kinds_of_diff()
    {
        var provider = new MockProvider(Http);
        BillItem Pay(string no, long amount) => new() { Kind = BillItemKind.Pay, OrderNo = no, UpstreamTradeNo = "T" + no, Amount = amount };
        var local = new[] { Pay("P1", 100), Pay("P2", 200), Pay("P3", 300) };
        BillContext Ctx(PassageConfig c) => new() { PassageId = 1, Config = c, BillDate = new DateOnly(2026, 11, 26), LocalItems = local };

        var echo = await provider.DownloadBillAsync(Ctx(Config(("secret", "s"))), default);
        Assert.Equal(local.Select(i => (i.OrderNo, i.Amount)), echo.Select(i => (i.OrderNo, i.Amount)));

        var bad = await provider.DownloadBillAsync(Ctx(Config(("secret", "s"), ("billMismatch", "true"))), default);
        Assert.DoesNotContain(bad, i => i.OrderNo == "P1");                       // 本地有上游无
        Assert.Equal(201, bad.Single(i => i.OrderNo == "P2").Amount);             // 金额不一致
        Assert.Contains(bad, i => i.OrderNo.StartsWith("PUNKNOWN"));              // 上游有本地无
    }
}
