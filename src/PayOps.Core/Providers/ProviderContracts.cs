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
    /// <summary>是否为自动续费订阅的首购。</summary>
    public bool Subscribe { get; init; }
    /// <summary>是否为签约代扣的签约单（此时返回的是拉起签约的凭据，不产生支付）。</summary>
    public bool SignAgreement { get; init; }
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
}

/// <summary>一次订阅扣款（首购或续费）携带的订阅信息。</summary>
public sealed class SubscriptionInfo
{
    /// <summary>上游订阅标识，同一订阅的各期扣款保持不变（苹果 originalTransactionId、华为 subscriptionId）。</summary>
    public required string UpstreamSubId { get; init; }
    /// <summary>后续查询/退订所需的上游凭证（如华为 purchaseToken），没有则为空。</summary>
    public string Token { get; init; } = "";
    public DateTime? PeriodStart { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public bool AutoRenew { get; init; } = true;
    /// <summary>本期实际扣款金额（分），仅用于记录续费单金额，不参与金额校验；取不到则为 null。</summary>
    public long? Amount { get; init; }
    /// <summary>扣款对应的商品（升级后与订阅原商品不同）。空表示沿用订阅上的商品。</summary>
    public string ProductId { get; init; } = "";
    /// <summary>下一期生效的商品（降级/换购预约）。</summary>
    public string NextProductId { get; init; } = "";
}

public sealed class SubscriptionContext
{
    public required long PassageId { get; init; }
    public required PassageConfig Config { get; init; }
    public required string UpstreamSubId { get; init; }
    public string Token { get; init; } = "";
    public string ProductId { get; init; } = "";
}

/// <summary>发起一次代扣（签约代扣模式下由本系统按周期调用）。</summary>
public sealed class DeductContext
{
    public required long PassageId { get; init; }
    public required PassageConfig Config { get; init; }
    /// <summary>签约标识（上游的协议号 / 合约号）。</summary>
    public required string AgreementId { get; init; }
    public required string OrderNo { get; init; }
    public required string ProductId { get; init; }
    public required long Amount { get; init; }
    public required string Subject { get; init; }
    public required string NotifyUrl { get; init; }
    public string PayerId { get; init; } = "";
}

public sealed class DeductResult
{
    /// <summary>上游是否已明确扣款成功。false 表示处理中，最终结果以回调/查询为准。</summary>
    public bool Paid { get; init; }
    public string? UpstreamTradeNo { get; init; }
    public DateTime? PaidAt { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>主动查询得到的上游订阅现状。</summary>
public sealed class SubscriptionState
{
    /// <summary>上游是否给出了确定结论；false 表示该类型无法主动查询。</summary>
    public bool Known { get; init; }
    public bool Active { get; init; }
    public bool Revoked { get; init; }
    public bool Paused { get; init; }
    public bool AutoRenew { get; init; }
    /// <summary>上游当前生效的商品；与本地不同说明发生过升级/换购。</summary>
    public string? ProductId { get; init; }
    /// <summary>上游下一期将要扣费的商品（降级预约）。</summary>
    public string? NextProductId { get; init; }
    public DateTime? PeriodStart { get; init; }
    public DateTime? ExpiresAt { get; init; }
    /// <summary>最近一期扣款的上游交易号，用于识别是否发生了本地尚未记录的续费。</summary>
    public string? LatestTradeNo { get; init; }
    public long? LatestAmount { get; init; }
    public DateTime? LatestPaidAt { get; init; }
    public string Message { get; init; } = "";
}

public enum BillItemKind { Pay = 1, Refund = 2 }

/// <summary>上游对账单中的一行（已归一化）。</summary>
public sealed class BillItem
{
    public BillItemKind Kind { get; init; }
    /// <summary>平台订单号（上游的商户订单号）。</summary>
    public string OrderNo { get; init; } = "";
    public string UpstreamTradeNo { get; init; } = "";
    /// <summary>平台退款单号（上游的商户退款单号），仅退款行。</summary>
    public string RefundNo { get; init; } = "";
    /// <summary>金额（分，正数）：支付行为订单金额，退款行为退款金额。</summary>
    public long Amount { get; init; }
    /// <summary>上游收取的手续费（分，保留上游的正负号：支付为正、退款为负）；账单没有该列时为 null。</summary>
    public long? Fee { get; init; }
    public DateTime? Time { get; init; }
}

/// <summary>资金账单汇总（按某一天）。金额单位分，收入与支出均为正数。</summary>
public sealed class FundBillSummary
{
    public long Income { get; init; }
    public long Expense { get; init; }
    public long Refund { get; init; }
    public long Fee { get; init; }
    public string Message { get; init; } = "";
}

public sealed class BillContext
{
    public required long PassageId { get; init; }
    public required PassageConfig Config { get; init; }
    /// <summary>账单日（业务时区的自然日）。</summary>
    public required DateOnly BillDate { get; init; }
    /// <summary>本地当日成功记录，仅供模拟通道合成"上游账单"使用，真实类型必须忽略。</summary>
    public IReadOnlyList<BillItem> LocalItems { get; init; } = [];
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
    /// <summary>该笔交易属于自动续费订阅时给出。</summary>
    public SubscriptionInfo? Subscription { get; init; }
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
    /// <summary>订单是否为订阅首购（部分商店的订阅凭证需走不同的核验接口）。</summary>
    public bool Subscribe { get; init; }
    /// <summary>客户端 SDK 返回的凭证字段：purchaseToken / subscriptionId / transactionId / signedTransaction 等。</summary>
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

public enum NotifyEventType { Ignore = 0, Paid = 1, Refund = 2, Subscription = 3, Agreement = 4 }

/// <summary>签约结果（NotifyEvent.Type = Agreement 时使用）。</summary>
public sealed class AgreementInfo
{
    /// <summary>上游协议号 / 合约号。</summary>
    public required string AgreementId { get; init; }
    /// <summary>签约用户标识（支付宝 buyer_id、微信 openid 等），扣款时可能需要。</summary>
    public string PayerId { get; init; } = "";
    public DateTime? SignedAt { get; init; }
    public bool Signed { get; init; } = true;
    public string Message { get; init; } = "";
}

/// <summary>
/// 订阅事件。Charged = 一次成功扣款（首购或续费，由服务层根据订阅是否已存在来区分）；
/// AutoRenewOff = 退订（当前周期结束后不再续订）；AutoRenewOn = 恢复续订；Expired = 到期失效；Revoked = 撤销（退款/撤回）。
/// </summary>
public enum SubscriptionEventKind
{
    Charged = 1,
    AutoRenewOff = 2,
    AutoRenewOn = 3,
    Expired = 4,
    Revoked = 5,
    /// <summary>立即生效的升级/换购：本期商品已改变。</summary>
    Upgraded = 6,
    /// <summary>降级：下一期生效，本期商品不变。</summary>
    Downgraded = 7,
    /// <summary>暂停（商店侧的账单暂停 / 账号冻结）。</summary>
    Paused = 8,
    /// <summary>从暂停恢复。</summary>
    Resumed = 9,
}

public sealed class NotifyEvent
{
    public NotifyEventType Type { get; init; }
    public SubscriptionEventKind SubKind { get; init; }
    /// <summary>订阅事件必填；Paid 事件带上它表示这是订阅首购。</summary>
    public SubscriptionInfo? Subscription { get; init; }
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
    /// <summary>签约事件（Type = Agreement 时使用）。</summary>
    public AgreementInfo? Agreement { get; init; }
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
    /// <summary>支持自动续费订阅（首购、续订、退订、到期事件）。</summary>
    public bool Subscription { get; init; }
    /// <summary>支持由服务端发起退订；不支持的类型只能由用户在商店侧退订，退订结果经通知同步。</summary>
    public bool SubscriptionCancel { get; init; }
    /// <summary>支持暂停/恢复（目前只有小米的订阅群组语义与商店侧暂停可用，商店主动通知驱动）。</summary>
    public bool SubscriptionPause { get; init; }
    /// <summary>支持升级/降级（商店侧发起，通知驱动）。</summary>
    public bool SubscriptionChange { get; init; }
    /// <summary>支持下载交易账单。</summary>
    public bool Bill { get; init; }
    /// <summary>支持下载资金账单（账户收支、手续费）。</summary>
    public bool FundBill { get; init; }
    /// <summary>支持签约代扣：服务端可发起签约、扣款与解约。</summary>
    public bool Deduct { get; init; }
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

    /// <summary>主动查询订阅现状（到期巡检、后台同步使用）。</summary>
    Task<SubscriptionState> QuerySubscriptionAsync(SubscriptionContext ctx, CancellationToken ct);

    /// <summary>服务端发起退订（停止自动续订，当前周期仍有效）。</summary>
    Task CancelSubscriptionAsync(SubscriptionContext ctx, CancellationToken ct);

    /// <summary>签约代扣：服务端发起一次扣款。</summary>
    Task<DeductResult> DeductAsync(DeductContext ctx, CancellationToken ct);

    /// <summary>签约代扣：服务端解约（终止协议，之后不再允许扣款）。</summary>
    Task CancelAgreementAsync(SubscriptionContext ctx, CancellationToken ct);

    /// <summary>下载并解析指定账单日的交易账单。</summary>
    Task<List<BillItem>> DownloadBillAsync(BillContext ctx, CancellationToken ct);

    /// <summary>下载并解析指定账单日的资金账单（账户收支与手续费）。</summary>
    Task<FundBillSummary> DownloadFundBillAsync(BillContext ctx, CancellationToken ct);

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
