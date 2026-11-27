using PayOps.Core.Domain;

namespace PayOps.Service.Admin;

// ---------------- 应用 / 渠道 / 通道 / 绑定 ----------------

public class AppItem
{
    public long Id { get; set; }
    public string AppCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string NotifyUrl { get; set; } = "";
    public int Status { get; set; }
    public string Remark { get; set; } = "";
    public int ChannelCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AppInput
{
    public string AppCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string NotifyUrl { get; set; } = "";
    public string Remark { get; set; } = "";
    public int Status { get; set; } = 1;
}

public class ChannelItem
{
    public long Id { get; set; }
    public long AppId { get; set; }
    public string AppCode { get; set; } = "";
    public string AppName { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Status { get; set; }
    public string Remark { get; set; } = "";
    public int BindingCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ChannelInput
{
    public long AppId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Remark { get; set; } = "";
    public int Status { get; set; } = 1;
}

public class ChannelCopyInput
{
    public long TargetAppId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

public class PassageItem
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public int Status { get; set; }
    public string Remark { get; set; } = "";
    public int BindingCount { get; set; }
    public string NotifyUrl { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PassageDetail : PassageItem
{
    public Dictionary<string, string> Config { get; set; } = [];
}

public class PassageInput
{
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public int Status { get; set; } = 1;
    public string Remark { get; set; } = "";
    public Dictionary<string, string>? Config { get; set; }
}

public class BindingInput
{
    public long PassageId { get; set; }
    public List<string> Methods { get; set; } = [];
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
}

public class BindingRuntime
{
    public long TodayAmount { get; set; }
    public int TodayCount { get; set; }
    public bool BreakerOpen { get; set; }
    public int RecentFailures { get; set; }
}

public class BindingItem : BindingInput
{
    public long Id { get; set; }
    public long ChannelId { get; set; }
    public string PassageName { get; set; } = "";
    public int PassageStatus { get; set; }
    public string Provider { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public BindingRuntime Runtime { get; set; } = new();
}

public class RouteSimulateInput
{
    public string PayMethod { get; set; } = "";
    public long Amount { get; set; }
    public int Times { get; set; } = 1000;
}

public class RouteSimulateItem
{
    public long BindingId { get; set; }
    public long PassageId { get; set; }
    public string PassageName { get; set; } = "";
    public string Provider { get; set; } = "";
    public int Priority { get; set; }
    public int Weight { get; set; }
    public bool Eligible { get; set; }
    public string Reason { get; set; } = "";
    public int HitCount { get; set; }
    public double HitRate { get; set; }
}

public class RouteSimulateResult
{
    public string PayMethod { get; set; } = "";
    public long Amount { get; set; }
    public int Times { get; set; }
    public List<RouteSimulateItem> Items { get; set; } = [];
}

// ---------------- 订单 / 退款 / 通知 ----------------

public class OrderItem
{
    public long Id { get; set; }
    public string OrderNo { get; set; } = "";
    public string MchOrderNo { get; set; } = "";
    public long AppId { get; set; }
    public string AppCode { get; set; } = "";
    public long ChannelId { get; set; }
    public string ChannelCode { get; set; } = "";
    public long PassageId { get; set; }
    public string PassageName { get; set; } = "";
    public string Provider { get; set; } = "";
    public string PayMethod { get; set; } = "";
    public OrderType OrderType { get; set; }
    public string SubNo { get; set; } = "";
    public string Subject { get; set; } = "";
    public long Amount { get; set; }
    public long RefundAmount { get; set; }
    public string Currency { get; set; } = "";
    public OrderStatus Status { get; set; }
    public string UpstreamTradeNo { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime ExpireAt { get; set; }
}

public class OrderDetail : OrderItem
{
    public string Body { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public string BuyerId { get; set; } = "";
    public string NotifyUrl { get; set; } = "";
    public string ReturnUrl { get; set; } = "";
    public string Attach { get; set; } = "";
    public string Credential { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public string ErrorMsg { get; set; } = "";
    public DateTime? ClosedAt { get; set; }
    public int CheckCount { get; set; }
    public DateTime? NextCheckAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long BindingId { get; set; }
    public List<RefundItem> Refunds { get; set; } = [];
    public List<NotifyTaskItem> NotifyTasks { get; set; } = [];
    public List<UpstreamNotifyItem> UpstreamNotifies { get; set; } = [];
}

public class OrderQuery : PageQuery
{
    public string? OrderNo { get; set; }
    public string? MchOrderNo { get; set; }
    public string? UpstreamTradeNo { get; set; }
    public long? AppId { get; set; }
    public long? ChannelId { get; set; }
    public long? PassageId { get; set; }
    public string? Provider { get; set; }
    public int? Status { get; set; }
    public int? OrderType { get; set; }
    public string? SubNo { get; set; }
    public string? UserId { get; set; }
}

// ---------------- 订阅 ----------------

public class SubscriptionItem
{
    public long Id { get; set; }
    public string SubNo { get; set; } = "";
    public long AppId { get; set; }
    public string AppCode { get; set; } = "";
    public string ChannelCode { get; set; } = "";
    public long PassageId { get; set; }
    public string PassageName { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UpstreamSubId { get; set; } = "";
    public SubscriptionStatus Status { get; set; }
    public bool AutoRenew { get; set; }
    public long Amount { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? CancelAt { get; set; }
    public int RenewCount { get; set; }
    public string FirstOrderNo { get; set; } = "";
    public string LastOrderNo { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SubscriptionDetail : SubscriptionItem
{
    public string NotifyUrl { get; set; } = "";
    public string Attach { get; set; } = "";
    public DateTime? NextCheckAt { get; set; }
    public int CheckCount { get; set; }
    public List<OrderItem> Orders { get; set; } = [];
    public List<NotifyTaskItem> NotifyTasks { get; set; } = [];
}

public class SubscriptionQuery : PageQuery
{
    public string? SubNo { get; set; }
    public long? AppId { get; set; }
    public string? Provider { get; set; }
    public int? Status { get; set; }
    public bool? AutoRenew { get; set; }
    public string? UserId { get; set; }
    public string? ProductId { get; set; }
}

// ---------------- 对账 ----------------

public class ReconBatchItem
{
    public long Id { get; set; }
    public long PassageId { get; set; }
    public string PassageName { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ProviderName { get; set; } = "";
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
    public int OpenDiffCount { get; set; }
    public string ErrorMsg { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ReconBatchQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public long? PassageId { get; set; }
    public string? Provider { get; set; }
    public int? Status { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
}

public class ReconDiffItem
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public long PassageId { get; set; }
    public string PassageName { get; set; } = "";
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

public class ReconDiffQuery : ReconBatchQuery
{
    public long? BatchId { get; set; }
    public int? DiffType { get; set; }
    public int? BizType { get; set; }
    public string? OrderNo { get; set; }
}

public class RefundItem
{
    public long Id { get; set; }
    public string RefundNo { get; set; } = "";
    public string OrderNo { get; set; } = "";
    public string MchRefundNo { get; set; } = "";
    public long AppId { get; set; }
    public string AppCode { get; set; } = "";
    public long PassageId { get; set; }
    public string Provider { get; set; } = "";
    public long Amount { get; set; }
    public string Reason { get; set; } = "";
    public RefundStatus Status { get; set; }
    public string UpstreamRefundNo { get; set; } = "";
    public string ErrorMsg { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? SuccessAt { get; set; }
}

public class RefundQuery : PageQuery
{
    public string? RefundNo { get; set; }
    public string? OrderNo { get; set; }
    public string? MchRefundNo { get; set; }
    public long? AppId { get; set; }
    public int? Status { get; set; }
}

public class NotifyTaskItem
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
    public DateTime CreatedAt { get; set; }
}

public class NotifyTaskQuery : PageQuery
{
    public string? BizNo { get; set; }
    public long? AppId { get; set; }
    public int? Status { get; set; }
    public int? Type { get; set; }
}

public class UpstreamNotifyItem
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

public class UpstreamNotifyQuery : PageQuery
{
    public string? Provider { get; set; }
    public string? OrderNo { get; set; }
    public long? PassageId { get; set; }
    public bool? Verified { get; set; }
}

public class PageQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }

    public DateTime? StartUtc => ToUtc(StartTime);
    public DateTime? EndUtc => ToUtc(EndTime);

    private static DateTime? ToUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        { } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };
}

// ---------------- 仪表盘 ----------------

public class DashboardStat
{
    public long OrderCount { get; set; }
    public long PaidCount { get; set; }
    public long PaidAmount { get; set; }
    public long RefundAmount { get; set; }
    public double SuccessRate { get; set; }
}

public class DashboardSummary
{
    public DashboardStat Today { get; set; } = new();
    public DashboardStat Yesterday { get; set; } = new();
    public long PendingNotify { get; set; }
    public long FailedNotify { get; set; }
    public long ProcessingRefund { get; set; }
    public long ActiveSubscriptions { get; set; }
    public long OpenReconDiffs { get; set; }
}

public class TrendPoint
{
    public string Date { get; set; } = "";
    public long OrderCount { get; set; }
    public long PaidCount { get; set; }
    public long PaidAmount { get; set; }
}

public class DistributionItem
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public long OrderCount { get; set; }
    public long PaidCount { get; set; }
    public long PaidAmount { get; set; }
    public double SuccessRate { get; set; }
}

// ---------------- 用户 / 审计 ----------------

public class UserItem
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public int Status { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UserInput
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
    public int Status { get; set; } = 1;
}

public class AuditQuery : PageQuery
{
    public string? Username { get; set; }
    public string? Action { get; set; }
}
