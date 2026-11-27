using Microsoft.Extensions.Options;
using PayOps.Core;
using PayOps.Core.Routing;
using PayOps.Service.Infra;

namespace PayOps.Service.Pay;

public sealed record RouteResult(List<RouteEvaluation> Evaluations, List<RouteCandidate> Ordered);

/// <summary>按渠道路由通道：配置来自本地快照，运行时状态（日限额、熔断）来自 Redis。</summary>
public sealed class RouteService(ConfigCache cache, RedisStore redis, IOptions<PayOpsOptions> options)
{
    private readonly PayOpsOptions _options = options.Value;

    public DateTime BusinessNow => DateTime.UtcNow + _options.TimeZoneOffset;

    public string BusinessDay => BusinessNow.ToString("yyyyMMdd");

    public async Task<RouteResult> RouteAsync(long channelId, string provider, string method, long amount)
    {
        var evaluations = await EvaluateAsync(channelId, provider, method, amount);
        return new RouteResult(evaluations, PassageRouter.Order(evaluations, Random.Shared));
    }

    public async Task<List<RouteEvaluation>> EvaluateAsync(long channelId, string provider, string method, long amount)
    {
        var candidates = await cache.GetBindingsAsync(channelId);
        var runtime = await redis.GetRuntimeAsync(candidates.Select(c => c.BindingId).ToList(), BusinessDay);
        var request = new RouteRequest(provider, method, amount, TimeOnly.FromDateTime(BusinessNow));
        return PassageRouter.Evaluate(candidates, request, id => runtime.GetValueOrDefault(id, RouteRuntime.Empty));
    }
}
