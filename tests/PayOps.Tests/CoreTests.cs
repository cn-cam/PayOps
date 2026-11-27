using PayOps.Core;
using PayOps.Core.Routing;
using PayOps.Core.Security;

namespace PayOps.Tests;

public class PassageRouterTests
{
    private static RouteCandidate Candidate(long id, int weight = 100, int priority = 0, long min = 0, long max = 0,
        string provider = "wechat", string[]? methods = null, long dailyAmount = 0, int dailyCount = 0,
        string? start = null, string? end = null, bool enabled = true, bool passageEnabled = true) => new()
    {
        BindingId = id,
        PassageId = id,
        PassageName = "P" + id,
        Provider = provider,
        Methods = (methods ?? []).ToHashSet(),
        Weight = weight,
        Priority = priority,
        MinAmount = min,
        MaxAmount = max,
        DailyLimitAmount = dailyAmount,
        DailyLimitCount = dailyCount,
        StartTime = PassageRouter.ParseTime(start),
        EndTime = PassageRouter.ParseTime(end),
        Enabled = enabled,
        PassageEnabled = passageEnabled,
    };

    private static RouteRequest Request(long amount, string method = "wechat_app", string provider = "wechat", string time = "12:00") =>
        new(provider, method, amount, TimeOnly.Parse(time));

    private static List<RouteEvaluation> Evaluate(IEnumerable<RouteCandidate> candidates, RouteRequest request, Dictionary<long, RouteRuntime>? runtime = null) =>
        PassageRouter.Evaluate(candidates, request, id => runtime?.GetValueOrDefault(id) ?? RouteRuntime.Empty);

    private static Dictionary<long, int> FirstChoiceHits(List<RouteEvaluation> evaluations, int times = 20000)
    {
        var random = new Random(42);
        var hits = new Dictionary<long, int>();
        for (var i = 0; i < times; i++)
        {
            var first = PassageRouter.Order(evaluations, random)[0];
            hits[first.BindingId] = hits.GetValueOrDefault(first.BindingId) + 1;
        }
        return hits;
    }

    [Fact]
    public void Same_type_passages_split_traffic_by_weight()
    {
        var evaluations = Evaluate([Candidate(1, weight: 70), Candidate(2, weight: 30)], Request(1000));
        var hits = FirstChoiceHits(evaluations);
        Assert.InRange(hits[1] / 20000d, 0.68, 0.72);
        Assert.InRange(hits[2] / 20000d, 0.28, 0.32);
    }

    [Fact]
    public void Amount_below_min_amount_excludes_passage()
    {
        var candidates = new[] { Candidate(1, weight: 50), Candidate(2, weight: 50, min: 5000) };

        var small = Evaluate(candidates, Request(4999));
        Assert.False(small.Single(e => e.Candidate.BindingId == 2).Eligible);
        Assert.Contains("起付金额", small.Single(e => e.Candidate.BindingId == 2).Reason);
        Assert.All(Enumerable.Range(0, 50), _ => Assert.Equal(1, PassageRouter.Order(small, Random.Shared)[0].BindingId));

        var exact = Evaluate(candidates, Request(5000));
        Assert.All(exact, e => Assert.True(e.Eligible));
    }

    [Fact]
    public void Amount_above_max_amount_excludes_passage()
    {
        var evaluations = Evaluate([Candidate(1, max: 10000), Candidate(2)], Request(10001));
        Assert.False(evaluations[0].Eligible);
        Assert.Equal(2, PassageRouter.Order(evaluations, Random.Shared).Single().BindingId);
    }

    [Fact]
    public void Lower_priority_number_wins_and_next_group_is_failover()
    {
        var evaluations = Evaluate([Candidate(1, priority: 10), Candidate(2, priority: 0), Candidate(3, priority: 0)], Request(100));
        for (var i = 0; i < 50; i++)
        {
            var ordered = PassageRouter.Order(evaluations, Random.Shared);
            Assert.Equal(3, ordered.Count);
            Assert.Contains(ordered[0].BindingId, new long[] { 2, 3 });
            Assert.Equal(1, ordered[2].BindingId);
        }
    }

    [Fact]
    public void Zero_weight_is_standby_within_its_priority_group()
    {
        var evaluations = Evaluate([Candidate(1, weight: 0), Candidate(2, weight: 10)], Request(100));
        for (var i = 0; i < 50; i++)
        {
            var ordered = PassageRouter.Order(evaluations, Random.Shared);
            Assert.Equal([2L, 1L], ordered.Select(c => c.BindingId));
        }

        var onlyStandby = Evaluate([Candidate(1, weight: 0)], Request(100));
        Assert.Equal(1, PassageRouter.Order(onlyStandby, Random.Shared).Single().BindingId);
    }

    [Fact]
    public void Provider_method_and_status_filters_apply()
    {
        var evaluations = Evaluate(
        [
            Candidate(1, provider: "alipay"),
            Candidate(2, methods: ["wechat_h5"]),
            Candidate(3, enabled: false),
            Candidate(4, passageEnabled: false),
            Candidate(5, methods: ["wechat_app", "wechat_h5"]),
        ], Request(100));
        Assert.Equal([5L], evaluations.Where(e => e.Eligible).Select(e => e.Candidate.BindingId));
    }

    [Fact]
    public void Daily_limits_use_shared_runtime_counters()
    {
        var runtime = new Dictionary<long, RouteRuntime>
        {
            [1] = new(9500, 3, false, 0),
            [2] = new(0, 5, false, 0),
        };
        var evaluations = Evaluate([Candidate(1, dailyAmount: 10000), Candidate(2, dailyCount: 5), Candidate(3)], Request(600), runtime);
        Assert.Equal("超过日限额", evaluations[0].Reason);
        Assert.Equal("超过日笔数限额", evaluations[1].Reason);
        Assert.True(evaluations[2].Eligible);

        Assert.True(Evaluate([Candidate(1, dailyAmount: 10000)], Request(500), runtime)[0].Eligible);
    }

    [Theory]
    [InlineData("09:00", "18:00", "12:00", true)]
    [InlineData("09:00", "18:00", "18:00", false)]
    [InlineData("09:00", "18:00", "08:59", false)]
    [InlineData("22:00", "06:00", "23:30", true)]
    [InlineData("22:00", "06:00", "05:59", true)]
    [InlineData("22:00", "06:00", "12:00", false)]
    [InlineData(null, null, "03:00", true)]
    public void Time_window_supports_cross_midnight(string? start, string? end, string now, bool expected)
    {
        var evaluations = Evaluate([Candidate(1, start: start, end: end)], Request(100, time: now));
        Assert.Equal(expected, evaluations[0].Eligible);
    }

    [Fact]
    public void Open_breaker_is_skipped_but_used_as_last_resort()
    {
        var runtime = new Dictionary<long, RouteRuntime> { [1] = new(0, 0, true, 5) };

        var withHealthy = Evaluate([Candidate(1, weight: 100), Candidate(2, weight: 1)], Request(100), runtime);
        Assert.Equal([2L, 1L], PassageRouter.Order(withHealthy, Random.Shared).Select(c => c.BindingId));

        var allOpen = Evaluate([Candidate(1)], Request(100), runtime);
        Assert.False(allOpen[0].Eligible);
        Assert.Equal(1, PassageRouter.Order(allOpen, Random.Shared).Single().BindingId);
    }

    [Fact]
    public void No_candidates_yields_empty_order()
    {
        Assert.Empty(PassageRouter.Order(Evaluate([Candidate(1, min: 1000)], Request(100)), Random.Shared));
    }
}

public class SecurityTests
{
    [Fact]
    public void Secret_protector_round_trips_and_detects_tampering()
    {
        var protector = new SecretProtector("unit-test-master-key");
        var cipher = protector.Protect("{\"mchId\":\"1900000001\",\"密钥\":\"值\"}");
        Assert.StartsWith("v1:", cipher);
        Assert.NotEqual(cipher, protector.Protect("{\"mchId\":\"1900000001\",\"密钥\":\"值\"}"));
        Assert.Equal("{\"mchId\":\"1900000001\",\"密钥\":\"值\"}", protector.Unprotect(cipher));

        var tampered = cipher[..^4] + (cipher[^4] == 'A' ? 'B' : 'A') + cipher[^3..];
        Assert.ThrowsAny<Exception>(() => protector.Unprotect(tampered));
        Assert.ThrowsAny<Exception>(() => new SecretProtector("another-key").Unprotect(cipher));
    }

    [Fact]
    public void Api_signature_verifies_and_rejects_any_change()
    {
        var sign = ApiSigner.Sign("secret", "post", "/api/pay/orders", "1700000000", "nonce", "{\"a\":1}");
        Assert.Equal(64, sign.Length);
        Assert.True(ApiSigner.Verify("secret", "POST", "/api/pay/orders", "1700000000", "nonce", "{\"a\":1}", sign.ToUpperInvariant()));
        Assert.False(ApiSigner.Verify("secret", "POST", "/api/pay/orders", "1700000000", "nonce", "{\"a\":2}", sign));
        Assert.False(ApiSigner.Verify("secret2", "POST", "/api/pay/orders", "1700000000", "nonce", "{\"a\":1}", sign));
        Assert.False(ApiSigner.Verify("secret", "POST", "/api/pay/orders", "1700000001", "nonce", "{\"a\":1}", sign));
        Assert.False(ApiSigner.Verify("secret", "POST", "/api/pay/orders", "1700000000", "nonce", "{\"a\":1}", ""));
    }

    [Fact]
    public void Password_hash_verifies()
    {
        var hash = PasswordHasher.Hash("Admin@123456");
        Assert.True(PasswordHasher.Verify("Admin@123456", hash));
        Assert.False(PasswordHasher.Verify("admin@123456", hash));
        Assert.False(PasswordHasher.Verify("Admin@123456", "garbage"));
    }

    [Fact]
    public void Order_numbers_are_unique_and_fixed_length()
    {
        var a = new OrderNoGenerator { WorkerId = 7 };
        var b = new OrderNoGenerator { WorkerId = 8 };
        var set = new HashSet<string>();
        Parallel.For(0, 4, _ =>
        {
            for (var i = 0; i < 60000; i++)
            {
                var x = a.Next('P');
                var y = b.Next('P');
                lock (set)
                {
                    Assert.True(set.Add(x));
                    Assert.True(set.Add(y));
                }
            }
        });
        Assert.All(set.Take(1000), no =>
        {
            Assert.Equal(21, no.Length);
            Assert.StartsWith("P", no);
            Assert.True(no.Skip(1).All(char.IsAsciiDigit));
        });
    }
}
