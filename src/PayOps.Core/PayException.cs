namespace PayOps.Core;

/// <summary>业务异常：code 与 message 会原样返回给调用方。</summary>
public class PayException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ErrorCodes
{
    public const string InvalidParam = "INVALID_PARAM";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string SignError = "SIGN_ERROR";
    public const string AppDisabled = "APP_DISABLED";
    public const string ChannelNotFound = "CHANNEL_NOT_FOUND";
    public const string NoAvailablePassage = "NO_AVAILABLE_PASSAGE";
    public const string PassageError = "PASSAGE_ERROR";
    public const string OrderNotFound = "ORDER_NOT_FOUND";
    public const string OrderExists = "ORDER_EXISTS";
    public const string OrderPaid = "ORDER_PAID";
    public const string OrderStateError = "ORDER_STATE_ERROR";
    public const string RefundNotFound = "REFUND_NOT_FOUND";
    public const string RefundExceeded = "REFUND_EXCEEDED";
    public const string NotSupported = "NOT_SUPPORTED";
    public const string ReceiptInvalid = "RECEIPT_INVALID";
    public const string Busy = "SYSTEM_BUSY";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string Internal = "INTERNAL_ERROR";
}
