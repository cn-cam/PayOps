using Microsoft.Extensions.Logging;
using PayOps.Core.Domain;
using PayOps.Core.Providers;
using PayOps.Service.Data;
using PayOps.Service.Infra;

namespace PayOps.Service.Pay;

public sealed class UpstreamNotifyRequest
{
    public required string Provider { get; init; }
    public required long PassageId { get; init; }
    public string Kind { get; init; } = "";
    public required string HttpMethod { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required IReadOnlyDictionary<string, string> Query { get; init; }
    public required IReadOnlyDictionary<string, string> Form { get; init; }
    public required string Body { get; init; }
    public string ClientIp { get; init; } = "";
}

/// <summary>
/// 上游回调统一入口：/api/notify/{provider}/{passageId}[/{kind}]。
/// 通道 ID 在 URL 中，用于定位验签密钥；验签通过后还会校验订单确实属于该通道，
/// 防止用 A 通道（例如模拟通道）的密钥为 B 通道的订单伪造支付成功。
/// </summary>
public sealed class UpstreamNotifyService(
    Db db,
    ConfigCache cache,
    OrderService orders,
    RefundService refunds,
    SubscriptionService subscriptions,
    IPayProviderRegistry providers,
    ILogger<UpstreamNotifyService> logger)
{
    public async Task<NotifyReply> HandleAsync(UpstreamNotifyRequest req, CancellationToken ct)
    {
        var provider = providers.Find(req.Provider);
        if (provider is null) return NotifyReply.Text("unknown provider", 404);

        var passage = await cache.GetPassageAsync(req.PassageId);
        if (passage is null || !passage.Passage.Provider.Equals(provider.Meta.Code, StringComparison.OrdinalIgnoreCase))
        {
            await LogAsync(req, "", false, "通道不存在或类型不匹配");
            return provider.BuildReply(NotifyOutcome.Error, "passage not found");
        }

        NotifyParseResult parsed;
        try
        {
            parsed = await provider.ParseNotifyAsync(new NotifyContext
            {
                PassageId = req.PassageId,
                Config = passage.Config,
                Kind = req.Kind,
                HttpMethod = req.HttpMethod,
                Headers = req.Headers,
                Query = req.Query,
                Form = req.Form,
                Body = req.Body,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "解析 {Provider} 回调失败（通道 {PassageId}）", req.Provider, req.PassageId);
            await LogAsync(req, "", false, "解析失败：" + ex.Message);
            return provider.BuildReply(NotifyOutcome.Error, "parse error");
        }

        if (!parsed.Verified)
        {
            logger.LogWarning("{Provider} 回调验签未通过（通道 {PassageId}）：{Message}", req.Provider, req.PassageId, parsed.Message);
            await LogAsync(req, "", false, parsed.Message);
            return provider.BuildReply(NotifyOutcome.SignError, "sign error");
        }

        var outcome = NotifyOutcome.Success;
        var messages = new List<string>();
        var orderNo = "";
        if (parsed.Events.Count == 0) messages.Add(parsed.Message.Length > 0 ? parsed.Message : "无需处理");

        foreach (var e in parsed.Events)
        {
            if (e.OrderNo.Length > 0) orderNo = e.OrderNo;
            NotifyOutcome one;
            string message;
            try
            {
                (one, message) = e.Type switch
                {
                    NotifyEventType.Paid => await HandlePaidAsync(e, req),
                    NotifyEventType.Refund => await refunds.HandleUpstreamEventAsync(e, provider.Meta.Code, req.PassageId)
                        ? (NotifyOutcome.Success, "退款事件已处理")
                        : (NotifyOutcome.OrderNotFound, "退款事件找不到对应单据"),
                    NotifyEventType.Subscription => await subscriptions.HandleEventAsync(e, provider.Meta.Code, req.PassageId),
                    NotifyEventType.Agreement => await subscriptions.HandleAgreementAsync(e, provider.Meta.Code, req.PassageId),
                    _ => (NotifyOutcome.Success, e.Message.Length > 0 ? e.Message : "忽略"),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "处理 {Provider} 回调事件失败，订单 {OrderNo}", req.Provider, e.OrderNo);
                (one, message) = (NotifyOutcome.Error, "处理异常：" + ex.Message);
            }
            messages.Add(message);
            if (one != NotifyOutcome.Success) outcome = one;
        }

        var summary = string.Join("；", messages);
        await LogAsync(req, orderNo, true, summary);
        return provider.BuildReply(outcome, outcome == NotifyOutcome.Success ? "success" : summary);
    }

    private async Task<(NotifyOutcome, string)> HandlePaidAsync(NotifyEvent e, UpstreamNotifyRequest req)
    {
        var order = await orders.FindAsync(e.OrderNo);
        if (order is null) return (NotifyOutcome.OrderNotFound, $"订单 {e.OrderNo} 不存在");
        if (order.PassageId != req.PassageId)
        {
            logger.LogError("回调通道 {PassageId} 与订单 {OrderNo} 所属通道 {OrderPassage} 不一致，拒绝入账", req.PassageId, order.OrderNo, order.PassageId);
            return (NotifyOutcome.OrderNotFound, "订单不属于该通道");
        }
        if (!string.IsNullOrEmpty(e.ProductId) && order.ProductId.Length > 0 && e.ProductId != order.ProductId)
            return (NotifyOutcome.AmountMismatch, "商品与订单不一致");

        var outcome = await orders.HandlePaidAsync(order, new PaidInfo(e.UpstreamTradeNo, e.Amount, e.PaidAt, e.BuyerId, e.Subscription), "notify");
        return outcome switch
        {
            PaidOutcome.Success => (NotifyOutcome.Success, "支付成功已入账"),
            PaidOutcome.AlreadyPaid => (NotifyOutcome.Success, "重复通知，订单已支付"),
            PaidOutcome.OrderNotFound => (NotifyOutcome.OrderNotFound, "订单不存在"),
            PaidOutcome.AmountMismatch => (NotifyOutcome.AmountMismatch, "金额与订单不一致"),
            PaidOutcome.TradeReused => (NotifyOutcome.Error, "上游交易号已被其他订单使用"),
            _ => (NotifyOutcome.Error, "订单已由其他交易支付"),
        };
    }

    private async Task LogAsync(UpstreamNotifyRequest req, string orderNo, bool verified, string result)
    {
        try
        {
            var body = req.Body.Length > 0 ? req.Body : string.Join('&', (req.Form.Count > 0 ? req.Form : req.Query).Select(kv => $"{kv.Key}={kv.Value}"));
            await db.InsertAsync(new PayUpstreamNotify
            {
                Provider = req.Provider,
                PassageId = req.PassageId,
                Kind = req.Kind,
                OrderNo = orderNo.Length > 32 ? orderNo[..32] : orderNo,
                Verified = verified,
                Result = result.Length > 500 ? result[..500] : result,
                RequestBody = body.Length > 8000 ? body[..8000] : body,
                ClientIp = req.ClientIp,
                CreatedAt = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning("记录上游回调日志失败：{Message}", ex.Message);
        }
    }
}
