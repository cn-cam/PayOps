namespace PayOps.Core.Routing;

/// <summary>一条渠道通道绑定（路由规则）的静态配置快照。</summary>
public sealed record RouteCandidate
{
    public required long BindingId { get; init; }
    public required long PassageId { get; init; }
    public string PassageName { get; init; } = "";
    public required string Provider { get; init; }
    /// <summary>允许的支付方式；空集合 = 该类型的全部方式。</summary>
    public IReadOnlySet<string> Methods { get; init; } = new HashSet<string>();
    public int Weight { get; init; } = 100;
    public int Priority { get; init; }
    public long MinAmount { get; init; }
    public long MaxAmount { get; init; }
    public long DailyLimitAmount { get; init; }
    public int DailyLimitCount { get; init; }
    public TimeOnly? StartTime { get; init; }
    public TimeOnly? EndTime { get; init; }
    public bool Enabled { get; init; } = true;
    public bool PassageEnabled { get; init; } = true;
}

/// <summary>绑定的运行时状态（来自 Redis，多副本共享）。</summary>
public sealed record RouteRuntime(long TodayAmount, int TodayCount, bool BreakerOpen, int RecentFailures)
{
    public static readonly RouteRuntime Empty = new(0, 0, false, 0);
}

public sealed record RouteRequest(string Provider, string Method, long Amount, TimeOnly LocalTime);

public sealed record RouteEvaluation(RouteCandidate Candidate, bool Eligible, bool BreakerOpen, string Reason);

/// <summary>
/// 多通道路由算法（纯函数，便于测试与后台"路由模拟"复用）：
/// 1. 过滤：启停、支付类型/方式、起付金额、单笔上限、可用时段、日限额、熔断；
/// 2. 按优先级分组（数值小者优先），组内按权重做不放回加权随机；权重 0 为备用，排在组内末尾；
/// 3. 输出完整的故障转移顺序；若全部被熔断，则把熔断通道作为兜底（探测性放行），避免全量不可用。
/// </summary>
public static class PassageRouter
{
    public static List<RouteEvaluation> Evaluate(
        IEnumerable<RouteCandidate> candidates,
        RouteRequest request,
        Func<long, RouteRuntime> runtimeOf)
    {
        var list = new List<RouteEvaluation>();
        foreach (var c in candidates)
        {
            var rt = runtimeOf(c.BindingId);
            var reason = Check(c, request, rt);
            list.Add(new RouteEvaluation(c, reason is null, rt.BreakerOpen, reason ?? ""));
        }
        return list;
    }

    private static string? Check(RouteCandidate c, RouteRequest r, RouteRuntime rt)
    {
        if (!string.Equals(c.Provider, r.Provider, StringComparison.OrdinalIgnoreCase)) return "支付类型不匹配";
        if (!c.Enabled) return "绑定已停用";
        if (!c.PassageEnabled) return "通道已停用";
        if (c.Methods.Count > 0 && !c.Methods.Contains(r.Method)) return "未开通该支付方式";
        if (r.Amount < c.MinAmount) return $"低于起付金额 {Yuan(c.MinAmount)}";
        if (c.MaxAmount > 0 && r.Amount > c.MaxAmount) return $"超过单笔上限 {Yuan(c.MaxAmount)}";
        if (!InWindow(c.StartTime, c.EndTime, r.LocalTime)) return "不在可用时段";
        if (c.DailyLimitAmount > 0 && rt.TodayAmount + r.Amount > c.DailyLimitAmount) return "超过日限额";
        if (c.DailyLimitCount > 0 && rt.TodayCount >= c.DailyLimitCount) return "超过日笔数限额";
        if (rt.BreakerOpen) return "熔断中";
        return null;
    }

    public static bool InWindow(TimeOnly? start, TimeOnly? end, TimeOnly now)
    {
        if (start is null || end is null || start == end) return true;
        return start < end
            ? now >= start && now < end
            : now >= start || now < end; // 跨零点
    }

    /// <summary>生成故障转移顺序：第一个即首选通道。</summary>
    public static List<RouteCandidate> Order(IReadOnlyList<RouteEvaluation> evaluations, Random random)
    {
        var eligible = evaluations.Where(e => e.Eligible).Select(e => e.Candidate).ToList();
        var result = new List<RouteCandidate>(eligible.Count);
        foreach (var group in eligible.GroupBy(c => c.Priority).OrderBy(g => g.Key))
        {
            result.AddRange(WeightedShuffle(group.Where(c => c.Weight > 0).ToList(), random));
            result.AddRange(Shuffle(group.Where(c => c.Weight <= 0).ToList(), random));
        }

        // 仅因熔断被排除的通道作为最后兜底
        var fallback = evaluations
            .Where(e => !e.Eligible && e.BreakerOpen && e.Reason == "熔断中")
            .Select(e => e.Candidate)
            .OrderBy(c => c.Priority)
            .ThenByDescending(c => c.Weight);
        result.AddRange(fallback);
        return result;
    }

    private static List<RouteCandidate> WeightedShuffle(List<RouteCandidate> items, Random random)
    {
        var output = new List<RouteCandidate>(items.Count);
        var pool = new List<RouteCandidate>(items);
        while (pool.Count > 0)
        {
            long total = 0;
            foreach (var p in pool) total += p.Weight;
            var roll = random.NextInt64(total);
            var idx = 0;
            for (; idx < pool.Count - 1; idx++)
            {
                roll -= pool[idx].Weight;
                if (roll < 0) break;
            }
            output.Add(pool[idx]);
            pool.RemoveAt(idx);
        }
        return output;
    }

    private static List<RouteCandidate> Shuffle(List<RouteCandidate> items, Random random)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
        return items;
    }

    public static TimeOnly? ParseTime(string? value) =>
        !string.IsNullOrWhiteSpace(value) && TimeOnly.TryParseExact(value.Trim(), "HH:mm", out var t) ? t : null;

    private static string Yuan(long fen) => (fen / 100m).ToString("0.00");
}
