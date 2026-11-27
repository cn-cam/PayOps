using Microsoft.AspNetCore.Mvc;
using PayOps.Server.Infrastructure;
using PayOps.Service.Pay;

namespace PayOps.Server.Controllers.Gateway;

/// <summary>上游（微信/支付宝/抖音/各应用商店）回调入口。验签与应答格式由对应的支付类型实现决定。</summary>
[ApiController]
[ServiceRole("Gateway")]
public sealed class NotifyController(UpstreamNotifyService service) : ControllerBase
{
    [HttpGet("api/notify/{provider}/{passageId:long}/{kind?}")]
    [HttpPost("api/notify/{provider}/{passageId:long}/{kind?}")]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<IActionResult> Receive(string provider, long passageId, string? kind, CancellationToken ct)
    {
        var body = await Request.ReadRawBodyAsync();
        var isForm = Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true;

        var reply = await service.HandleAsync(new UpstreamNotifyRequest
        {
            Provider = provider.ToLowerInvariant(),
            PassageId = passageId,
            Kind = kind ?? "",
            HttpMethod = Request.Method,
            Headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            Query = Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal),
            Form = isForm ? HttpExtensions.ParseForm(body) : new Dictionary<string, string>(),
            Body = isForm ? "" : body,
            ClientIp = HttpContext.ClientIp(),
        }, ct);

        return new ContentResult { StatusCode = reply.StatusCode, ContentType = reply.ContentType, Content = reply.Body };
    }
}
