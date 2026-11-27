using Microsoft.Extensions.DependencyInjection;
using PayOps.Core;
using PayOps.Core.Providers;
using PayOps.Providers.Alipay;
using PayOps.Providers.Apple;
using PayOps.Providers.Common;
using PayOps.Providers.Douyin;
using PayOps.Providers.Honor;
using PayOps.Providers.Huawei;
using PayOps.Providers.Mock;
using PayOps.Providers.Stores;
using PayOps.Providers.Wechat;

namespace PayOps.Providers;

public sealed class PayProviderRegistry : IPayProviderRegistry
{
    private readonly Dictionary<string, IPayProvider> _byCode;
    private readonly Dictionary<string, IPayProvider> _byMethod;

    public PayProviderRegistry(IEnumerable<IPayProvider> providers)
    {
        All = providers.ToList();
        _byCode = All.ToDictionary(p => p.Meta.Code, StringComparer.OrdinalIgnoreCase);
        _byMethod = All.SelectMany(p => p.Meta.Methods.Select(m => (m.Code, Provider: p)))
            .ToDictionary(x => x.Code, x => x.Provider, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IPayProvider> All { get; }

    public IPayProvider? Find(string code) => _byCode.GetValueOrDefault(code);

    public IPayProvider Get(string code) =>
        Find(code) ?? throw new PayException(ErrorCodes.NotSupported, $"未知的支付类型：{code}");

    public IPayProvider? FindByMethod(string method) => _byMethod.GetValueOrDefault(method);
}

public static class ProviderServiceCollectionExtensions
{
    /// <summary>新增支付类型：实现 IPayProvider 并在此注册一行即可。</summary>
    public static IServiceCollection AddPayProviders(this IServiceCollection services)
    {
        services.AddHttpClient(PayProviderBase.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IPayProvider, WechatProvider>();
        services.AddSingleton<IPayProvider, AlipayProvider>();
        services.AddSingleton<IPayProvider, DouyinProvider>();
        services.AddSingleton<IPayProvider, DouyinMpProvider>();
        services.AddSingleton<IPayProvider, HuaweiProvider>();
        services.AddSingleton<IPayProvider, HonorProvider>();
        services.AddSingleton<IPayProvider, OppoProvider>();
        services.AddSingleton<IPayProvider, VivoProvider>();
        services.AddSingleton<IPayProvider, XiaomiProvider>();
        services.AddSingleton<IPayProvider, AppleProvider>();
        services.AddSingleton<IPayProvider, MockProvider>();
        services.AddSingleton<IPayProviderRegistry, PayProviderRegistry>();
        return services;
    }
}
