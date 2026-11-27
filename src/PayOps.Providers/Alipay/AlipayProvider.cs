using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Alipay;

/// <summary>支付宝开放平台（公钥模式，RSA2）：APP / 手机网站 / 电脑网站 / 当面付扫码。</summary>
public sealed class AlipayProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    private const string DefaultGateway = "https://openapi.alipay.com/gateway.do";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "alipay",
        Name = "支付宝",
        Category = "direct",
        Methods =
        [
            new("alipay_app", "支付宝APP支付"),
            new("alipay_wap", "支付宝手机网站支付"),
            new("alipay_page", "支付宝电脑网站支付"),
            new("alipay_qr", "支付宝扫码支付"),
        ],
        Capabilities = new() { Query = true, Close = true, Refund = true, Bill = true },
        ConfigFields =
        [
            new() { Key = "appId", Label = "应用AppID", Required = true },
            new() { Key = "privateKey", Label = "应用私钥", Type = "textarea", Required = true, Secret = true, Help = "PKCS8 / PKCS1，PEM 或裸 Base64 均可" },
            new() { Key = "alipayPublicKey", Label = "支付宝公钥", Type = "textarea", Required = true, Help = "开放平台「接口加签方式-公钥模式」下查看的支付宝公钥（不是应用公钥）" },
            new() { Key = "gateway", Label = "网关地址", DefaultValue = DefaultGateway, Help = "沙箱：https://openapi-sandbox.dl.alipaydev.com/gateway.do" },
        ],
        NotifyNote = "回调地址在下单时自动传给支付宝，无需在开放平台单独配置。当前实现为公钥模式，暂不支持证书模式。",
    };

    protected override string? ValidateKeys(PassageConfig config)
    {
        using var pri = KeyUtil.LoadRsaPrivateKey(config.Require("privateKey"));
        using var pub = KeyUtil.LoadRsaPublicKey(config.Require("alipayPublicKey"));
        return null;
    }

    public override async Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct)
    {
        var (apiMethod, productCode) = ctx.Method switch
        {
            "alipay_app" => ("alipay.trade.app.pay", "QUICK_MSECURITY_PAY"),
            "alipay_wap" => ("alipay.trade.wap.pay", "QUICK_WAP_WAY"),
            "alipay_page" => ("alipay.trade.page.pay", "FAST_INSTANT_TRADE_PAY"),
            "alipay_qr" => ("alipay.trade.precreate", ""),
            _ => throw BadRequest($"支付宝不支持的支付方式：{ctx.Method}"),
        };

        var biz = new Dictionary<string, object?>
        {
            ["out_trade_no"] = ctx.OrderNo,
            ["total_amount"] = Money.ToYuan(ctx.Amount),
            ["subject"] = Truncate(ctx.Subject, 128),
            ["time_expire"] = TimeUtil.BeijingString(ctx.ExpireAt, TimeFormat),
        };
        if (productCode.Length > 0) biz["product_code"] = productCode;
        if (ctx.Body.Length > 0) biz["body"] = Truncate(ctx.Body, 128);
        if (ctx.Attach.Length > 0) biz["passback_params"] = Uri.EscapeDataString(ctx.Attach);

        var parameters = BuildParams(ctx.Config, apiMethod, JsonSerializer.Serialize(biz), ctx.NotifyUrl,
            ctx.Method is "alipay_wap" or "alipay_page" ? ctx.ReturnUrl : "");

        switch (ctx.Method)
        {
            case "alipay_app":
                return new PayCredential { Type = CredentialTypes.OrderString, Data = { ["orderString"] = ToQuery(parameters) } };
            case "alipay_wap" or "alipay_page":
                return new PayCredential { Type = CredentialTypes.RedirectUrl, Data = { ["url"] = Gateway(ctx.Config) + "?" + ToQuery(parameters) } };
        }

        var resp = await PostAsync(ctx.Config, parameters, apiMethod, ct);
        EnsureSuccess(resp, "支付宝预下单失败");
        return new PayCredential { Type = CredentialTypes.QrCode, Data = { ["codeUrl"] = resp.Str("qr_code") } };
    }

    public override async Task<QueryResult> QueryAsync(OrderContext ctx, CancellationToken ct)
    {
        const string api = "alipay.trade.query";
        var parameters = BuildParams(ctx.Config, api, JsonSerializer.Serialize(new { out_trade_no = ctx.OrderNo }));
        var resp = await PostAsync(ctx.Config, parameters, api, ct);
        if (resp.Str("code") == "40004" && resp.Str("sub_code") == "ACQ.TRADE_NOT_EXIST")
            return new QueryResult { State = UpstreamState.NotPaid, Message = "交易不存在（用户尚未进入收银台）" };
        EnsureSuccess(resp, "支付宝查单失败");
        return new QueryResult
        {
            State = MapTradeStatus(resp.Str("trade_status")),
            UpstreamTradeNo = resp.Str("trade_no") is { Length: > 0 } t ? t : null,
            Amount = Money.FromYuan(resp.Str("total_amount")),
            PaidAt = TimeUtil.ParseBeijing(resp.Str("send_pay_date"), TimeFormat),
            BuyerId = FirstNonEmpty(resp.Str("buyer_open_id"), resp.Str("buyer_user_id")),
            Message = resp.Str("trade_status"),
        };
    }

    private static UpstreamState MapTradeStatus(string status) => status switch
    {
        "TRADE_SUCCESS" or "TRADE_FINISHED" => UpstreamState.Paid,
        "TRADE_CLOSED" => UpstreamState.Closed,
        _ => UpstreamState.NotPaid,
    };

    public override async Task CloseAsync(OrderContext ctx, CancellationToken ct)
    {
        const string api = "alipay.trade.close";
        var parameters = BuildParams(ctx.Config, api, JsonSerializer.Serialize(new { out_trade_no = ctx.OrderNo }));
        var resp = await PostAsync(ctx.Config, parameters, api, ct);
        if (resp.Str("sub_code") == "ACQ.TRADE_NOT_EXIST") return;
        EnsureSuccess(resp, "支付宝关单失败");
    }

    public override async Task<RefundResult> RefundAsync(RefundContext ctx, CancellationToken ct)
    {
        const string api = "alipay.trade.refund";
        var biz = JsonSerializer.Serialize(new
        {
            out_trade_no = ctx.OrderNo,
            refund_amount = Money.ToYuan(ctx.RefundAmount),
            out_request_no = ctx.RefundNo,
            refund_reason = Truncate(ctx.Reason.Length > 0 ? ctx.Reason : "退款", 128),
        });
        var resp = await PostAsync(ctx.Config, BuildParams(ctx.Config, api, biz), api, ct);
        EnsureSuccess(resp, "支付宝退款失败");
        return new RefundResult
        {
            State = resp.Str("fund_change") == "Y" ? UpstreamRefundState.Success : UpstreamRefundState.Processing,
            UpstreamRefundNo = resp.Str("trade_no") is { Length: > 0 } t ? t + "_" + ctx.RefundNo : null,
        };
    }

    public override async Task<RefundResult> QueryRefundAsync(RefundContext ctx, CancellationToken ct)
    {
        const string api = "alipay.trade.fastpay.refund.query";
        var biz = JsonSerializer.Serialize(new { out_trade_no = ctx.OrderNo, out_request_no = ctx.RefundNo });
        var resp = await PostAsync(ctx.Config, BuildParams(ctx.Config, api, biz), api, ct);
        EnsureSuccess(resp, "支付宝退款查询失败");
        var success = resp.Str("refund_status") == "REFUND_SUCCESS"
                      || (resp.Str("refund_status").Length == 0 && resp.Str("refund_amount").Length > 0);
        return new RefundResult { State = success ? UpstreamRefundState.Success : UpstreamRefundState.Processing };
    }

    // ------------------------------------------------------------------ 账单

    /// <summary>
    /// 查询交易账单下载地址（bill_type=trade）并下载解析。下载地址 30 秒内有效；文件为 zip，内含 GBK 编码的"业务明细"与"汇总"两个 CSV。
    /// </summary>
    public override async Task<List<BillItem>> DownloadBillAsync(BillContext ctx, CancellationToken ct)
    {
        const string api = "alipay.data.dataservice.bill.downloadurl.query";
        var biz = JsonSerializer.Serialize(new { bill_type = "trade", bill_date = ctx.BillDate.ToString("yyyy-MM-dd") });
        var resp = await PostAsync(ctx.Config, BuildParams(ctx.Config, api, biz), api, ct);
        // 当日无业务
        if (resp.Str("sub_code") == "NO_BILL_DATA" || resp.Str("bill_file_code") == "EMPTY_DATA_WITH_BILL_FILE") return [];
        EnsureSuccess(resp, "查询支付宝账单下载地址失败");

        byte[] bytes;
        try
        {
            using var file = await Http.GetAsync(resp.Str("bill_download_url"), ct);
            bytes = await file.Content.ReadAsByteArrayAsync(ct);
            if (!file.IsSuccessStatusCode) throw Upstream("BILL_DOWNLOAD", $"下载支付宝账单失败：HTTP {(int)file.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new ProviderException("UPSTREAM_UNREACHABLE", $"下载支付宝账单网络异常：{ex.Message}", inner: ex);
        }
        return ParseBill(ExtractDetailCsv(bytes));
    }

    /// <summary>从账单 zip 中取出"业务明细"CSV 文本（跳过文件名含"汇总"的那个）。</summary>
    public static string ExtractDetailCsv(byte[] zip)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip), System.IO.Compression.ZipArchiveMode.Read, false, Encoding.GetEncoding(936));
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains("汇总"))
                    ?? archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    ?? throw new ProviderException("BILL_FORMAT", "支付宝账单压缩包内没有 CSV 文件");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return BillCsv.Decode(buffer.ToArray());
    }

    /// <summary>
    /// 解析业务明细（按表头名取列；以 # 开头的说明行会被自动跳过）。业务类型"交易"为支付行、"退款"为退款行；
    /// 退款行金额为负数，商户退款请求号在"退款批次号/请求号"列。
    /// </summary>
    public static List<BillItem> ParseBill(string csv)
    {
        var items = new List<BillItem>();
        foreach (var row in BillCsv.Rows(csv, ["商户订单号", "业务类型"]))
        {
            var amount = Math.Abs(BillCsv.Fen(row.GetValueOrDefault("订单金额（元）") ?? row.GetValueOrDefault("订单金额(元)", "0")));
            switch (row["业务类型"])
            {
                case "交易":
                    items.Add(new BillItem
                    {
                        Kind = BillItemKind.Pay,
                        OrderNo = row["商户订单号"],
                        UpstreamTradeNo = row.GetValueOrDefault("支付宝交易号", ""),
                        Amount = amount,
                    });
                    break;
                case "退款":
                    items.Add(new BillItem
                    {
                        Kind = BillItemKind.Refund,
                        OrderNo = row["商户订单号"],
                        UpstreamTradeNo = row.GetValueOrDefault("支付宝交易号", ""),
                        RefundNo = row.GetValueOrDefault("退款批次号/请求号") ?? row.GetValueOrDefault("退款批次号", ""),
                        Amount = amount,
                    });
                    break;
            }
        }
        return items;
    }

    public override Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var form = ctx.Form.Count > 0 ? ctx.Form : ctx.Query;
        if (!form.TryGetValue("sign", out var sign) || sign.Length == 0)
            return Task.FromResult(NotifyParseResult.Fail("缺少 sign"));
        var content = string.Join('&', form
            .Where(kv => kv.Key is not ("sign" or "sign_type") && kv.Value.Length > 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        if (!KeyUtil.RsaVerify(ctx.Config.Require("alipayPublicKey"), content, sign, HashAlgorithmName.SHA256))
            return Task.FromResult(NotifyParseResult.Fail("验签失败"));
        if (Value(form, "app_id") != ctx.Config.Require("appId"))
            return Task.FromResult(NotifyParseResult.Fail("app_id 不匹配"));

        var status = Value(form, "trade_status");
        if (MapTradeStatus(status) != UpstreamState.Paid)
            return Task.FromResult(NotifyParseResult.Ignore($"交易状态 {status}"));
        return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
        {
            Type = NotifyEventType.Paid,
            OrderNo = Value(form, "out_trade_no"),
            UpstreamTradeNo = Value(form, "trade_no"),
            Amount = Money.FromYuan(Value(form, "total_amount")),
            PaidAt = TimeUtil.ParseBeijing(Value(form, "gmt_payment"), TimeFormat),
            BuyerId = FirstNonEmpty(Value(form, "buyer_open_id"), Value(form, "buyer_id")),
        }));
    }

    private static string Value(IReadOnlyDictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";

    private static string FirstNonEmpty(string a, string b) => a.Length > 0 ? a : b;

    private static string Gateway(PassageConfig cfg) => cfg.Get("gateway", DefaultGateway);

    public static SortedDictionary<string, string> BuildParams(PassageConfig cfg, string method, string bizContent, string notifyUrl = "", string returnUrl = "")
    {
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["app_id"] = cfg.Require("appId"),
            ["method"] = method,
            ["format"] = "JSON",
            ["charset"] = "utf-8",
            ["sign_type"] = "RSA2",
            ["timestamp"] = TimeUtil.BeijingString(DateTime.UtcNow, TimeFormat),
            ["version"] = "1.0",
            ["biz_content"] = bizContent,
        };
        if (notifyUrl.Length > 0) p["notify_url"] = notifyUrl;
        if (returnUrl.Length > 0) p["return_url"] = returnUrl;
        p["sign"] = KeyUtil.RsaSign(cfg.Require("privateKey"), SignContent(p), HashAlgorithmName.SHA256);
        return p;
    }

    public static string SignContent(IEnumerable<KeyValuePair<string, string>> p) =>
        string.Join('&', p.Where(kv => kv.Key != "sign" && kv.Value.Length > 0).Select(kv => $"{kv.Key}={kv.Value}"));

    private static string ToQuery(IEnumerable<KeyValuePair<string, string>> p) =>
        string.Join('&', p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

    private async Task<JsonElement> PostAsync(PassageConfig cfg, SortedDictionary<string, string> parameters, string apiMethod, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Gateway(cfg) + "?charset=utf-8")
        {
            Content = new StringContent(ToQuery(parameters), Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        var (_, text) = await SendAsync(req, ct);
        var root = ParseJson(text, Meta.Name);
        var node = root.Obj(apiMethod.Replace('.', '_') + "_response") ?? root.Obj("error_response");
        if (node is null) throw Upstream("UPSTREAM_BAD_RESPONSE", "支付宝返回内容缺少响应节点");
        return node.Value;
    }

    private static void EnsureSuccess(JsonElement resp, string prefix)
    {
        if (resp.Str("code") == "10000") return;
        var code = FirstNonEmpty(resp.Str("sub_code"), resp.Str("code"));
        throw Upstream(code, $"{prefix}：{FirstNonEmpty(resp.Str("sub_msg"), resp.Str("msg"))}");
    }
}
