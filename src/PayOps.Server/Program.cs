using System.Text.Encodings.Web;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.IdentityModel.Tokens;
using PayOps.Core;
using PayOps.Server.Controllers.Admin;
using PayOps.Server.Infrastructure;
using PayOps.Service;
using PayOps.Service.Data;
using PayOps.Service.Infra;

// 容器健康检查模式：运行时镜像里没有 curl，由程序自身探测就绪接口（docker HEALTHCHECK / compose 使用）
if (args.Contains("--healthcheck"))
{
    var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
    var port = !string.IsNullOrEmpty(urls) ? new Uri(urls.Split(';')[0].Replace("+", "localhost").Replace("*", "localhost")).Port
        : int.TryParse(ports?.Split(';')[0], out var httpPort) ? httpPort
        : 5080;
    try
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var response = await probe.GetAsync($"http://127.0.0.1:{port}/healthz/ready");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

// 未显式指定监听地址（ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS / 配置文件 Urls / --urls）时默认 5080。
// 不把默认值写进 appsettings.json：配置文件的优先级高于 ASPNETCORE_URLS，会让容器里的端口设置失效。
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["http_ports"]))
    builder.WebHost.UseUrls("http://*:5080");

var options = builder.Configuration.GetSection(PayOpsOptions.Section).Get<PayOpsOptions>() ?? new PayOpsOptions();
if (string.IsNullOrWhiteSpace(options.Security.MasterKey) || string.IsNullOrWhiteSpace(options.Security.JwtKey))
    throw new InvalidOperationException("必须配置 PayOps:Security:MasterKey 与 PayOps:Security:JwtKey（所有副本保持一致）");

builder.Services.AddPayOps(builder.Configuration);

builder.Services
    .AddControllers(mvc => mvc.Filters.Add<ApiExceptionFilter>())
    .ConfigureApplicationPartManager(parts =>
    {
        // 按进程角色（Gateway / Admin / Worker）裁剪控制器
        foreach (var provider in parts.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
            parts.FeatureProviders.Remove(provider);
        parts.FeatureProviders.Add(new RoleControllerFeatureProvider(options));
    })
    .AddJsonOptions(json => json.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

builder.Services.Configure<ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = InvalidModelState.Create);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(jwt =>
    {
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = "payops",
            ValidAudience = "payops-admin",
            IssuerSigningKey = JwtKeys.Create(options.Security.JwtKey),
            NameClaimType = "name",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // 部署在 Nginx / Ingress 之后：信任代理传入的客户端 IP 与协议
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PayOps");

if (options.Database.AutoMigrate)
    await app.Services.GetRequiredService<Migrator>().MigrateAsync();

app.UseForwardedHeaders();

// 存活探针：进程在即可；就绪探针：数据库可用才接流量（Redis / MQ 故障时主链路可降级运行，不摘流量）
app.MapGet("/healthz/live", () => Results.Text("ok"));
app.MapGet("/healthz/ready", async (Db db, RedisStore redis, MqBus mq) =>
{
    try
    {
        await using var conn = await db.OpenAsync();
        await conn.ExecuteScalarAsync<int>("SELECT 1");
        return Results.Json(new { status = "ready", redis = redis.IsConnected, rabbitmq = mq.IsConnected });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unavailable", error = ex.Message }, statusCode: 503);
    }
});

var serveAdminUi = options.HasRole("Admin") && File.Exists(Path.Combine(app.Environment.WebRootPath ?? "", "index.html"));
if (serveAdminUi)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapFallback(async context =>
{
    if (serveAdminUi && !context.Request.Path.StartsWithSegments("/api") && HttpMethods.IsGet(context.Request.Method))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath!, "index.html"));
        return;
    }
    context.Response.StatusCode = 404;
    await context.Response.WriteAsJsonAsync(ApiResult.Fail(ErrorCodes.NotFound, "接口不存在", 404));
});

logger.LogInformation("PayOps 启动：角色 [{Roles}]，实例 {Instance}，管理后台静态资源 {Ui}",
    string.Join(",", options.RoleList()), app.Services.GetRequiredService<InstanceInfo>().InstanceId, serveAdminUi ? "已启用" : "未启用");

await app.RunAsync();
return 0;

public partial class Program;
