using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Core.Providers;
using PayOps.Core.Routing;
using PayOps.Core.Security;
using PayOps.Service.Data;
using PayOps.Service.Infra;
using PayOps.Service.Pay;

namespace PayOps.Service.Admin;

/// <summary>应用 / 渠道 / 通道 / 渠道通道绑定的后台维护。任何修改都会广播配置失效，所有副本即时生效。</summary>
public sealed partial class AdminConfigService(
    Db db,
    ConfigCache cache,
    RedisStore redis,
    RouteService router,
    OrderService orders,
    SecretProtector protector,
    IPayProviderRegistry providers)
{
    public const string Mask = "******";

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_-]{1,31}$")]
    private static partial Regex CodeRegex();

    [GeneratedRegex("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    private static partial Regex TimeRegex();

    // ------------------------------------------------------------------ 应用

    public Task<PageResult<AppItem>> ListAppsAsync(int page, int pageSize, string? keyword, int? status)
    {
        var where = new SqlWhere().Add("a.status = {0}", status);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            where.Parameters.Add("kw", "%" + keyword.Trim() + "%");
            where.Raw("(a.app_code LIKE @kw OR a.name LIKE @kw)");
        }
        return db.PageAsync<AppItem>(
            "a.id, a.app_code, a.name, a.notify_url, a.status, a.remark, a.created_at, a.updated_at, (SELECT COUNT(*) FROM pay_channel c WHERE c.app_id = a.id) AS channel_count",
            "pay_app a", where, "a.id DESC", page, pageSize);
    }

    public Task<List<AppItem>> AllAppsAsync() =>
        db.QueryAsync<AppItem>("SELECT id, app_code, name, status FROM pay_app ORDER BY id");

    public async Task<(long Id, string Secret)> CreateAppAsync(AppInput input)
    {
        input.AppCode = input.AppCode.Trim();
        if (!CodeRegex().IsMatch(input.AppCode)) throw Invalid("应用编码需为 2~32 位字母、数字、下划线或中划线，且以字母开头");
        ValidateApp(input);
        var secret = ApiSigner.NewSecret();
        var now = DateTime.UtcNow;
        try
        {
            var id = await db.InsertAsync(new PayApp
            {
                AppCode = input.AppCode,
                Name = input.Name.Trim(),
                SecretCipher = protector.Protect(secret),
                NotifyUrl = input.NotifyUrl.Trim(),
                Status = input.Status == 0 ? 0 : 1,
                Remark = input.Remark,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await cache.InvalidateAllAsync();
            return (id, secret);
        }
        catch (MySqlException ex) when (Db.IsDuplicateKey(ex))
        {
            throw new PayException(ErrorCodes.Conflict, "应用编码已存在");
        }
    }

    public async Task UpdateAppAsync(long id, AppInput input)
    {
        ValidateApp(input);
        var rows = await db.ExecuteAsync(
            "UPDATE pay_app SET name=@name, notify_url=@notifyUrl, remark=@remark, status=@status, updated_at=@now WHERE id=@id",
            new { id, name = input.Name.Trim(), notifyUrl = input.NotifyUrl.Trim(), remark = input.Remark, status = input.Status == 0 ? 0 : 1, now = DateTime.UtcNow });
        if (rows == 0) throw NotFound("应用不存在");
        await cache.InvalidateAllAsync();
    }

    private static void ValidateApp(AppInput input)
    {
        if (input.Name.Trim().Length is 0 or > 64) throw Invalid("应用名称长度需在 1~64 之间");
        if (input.NotifyUrl.Trim().Length > 0 && !OrderService.IsHttpUrl(input.NotifyUrl.Trim())) throw Invalid("通知地址必须是 http(s) 地址");
        if (input.Remark.Length > 255) throw Invalid("备注超长");
    }

    public async Task DeleteAppAsync(long id)
    {
        if (await db.ScalarAsync<long>("SELECT COUNT(*) FROM pay_channel WHERE app_id=@id", new { id }) > 0)
            throw new PayException(ErrorCodes.Conflict, "该应用下还有渠道，请先删除渠道");
        if (await db.ScalarAsync<long>("SELECT COUNT(*) FROM (SELECT 1 FROM pay_order WHERE app_id=@id LIMIT 1) t", new { id }) > 0)
            throw new PayException(ErrorCodes.Conflict, "该应用已有订单，不能删除，可改为停用");
        if (await db.ExecuteAsync("DELETE FROM pay_app WHERE id=@id", new { id }) == 0) throw NotFound("应用不存在");
        await cache.InvalidateAllAsync();
    }

    public async Task<string> GetAppSecretAsync(long id)
    {
        var app = await db.GetAsync<PayApp>(id) ?? throw NotFound("应用不存在");
        return protector.Unprotect(app.SecretCipher);
    }

    public async Task<string> ResetAppSecretAsync(long id)
    {
        var secret = ApiSigner.NewSecret();
        var rows = await db.ExecuteAsync("UPDATE pay_app SET secret_cipher=@cipher, updated_at=@now WHERE id=@id",
            new { id, cipher = protector.Protect(secret), now = DateTime.UtcNow });
        if (rows == 0) throw NotFound("应用不存在");
        await cache.InvalidateAllAsync();
        return secret;
    }

    // ------------------------------------------------------------------ 渠道

    private const string ChannelSelect =
        "c.id, c.app_id, a.app_code, a.name AS app_name, c.code, c.name, c.status, c.remark, c.created_at, c.updated_at, (SELECT COUNT(*) FROM pay_channel_passage b WHERE b.channel_id = c.id) AS binding_count";

    private const string ChannelFrom = "pay_channel c JOIN pay_app a ON a.id = c.app_id";

    public Task<PageResult<ChannelItem>> ListChannelsAsync(int page, int pageSize, long? appId, string? keyword, int? status)
    {
        var where = new SqlWhere().Add("c.app_id = {0}", appId).Add("c.status = {0}", status);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            where.Parameters.Add("kw", "%" + keyword.Trim() + "%");
            where.Raw("(c.code LIKE @kw OR c.name LIKE @kw)");
        }
        return db.PageAsync<ChannelItem>(ChannelSelect, ChannelFrom, where, "c.app_id, c.id", page, pageSize);
    }

    public Task<List<ChannelItem>> AllChannelsAsync(long? appId) =>
        db.QueryAsync<ChannelItem>(
            "SELECT id, app_id, code, name, status FROM pay_channel WHERE (@appId IS NULL OR app_id = @appId) ORDER BY app_id, id", new { appId });

    public async Task<ChannelItem> GetChannelAsync(long id) =>
        await db.FirstOrDefaultAsync<ChannelItem>($"SELECT {ChannelSelect} FROM {ChannelFrom} WHERE c.id = @id", new { id })
        ?? throw NotFound("渠道不存在");

    public async Task<long> CreateChannelAsync(ChannelInput input)
    {
        input.Code = input.Code.Trim();
        if (!CodeRegex().IsMatch(input.Code)) throw Invalid("渠道编码需为 2~32 位字母、数字、下划线或中划线，且以字母开头");
        if (input.Name.Trim().Length is 0 or > 64) throw Invalid("渠道名称长度需在 1~64 之间");
        if (await db.GetAsync<PayApp>(input.AppId) is null) throw Invalid("所属应用不存在");
        var now = DateTime.UtcNow;
        try
        {
            var id = await db.InsertAsync(new PayChannel
            {
                AppId = input.AppId,
                Code = input.Code,
                Name = input.Name.Trim(),
                Status = input.Status == 0 ? 0 : 1,
                Remark = input.Remark,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await cache.InvalidateAllAsync();
            return id;
        }
        catch (MySqlException ex) when (Db.IsDuplicateKey(ex))
        {
            throw new PayException(ErrorCodes.Conflict, "该应用下已存在相同编码的渠道");
        }
    }

    public async Task UpdateChannelAsync(long id, ChannelInput input)
    {
        if (input.Name.Trim().Length is 0 or > 64) throw Invalid("渠道名称长度需在 1~64 之间");
        var rows = await db.ExecuteAsync("UPDATE pay_channel SET name=@name, remark=@remark, status=@status, updated_at=@now WHERE id=@id",
            new { id, name = input.Name.Trim(), remark = input.Remark, status = input.Status == 0 ? 0 : 1, now = DateTime.UtcNow });
        if (rows == 0) throw NotFound("渠道不存在");
        await cache.InvalidateAllAsync();
    }

    public async Task DeleteChannelAsync(long id)
    {
        await db.TransactionAsync(async (conn, tx) =>
        {
            if (await conn.ExecuteAsync("DELETE FROM pay_channel WHERE id=@id", new { id }, tx) == 0) throw NotFound("渠道不存在");
            await conn.ExecuteAsync("DELETE FROM pay_channel_passage WHERE channel_id=@id", new { id }, tx);
            return 0;
        });
        await cache.InvalidateAllAsync();
    }

    public async Task<long> CopyChannelAsync(long id, ChannelCopyInput input)
    {
        var source = await db.GetAsync<PayChannel>(id) ?? throw NotFound("渠道不存在");
        var newId = await CreateChannelAsync(new ChannelInput
        {
            AppId = input.TargetAppId,
            Code = input.Code,
            Name = input.Name,
            Remark = source.Remark,
            Status = source.Status,
        });
        var now = DateTime.UtcNow;
        await db.ExecuteAsync(
            """
            INSERT INTO pay_channel_passage
              (channel_id, passage_id, provider, methods, weight, priority, min_amount, max_amount, daily_limit_amount, daily_limit_count,
               start_time, end_time, status, remark, created_at, updated_at)
            SELECT @newId, passage_id, provider, methods, weight, priority, min_amount, max_amount, daily_limit_amount, daily_limit_count,
               start_time, end_time, status, remark, @now, @now
            FROM pay_channel_passage WHERE channel_id = @id
            """, new { newId, id, now });
        await cache.InvalidateAllAsync();
        return newId;
    }

    // ------------------------------------------------------------------ 通道

    public async Task<PageResult<PassageItem>> ListPassagesAsync(int page, int pageSize, string? provider, string? keyword, int? status)
    {
        var where = new SqlWhere().Add("p.provider = {0}", provider).Add("p.status = {0}", status).Like("p.name", keyword);
        var result = await db.PageAsync<PassageItem>(
            "p.id, p.name, p.provider, p.status, p.fee_rate_bp, p.remark, p.created_at, p.updated_at, (SELECT COUNT(*) FROM pay_channel_passage b WHERE b.passage_id = p.id) AS binding_count",
            "pay_passage p", where, "p.id DESC", page, pageSize);
        foreach (var item in result.Items) Decorate(item);
        return result;
    }

    public async Task<List<PassageItem>> AllPassagesAsync(string? provider)
    {
        var list = await db.QueryAsync<PassageItem>(
            "SELECT id, name, provider, status FROM pay_passage WHERE (@provider IS NULL OR provider = @provider) ORDER BY id",
            new { provider = string.IsNullOrWhiteSpace(provider) ? null : provider });
        foreach (var item in list) item.ProviderName = providers.Find(item.Provider)?.Meta.Name ?? item.Provider;
        return list;
    }

    private void Decorate(PassageItem item)
    {
        item.ProviderName = providers.Find(item.Provider)?.Meta.Name ?? item.Provider;
        item.NotifyUrl = orders.UpstreamNotifyUrl(item.Provider, item.Id);
    }

    public async Task<PassageDetail> GetPassageAsync(long id)
    {
        var p = await db.GetAsync<PayPassage>(id) ?? throw NotFound("通道不存在");
        var detail = new PassageDetail
        {
            Id = p.Id, Name = p.Name, Provider = p.Provider, Status = p.Status, FeeRateBp = p.FeeRateBp, Remark = p.Remark,
            CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt,
            BindingCount = await db.ScalarAsync<int>("SELECT COUNT(*) FROM pay_channel_passage WHERE passage_id=@id", new { id }),
        };
        Decorate(detail);
        var config = ConfigCache.DecryptConfig(protector, p.ConfigCipher);
        var fields = providers.Find(p.Provider)?.Meta.ConfigFields ?? [];
        foreach (var f in fields)
        {
            var value = config.GetValueOrDefault(f.Key, "");
            detail.Config[f.Key] = f.Secret && value.Length > 0 ? Mask : value;
        }
        return detail;
    }

    public async Task<long> CreatePassageAsync(PassageInput input)
    {
        var provider = providers.Find(input.Provider) ?? throw Invalid($"未知的支付类型：{input.Provider}");
        if (input.Name.Trim().Length is 0 or > 64) throw Invalid("通道名称长度需在 1~64 之间");
        var config = MergeConfig(provider, [], input.Config);
        var now = DateTime.UtcNow;
        var id = await db.InsertAsync(new PayPassage
        {
            Name = input.Name.Trim(),
            Provider = provider.Meta.Code,
            ConfigCipher = protector.Protect(JsonSerializer.Serialize(config)),
            Status = input.Status == 0 ? 0 : 1,
            FeeRateBp = ValidateFeeRate(input.FeeRateBp),
            Remark = input.Remark,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await cache.InvalidateAllAsync();
        return id;
    }

    public async Task UpdatePassageAsync(long id, PassageInput input)
    {
        var p = await db.GetAsync<PayPassage>(id) ?? throw NotFound("通道不存在");
        var provider = providers.Get(p.Provider);
        if (input.Name.Trim().Length is 0 or > 64) throw Invalid("通道名称长度需在 1~64 之间");
        var config = MergeConfig(provider, ConfigCache.DecryptConfig(protector, p.ConfigCipher), input.Config);
        await db.ExecuteAsync(
            "UPDATE pay_passage SET name=@name, config_cipher=@cipher, status=@status, fee_rate_bp=@feeRate, remark=@remark, updated_at=@now WHERE id=@id",
            new { id, name = input.Name.Trim(), cipher = protector.Protect(JsonSerializer.Serialize(config)), status = input.Status == 0 ? 0 : 1, feeRate = ValidateFeeRate(input.FeeRateBp), remark = input.Remark, now = DateTime.UtcNow });
        await cache.InvalidateAllAsync();
    }

    /// <summary>合并配置：只保留支付类型声明的字段；密钥字段传掩码或空串表示保持原值；缺省值自动补齐。</summary>
    private static Dictionary<string, string> MergeConfig(IPayProvider provider, Dictionary<string, string> current, Dictionary<string, string>? input)
    {
        var result = new Dictionary<string, string>();
        foreach (var f in provider.Meta.ConfigFields)
        {
            var old = current.GetValueOrDefault(f.Key, "");
            var hasInput = input is not null && input.TryGetValue(f.Key, out _);
            var value = hasInput ? (input![f.Key] ?? "").Trim() : old;
            if (f.Secret && (value == Mask || value.Length == 0)) value = old;
            if (value.Length == 0 && !hasInput && f.DefaultValue.Length > 0) value = f.DefaultValue;
            if (f.Required && value.Length == 0) throw Invalid($"缺少必填项：{f.Label}");
            result[f.Key] = value;
        }
        return result;
    }

    public async Task DeletePassageAsync(long id)
    {
        if (await db.ScalarAsync<long>("SELECT COUNT(*) FROM pay_channel_passage WHERE passage_id=@id", new { id }) > 0)
            throw new PayException(ErrorCodes.Conflict, "该通道已被渠道绑定，请先解除绑定");
        if (await db.ExecuteAsync("DELETE FROM pay_passage WHERE id=@id", new { id }) == 0) throw NotFound("通道不存在");
        await cache.InvalidateAllAsync();
    }

    public async Task<(bool Ok, string Message)> TestPassageAsync(long id)
    {
        var p = await db.GetAsync<PayPassage>(id) ?? throw NotFound("通道不存在");
        var provider = providers.Get(p.Provider);
        try
        {
            var problem = provider.ValidateConfig(new PassageConfig(ConfigCache.DecryptConfig(protector, p.ConfigCipher)));
            return problem is null ? (true, "配置自检通过（必填项与密钥格式正确；是否与上游匹配需以真实交易验证）") : (false, problem);
        }
        catch (Exception ex)
        {
            return (false, "配置无法解析：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ 渠道通道绑定

    public async Task<List<BindingItem>> ListBindingsAsync(long channelId)
    {
        var rows = await db.QueryAsync<PayChannelPassage>(
            "SELECT * FROM pay_channel_passage WHERE channel_id=@channelId ORDER BY priority, id", new { channelId });
        if (rows.Count == 0) return [];
        var passages = (await db.QueryAsync<PayPassage>("SELECT id, name, provider, status FROM pay_passage WHERE id IN @ids",
            new { ids = rows.Select(r => r.PassageId).Distinct() })).ToDictionary(p => p.Id);
        var runtime = await redis.GetRuntimeAsync(rows.Select(r => r.Id).ToList(), router.BusinessDay);

        return rows.Select(r =>
        {
            var passage = passages.GetValueOrDefault(r.PassageId);
            var rt = runtime.GetValueOrDefault(r.Id, RouteRuntime.Empty);
            return new BindingItem
            {
                Id = r.Id, ChannelId = r.ChannelId, PassageId = r.PassageId,
                PassageName = passage?.Name ?? "(已删除)", PassageStatus = passage?.Status ?? 0,
                Provider = r.Provider, ProviderName = providers.Find(r.Provider)?.Meta.Name ?? r.Provider,
                Methods = ConfigCache.SplitMethods(r.Methods).ToList(),
                Weight = r.Weight, Priority = r.Priority, MinAmount = r.MinAmount, MaxAmount = r.MaxAmount,
                DailyLimitAmount = r.DailyLimitAmount, DailyLimitCount = r.DailyLimitCount,
                StartTime = r.StartTime, EndTime = r.EndTime, Status = r.Status, Remark = r.Remark,
                CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt,
                Runtime = new BindingRuntime { TodayAmount = rt.TodayAmount, TodayCount = rt.TodayCount, BreakerOpen = rt.BreakerOpen, RecentFailures = rt.RecentFailures },
            };
        }).ToList();
    }

    public async Task<long> CreateBindingAsync(long channelId, BindingInput input)
    {
        if (await db.GetAsync<PayChannel>(channelId) is null) throw NotFound("渠道不存在");
        var passage = await db.GetAsync<PayPassage>(input.PassageId) ?? throw Invalid("通道不存在");
        var entity = ToEntity(input, passage);
        entity.ChannelId = channelId;
        entity.CreatedAt = entity.UpdatedAt = DateTime.UtcNow;
        var id = await db.InsertAsync(entity);
        await cache.InvalidateAllAsync();
        return id;
    }

    public async Task UpdateBindingAsync(long id, BindingInput input)
    {
        var current = await db.GetAsync<PayChannelPassage>(id) ?? throw NotFound("绑定不存在");
        var passage = await db.GetAsync<PayPassage>(current.PassageId) ?? throw Invalid("通道不存在");
        var entity = ToEntity(input, passage);
        entity.Id = id;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.ExecuteAsync(
            """
            UPDATE pay_channel_passage SET methods=@Methods, weight=@Weight, priority=@Priority, min_amount=@MinAmount, max_amount=@MaxAmount,
              daily_limit_amount=@DailyLimitAmount, daily_limit_count=@DailyLimitCount, start_time=@StartTime, end_time=@EndTime,
              status=@Status, remark=@Remark, updated_at=@UpdatedAt
            WHERE id=@Id
            """, entity);
        await cache.InvalidateAllAsync();
    }

    private PayChannelPassage ToEntity(BindingInput input, PayPassage passage)
    {
        var provider = providers.Get(passage.Provider);
        var allowed = provider.Meta.Methods.Select(m => m.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var methods = input.Methods.Select(m => m.Trim()).Where(m => m.Length > 0).Distinct().ToList();
        var unknown = methods.FirstOrDefault(m => !allowed.Contains(m));
        if (unknown is not null) throw Invalid($"{provider.Meta.Name}不支持支付方式 {unknown}");
        if (input.Weight is < 0 or > 10000) throw Invalid("权重需在 0~10000 之间");
        if (input.MinAmount < 0 || input.MaxAmount < 0 || input.DailyLimitAmount < 0 || input.DailyLimitCount < 0) throw Invalid("金额与限额不能为负数");
        if (input.MaxAmount > 0 && input.MaxAmount < input.MinAmount) throw Invalid("单笔上限不能小于起付金额");
        var start = input.StartTime.Trim();
        var end = input.EndTime.Trim();
        if ((start.Length > 0 && !TimeRegex().IsMatch(start)) || (end.Length > 0 && !TimeRegex().IsMatch(end))) throw Invalid("可用时段格式应为 HH:mm");
        if (start.Length == 0 != (end.Length == 0)) throw Invalid("可用时段的起止时间需同时填写或同时留空");
        if (input.Remark.Length > 255) throw Invalid("备注超长");

        return new PayChannelPassage
        {
            PassageId = passage.Id,
            Provider = passage.Provider,
            Methods = string.Join(',', methods),
            Weight = input.Weight,
            Priority = input.Priority,
            MinAmount = input.MinAmount,
            MaxAmount = input.MaxAmount,
            DailyLimitAmount = input.DailyLimitAmount,
            DailyLimitCount = input.DailyLimitCount,
            StartTime = start,
            EndTime = end,
            Status = input.Status == 0 ? 0 : 1,
            Remark = input.Remark,
        };
    }

    public async Task DeleteBindingAsync(long id)
    {
        if (await db.ExecuteAsync("DELETE FROM pay_channel_passage WHERE id=@id", new { id }) == 0) throw NotFound("绑定不存在");
        await cache.InvalidateAllAsync();
    }

    public Task ResetBreakerAsync(long id) => redis.ResetBreakerAsync(id);

    /// <summary>路由模拟：用与真实下单完全相同的算法，按当前配置与运行时状态模拟 N 次首选通道分布。</summary>
    public async Task<RouteSimulateResult> SimulateAsync(long channelId, RouteSimulateInput input)
    {
        var provider = providers.FindByMethod(input.PayMethod) ?? throw Invalid($"不支持的支付方式：{input.PayMethod}");
        if (input.Amount <= 0) throw Invalid("金额必须大于 0");
        var times = Math.Clamp(input.Times <= 0 ? 1000 : input.Times, 1, 100_000);

        cache.Invalidate();
        var evaluations = await router.EvaluateAsync(channelId, provider.Meta.Code, input.PayMethod, input.Amount);
        var hits = new Dictionary<long, int>();
        var random = new Random();
        for (var i = 0; i < times; i++)
        {
            var first = PassageRouter.Order(evaluations, random).FirstOrDefault();
            if (first is not null) hits[first.BindingId] = hits.GetValueOrDefault(first.BindingId) + 1;
        }

        return new RouteSimulateResult
        {
            PayMethod = input.PayMethod,
            Amount = input.Amount,
            Times = times,
            Items = evaluations.Select(e => new RouteSimulateItem
            {
                BindingId = e.Candidate.BindingId,
                PassageId = e.Candidate.PassageId,
                PassageName = e.Candidate.PassageName,
                Provider = e.Candidate.Provider,
                Priority = e.Candidate.Priority,
                Weight = e.Candidate.Weight,
                Eligible = e.Eligible,
                Reason = e.Reason,
                HitCount = hits.GetValueOrDefault(e.Candidate.BindingId),
                HitRate = Math.Round(hits.GetValueOrDefault(e.Candidate.BindingId) / (double)times, 4),
            }).ToList(),
        };
    }

    /// <summary>手续费率（万分比）：0~10000（0%~100%）。</summary>
    private static int ValidateFeeRate(int bp) =>
        bp is >= 0 and <= 10_000 ? bp : throw Invalid("手续费率需在 0~10000 万分比之间");

    private static PayException Invalid(string message) => new(ErrorCodes.InvalidParam, message);

    private static PayException NotFound(string message) => new(ErrorCodes.NotFound, message);
}
