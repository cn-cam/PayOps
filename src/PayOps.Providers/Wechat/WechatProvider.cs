using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Wechat;

/// <summary>微信支付 APIv3（直连商户）：APP / JSAPI(公众号、小程序) / Native / H5。</summary>
public sealed class WechatProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    private const string Host = "https://api.mch.weixin.qq.com";

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "wechat",
        Name = "微信支付",
        Category = "direct",
        Methods =
        [
            new("wechat_app", "微信APP支付"),
            new("wechat_jsapi", "微信JSAPI/小程序支付"),
            new("wechat_native", "微信扫码支付"),
            new("wechat_h5", "微信H5支付"),
        ],
        Capabilities = new() { Query = true, Close = true, Refund = true, Bill = true },
        ConfigFields =
        [
            new() { Key = "appId", Label = "AppID", Required = true, Placeholder = "wx...", Help = "与商户号绑定的开放平台/公众号/小程序 AppID" },
            new() { Key = "mchId", Label = "商户号", Required = true },
            new() { Key = "serialNo", Label = "商户API证书序列号", Required = true },
            new() { Key = "privateKey", Label = "商户API私钥", Type = "textarea", Required = true, Secret = true, Help = "apiclient_key.pem 内容" },
            new() { Key = "apiV3Key", Label = "APIv3密钥", Type = "password", Required = true, Secret = true, Help = "32 位，用于回调解密" },
            new() { Key = "platformPublicKey", Label = "微信支付公钥/平台证书", Type = "textarea", Required = true, Help = "PEM 格式，用于回调验签；支持微信支付公钥或平台证书" },
            new() { Key = "platformSerial", Label = "公钥ID/平台证书序列号", Help = "使用微信支付公钥时填写 PUB_KEY_ID_ 开头的公钥ID；填写后会校验回调的 Wechatpay-Serial" },
        ],
        NotifyNote = "回调地址在下单时自动传给微信，无需在商户平台单独配置；需保证域名已备案且为 HTTPS。",
    };

    protected override string? ValidateKeys(PassageConfig config)
    {
        using var pri = KeyUtil.LoadRsaPrivateKey(config.Require("privateKey"));
        using var pub = KeyUtil.LoadRsaPublicKey(config.Require("platformPublicKey"));
        return Encoding.UTF8.GetByteCount(config.Require("apiV3Key")) == 32 ? null : "APIv3密钥必须为 32 字节";
    }

    public override async Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct)
    {
        var cfg = ctx.Config;
        var appId = cfg.Require("appId");
        var kind = ctx.Method switch
        {
            "wechat_app" => "app",
            "wechat_jsapi" => "jsapi",
            "wechat_native" => "native",
            "wechat_h5" => "h5",
            _ => throw BadRequest($"微信支付不支持的支付方式：{ctx.Method}"),
        };
        if (kind == "jsapi" && ctx.OpenId.Length == 0) throw BadRequest("JSAPI/小程序支付必须传 openId");

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
        if (ctx.Attach.Length > 0) body["attach"] = Truncate(ctx.Attach, 127);
        if (kind == "jsapi") body["payer"] = new { openid = ctx.OpenId };
        if (kind == "h5")
            body["scene_info"] = new { payer_client_ip = ctx.ClientIp.Length > 0 ? ctx.ClientIp : "127.0.0.1", h5_info = new { type = "Wap" } };

        var (status, text) = await CallAsync(cfg, HttpMethod.Post, $"/v3/pay/transactions/{kind}", JsonSerializer.Serialize(body), ct);
        var json = ParseJson(text, Meta.Name);
        if (status is < 200 or >= 300) throw Upstream(json.Str("code"), $"微信下单失败：{json.Str("message")}");

        switch (kind)
        {
            case "native":
                return new PayCredential { Type = CredentialTypes.QrCode, Data = { ["codeUrl"] = json.Str("code_url") } };
            case "h5":
                return new PayCredential { Type = CredentialTypes.RedirectUrl, Data = { ["url"] = json.Str("h5_url") } };
        }

        var prepayId = json.Str("prepay_id");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        if (kind == "app")
        {
            var sign = KeyUtil.RsaSign(cfg.Require("privateKey"), $"{appId}\n{ts}\n{nonce}\n{prepayId}\n", HashAlgorithmName.SHA256);
            return new PayCredential
            {
                Type = CredentialTypes.AppParams,
                Data =
                {
                    ["appid"] = appId, ["partnerid"] = cfg.Require("mchId"), ["prepayid"] = prepayId,
                    ["package"] = "Sign=WXPay", ["noncestr"] = nonce, ["timestamp"] = ts, ["sign"] = sign,
                },
            };
        }

        var package = "prepay_id=" + prepayId;
        var paySign = KeyUtil.RsaSign(cfg.Require("privateKey"), $"{appId}\n{ts}\n{nonce}\n{package}\n", HashAlgorithmName.SHA256);
        return new PayCredential
        {
            Type = CredentialTypes.JsapiParams,
            Data =
            {
                ["appId"] = appId, ["timeStamp"] = ts, ["nonceStr"] = nonce,
                ["package"] = package, ["signType"] = "RSA", ["paySign"] = paySign,
            },
        };
    }

    public override async Task<QueryResult> QueryAsync(OrderContext ctx, CancellationToken ct)
    {
        var path = $"/v3/pay/transactions/out-trade-no/{ctx.OrderNo}?mchid={ctx.Config.Require("mchId")}";
        var (status, text) = await CallAsync(ctx.Config, HttpMethod.Get, path, "", ct);
        var json = ParseJson(text, Meta.Name);
        if (status == 404) return new QueryResult { State = UpstreamState.NotPaid, Message = json.Str("message") };
        if (status is < 200 or >= 300) throw Upstream(json.Str("code"), $"微信查单失败：{json.Str("message")}");
        return ToQueryResult(json);
    }

    private static QueryResult ToQueryResult(JsonElement json)
    {
        var state = json.Str("trade_state") switch
        {
            "SUCCESS" => UpstreamState.Paid,
            "REFUND" => UpstreamState.Refunded,
            "CLOSED" or "REVOKED" => UpstreamState.Closed,
            "PAYERROR" => UpstreamState.Failed,
            _ => UpstreamState.NotPaid,
        };
        return new QueryResult
        {
            State = state,
            UpstreamTradeNo = json.Str("transaction_id") is { Length: > 0 } t ? t : null,
            Amount = json.Obj("amount")?.Long("total"),
            PaidAt = DateTimeOffset.TryParse(json.Str("success_time"), out var paid) ? paid.UtcDateTime : null,
            BuyerId = json.Obj("payer")?.Str("openid") ?? "",
            Message = json.Str("trade_state_desc"),
        };
    }

    public override async Task CloseAsync(OrderContext ctx, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { mchid = ctx.Config.Require("mchId") });
        var (status, text) = await CallAsync(ctx.Config, HttpMethod.Post, $"/v3/pay/transactions/out-trade-no/{ctx.OrderNo}/close", body, ct);
        if (status is >= 200 and < 300 or 404) return;
        var json = ParseJson(text, Meta.Name);
        throw Upstream(json.Str("code"), $"微信关单失败：{json.Str("message")}");
    }

    public override async Task<RefundResult> RefundAsync(RefundContext ctx, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            out_trade_no = ctx.OrderNo,
            out_refund_no = ctx.RefundNo,
            reason = Truncate(ctx.Reason.Length > 0 ? ctx.Reason : "退款", 80),
            notify_url = ctx.NotifyUrl,
            amount = new { refund = ctx.RefundAmount, total = ctx.OrderAmount, currency = "CNY" },
        });
        var (status, text) = await CallAsync(ctx.Config, HttpMethod.Post, "/v3/refund/domestic/refunds", body, ct);
        var json = ParseJson(text, Meta.Name);
        if (status is < 200 or >= 300) throw Upstream(json.Str("code"), $"微信退款失败：{json.Str("message")}");
        return ToRefundResult(json.Str("status"), json.Str("refund_id"));
    }

    public override async Task<RefundResult> QueryRefundAsync(RefundContext ctx, CancellationToken ct)
    {
        var (status, text) = await CallAsync(ctx.Config, HttpMethod.Get, $"/v3/refund/domestic/refunds/{ctx.RefundNo}", "", ct);
        var json = ParseJson(text, Meta.Name);
        if (status == 404) return new RefundResult { State = UpstreamRefundState.Processing, Message = json.Str("message") };
        if (status is < 200 or >= 300) throw Upstream(json.Str("code"), $"微信退款查询失败：{json.Str("message")}");
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

    public override Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var cfg = ctx.Config;
        var timestamp = Header(ctx, "Wechatpay-Timestamp");
        var nonce = Header(ctx, "Wechatpay-Nonce");
        var signature = Header(ctx, "Wechatpay-Signature");
        var serial = Header(ctx, "Wechatpay-Serial");
        if (timestamp.Length == 0 || signature.Length == 0)
            return Task.FromResult(NotifyParseResult.Fail("缺少签名头"));
        if (!long.TryParse(timestamp, out var ts) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300)
            return Task.FromResult(NotifyParseResult.Fail("回调时间戳超出允许范围"));
        var expectSerial = cfg.Get("platformSerial");
        if (expectSerial.Length > 0 && !expectSerial.Equals(serial, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(NotifyParseResult.Fail($"平台序列号不匹配：{serial}"));
        if (!KeyUtil.RsaVerify(cfg.Require("platformPublicKey"), $"{timestamp}\n{nonce}\n{ctx.Body}\n", signature, HashAlgorithmName.SHA256))
            return Task.FromResult(NotifyParseResult.Fail("验签失败"));

        var root = ParseJson(ctx.Body, Meta.Name);
        var resource = root.Obj("resource");
        if (resource is null) return Task.FromResult(NotifyParseResult.Fail("缺少 resource"));
        JsonElement data;
        try
        {
            var plain = DecryptResource(cfg.Require("apiV3Key"), resource.Value.Str("nonce"), resource.Value.Str("associated_data"), resource.Value.Str("ciphertext"));
            data = ParseJson(plain, Meta.Name);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return Task.FromResult(NotifyParseResult.Fail("回调解密失败，请检查 APIv3 密钥"));
        }

        var eventType = root.Str("event_type");
        if (eventType.StartsWith("REFUND.", StringComparison.Ordinal))
        {
            var refundStatus = data.Str("refund_status");
            if (refundStatus is not ("SUCCESS" or "CLOSED" or "ABNORMAL"))
                return Task.FromResult(NotifyParseResult.Ignore($"退款状态 {refundStatus}"));
            return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
            {
                Type = NotifyEventType.Refund,
                OrderNo = data.Str("out_trade_no"),
                RefundNo = data.Str("out_refund_no"),
                UpstreamRefundNo = data.Str("refund_id"),
                RefundAmount = data.Obj("amount")?.Long("refund"),
                RefundSuccess = refundStatus == "SUCCESS",
                Message = refundStatus,
            }));
        }

        if (data.Str("mchid") != cfg.Require("mchId")) return Task.FromResult(NotifyParseResult.Fail("商户号不匹配"));
        var q = ToQueryResult(data);
        if (q.State != UpstreamState.Paid) return Task.FromResult(NotifyParseResult.Ignore($"交易状态 {data.Str("trade_state")}"));
        return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
        {
            Type = NotifyEventType.Paid,
            OrderNo = data.Str("out_trade_no"),
            UpstreamTradeNo = q.UpstreamTradeNo,
            Amount = q.Amount,
            PaidAt = q.PaidAt,
            BuyerId = q.BuyerId,
        }));
    }

    // ------------------------------------------------------------------ 账单

    /// <summary>申请交易账单（bill_type=ALL，明文）并下载解析。下载地址 5 分钟有效，下载请求同样需要签名。</summary>
    public override async Task<List<BillItem>> DownloadBillAsync(BillContext ctx, CancellationToken ct)
    {
        var cfg = ctx.Config;
        var (status, text) = await CallAsync(cfg, HttpMethod.Get, $"/v3/bill/tradebill?bill_date={ctx.BillDate:yyyy-MM-dd}&bill_type=ALL", "", ct);
        var json = ParseJson(text, Meta.Name);
        if (status is < 200 or >= 300)
        {
            // 当日无交易与退款
            if (json.Str("code") == "NO_STATEMENT_EXIST") return [];
            throw Upstream(json.Str("code"), $"申请微信交易账单失败：{json.Str("message")}");
        }

        var url = new Uri(json.Str("download_url"));
        var auth = BuildAuthorization(cfg.Require("mchId"), cfg.Require("serialNo"), cfg.Require("privateKey"),
            "GET", url.PathAndQuery, "", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), Guid.NewGuid().ToString("N"));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "WECHATPAY2-SHA256-RSA2048 " + auth);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "PayOps/1.0");
        byte[] bytes;
        try
        {
            using var resp = await Http.SendAsync(req, ct);
            bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw Upstream("BILL_DOWNLOAD", $"下载微信账单失败：HTTP {(int)resp.StatusCode} {Truncate(Encoding.UTF8.GetString(bytes), 200)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new ProviderException("UPSTREAM_UNREACHABLE", $"下载微信账单网络异常：{ex.Message}", inner: ex);
        }
        return ParseBill(BillCsv.Decode(BillCsv.Gunzip(bytes)));
    }

    /// <summary>解析 ALL 类型交易账单（按表头名取列，兼容新旧版列名）。SUCCESS 为支付行，REFUND 为退款行；金额单位元。</summary>
    public static List<BillItem> ParseBill(string csv)
    {
        var items = new List<BillItem>();
        foreach (var row in BillCsv.Rows(csv, ["商户订单号", "交易状态"], stopFirstField: "总交易单数"))
        {
            switch (row["交易状态"])
            {
                case "SUCCESS":
                    items.Add(new BillItem
                    {
                        Kind = BillItemKind.Pay,
                        OrderNo = row["商户订单号"],
                        UpstreamTradeNo = row.GetValueOrDefault("微信订单号", ""),
                        // 新版为"订单金额"，旧版商户账单为"总金额"
                        Amount = BillCsv.Fen(row.GetValueOrDefault("订单金额") ?? row.GetValueOrDefault("总金额", "0")),
                    });
                    break;
                case "REFUND":
                    // 退款状态是出账时的快照：仅成功的退款参与对账
                    if (row.GetValueOrDefault("退款状态", "") is not ("" or "SUCCESS")) break;
                    items.Add(new BillItem
                    {
                        Kind = BillItemKind.Refund,
                        OrderNo = row["商户订单号"],
                        UpstreamTradeNo = row.GetValueOrDefault("微信退款单号", ""),
                        RefundNo = row.GetValueOrDefault("商户退款单号", ""),
                        Amount = BillCsv.Fen(row.GetValueOrDefault("退款金额", "0")),
                    });
                    break;
            }
        }
        return items;
    }

    public override NotifyReply BuildReply(NotifyOutcome outcome, string message) =>
        outcome == NotifyOutcome.Success
            ? NotifyReply.Json("{\"code\":\"SUCCESS\",\"message\":\"成功\"}")
            : NotifyReply.Json(JsonSerializer.Serialize(new { code = "FAIL", message }), 500);

    private static string Header(NotifyContext ctx, string name) => ctx.Headers.TryGetValue(name, out var v) ? v : "";

    public static string DecryptResource(string apiV3Key, string nonce, string associatedData, string ciphertext)
    {
        var raw = Convert.FromBase64String(ciphertext);
        var plain = new byte[raw.Length - 16];
        using var aes = new AesGcm(Encoding.UTF8.GetBytes(apiV3Key), 16);
        aes.Decrypt(Encoding.UTF8.GetBytes(nonce), raw.AsSpan(0, raw.Length - 16), raw.AsSpan(raw.Length - 16), plain, Encoding.UTF8.GetBytes(associatedData));
        return Encoding.UTF8.GetString(plain);
    }

    public static string BuildAuthorization(string mchId, string serialNo, string privateKey, string method, string pathAndQuery, string body, string timestamp, string nonce)
    {
        var signature = KeyUtil.RsaSign(privateKey, $"{method}\n{pathAndQuery}\n{timestamp}\n{nonce}\n{body}\n", HashAlgorithmName.SHA256);
        return $"mchid=\"{mchId}\",nonce_str=\"{nonce}\",signature=\"{signature}\",timestamp=\"{timestamp}\",serial_no=\"{serialNo}\"";
    }

    private async Task<(int Status, string Body)> CallAsync(PassageConfig cfg, HttpMethod method, string pathAndQuery, string body, CancellationToken ct)
    {
        var auth = BuildAuthorization(cfg.Require("mchId"), cfg.Require("serialNo"), cfg.Require("privateKey"),
            method.Method, pathAndQuery, body, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), Guid.NewGuid().ToString("N"));
        using var req = new HttpRequestMessage(method, Host + pathAndQuery);
        req.Headers.TryAddWithoutValidation("Authorization", "WECHATPAY2-SHA256-RSA2048 " + auth);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "PayOps/1.0");
        var platformSerial = cfg.Get("platformSerial");
        if (platformSerial.Length > 0) req.Headers.TryAddWithoutValidation("Wechatpay-Serial", platformSerial);
        if (method != HttpMethod.Get) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var (status, text) = await SendAsync(req, ct);
        return (status, text.Length == 0 ? "{}" : text);
    }
}
