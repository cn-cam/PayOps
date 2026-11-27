using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PayOps.Core;
using PayOps.Core.Security;
using PayOps.Providers;
using PayOps.Service.Admin;
using PayOps.Service.Data;
using PayOps.Service.Infra;
using PayOps.Service.Jobs;
using PayOps.Service.Pay;
using StackExchange.Redis;

namespace PayOps.Service;

public static class ServiceRegistration
{
    public static IServiceCollection AddPayOps(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PayOpsOptions>(configuration.GetSection(PayOpsOptions.Section));
        var options = configuration.GetSection(PayOpsOptions.Section).Get<PayOpsOptions>() ?? new PayOpsOptions();

        services.AddSingleton(sp => new SecretProtector(sp.GetRequiredService<IOptions<PayOpsOptions>>().Value.Security.MasterKey));
        services.AddSingleton<OrderNoGenerator>();
        services.AddSingleton<InstanceInfo>();
        services.AddSingleton<Db>();
        services.AddSingleton<Migrator>();

        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var redis = ConfigurationOptions.Parse(sp.GetRequiredService<IOptions<PayOpsOptions>>().Value.Redis.ConnectionString);
            redis.AbortOnConnectFail = false; // Redis 暂不可用时不阻止进程启动，后台自动重连
            redis.ClientName = "payops";
            return ConnectionMultiplexer.Connect(redis);
        });
        services.AddSingleton<RedisStore>();
        services.AddSingleton<MqBus>();
        services.AddSingleton<ConfigCache>();

        services.AddPayProviders();
        services.AddHttpClient(NotifyService.HttpClientName);

        services.AddSingleton<RouteService>();
        services.AddSingleton<NotifyService>();
        services.AddSingleton<OrderService>();
        services.AddSingleton<RefundService>();
        services.AddSingleton<SubscriptionService>();
        services.AddSingleton<ReconService>();
        services.AddSingleton<UpstreamNotifyService>();
        services.AddSingleton<AppAuthService>();
        services.AddSingleton<AdminConfigService>();
        services.AddSingleton<AdminQueryService>();
        services.AddSingleton<AdminUserService>();

        // 所有角色都需要：节点号租约 + 心跳 + 配置失效订阅
        services.AddSingleton<NodeService>();
        services.AddHostedService(sp => sp.GetRequiredService<NodeService>());

        if (options.HasRole("Worker"))
        {
            services.AddHostedService<DispatchJob>();
            services.AddHostedService<ConsumerJob>();
        }
        return services;
    }
}
