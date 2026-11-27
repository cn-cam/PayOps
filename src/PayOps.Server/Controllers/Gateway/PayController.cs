using Microsoft.AspNetCore.Mvc;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Core.Providers;
using PayOps.Server.Infrastructure;
using PayOps.Service.Infra;
using PayOps.Service.Pay;

namespace PayOps.Server.Controllers.Gateway;

public sealed class OrderRef
{
    public string? OrderNo { get; set; }
    public string? MchOrderNo { get; set; }
}

public sealed class VerifyReceiptRequest
{
    public string? OrderNo { get; set; }
    public string? MchOrderNo { get; set; }
    /// <summary>客户端 SDK 返回的凭证：华为/荣耀 purchaseToken(+productId)，苹果 transactionId 或 signedTransaction。</summary>
    public Dictionary<string, string> Receipt { get; set; } = [];
}

/// <summary>业务接入 API（服务端到服务端，HMAC 签名）。</summary>
[ApiController]
[ServiceRole("Gateway")]
[AppAuth]
[Route("api/pay")]
public sealed class PayController(
    OrderService orders,
    RefundService refunds,
    RouteService router,
    ConfigCache cache,
    IPayProviderRegistry providers) : ControllerBase
{
    /// <summary>统一下单：按 渠道 + 支付方式 + 金额 路由到具体通道。</summary>
    [HttpPost("orders")]
    public async Task<ApiResult> Create([FromBody] CreateOrderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ClientIp)) request.ClientIp = HttpContext.ClientIp();
        return ApiResult.Ok(await orders.CreateAsync(HttpContext.CurrentApp(), request, ct));
    }

    /// <summary>查询订单；sync=true 时对待支付订单主动查询上游。</summary>
    [HttpGet("orders")]
    public async Task<ApiResult> Query([FromQuery] string? orderNo, [FromQuery] string? mchOrderNo, [FromQuery] bool sync, CancellationToken ct)
    {
        var order = await orders.RequireAsync(HttpContext.CurrentApp().Id, orderNo, mchOrderNo);
        if (sync && order.Status == OrderStatus.Pending)
        {
            var result = await orders.SyncAsync(order, ct);
            if (result.Changed) order = (await orders.FindAsync(order.OrderNo))!;
        }
        return ApiResult.Ok(ToView(order));
    }

    [HttpPost("orders/close")]
    public async Task<ApiResult> Close([FromBody] OrderRef request, CancellationToken ct)
    {
        var order = await orders.RequireAsync(HttpContext.CurrentApp().Id, request.OrderNo, request.MchOrderNo);
        await orders.CloseAsync(order, "业务方关闭", ct);
        return ApiResult.Ok(ToView((await orders.FindAsync(order.OrderNo))!));
    }

    /// <summary>应用商店内购凭证核验（华为 / 荣耀 / 苹果）：核验通过即入账并触发业务通知。</summary>
    [HttpPost("orders/verify")]
    public async Task<ApiResult> Verify([FromBody] VerifyReceiptRequest request, CancellationToken ct)
    {
        var order = await orders.RequireAsync(HttpContext.CurrentApp().Id, request.OrderNo, request.MchOrderNo);
        return ApiResult.Ok(ToView(await orders.VerifyReceiptAsync(order, request.Receipt, ct)));
    }

    [HttpPost("refunds")]
    public async Task<ApiResult> Refund([FromBody] RefundRequest request, CancellationToken ct)
    {
        var order = await orders.RequireAsync(HttpContext.CurrentApp().Id, request.OrderNo, request.MchOrderNo);
        return ApiResult.Ok(ToView(await refunds.CreateAsync(order, request, "api", ct)));
    }

    [HttpGet("refunds")]
    public async Task<ApiResult> QueryRefund([FromQuery] string? refundNo, [FromQuery] string? mchRefundNo, [FromQuery] bool sync, CancellationToken ct)
    {
        var refund = await refunds.RequireAsync(HttpContext.CurrentApp().Id, refundNo, mchRefundNo);
        if (sync && refund.Status == RefundStatus.Processing)
        {
            await refunds.SyncAsync(refund, ct);
            refund = (await refunds.FindAsync(refund.RefundNo))!;
        }
        return ApiResult.Ok(ToView(refund));
    }

    /// <summary>收银台可用支付方式：返回该渠道在给定金额下当前有可用通道的支付方式。</summary>
    [HttpGet("methods")]
    public async Task<ApiResult> Methods([FromQuery] string channel, [FromQuery] long amount)
    {
        var app = HttpContext.CurrentApp();
        var ch = await cache.GetChannelAsync(app.Id, channel ?? "");
        if (ch is null || ch.Status != 1) throw new PayException(ErrorCodes.ChannelNotFound, $"渠道不存在或已停用：{channel}");
        if (amount <= 0) amount = 1;

        var bindings = await cache.GetBindingsAsync(ch.Id);
        var result = new List<object>();
        foreach (var provider in providers.All.Where(p => bindings.Any(b => b.Provider.Equals(p.Meta.Code, StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var method in provider.Meta.Methods)
            {
                var evaluations = await router.EvaluateAsync(ch.Id, provider.Meta.Code, method.Code, amount);
                if (evaluations.Any(e => e.Eligible))
                    result.Add(new { payMethod = method.Code, name = method.Name, provider = provider.Meta.Code, providerName = provider.Meta.Name });
            }
        }
        return ApiResult.Ok(result);
    }

    private static object ToView(PayOrder o) => new
    {
        o.OrderNo,
        o.MchOrderNo,
        channel = o.ChannelCode,
        o.Provider,
        o.PayMethod,
        o.Amount,
        o.RefundAmount,
        o.Currency,
        status = (int)o.Status,
        upstreamTradeNo = o.UpstreamTradeNo ?? "",
        o.UserId,
        o.ProductId,
        o.Attach,
        o.ErrorMsg,
        o.CreatedAt,
        o.PaidAt,
        o.ExpireAt,
    };

    private static object ToView(PayRefund r) => new
    {
        r.RefundNo,
        r.MchRefundNo,
        r.OrderNo,
        r.Amount,
        status = (int)r.Status,
        upstreamRefundNo = r.UpstreamRefundNo ?? "",
        r.Reason,
        r.ErrorMsg,
        r.CreatedAt,
        r.SuccessAt,
    };
}
