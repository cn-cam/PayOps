namespace PayOps.Core.Domain;

public enum OrderStatus { Pending = 0, Paid = 1, Closed = 2, Failed = 3 }

public enum RefundStatus { Processing = 0, Success = 1, Failed = 2 }

public enum NotifyStatus { Pending = 0, Success = 1, Failed = 2 }

public enum NotifyType { Pay = 1, Refund = 2, Subscription = 3 }

/// <summary>订单类型：一次性支付、订阅首购、订阅续费（续费单由上游扣款事件生成，生来即为已支付）。</summary>
public enum OrderType { OneTime = 0, SubscriptionInitial = 1, SubscriptionRenewal = 2 }

public enum SubscriptionStatus { Active = 1, Expired = 2, Revoked = 3 }

public enum ReconStatus { Pending = 0, Balanced = 1, HasDiff = 2, Failed = 3 }

public enum ReconBizType { Pay = 1, Refund = 2 }

public enum ReconDiffType { UpstreamOnly = 1, LocalOnly = 2, AmountMismatch = 3 }

public static class Roles
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public const string Viewer = "viewer";
    public static readonly string[] All = [Admin, Operator, Viewer];
}

/// <summary>应用：自有业务产品，持有接入密钥。</summary>
public class PayApp
{
    public long Id { get; set; }
    public string AppCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string SecretCipher { get; set; } = "";
    public string NotifyUrl { get; set; } = "";
    public int Status { get; set; } = 1;
    public string Remark { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>渠道：应用的分发渠道（官方包、华为、OPPO、App Store……）。</summary>
public class PayChannel
{
    public long Id { get; set; }
    public long AppId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Status { get; set; } = 1;
    public string Remark { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>通道：某个支付类型下的一套商户/应用凭证，全局可复用。</summary>
public class PayPassage
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ConfigCipher { get; set; } = "";
    public int Status { get; set; } = 1;
    public string Remark { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>渠道通道绑定：路由规则（权重、优先级、起付金额、限额、时段）。</summary>
public class PayChannelPassage
{
    public long Id { get; set; }
    public long ChannelId { get; set; }
    public long PassageId { get; set; }
    public string Provider { get; set; } = "";
    /// <summary>逗号分隔的支付方式；空 = 通道类型支持的全部。</summary>
    public string Methods { get; set; } = "";
    public int Weight { get; set; } = 100;
    public int Priority { get; set; }
    public long MinAmount { get; set; }
    public long MaxAmount { get; set; }
    public long DailyLimitAmount { get; set; }
    public int DailyLimitCount { get; set; }
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public int Status { get; set; } = 1;
    public string Remark { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PayOrder
{
    public long Id { get; set; }
    public string OrderNo { get; set; } = "";
    public long AppId { get; set; }
    public string AppCode { get; set; } = "";
    public string MchOrderNo { get; set; } = "";
    public long ChannelId { get; set; }
    public string ChannelCode { get; set; } = "";
    public long PassageId { get; set; }
    public long BindingId { get; set; }
    public string Provider { get; set; } = "";
    public string PayMethod { get; set; } = "";
    public OrderType OrderType { get; set; }
    public string SubNo { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public long Amount { get; set; }
    /// <summary>已占用退款额（处理中 + 成功）。</summary>
    public long RefundAmount { get; set; }
    public string Currency { get; set; } = "CNY";
    public OrderStatus Status { get; set; }
    public string? UpstreamTradeNo { get; set; }
    public string UserId { get; set; } = "";
    public string BuyerId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public string NotifyUrl { get; set; } = "";
    public string ReturnUrl { get; set; } = "";
    public string Attach { get; set; } = "";
    public string Credential { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public string ErrorMsg { get; set; } = "";
    public DateTime ExpireAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime? NextCheckAt { get; set; }
    public int CheckCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PayRefund
{
    public long Id { get; set; }
    public string RefundNo { get; set; } = "";
    public string OrderNo { get; set; } = "";
    public long AppId { get; set; }
    public string AppCode { get; set; } = "";
    public string MchRefundNo { get; set; } = "";
    public long PassageId { get; set; }
    public string Provider { get; set; } = "";
    public long Amount { get; set; }
    public string Reason { get; set; } = "";
    public RefundStatus Status { get; set; }
    public string? UpstreamRefundNo { get; set; }
    public string NotifyUrl { get; set; } = "";
    public string ErrorMsg { get; set; } = "";
    /// <summary>api / admin / upstream</summary>
    public string Source { get; set; } = "api";
    public DateTime? SuccessAt { get; set; }
    public DateTime? NextCheckAt { get; set; }
    public int CheckCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>自动续费订阅：每个扣款周期对应一笔 <see cref="PayOrder"/>（首购或续费）。</summary>
public class PaySubscription
{
    public long Id { get; set; }
    public string SubNo { get; set; } = "";
    public long AppId { get; set; }
    public string AppCode { get; set; } = "";
    public long ChannelId { get; set; }
    public string ChannelCode { get; set; } = "";
    public long PassageId { get; set; }
    public string Provider { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UpstreamSubId { get; set; } = "";
    public string UpstreamToken { get; set; } = "";
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public bool AutoRenew { get; set; } = true;
    public long Amount { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? CancelAt { get; set; }
    public int RenewCount { get; set; }
    public string FirstOrderNo { get; set; } = "";
    public string LastOrderNo { get; set; } = "";
    public string NotifyUrl { get; set; } = "";
    public string Attach { get; set; } = "";
    public DateTime? NextCheckAt { get; set; }
    public int CheckCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>对账批次：每个通道每个账单日一条。</summary>
public class PayReconBatch
{
    public long Id { get; set; }
    public long PassageId { get; set; }
    public string Provider { get; set; } = "";
    public string BillDate { get; set; } = "";
    public ReconStatus Status { get; set; }
    public int UpstreamPayCount { get; set; }
    public long UpstreamPayAmount { get; set; }
    public int UpstreamRefundCount { get; set; }
    public long UpstreamRefundAmount { get; set; }
    public int LocalPayCount { get; set; }
    public long LocalPayAmount { get; set; }
    public int LocalRefundCount { get; set; }
    public long LocalRefundAmount { get; set; }
    public int DiffCount { get; set; }
    public string ErrorMsg { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PayReconDiff
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public long PassageId { get; set; }
    public string Provider { get; set; } = "";
    public string BillDate { get; set; } = "";
    public ReconBizType BizType { get; set; }
    public ReconDiffType DiffType { get; set; }
    public string OrderNo { get; set; } = "";
    public string RefundNo { get; set; } = "";
    public string UpstreamTradeNo { get; set; } = "";
    public long? LocalAmount { get; set; }
    public long? UpstreamAmount { get; set; }
    public string LocalStatus { get; set; } = "";
    public int Status { get; set; }
    public string Remark { get; set; } = "";
    public string HandledBy { get; set; } = "";
    public DateTime? HandledAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>业务通知任务，同时充当事务发件箱。</summary>
public class PayNotifyTask
{
    public long Id { get; set; }
    public long AppId { get; set; }
    public string AppCode { get; set; } = "";
    public NotifyType Type { get; set; }
    public string BizNo { get; set; } = "";
    public string Url { get; set; } = "";
    public string Payload { get; set; } = "";
    public NotifyStatus Status { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public DateTime NextTime { get; set; }
    public int LastHttpStatus { get; set; }
    public string LastResponse { get; set; } = "";
    public DateTime? LastTime { get; set; }
    public bool EventPublished { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PayUpstreamNotify
{
    public long Id { get; set; }
    public string Provider { get; set; } = "";
    public long PassageId { get; set; }
    public string Kind { get; set; } = "";
    public string OrderNo { get; set; } = "";
    public bool Verified { get; set; }
    public string Result { get; set; } = "";
    public string RequestBody { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class SysUser
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
    public int Status { get; set; } = 1;
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SysAuditLog
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Ip { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
