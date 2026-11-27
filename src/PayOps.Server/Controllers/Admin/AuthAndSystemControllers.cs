using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Core.Providers;
using PayOps.Server.Infrastructure;
using PayOps.Service.Admin;
using PayOps.Service.Data;
using PayOps.Service.Infra;

namespace PayOps.Server.Controllers.Admin;

public static class JwtKeys
{
    /// <summary>由配置的 JwtKey 派生 256 位签名密钥，所有副本配置一致即可互认令牌。</summary>
    public static SymmetricSecurityKey Create(string jwtKey) => new(SHA256.HashData(Encoding.UTF8.GetBytes(jwtKey)));
}

public sealed record LoginRequest(string Username, string Password);

public sealed record ChangePasswordRequest(string OldPassword, string NewPassword);

public sealed record ResetPasswordRequest(string Password);

[Route("api/admin/auth")]
public sealed class AuthController(AdminUserService users, IOptions<PayOpsOptions> options) : AdminControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ApiResult> Login([FromBody] LoginRequest request)
    {
        var user = await users.LoginAsync(request.Username ?? "", request.Password ?? "");
        var security = options.Value.Security;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim("sub", user.Id.ToString()),
                new Claim("name", user.Username),
                new Claim("role", user.Role),
            ]),
            Issuer = "payops",
            Audience = "payops-admin",
            Expires = DateTime.UtcNow.AddMinutes(security.JwtExpireMinutes),
            SigningCredentials = new SigningCredentials(JwtKeys.Create(security.JwtKey), SecurityAlgorithms.HmacSha256),
        });
        await users.AuditAsync(user.Id, user.Username, "登录", "", "", HttpContext.ClientIp());
        return Done(new
        {
            token,
            expiresIn = security.JwtExpireMinutes * 60,
            user = new { user.Id, user.Username, user.DisplayName, user.Role },
        });
    }

    [HttpGet("me")]
    public async Task<ApiResult> Me()
    {
        var user = await users.FindAsync(UserId);
        if (user is null || user.Status != 1) throw new PayException(ErrorCodes.Unauthorized, "账号不存在或已停用");
        return Done(new { user.Id, user.Username, user.DisplayName, user.Role });
    }

    [HttpPost("password")]
    public async Task<ApiResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        await users.ChangePasswordAsync(UserId, request.OldPassword ?? "", request.NewPassword ?? "");
        await AuditAsync("修改密码", Username);
        return Done();
    }
}

[Route("api/admin/users")]
[Authorize(Roles = Roles.Admin)]
public sealed class UsersController(AdminUserService users) : AdminControllerBase
{
    [HttpGet]
    public async Task<ApiResult> List() => Done(await users.ListAsync());

    [HttpPost]
    public async Task<ApiResult> Create([FromBody] UserInput input)
    {
        var id = await users.CreateAsync(input);
        await AuditAsync("新增用户", input.Username, new { input.Username, input.Role });
        return Done(new { id });
    }

    [HttpPut("{id:long}")]
    public async Task<ApiResult> Update(long id, [FromBody] UserInput input)
    {
        await users.UpdateAsync(id, input, UserId);
        await AuditAsync("修改用户", id.ToString(), new { input.DisplayName, input.Role, input.Status });
        return Done();
    }

    [HttpPost("{id:long}/reset-password")]
    public async Task<ApiResult> ResetPassword(long id, [FromBody] ResetPasswordRequest request)
    {
        await users.SetPasswordAsync(id, request.Password ?? "");
        await AuditAsync("重置用户密码", id.ToString());
        return Done();
    }

    [HttpDelete("{id:long}")]
    public async Task<ApiResult> Delete(long id)
    {
        await users.DeleteAsync(id, UserId);
        await AuditAsync("删除用户", id.ToString());
        return Done();
    }
}

[Route("api/admin")]
public sealed class SystemController(
    AdminUserService users,
    AdminQueryService query,
    IPayProviderRegistry providers,
    InstanceInfo instance,
    NodeService node,
    RedisStore redis,
    MqBus mq,
    Db db) : AdminControllerBase
{
    [HttpGet("audit-logs")]
    public async Task<ApiResult> AuditLogs([FromQuery] AuditQuery q) => Done(await users.ListAuditAsync(q));

    [HttpGet("meta/providers")]
    public ApiResult Providers() => Done(providers.All.Select(p => p.Meta));

    [HttpGet("dashboard/summary")]
    public async Task<ApiResult> Summary() => Done(await query.SummaryAsync());

    [HttpGet("dashboard/trend")]
    public async Task<ApiResult> Trend([FromQuery] int days = 7) => Done(await query.TrendAsync(days));

    [HttpGet("dashboard/distribution")]
    public async Task<ApiResult> Distribution([FromQuery] string? dimension, [FromQuery] int days = 1) =>
        Done(await query.DistributionAsync(dimension, days));

    [HttpGet("system/info")]
    public async Task<ApiResult> Info(CancellationToken ct)
    {
        var checks = new List<object>
        {
            await CheckAsync("MySQL", async () =>
            {
                await using var conn = await db.OpenAsync(ct);
                return "版本 " + await conn.ExecuteScalarAsync<string>("SELECT VERSION()");
            }),
            await CheckAsync("Redis", async () => $"延迟 {(await redis.PingAsync()).TotalMilliseconds:0.#} ms"),
            await CheckAsync("RabbitMQ", async () =>
            {
                await mq.EnsureConnectedAsync(ct);
                return "已连接";
            }),
        };

        List<NodeInfo> nodes;
        try
        {
            nodes = await redis.GetNodesAsync(TimeSpan.FromSeconds(35));
        }
        catch (Exception)
        {
            nodes = [];
        }

        return Done(new
        {
            instance.Version,
            instance.InstanceId,
            instance.Roles,
            node.WorkerId,
            instance.StartedAt,
            checks,
            nodes,
        });
    }

    private static async Task<object> CheckAsync(string name, Func<Task<string>> probe)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var task = probe();
            var message = await task.WaitAsync(cts.Token);
            return new { name, ok = true, message };
        }
        catch (Exception ex)
        {
            return new { name, ok = false, message = ex is OperationCanceledException ? "检测超时" : ex.Message };
        }
    }
}
