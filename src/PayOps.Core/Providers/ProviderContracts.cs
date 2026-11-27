namespace PayOps.Core.Providers;

/// <summary>上游侧订单状态的统一表达。</summary>
public enum UpstreamState
{
    Unknown = 0,
    NotPaid = 1,
    Paid = 2,
    Closed = 3,
    Failed = 4,
    Refunded = 5,
}

public enum UpstreamRefundState { Processing = 0, Success = 1, Failed = 2 }

/// <summary>通道凭证配置（解密后的键值对）。</summary>
public sealed class PassageConfig(IReadOnlyDictionary<string, string> values)
{
    private readonly IReadOnlyDictionary<string, string> _values = values;

    public IReadOnlyDictionary<string, string> Values => _values;

    public string Get(string key, string defaultValue = "") =>
        _values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : defaultValue;

    public string Require(string key)
    {
        var v = Get(key);
        if (v.Length == 0) throw new ProviderException("CONFIG_MISSING", $"通道配置缺少 {key}");
        return v;
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        var v = Get(key);
        return v.Length == 0 ? defaultValue : v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

    public int GetInt(string key, int defaultValue = 0) => int.TryParse(Get(key), out var n) ? n : defaultValue;
}

/// <summary>
/// 通道调用异常。<see cref="RequestFault"/> 为 true 表示请求本身的问题（换通道也无法成功），
/// 路由器不会因此故障转移，也不计入熔断。
/// </summary>
public class ProviderException(string code, string message, bool requestFault = false, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool RequestFault { get; } = requestFault;
}

public sealed class CreateContext
{
    public required long PassageId { get; init; }
    public required PassageConfig Config { get; init; }
    public required string OrderNo { get; init; }
    public required string Method { get; init; }
    public required long Amount { get; init; }
    public string Currency { get; init; } = "CNY";
    public required string Subject { get; init; }
    public string Body { get; init; } = "";
    public string ClientIp { get; init; } = "";
    public string OpenId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string Attach { get; init; } = "";
    public string ReturnUrl { get; init; } = "";
    /// <summary>上游支付结果回调地址（指向本系统）。</summary>
    public required string NotifyUrl { get; init; }
    public required DateTime ExpireAt { get; init; }
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
}

/// <summary>返回给客户端用于拉起支付的凭据。</summary>
public sealed class PayCredential
{
    public required string Type { get; init; }
    public Dictionary<string, object?> Data { get; init; } = [];
}

public static class CredentialTypes
{
    public const string AppParams = "app_params";
    public const string JsapiParams = "jsapi_params";
    public const string QrCode = "qr_code";
    public const string RedirectUrl = "redirect_url";
    public const string OrderString = "order_string";
    public const string SdkParams = "sdk_params";
    public const string Iap = "iap";
    public const string Mock = "mock";
}

public sealed class OrderContext
{
    public required long PassageId { get; init; }
    public required PassageConfig Config { get; init; }
    public required string OrderNo { get; init; }
    public required string Method { get; init; }
    public required long Amount { get; init; }
    public string? UpstreamTradeNo { get; init; }
    public string UserId { get; init; } = "";
    public string BuyerId { get; init; } = "";
    public string ProductId { get; init; } = "";
}

public sealed class QueryResult
{
    public UpstreamState State { get; init; }
    public string? UpstreamTradeNo { get; init; }
    /// <summary>上游返回的金额（分）；无法取得或币种不同则为 null，不参与金额校验。</summary>
    public long? Amount { get; init; }
    public DateTime? PaidAt { get; init; }
    public string BuyerId { get; init; } = "";
    /// <summary>上游凭证内携带的本系统订单号（内购凭证核验时用于防串单），可空。</summary>
    public string? BoundOrderNo { get; init; }
    public string? ProductId { get; init; }
    public string Message { get; init; } = "";
}

public sealed class RefundContext
{
    public required long PassageId { get; init; }
    public required PassageConfig Config { get; init; }
    public required string OrderNo { get; init; }
    public required string Method { get; init; }
    public string? UpstreamTradeNo { get; init; }
    public required string RefundNo { get; init; }
    public required long OrderAmount { get; init; }
    public required long RefundAmount { get; init; }
    public string Reason { get; init; } = "";
    public string? UpstreamRefundNo { get; init; }
    public required string NotifyUrl { get; init; }
}

public sealed class RefundResult
{
    public UpstreamRefundState State { get; init; }
    public string? UpstreamRefundNo { get; init; }
    public string Message { get; init; } = "";
}

public sealed class ReceiptContext
{
    public required long PassageId { get; init; }
    public required PassageConfig Config { get; init; }
    public required string OrderNo { get; init; }
    public string ProductId { get; init; } = "";
    /// <summary>客户端 SDK 返回的凭证字段：purchaseToken / transactionId / signedTransaction 等。</summary>
    public required IReadOnlyDictionary<string, string> Receipt { get; init; }
}

public sealed class NotifyContext
{
    public required long PassageId { get; init; }
    public required PassageConfig Config { get; init; }
    /// <summary>URL 末段：空或 pay / refund。</summary>
    public string Kind { get; init; } = "";
    public required string HttpMethod { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required IReadOnlyDictionary<string, string> Query { get; init; }
    public required IReadOnlyDictionary<string, string> Form { get; init; }
    public required string Body { get; init; }
}

public enum NotifyEventType { Ignore = 0, Paid = 1, Refund = 2 }

public sealed class NotifyEvent
{
    public NotifyEventType Type { get; init; }
    public string OrderNo { get; init; } = "";
    public string? UpstreamTradeNo { get; init; }
    public long? Amount { get; init; }
    public DateTime? PaidAt { get; init; }
    public string BuyerId { get; init; } = "";
    public string? ProductId { get; init; }

    // 退款事件
    public string? RefundNo { get; init; }
    public string? UpstreamRefundNo { get; init; }
    public long? RefundAmount { get; init; }
    public bool RefundSuccess { get; init; } = true;
    public string Message { get; init; } = "";
}

public sealed class NotifyParseResult
{
    /// <summary>验签是否通过；false 时不会处理任何事件。</summary>
    public bool Verified { get; init; }
    public string Message { get; init; } = "";
    public List<NotifyEvent> Events { get; init; } = [];

    public static NotifyParseResult Fail(string message) => new() { Verified = false, Message = message };
    public static NotifyParseResult Ignore(string message) => new() { Verified = true, Message = message };
    public static NotifyParseResult Of(NotifyEvent e) => new() { Verified = true, Events = [e] };
}

public enum NotifyOutcome { Success, SignError, OrderNotFound, AmountMismatch, Error }

public sealed record NotifyReply(int StatusCode, string ContentType, string Body)
{
    public static NotifyReply Text(string body, int status = 200) => new(status, "text/plain; charset=utf-8", body);
    public static NotifyReply Json(string body, int status = 200) => new(status, "application/json; charset=utf-8", body);
}

// ---------------- 元数据（驱动管理后台动态表单） ----------------

public sealed record PayMethodMeta(string Code, string Name);

public sealed record ConfigOption(string Label, string Value);

public sealed class ConfigField
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    /// <summary>text | password | textarea | number | switch | select</summary>
    public string Type { get; init; } = "text";
    public bool Required { get; init; }
    public bool Secret { get; init; }
    public string Placeholder { get; init; } = "";
    public string Help { get; init; } = "";
    public string DefaultValue { get; init; } = "";
    public List<ConfigOption> Options { get; init; } = [];
}

public sealed class ProviderCapabilities
{
    public bool Query { get; init; }
    public bool Close { get; init; }
    public bool Refund { get; init; }
    public bool ReceiptVerify { get; init; }
    public bool ServerNotify { get; init; } = true;
}

public sealed class ProviderMeta
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    /// <summary>direct | store | mock</summary>
    public required string Category { get; init; }
    public required List<PayMethodMeta> Methods { get; init; }
    public required ProviderCapabilities Capabilities { get; init; }
    public required List<ConfigField> ConfigFields { get; init; }
    public string NotifyNote { get; init; } = "";
}

public interface IPayProvider
{
    ProviderMeta Meta { get; }

    /// <summary>校验配置，返回 null 表示通过，否则返回问题描述。</summary>
    string? ValidateConfig(PassageConfig config);

    Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct);

    Task<QueryResult> QueryAsync(OrderContext ctx, CancellationToken ct);

    Task CloseAsync(OrderContext ctx, CancellationToken ct);

    Task<RefundResult> RefundAsync(RefundContext ctx, CancellationToken ct);

    Task<RefundResult> QueryRefundAsync(RefundContext ctx, CancellationToken ct);

    /// <summary>内购凭证核验（客户端支付完成后提交凭证）。</summary>
    Task<QueryResult> VerifyReceiptAsync(ReceiptContext ctx, CancellationToken ct);

    /// <summary>解析并验签上游回调。</summary>
    Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct);

    /// <summary>按处理结果生成给上游的应答。</summary>
    NotifyReply BuildReply(NotifyOutcome outcome, string message);
}

public interface IPayProviderRegistry
{
    IReadOnlyList<IPayProvider> All { get; }
    IPayProvider? Find(string code);
    IPayProvider Get(string code);
    /// <summary>由支付方式反查支付类型，例如 wechat_app → wechat。</summary>
    IPayProvider? FindByMethod(string method);
}
