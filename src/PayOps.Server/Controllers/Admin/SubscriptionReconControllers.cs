using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Providers.Mock;
using PayOps.Server.Infrastructure;
using PayOps.Service.Admin;
using PayOps.Service.Data;
using PayOps.Service.Infra;
using PayOps.Service.Pay;

namespace PayOps.Server.Controllers.Admin;

public sealed record MockSubscriptionEventRequest(string Event);

[Route("api/admin/subscriptions")]
public sealed class SubscriptionsController(
    AdminQueryService query,
    SubscriptionService subscriptions,
    UpstreamNotifyService upstream,
    ConfigCache cache) : AdminControllerBase
{
    [HttpGet]
    public async Task<ApiResult> List([FromQuery] SubscriptionQuery q) => Done(await query.ListSubscriptionsAsync(q));

    [HttpGet("{subNo}")]
    public async Task<ApiResult> Get(string subNo) => Done(await query.GetSubscriptionAsync(subNo));

    private async Task<PaySubscription> RequireAsync(string subNo) =>
        await subscriptions.FindAsync(subNo) ?? throw new PayException(ErrorCodes.NotFound, "订阅不存在");

    [HttpPost("{subNo}/sync")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Sync(string subNo, CancellationToken ct)
    {
        var result = await subscriptions.SyncAsync(await RequireAsync(subNo), ct);
        await AuditAsync("订阅同步", subNo, result.Message);
        return Done(result);
    }

    [HttpPost("{subNo}/cancel")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Cancel(string subNo, CancellationToken ct)
    {
        await subscriptions.CancelAsync(await RequireAsync(subNo), ct);
        await AuditAsync("后台退订", subNo);
        return Done();
    }

    /// <summary>模拟通道的订阅事件：构造带签名的模拟上游回调，走与真实商店通知完全相同的处理链路。</summary>
    [HttpPost("{subNo}/mock-event")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> MockEvent(string subNo, [FromBody] MockSubscriptionEventRequest request, CancellationToken ct)
    {
        var sub = await RequireAsync(subNo);
        if (sub.Provider != MockProvider.ProviderCode) throw new PayException(ErrorCodes.NotSupported, "仅模拟通道的订阅支持模拟事件");
        var passage = await cache.GetPassageAsync(sub.PassageId) ?? throw new PayException(ErrorCodes.PassageError, "订阅所属通道不存在");
        var secret = passage.Config.Require("secret");

        string body;
        switch (request.Event)
        {
            case "renew":
                // 新的一期：从当前到期时间（已过期则从现在）顺延一个周期，交易号每期不同
                var from = sub.ExpiresAt is { } e && e > DateTime.UtcNow ? e : DateTime.UtcNow;
                var expiresAt = new DateTimeOffset(from.AddMinutes(MockProvider.PeriodMinutes(passage.Config))).ToUnixTimeSeconds();
                body = MockProvider.BuildSubscriptionCallback(secret, MockProvider.EventCharged, sub.UpstreamSubId, "",
                    $"MOCKRENEW{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", sub.Amount, expiresAt);
                break;
            case "cancel" or "resume" or "expire" or "revoke":
                var name = request.Event switch
                {
                    "cancel" => MockProvider.EventCancel,
                    "resume" => MockProvider.EventResume,
                    "expire" => MockProvider.EventExpire,
                    _ => MockProvider.EventRevoke,
                };
                body = MockProvider.BuildSubscriptionCallback(secret, name, sub.UpstreamSubId, "", "", 0, 0);
                break;
            default:
                throw new PayException(ErrorCodes.InvalidParam, "event 仅支持 renew / cancel / resume / expire / revoke");
        }

        var reply = await upstream.HandleAsync(new UpstreamNotifyRequest
        {
            Provider = MockProvider.ProviderCode,
            PassageId = sub.PassageId,
            HttpMethod = "POST",
            Headers = new Dictionary<string, string>(),
            Query = new Dictionary<string, string>(),
            Form = new Dictionary<string, string>(),
            Body = body,
            ClientIp = HttpContext.ClientIp(),
        }, ct);
        if (reply.StatusCode != 200) throw new PayException(ErrorCodes.PassageError, "模拟事件处理失败：" + reply.Body);
        await AuditAsync("模拟订阅事件", subNo, request.Event);
        return Done();
    }
}

public sealed record ReconTriggerRequest(long PassageId, string BillDate);

public sealed record ReconHandleRequest(string Remark);

[Route("api/admin/recon")]
public sealed class ReconController(AdminQueryService query, ReconService recon, Db db) : AdminControllerBase
{
    [HttpGet("batches")]
    public async Task<ApiResult> Batches([FromQuery] ReconBatchQuery q) => Done(await query.ListReconBatchesAsync(q));

    [HttpPost("batches")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Trigger([FromBody] ReconTriggerRequest request)
    {
        var id = await recon.TriggerAsync(request.PassageId, request.BillDate ?? "");
        await AuditAsync("发起对账", $"passage:{request.PassageId}", request.BillDate);
        return Done(new { id });
    }

    [HttpPost("batches/{id:long}/rerun")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Rerun(long id)
    {
        var batch = await db.GetAsync<PayReconBatch>(id) ?? throw new PayException(ErrorCodes.NotFound, "对账批次不存在");
        await recon.TriggerAsync(batch.PassageId, batch.BillDate);
        await AuditAsync("重跑对账", $"batch:{id}", batch.BillDate);
        return Done();
    }

    [HttpGet("diffs")]
    public async Task<ApiResult> Diffs([FromQuery] ReconDiffQuery q) => Done(await query.ListReconDiffsAsync(q));

    [HttpPost("diffs/{id:long}/handle")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Handle(long id, [FromBody] ReconHandleRequest request)
    {
        await recon.HandleDiffAsync(id, request.Remark ?? "", Username);
        await AuditAsync("处理对账差异", $"diff:{id}", request.Remark);
        return Done();
    }
}
