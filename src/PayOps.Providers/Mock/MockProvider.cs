using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Mock;

/// <summary>
/// 演示/联调用的模拟通道：不请求任何上游。下单返回一份可直接 POST 到回调地址的模拟回调，
/// 可配置"下单失败"以演示多通道故障转移与熔断。生产环境请勿绑定到正式渠道。
/// </summary>
public sealed class MockProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    public const string ProviderCode = "mock";

    public override ProviderMeta Meta { get; } = new()
    {
        Code = ProviderCode,
        Name = "模拟支付",
        Category = "mock",
        Methods = [new("mock", "模拟支付")],
        Capabilities = new() { Close = true, Refund = true },
        ConfigFields =
        [
            new() { Key = "secret", Label = "回调签名密钥", Type = "password", Required = true, Secret = true, DefaultValue = "mock-secret", Help = "模拟上游回调的 HMAC 密钥" },
            new() { Key = "failCreate", Label = "模拟下单失败", Type = "switch", DefaultValue = "false", Help = "开启后该通道下单必定失败，用于演示故障转移与熔断" },
            new() { Key = "refundAsync", Label = "退款异步到账", Type = "switch", DefaultValue = "false", Help = "开启后退款先返回处理中，由后台查单任务置为成功" },
        ],
        NotifyNote = "模拟通道无真实上游。可在后台订单详情点击「模拟支付」，或把下单凭据中的 body 以 POST 发送到 notifyUrl。",
    };

    public override Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct)
    {
        if (ctx.Config.GetBool("failCreate"))
            throw Upstream("MOCK_FAIL", "模拟通道：下单失败（failCreate 已开启）");
        var body = BuildCallbackBody(ctx.Config.Require("secret"), ctx.OrderNo, ctx.Amount);
        return Task.FromResult(new PayCredential
        {
            Type = CredentialTypes.Mock,
            Data = { ["notifyUrl"] = ctx.NotifyUrl, ["method"] = "POST", ["body"] = body },
        });
    }

    public static string BuildCallbackBody(string secret, string orderNo, long amount)
    {
        var tradeNo = "MOCK" + orderNo;
        return JsonSerializer.Serialize(new { orderNo, tradeNo, amount, sign = Sign(secret, orderNo, tradeNo, amount) });
    }

    public static string Sign(string secret, string orderNo, string tradeNo, long amount) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{orderNo}|{tradeNo}|{amount}")));

    public override Task<RefundResult> RefundAsync(RefundContext ctx, CancellationToken ct) =>
        Task.FromResult(new RefundResult
        {
            State = ctx.Config.GetBool("refundAsync") ? UpstreamRefundState.Processing : UpstreamRefundState.Success,
            UpstreamRefundNo = "MOCK" + ctx.RefundNo,
        });

    public override Task<RefundResult> QueryRefundAsync(RefundContext ctx, CancellationToken ct) =>
        Task.FromResult(new RefundResult { State = UpstreamRefundState.Success, UpstreamRefundNo = "MOCK" + ctx.RefundNo });

    public override Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var json = ParseJson(ctx.Body, Meta.Name);
        var orderNo = json.Str("orderNo");
        var tradeNo = json.Str("tradeNo");
        var amount = json.Long("amount") ?? -1;
        var expected = Sign(ctx.Config.Require("secret"), orderNo, tradeNo, amount);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(json.Str("sign"))))
            return Task.FromResult(NotifyParseResult.Fail("验签失败"));
        return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
        {
            Type = NotifyEventType.Paid,
            OrderNo = orderNo,
            UpstreamTradeNo = tradeNo,
            Amount = amount,
            PaidAt = DateTime.UtcNow,
            BuyerId = "mock-buyer",
        }));
    }
}
