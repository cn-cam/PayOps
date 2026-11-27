using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PayOps.Core;
using PayOps.Core.Security;
using PayOps.Server.Infrastructure;
using PayOps.Service.Infra;

namespace PayOps.Server.Controllers.Gateway;

/// <summary>
/// 演示用的"业务方通知接收端"：校验 PayOps 的通知签名后应答 success，便于在没有业务系统时走通完整闭环。
/// 仅当 PayOps:EnableDemoEndpoints = true 时可用，同时也是业务方接入通知验签的参考实现。
/// </summary>
[ApiController]
[ServiceRole("Gateway")]
public sealed class DemoMerchantController(ConfigCache cache, IOptions<PayOpsOptions> options, ILogger<DemoMerchantController> logger) : ControllerBase
{
    [HttpPost("api/demo/merchant-notify")]
    public async Task<IActionResult> Receive()
    {
        if (!options.Value.EnableDemoEndpoints) return NotFound();

        var body = await Request.ReadRawBodyAsync();
        var appId = Request.Headers[ApiSigner.HeaderAppId].ToString();
        var app = await cache.GetAppByCodeAsync(appId);
        var valid = app is not null && ApiSigner.Verify(
            app.Secret, "POST", Request.Path + Request.QueryString,
            Request.Headers[ApiSigner.HeaderTimestamp].ToString(),
            Request.Headers[ApiSigner.HeaderNonce].ToString(),
            body,
            Request.Headers[ApiSigner.HeaderSign].ToString());
        if (!valid)
        {
            logger.LogWarning("演示接收端：通知验签失败 {Body}", body);
            return Content("sign error");
        }

        logger.LogInformation("演示接收端：收到业务通知 {Body}", body);
        return Content("success");
    }
}
