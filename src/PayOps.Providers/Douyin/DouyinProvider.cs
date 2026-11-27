using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Douyin;

/// <summary>
/// 抖音支付（商家平台 pay.douyinpay.com，直连商户）：App 支付、H5 支付、Native（PC 扫码）支付，
/// 以及查单、关单、退款、支付/退款结果通知、交易账单。
/// 协议与微信支付 APIv3 同构但细节不同：认证方案 DouyinPay-RSA、头部前缀 Douyinpay-、
/// 验签只有平台证书模式、关单成功为 200、退款查询必须带 mchid、二维码仅 2 分钟有效等。
/// </summary>
public sealed class DouyinProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    private const string Host = "https://api.douyinpay.com";
    private const string Scheme = "DouyinPay-RSA";
    private static readonly ConcurrentDictionary<string, (Dictionary<string, string> Certs, DateTime ExpireAt)> PlatformCerts = new();
    private static readonly ConcurrentDictionary<string, DateTime> ForcedRefresh = new();

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "douyin",
        Name = "抖音支付",
        Category = "direct",
        Methods =
        [
            new("douyin_app", "抖音APP支付"),
            new("douyin_h5", "抖音H5支付"),
            new("douyin_pc", "抖音PC扫码支付(Native)"),
        ],
        Capabilities = new() { Query = true, Close = true, Refund = true, Bill = true },
        ConfigFields =
        [
            new() { Key = "appId", Label = "应用AppID", Required = true, Help = "与商户号绑定的抖音开放平台应用：APP 支付填移动应用 AppID；H5 / PC 扫码填网站应用 AppID。两类应用请各建一个通道" },
            new() { Key = "mchId", Label = "直连商户号", Required = true },
            new() { Key = "serialNo", Label = "商户API证书序列号", Required = true, Help = "商家平台【产品中心 → 密钥管理 → 接口加签证书】中商户证书的序列号" },
            new() { Key = "privateKey", Label = "商户API私钥", Type = "textarea", Required = true, Secret = true, Help = "PKCS#8 / PKCS#1，PEM 或裸 Base64" },
            new() { Key = "apiKey", Label = "接口加密密钥", Type = "password", Required = true, Secret = true, Help = "32 位对称密钥，用于回调与平台证书解密；未设置时抖音可能不发送回调" },
            new() { Key = "platformCert", Label = "抖音支付平台证书", Type = "textarea", Help = "PEM 格式，用于回调验签。留空则自动调用平台证书下载接口获取并缓存（证书轮换时自动跟随）" },
        ],
        NotifyNote = "回调地址在下单/退款时自动携带（须 HTTPS 且不带参数）。H5 支付要求发起跳转的页面域名已配置为 H5 支付域名；PC 扫码二维码 2 分钟内有效，过期请用相同参数重新下单刷新。",
    };

    protected override string? ValidateKeys(PassageConfig config)
    {
        using var pri = KeyUtil.LoadRsaPrivateKey(config.Require("privateKey"));
        if (config.Get("platformCert").Length > 0)
        {
            using var pub = KeyUtil.LoadRsaPublicKey(config.Get("platformCert"));
        }
        return Encoding.UTF8.GetByteCount(config.Require("apiKey")) == 32 ? null : "接口加密密钥必须为 32 字节";
    }

    // ------------------------------------------------------------------ 下单

    public override async Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct)
    {
        var cfg = ctx.Config;
        var appId = cfg.Require("appId");
        var kind = ctx.Method switch
        {
            "douyin_app" => "app",
            "douyin_h5" => "h5",
            "douyin_pc" => "native",
            _ => throw BadRequest($"抖音支付不支持的支付方式：{ctx.Method}"),
        };

        var body = new Dictionary<string, object?>
        {
            ["appid"] = appId,
            ["mchid"] = cfg.Require("mchId"),
            ["description"] = Truncate(ctx.Subject, 127),
            ["out_trade_no"] = ctx.OrderNo,
            ["time_expire"] = TimeUtil.BeijingString(ctx.ExpireAt, "yyyy-MM-dd'T'HH:mm:ss") + "+08:00",
            ["notify_url"] = ctx.NotifyUrl,
            ["amount"] = new { total = ctx.Amount, currency = "CNY" },
        };
        if (ctx.Attach.Length > 0) body["attach"] = Truncate(ctx.Attach, 1024);
        if (kind == "h5")
        {
            body["scene_info"] = new
            {
                payer_client_ip = ctx.ClientIp.Length > 0 ? ctx.ClientIp : "127.0.0.1",
                h5_info = new { type = ctx.Extra.GetValueOrDefault("h5Type", "Wap") },
            };
        }
        else if (ctx.ClientIp.Length > 0)
        {
            body["scene_info"] = new { payer_client_ip = ctx.ClientIp };
        }

        var (status, text) = await CallAsync(cfg, HttpMethod.Post, $"/v1/trade/transactions/{kind}", JsonSerializer.Serialize(body), ct);
        var json = ParseJson(text, Meta.Name);
        if (status is < 200 or >= 300) throw Upstream(json.Str("code"), $"抖音支付下单失败：{json.Str("message")}");

        switch (kind)
        {
            case "native":
                return new PayCredential
                {
                    Type = CredentialTypes.QrCode,
                    // 二维码仅 2 分钟有效：过期后用相同 mchOrderNo 重新请求下单接口即可刷新
                    Data = { ["codeUrl"] = json.Str("code_url"), ["expiresInSeconds"] = 120 },
                };
            case "h5":
                var h5Url = json.Str("h5_url");
                if (ctx.ReturnUrl.Length > 0)
                    h5Url += (h5Url.Contains('?') ? "&" : "?") + "return_url=" + Uri.EscapeDataString(ctx.ReturnUrl);
                return new PayCredential { Type = CredentialTypes.RedirectUrl, Data = { ["url"] = h5Url, ["expiresInSeconds"] = 300 } };
        }

        var prepayId = json.Str("prepay_id");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        return new PayCredential
        {
            Type = CredentialTypes.AppParams,
            Data =
            {
                ["appid"] = appId, ["partnerid"] = cfg.Require("mchId"), ["prepayid"] = prepayId,
                ["package"] = "Sign=DYPay", ["noncestr"] = nonce, ["timestamp"] = ts,
                ["sign"] = AppSign(cfg.Require("privateKey"), appId, ts, nonce, prepayId),
            },
        };
    }

    /// <summary>App 调起支付签名：应用id\n时间戳\n随机串\n预支付会话ID\n，商户私钥 SHA256withRSA。</summary>
    public static string AppSign(string privateKey, string appId, string timestamp, string nonce, string prepayId) =>
        KeyUtil.RsaSign(privateKey, $"{appId}\n{timestamp}\n{nonce}\n{prepayId}\n", HashAlgorithmName.SHA256);

    // ------------------------------------------------------------------ 查单 / 关单 / 退款

    public override async Task<QueryResult> QueryAsync(OrderContext ctx, CancellationToken ct)
    {
        var path = $"/v1/trade/transactions/out-trade-no/{ctx.OrderNo}?mchid={ctx.Config.Require("mchId")}";
        var (status, text) = await CallAsync(ctx.Config, HttpMethod.Get, path, "", ct);
        var json = ParseJson(text, Meta.Name);
        if (status == 404) return new QueryResult { State = UpstreamState.NotPaid, Message = json.Str("message") };
        if (status is < 200 or >= 300) throw Upstream(json.Str("code"), $"抖音支付查单失败：{json.Str("message")}");
        return ToQueryResult(json);
    }

    private static QueryResult ToQueryResult(JsonElement json) => new()
    {
        State = json.Str("trade_state") switch
        {
            "SUCCESS" => UpstreamState.Paid,
            "REFUND" => UpstreamState.Refunded,
            "CLOSED" => UpstreamState.Closed,
            "PAYERROR" => UpstreamState.Failed,
            _ => UpstreamState.NotPaid,
        },
        UpstreamTradeNo = json.Str("transaction_id") is { Length: > 0 } t ? t : null,
        Amount = json.Obj("amount")?.Long("total"),
        PaidAt = DateTimeOffset.TryParse(json.Str("success_time"), out var paid) ? paid.UtcDateTime : null,
        BuyerId = json.Obj("payer")?.Str("openid") ?? "",
        Message = json.Str("trade_state_desc"),
    };

    public override async Task CloseAsync(OrderContext ctx, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { mchid = ctx.Config.Require("mchId") });
        var (status, text) = await CallAsync(ctx.Config, HttpMethod.Post, $"/v1/trade/transactions/out-trade-no/{ctx.OrderNo}/close", body, ct);
        if (status is >= 200 and < 300 or 404) return;
        var json = ParseJson(text, Meta.Name);
        throw Upstream(json.Str("code"), $"抖音支付关单失败：{json.Str("message")}");
    }

    public override async Task<RefundResult> RefundAsync(RefundContext ctx, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            mchid = ctx.Config.Require("mchId"),
            out_trade_no = ctx.OrderNo,
            out_refund_no = ctx.RefundNo,
            reason = Truncate(ctx.Reason.Length > 0 ? ctx.Reason : "退款", 80),
            notify_url = ctx.NotifyUrl,
            amount = new { refund = ctx.RefundAmount, total = ctx.OrderAmount, currency = "CNY" },
        });
        var (status, text) = await CallAsync(ctx.Config, HttpMethod.Post, "/v1/trade/refund/domestic/refunds", body, ct);
        var json = ParseJson(text, Meta.Name);
        if (status is < 200 or >= 300) throw Upstream(json.Str("code"), $"抖音支付退款失败：{json.Str("message")}");
        return ToRefundResult(json.Str("status"), json.Str("refund_id"));
    }

    public override async Task<RefundResult> QueryRefundAsync(RefundContext ctx, CancellationToken ct)
    {
        var path = $"/v1/trade/refund/domestic/refunds/{ctx.RefundNo}?mchid={ctx.Config.Require("mchId")}";
        var (status, text) = await CallAsync(ctx.Config, HttpMethod.Get, path, "", ct);
        var json = ParseJson(text, Meta.Name);
        if (status == 404) return new RefundResult { State = UpstreamRefundState.Processing, Message = json.Str("message") };
        if (status is < 200 or >= 300) throw Upstream(json.Str("code"), $"抖音支付退款查询失败：{json.Str("message")}");
        return ToRefundResult(json.Str("status"), json.Str("refund_id"));
    }

    private static RefundResult ToRefundResult(string status, string refundId) => new()
    {
        State = status switch
        {
            "SUCCESS" => UpstreamRefundState.Success,
            "CLOSED" or "ABNORMAL" => UpstreamRefundState.Failed,
            _ => UpstreamRefundState.Processing,
        },
        UpstreamRefundNo = refundId.Length > 0 ? refundId : null,
        Message = status,
    };

    // ------------------------------------------------------------------ 回调

    public override async Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var cfg = ctx.Config;
        string Header(string name) => ctx.Headers.TryGetValue(name, out var v) ? v : "";
        var timestamp = Header("Douyinpay-Timestamp");
        var nonce = Header("Douyinpay-Nonce");
        var signature = Header("Douyinpay-Signature");
        var serial = Header("Douyinpay-Serial");
        if (timestamp.Length == 0 || signature.Length == 0) return NotifyParseResult.Fail("缺少签名头");

        var message = VerifyMessage(timestamp, nonce, ctx.Body);
        if (!await VerifyAsync(cfg, serial, message, signature, ct)) return NotifyParseResult.Fail("验签失败");

        var root = ParseJson(ctx.Body, Meta.Name);
        var resource = root.Obj("resource");
        if (resource is null) return NotifyParseResult.Fail("缺少 resource");
        JsonElement data;
        try
        {
            data = ParseJson(Decrypt(cfg.Require("apiKey"), resource.Value.Str("nonce"), resource.Value.Str("associated_data"), resource.Value.Str("ciphertext")), Meta.Name);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return NotifyParseResult.Fail("回调解密失败，请检查接口加密密钥");
        }
        if (data.Str("mchid") != cfg.Require("mchId")) return NotifyParseResult.Fail("商户号不匹配");

        if (root.Str("event_type").StartsWith("REFUND.", StringComparison.Ordinal))
        {
            var refundStatus = data.Str("refund_status");
            if (refundStatus is not ("SUCCESS" or "CLOSED" or "ABNORMAL")) return NotifyParseResult.Ignore($"退款状态 {refundStatus}");
            return NotifyParseResult.Of(new NotifyEvent
            {
                Type = NotifyEventType.Refund,
                OrderNo = data.Str("out_trade_no"),
                RefundNo = data.Str("out_refund_no"),
                UpstreamRefundNo = data.Str("refund_id"),
                RefundAmount = data.Obj("amount")?.Long("refund"),
                RefundSuccess = refundStatus == "SUCCESS",
                Message = refundStatus,
            });
        }

        var q = ToQueryResult(data);
        if (q.State != UpstreamState.Paid) return NotifyParseResult.Ignore($"交易状态 {data.Str("trade_state")}");
        return NotifyParseResult.Of(new NotifyEvent
        {
            Type = NotifyEventType.Paid,
            OrderNo = data.Str("out_trade_no"),
            UpstreamTradeNo = q.UpstreamTradeNo,
            Amount = q.Amount,
            PaidAt = q.PaidAt,
            BuyerId = q.BuyerId,
        });
    }

    public override NotifyReply BuildReply(NotifyOutcome outcome, string message) =>
        outcome == NotifyOutcome.Success
            ? NotifyReply.Json("", 200)
            : NotifyReply.Json(JsonSerializer.Serialize(new { code = "FAIL", message = Truncate(message, 60) }), 500);

    /// <summary>应答/回调验签串：时间戳\n随机串\n报文主体\n。</summary>
    public static string VerifyMessage(string timestamp, string nonce, string body) => $"{timestamp}\n{nonce}\n{body}\n";

    private async Task<bool> VerifyAsync(PassageConfig cfg, string serial, string message, string signature, CancellationToken ct)
    {
        var configured = cfg.Get("platformCert");
        if (configured.Length > 0 && KeyUtil.RsaVerify(configured, message, signature, HashAlgorithmName.SHA256)) return true;

        // 未配置证书，或证书已轮换：按回调头里的序列号从平台证书接口获取（进程内缓存 12 小时）。
        // 序列号未知时才强制刷新，且每个商户号 10 分钟最多一次，避免伪造回调放大成对上游的请求洪水。
        try
        {
            var key = serial.ToUpperInvariant();
            var certs = await GetPlatformCertsAsync(cfg, force: false, ct);
            if (!certs.ContainsKey(key))
            {
                var mchId = cfg.Require("mchId");
                var now = DateTime.UtcNow;
                if (ForcedRefresh.TryGetValue(mchId, out var last) && now - last < TimeSpan.FromMinutes(10)) return false;
                ForcedRefresh[mchId] = now;
                certs = await GetPlatformCertsAsync(cfg, force: true, ct);
            }
            return certs.TryGetValue(key, out var pem) && KeyUtil.RsaVerify(pem, message, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
    private async Task<Dictionary<string, string>> GetPlatformCertsAsync(PassageConfig cfg, bool force, CancellationToken ct)
    {
        var mchId = cfg.Require("mchId");
        if (!force && PlatformCerts.TryGetValue(mchId, out var cached) && cached.ExpireAt > DateTime.UtcNow) return cached.Certs;

        var (status, text) = await CallAsync(cfg, HttpMethod.Get, "/v1/merchant/certificates/getPlatformCertificates", "", ct);
        var json = ParseJson(text, Meta.Name);
        if (status is < 200 or >= 300) throw Upstream(json.Str("code"), $"获取抖音支付平台证书失败：{json.Str("message")}");

        var certs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (json.TryGetProperty("certificates", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var enc = item.Obj("encrypt_certificate");
                if (enc is null) continue;
                try
                {
                    var plain = Decrypt(cfg.Require("apiKey"), enc.Value.Str("nonce"), enc.Value.Str("associated_data"), enc.Value.Str("cipher_text"));
                    // 明文应为 PEM 证书；若为裸 Base64(DER) 则补上 PEM 头尾
                    if (!plain.Contains("-----BEGIN", StringComparison.Ordinal))
                        plain = "-----BEGIN CERTIFICATE-----\n" + plain.Trim() + "\n-----END CERTIFICATE-----";
                    using var check = X509Certificate2.CreateFromPem(plain);
                    certs[item.Str("cert_no").ToUpperInvariant()] = plain;
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    // 单张证书解不开不影响其他证书
                }
            }
        }
        PlatformCerts[mchId] = (certs, DateTime.UtcNow.AddHours(12));
        return certs;
    }

    /// <summary>AEAD-AES-256-GCM 解密：密钥为 32 字节接口加密密钥，密文末 16 字节为认证标签，附加数据可为空。</summary>
    public static string Decrypt(string apiKey, string nonce, string associatedData, string ciphertext)
    {
        var raw = Convert.FromBase64String(ciphertext);
        var plain = new byte[raw.Length - 16];
        using var aes = new AesGcm(Encoding.UTF8.GetBytes(apiKey), 16);
        var aad = associatedData.Length > 0 ? Encoding.UTF8.GetBytes(associatedData) : null;
        aes.Decrypt(Encoding.UTF8.GetBytes(nonce), raw.AsSpan(0, raw.Length - 16), raw.AsSpan(raw.Length - 16), plain, aad);
        return Encoding.UTF8.GetString(plain);
    }

    // ------------------------------------------------------------------ 账单

    public override async Task<List<BillItem>> DownloadBillAsync(BillContext ctx, CancellationToken ct)
    {
        var cfg = ctx.Config;
        var path = $"/v1/bill/billapply?mchid={cfg.Require("mchId")}&bill_type=TRADE&bill_date={ctx.BillDate:yyyy-MM-dd}&tar_type=GZIP";
        var (status, text) = await CallAsync(cfg, HttpMethod.Get, path, "", ct);
        var json = ParseJson(text, Meta.Name);
        if (status is < 200 or >= 300)
        {
            // 当日无交易时不生成账单文件
            if (json.Str("code") == "NO_STATEMENT_EXIST") return [];
            throw Upstream(json.Str("code"), $"申请抖音支付交易账单失败：{json.Str("message")}");
        }

        var url = new Uri(json.Str("download_url"));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", Scheme + " " + BuildAuthorization(cfg, "GET", url.PathAndQuery, ""));
        byte[] bytes;
        try
        {
            using var resp = await Http.SendAsync(req, ct);
            bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw Upstream("BILL_DOWNLOAD", $"下载抖音支付账单失败：HTTP {(int)resp.StatusCode} {Truncate(Encoding.UTF8.GetString(bytes), 200)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new ProviderException("UPSTREAM_UNREACHABLE", $"下载抖音支付账单网络异常：{ex.Message}", inner: ex);
        }
        return ParseBill(BillCsv.Decode(BillCsv.Gunzip(bytes)));
    }

    /// <summary>解析交易账单明细（按表头名取列）。SUCCESS 为支付行，REFUND 为退款行；金额单位元。</summary>
    public static List<BillItem> ParseBill(string csv)
    {
        var items = new List<BillItem>();
        foreach (var row in BillCsv.Rows(csv, ["商户订单号", "交易状态", "订单金额"], stopFirstField: "总交易单数"))
        {
            switch (row["交易状态"])
            {
                case "SUCCESS":
                    items.Add(new BillItem
                    {
                        Kind = BillItemKind.Pay,
                        OrderNo = row["商户订单号"],
                        UpstreamTradeNo = row.GetValueOrDefault("抖音支付订单号", ""),
                        Amount = BillCsv.Fen(row["订单金额"]),
                    });
                    break;
                case "REFUND":
                    // 退款状态是出账时的快照：仅成功（或未给出状态）的退款参与对账
                    if (row.GetValueOrDefault("退款状态", "") is not ("" or "SUCCESS")) break;
                    items.Add(new BillItem
                    {
                        Kind = BillItemKind.Refund,
                        OrderNo = row["商户订单号"],
                        UpstreamTradeNo = row.GetValueOrDefault("抖音支付退款单号", ""),
                        RefundNo = row.GetValueOrDefault("商户退款单号", ""),
                        Amount = BillCsv.Fen(row.GetValueOrDefault("退款金额", "0")),
                    });
                    break;
            }
        }
        return items;
    }

    // ------------------------------------------------------------------ 请求签名

    /// <summary>请求签名串：方法\nURL(路径+查询)\n时间戳\n随机串\n报文主体\n，商户私钥 SHA256withRSA。</summary>
    public static string BuildAuthorization(string mchId, string serialNo, string privateKey, string method, string pathAndQuery, string body, string timestamp, string nonce)
    {
        var signature = KeyUtil.RsaSign(privateKey, $"{method}\n{pathAndQuery}\n{timestamp}\n{nonce}\n{body}\n", HashAlgorithmName.SHA256);
        return $"mchid=\"{mchId}\",nonce_str=\"{nonce}\",timestamp=\"{timestamp}\",serial_no=\"{serialNo}\",signature=\"{signature}\"";
    }

    private static string BuildAuthorization(PassageConfig cfg, string method, string pathAndQuery, string body) =>
        BuildAuthorization(cfg.Require("mchId"), cfg.Require("serialNo"), cfg.Require("privateKey"), method, pathAndQuery, body,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), Guid.NewGuid().ToString("N"));

    private async Task<(int Status, string Body)> CallAsync(PassageConfig cfg, HttpMethod method, string pathAndQuery, string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, Host + pathAndQuery);
        req.Headers.TryAddWithoutValidation("Authorization", Scheme + " " + BuildAuthorization(cfg, method.Method, pathAndQuery, body));
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "PayOps/1.0");
        // 文档要求 GET 也带 Content-Type: application/json
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var (status, text) = await SendAsync(req, ct);
        return (status, text.Length == 0 ? "{}" : text);
    }
}
