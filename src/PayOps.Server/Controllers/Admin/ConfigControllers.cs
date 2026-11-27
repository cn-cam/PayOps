using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayOps.Core.Domain;
using PayOps.Server.Infrastructure;
using PayOps.Service.Admin;

namespace PayOps.Server.Controllers.Admin;

[Route("api/admin/apps")]
public sealed class AppsController(AdminConfigService config) : AdminControllerBase
{
    [HttpGet]
    public async Task<ApiResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? keyword = null, [FromQuery] int? status = null) =>
        Done(await config.ListAppsAsync(page, pageSize, keyword, status));

    [HttpGet("all")]
    public async Task<ApiResult> All() =>
        Done((await config.AllAppsAsync()).Select(a => new { a.Id, a.AppCode, a.Name, a.Status }));

    [HttpPost]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Create([FromBody] AppInput input)
    {
        var (id, secret) = await config.CreateAppAsync(input);
        await AuditAsync("新增应用", input.AppCode, new { input.AppCode, input.Name, input.NotifyUrl });
        return Done(new { id, appSecret = secret });
    }

    [HttpPut("{id:long}")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Update(long id, [FromBody] AppInput input)
    {
        await config.UpdateAppAsync(id, input);
        await AuditAsync("修改应用", id.ToString(), new { input.Name, input.NotifyUrl, input.Status });
        return Done();
    }

    [HttpDelete("{id:long}")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Delete(long id)
    {
        await config.DeleteAppAsync(id);
        await AuditAsync("删除应用", id.ToString());
        return Done();
    }

    [HttpGet("{id:long}/secret")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ApiResult> Secret(long id)
    {
        var secret = await config.GetAppSecretAsync(id);
        await AuditAsync("查看应用密钥", id.ToString());
        return Done(new { appSecret = secret });
    }

    [HttpPost("{id:long}/reset-secret")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ApiResult> ResetSecret(long id)
    {
        var secret = await config.ResetAppSecretAsync(id);
        await AuditAsync("重置应用密钥", id.ToString());
        return Done(new { appSecret = secret });
    }
}

[Route("api/admin")]
public sealed class ChannelsController(AdminConfigService config) : AdminControllerBase
{
    [HttpGet("channels")]
    public async Task<ApiResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] long? appId = null, [FromQuery] string? keyword = null, [FromQuery] int? status = null) =>
        Done(await config.ListChannelsAsync(page, pageSize, appId, keyword, status));

    [HttpGet("channels/all")]
    public async Task<ApiResult> All([FromQuery] long? appId) =>
        Done((await config.AllChannelsAsync(appId)).Select(c => new { c.Id, c.AppId, c.Code, c.Name, c.Status }));

    [HttpGet("channels/{id:long}")]
    public async Task<ApiResult> Get(long id) => Done(await config.GetChannelAsync(id));

    [HttpPost("channels")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Create([FromBody] ChannelInput input)
    {
        var id = await config.CreateChannelAsync(input);
        await AuditAsync("新增渠道", input.Code, input);
        return Done(new { id });
    }

    [HttpPut("channels/{id:long}")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Update(long id, [FromBody] ChannelInput input)
    {
        await config.UpdateChannelAsync(id, input);
        await AuditAsync("修改渠道", id.ToString(), new { input.Name, input.Status });
        return Done();
    }

    [HttpDelete("channels/{id:long}")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Delete(long id)
    {
        await config.DeleteChannelAsync(id);
        await AuditAsync("删除渠道", id.ToString());
        return Done();
    }

    [HttpPost("channels/{id:long}/copy")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Copy(long id, [FromBody] ChannelCopyInput input)
    {
        var newId = await config.CopyChannelAsync(id, input);
        await AuditAsync("复制渠道", id.ToString(), input);
        return Done(new { id = newId });
    }

    // ---------------- 渠道通道绑定（路由规则） ----------------

    [HttpGet("channels/{channelId:long}/bindings")]
    public async Task<ApiResult> Bindings(long channelId) => Done(await config.ListBindingsAsync(channelId));

    [HttpPost("channels/{channelId:long}/bindings")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> CreateBinding(long channelId, [FromBody] BindingInput input)
    {
        var id = await config.CreateBindingAsync(channelId, input);
        await AuditAsync("新增渠道通道绑定", $"channel:{channelId}", input);
        return Done(new { id });
    }

    [HttpPut("bindings/{id:long}")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> UpdateBinding(long id, [FromBody] BindingInput input)
    {
        await config.UpdateBindingAsync(id, input);
        await AuditAsync("修改渠道通道绑定", $"binding:{id}", input);
        return Done();
    }

    [HttpDelete("bindings/{id:long}")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> DeleteBinding(long id)
    {
        await config.DeleteBindingAsync(id);
        await AuditAsync("删除渠道通道绑定", $"binding:{id}");
        return Done();
    }

    [HttpPost("bindings/{id:long}/reset-breaker")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> ResetBreaker(long id)
    {
        await config.ResetBreakerAsync(id);
        await AuditAsync("手动恢复熔断", $"binding:{id}");
        return Done();
    }

    [HttpPost("channels/{channelId:long}/route-simulate")]
    public async Task<ApiResult> Simulate(long channelId, [FromBody] RouteSimulateInput input) =>
        Done(await config.SimulateAsync(channelId, input));
}

[Route("api/admin/passages")]
public sealed class PassagesController(AdminConfigService config) : AdminControllerBase
{
    [HttpGet]
    public async Task<ApiResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? provider = null, [FromQuery] string? keyword = null, [FromQuery] int? status = null) =>
        Done(await config.ListPassagesAsync(page, pageSize, provider, keyword, status));

    [HttpGet("all")]
    public async Task<ApiResult> All([FromQuery] string? provider) =>
        Done((await config.AllPassagesAsync(provider)).Select(p => new { p.Id, p.Name, p.Provider, p.ProviderName, p.Status }));

    [HttpGet("{id:long}")]
    public async Task<ApiResult> Get(long id) => Done(await config.GetPassageAsync(id));

    [HttpPost]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Create([FromBody] PassageInput input)
    {
        var id = await config.CreatePassageAsync(input);
        await AuditAsync("新增通道", input.Name, new { input.Name, input.Provider });
        return Done(new { id });
    }

    [HttpPut("{id:long}")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Update(long id, [FromBody] PassageInput input)
    {
        await config.UpdatePassageAsync(id, input);
        // 审计只记录被修改的配置项名称，不落任何凭证内容
        await AuditAsync("修改通道", id.ToString(), new { input.Name, input.Status, configKeys = input.Config?.Keys });
        return Done();
    }

    [HttpDelete("{id:long}")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ApiResult> Delete(long id)
    {
        await config.DeletePassageAsync(id);
        await AuditAsync("删除通道", id.ToString());
        return Done();
    }

    [HttpPost("{id:long}/test")]
    public async Task<ApiResult> Test(long id)
    {
        var (ok, message) = await config.TestPassageAsync(id);
        return Done(new { ok, message });
    }
}
