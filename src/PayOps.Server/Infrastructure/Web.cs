using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Core.Security;
using PayOps.Service.Admin;
using PayOps.Service.Pay;

namespace PayOps.Server.Infrastructure;

/// <summary>统一响应包：code = 0 成功；非 0 失败，errorCode 为机器可读错误码，message 可直接展示。</summary>
public sealed record ApiResult(int Code, string Message, object? Data = null, string? ErrorCode = null)
{
    public static ApiResult Ok(object? data = null) => new(0, "ok", data);
    public static ApiResult Fail(string errorCode, string message, int code = 1) => new(code, message, null, errorCode);
}

/// <summary>标记控制器所属的进程角色；进程未启用该角色时控制器不会被注册。</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ServiceRoleAttribute(string role) : Attribute
{
    public string Role { get; } = role;
}

public sealed class RoleControllerFeatureProvider(PayOpsOptions options) : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo typeInfo)
    {
        if (!base.IsController(typeInfo)) return false;
        var role = typeInfo.GetCustomAttribute<ServiceRoleAttribute>();
        return role is null || options.HasRole(role.Role);
    }
}

public sealed class ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case PayException ex:
                context.Result = new ObjectResult(ApiResult.Fail(ex.Code, ex.Message))
                {
                    StatusCode = ex.Code is ErrorCodes.Unauthorized or ErrorCodes.SignError or ErrorCodes.AppDisabled ? 401 : 200,
                };
                break;
            case OperationCanceledException when context.HttpContext.RequestAborted.IsCancellationRequested:
                context.Result = new StatusCodeResult(499);
                break;
            default:
                logger.LogError(context.Exception, "未处理异常 {Path}", context.HttpContext.Request.Path);
                context.Result = new ObjectResult(ApiResult.Fail(ErrorCodes.Internal, "系统内部错误，请稍后重试", 500)) { StatusCode = 500 };
                break;
        }
        context.ExceptionHandled = true;
    }
}

/// <summary>把模型绑定错误转成统一响应包。</summary>
public static class InvalidModelState
{
    public static IActionResult Create(ActionContext context)
    {
        var first = context.ModelState.Where(kv => kv.Value?.Errors.Count > 0)
            .Select(kv => $"{kv.Key}: {kv.Value!.Errors[0].ErrorMessage}")
            .FirstOrDefault() ?? "请求参数格式不正确";
        return new OkObjectResult(ApiResult.Fail(ErrorCodes.InvalidParam, "请求参数格式不正确（" + first + "）"));
    }
}

/// <summary>
/// 业务接入签名校验。在模型绑定之前读取原始请求体做 HMAC 校验，通过后把应用放入 HttpContext.Items。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AppAuthAttribute : Attribute, IAsyncResourceFilter
{
    public const string ItemKey = "payops.app";

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var request = http.Request;
        var body = await request.ReadRawBodyAsync();
        try
        {
            var app = await http.RequestServices.GetRequiredService<AppAuthService>().AuthenticateAsync(
                request.Headers[ApiSigner.HeaderAppId].ToString(),
                request.Headers[ApiSigner.HeaderTimestamp].ToString(),
                request.Headers[ApiSigner.HeaderNonce].ToString(),
                request.Headers[ApiSigner.HeaderSign].ToString(),
                request.Method,
                request.PathBase + request.Path + request.QueryString,
                body);
            http.Items[ItemKey] = app;
        }
        catch (PayException ex)
        {
            context.Result = new ObjectResult(ApiResult.Fail(ex.Code, ex.Message)) { StatusCode = 401 };
            return;
        }
        await next();
    }
}

public static class HttpExtensions
{
    public static async Task<string> ReadRawBodyAsync(this HttpRequest request)
    {
        request.EnableBuffering();
        request.Body.Position = 0;
        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        request.Body.Position = 0;
        return body;
    }

    public static PayApp CurrentApp(this HttpContext http) =>
        http.Items[AppAuthAttribute.ItemKey] as PayApp ?? throw new PayException(ErrorCodes.Unauthorized, "未鉴权");

    public static string ClientIp(this HttpContext http) => http.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "";

    public static Dictionary<string, string> ParseForm(string body) =>
        QueryHelpers.ParseQuery(body).ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
}

/// <summary>管理后台控制器基类：当前用户与审计。</summary>
[ApiController]
[ServiceRole("Admin")]
[Microsoft.AspNetCore.Authorization.Authorize]
public abstract class AdminControllerBase : ControllerBase
{
    protected const string WriteRoles = Roles.Admin + "," + Roles.Operator;

    protected long UserId => long.TryParse(User.FindFirstValue("sub"), out var id) ? id : 0;

    protected string Username => User.FindFirstValue("name") ?? "";

    protected static ApiResult Done(object? data = null) => ApiResult.Ok(data);

    protected Task AuditAsync(string action, string target, object? detail = null) =>
        HttpContext.RequestServices.GetRequiredService<AdminUserService>().AuditAsync(
            UserId, Username, action, target,
            detail is null ? "" : detail as string ?? System.Text.Json.JsonSerializer.Serialize(detail, JsonDefaults.Web),
            HttpContext.ClientIp());
}
