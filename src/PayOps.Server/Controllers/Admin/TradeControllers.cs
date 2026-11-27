using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Providers.Mock;
using PayOps.Server.Infrastructure;
using PayOps.Service.Admin;
using PayOps.Service.Infra;
using PayOps.Service.Pay;

namespace PayOps.Server.Controllers.Admin;

public sealed record AdminRefundRequest(long Amount, string? Reason);

public sealed class TestOrderRequest
{
    public long AppId { get; set; }
    public string ChannelCode { get; set; } = "";
    public string PayMethod { get; set; } = "";
    public long Amount { get; set; }
    public string? Subject { get; set; }
    public string? ProductId { get; set; }
    public string? OpenId { get; set; }
    public string? UserId { get; set; }
    public bool Subscribe { get; set; }
}

[Route("api/admin/orders")]
public sealed class OrdersController(
    AdminQueryService query,
    OrderService orders,
    RefundService refunds,
    NotifyService notify,
    UpstreamNotifyService upstream,
    ConfigCache cache,
    OrderNoGenerator noGenerator) : AdminControllerBase
{
    [HttpGet]
    public async Task<ApiResult> List([FromQuery] OrderQuery q) => Done(await query.ListOrdersAsync(q));

    [HttpGet("{orderNo}")]
    public async Task<ApiResult> Get(string orderNo) => Done(await query.GetOrderAsync(orderNo));

    private async Task<PayOrder> RequireAsync(string orderNo) =>
        await orders.FindAsync(orderNo) ?? throw new PayException(ErrorCodes.OrderNotFound, "订单不存在");

    [HttpPost("{orderNo}/sync")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Sync(string orderNo, CancellationToken ct)
    {
        var result = await orders.SyncAsync(await RequireAsync(orderNo), ct);
        await AuditAsync("订单补单", orderNo, result.Message);
        return Done(result);
    }

    [HttpPost("{orderNo}/close")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Close(string orderNo, CancellationToken ct)
    {
        await orders.CloseAsync(await RequireAsync(orderNo), $"后台关闭（{Username}）", ct);
        await AuditAsync("关闭订单", orderNo);
        return Done();
    }

    [HttpPost("{orderNo}/refund")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Refund(string orderNo, [FromBody] AdminRefundRequest request, CancellationToken ct)
    {
        var order = await RequireAsync(orderNo);
        var refund = await refunds.CreateAsync(order, new RefundRequest
        {
            OrderNo = orderNo,
            MchRefundNo = "ADM" + noGenerator.Next('A')[1..],
            Amount = request.Amount,
            Reason = request.Reason ?? "",
        }, "admin", ct);
        await AuditAsync("后台退款", orderNo, new { request.Amount, request.Reason, refund.RefundNo, status = (int)refund.Status });
        return Done(new { refund.RefundNo, status = (int)refund.Status });
    }

    [HttpPost("{orderNo}/renotify")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Renotify(string orderNo)
    {
        await notify.RenotifyOrderAsync(await RequireAsync(orderNo));
        await AuditAsync("重发业务通知", orderNo);
        return Done();
    }

    /// <summary>模拟通道订单：构造一份带签名的模拟上游回调，走与真实回调完全相同的处理链路。</summary>
    [HttpPost("{orderNo}/mock-pay")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> MockPay(string orderNo, CancellationToken ct)
    {
        var order = await RequireAsync(orderNo);
        if (order.Provider != MockProvider.ProviderCode) throw new PayException(ErrorCodes.NotSupported, "仅模拟通道订单支持模拟支付");
        if (order.Status != OrderStatus.Pending) throw new PayException(ErrorCodes.OrderStateError, "仅待支付订单可模拟支付");
        var passage = await cache.GetPassageAsync(order.PassageId) ?? throw new PayException(ErrorCodes.PassageError, "订单通道不存在");

        var reply = await upstream.HandleAsync(new UpstreamNotifyRequest
        {
            Provider = MockProvider.ProviderCode,
            PassageId = order.PassageId,
            HttpMethod = "POST",
            Headers = new Dictionary<string, string>(),
            Query = new Dictionary<string, string>(),
            Form = new Dictionary<string, string>(),
            Body = MockCallbackBody(order, passage.Config.Require("secret")),
            ClientIp = HttpContext.ClientIp(),
        }, ct);
        if (reply.StatusCode != 200) throw new PayException(ErrorCodes.PassageError, "模拟回调处理失败：" + reply.Body);
        await AuditAsync("模拟支付", orderNo);
        return Done();
    }

    /// <summary>订阅首购订单要回放下单凭据里那份带订阅信息的回调；一次性订单直接构造即可。</summary>
    private static string MockCallbackBody(PayOrder order, string secret)
    {
        if (order.OrderType == OrderType.SubscriptionInitial && !string.IsNullOrEmpty(order.Credential))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(order.Credential);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("body", out var body) && body.GetString() is { Length: > 0 } stored)
                return stored;
        }
        return MockProvider.BuildCallbackBody(secret, order.OrderNo, order.Amount);
    }

    /// <summary>后台测试下单：跳过接入签名，直接走真实的路由与下单流程。</summary>
    [HttpPost("test")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Test([FromBody] TestOrderRequest request, CancellationToken ct)
    {
        var app = (await cache.GetAppAsync(request.AppId))?.App ?? throw new PayException(ErrorCodes.InvalidParam, "应用不存在");
        var result = await orders.CreateAsync(app, new CreateOrderRequest
        {
            Channel = request.ChannelCode,
            PayMethod = request.PayMethod,
            MchOrderNo = "TEST" + noGenerator.Next('T')[1..],
            Amount = request.Amount,
            Subject = string.IsNullOrWhiteSpace(request.Subject) ? "后台测试下单" : request.Subject,
            ProductId = request.ProductId ?? "",
            OpenId = request.OpenId ?? "",
            UserId = request.UserId ?? "",
            Subscribe = request.Subscribe,
            ClientIp = HttpContext.ClientIp(),
        }, ct);
        await AuditAsync("测试下单", result.OrderNo, new { request.ChannelCode, request.PayMethod, request.Amount, result.PassageName });
        return Done(result);
    }
}

[Route("api/admin")]
public sealed class TradeQueryController(AdminQueryService query, RefundService refunds, NotifyService notify) : AdminControllerBase
{
    [HttpGet("refunds")]
    public async Task<ApiResult> Refunds([FromQuery] RefundQuery q) => Done(await query.ListRefundsAsync(q));

    [HttpPost("refunds/{refundNo}/sync")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> SyncRefund(string refundNo, CancellationToken ct)
    {
        var refund = await refunds.FindAsync(refundNo) ?? throw new PayException(ErrorCodes.RefundNotFound, "退款单不存在");
        var result = await refunds.SyncAsync(refund, ct);
        await AuditAsync("退款同步", refundNo, result.Message);
        return Done(result);
    }

    [HttpGet("notify-tasks")]
    public async Task<ApiResult> NotifyTasks([FromQuery] NotifyTaskQuery q) => Done(await query.ListNotifyTasksAsync(q));

    [HttpPost("notify-tasks/{id:long}/retry")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> RetryNotify(long id)
    {
        await notify.RetryAsync(id);
        await AuditAsync("重发通知任务", id.ToString());
        return Done();
    }

    [HttpGet("upstream-notifies")]
    public async Task<ApiResult> UpstreamNotifies([FromQuery] UpstreamNotifyQuery q) => Done(await query.ListUpstreamNotifiesAsync(q));
}
