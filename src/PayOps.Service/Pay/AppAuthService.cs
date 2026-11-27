using Microsoft.Extensions.Options;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Core.Security;
using PayOps.Service.Infra;

namespace PayOps.Service.Pay;

/// <summary>业务接入方鉴权：HMAC-SHA256 签名 + 时间戳窗口 + nonce 防重放。</summary>
public sealed class AppAuthService(ConfigCache cache, RedisStore redis, IOptions<PayOpsOptions> options)
{
    private readonly SecurityOptions _security = options.Value.Security;

    public async Task<PayApp> AuthenticateAsync(string appId, string timestamp, string nonce, string sign, string method, string pathAndQuery, string body)
    {
        if (appId.Length == 0 || timestamp.Length == 0 || nonce.Length == 0 || sign.Length == 0)
            throw new PayException(ErrorCodes.Unauthorized, "缺少签名头 X-App-Id / X-Timestamp / X-Nonce / X-Sign");
        if (nonce.Length > 64) throw new PayException(ErrorCodes.Unauthorized, "X-Nonce 过长");
        if (!long.TryParse(timestamp, out var ts) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > _security.SignToleranceSeconds)
            throw new PayException(ErrorCodes.Unauthorized, "X-Timestamp 无效或已过期（需为秒级时间戳）");

        var entry = await cache.GetAppByCodeAsync(appId) ?? throw new PayException(ErrorCodes.Unauthorized, "应用不存在");
        if (entry.App.Status != 1) throw new PayException(ErrorCodes.AppDisabled, "应用已停用");
        if (!ApiSigner.Verify(entry.Secret, method, pathAndQuery, timestamp, nonce, body, sign))
            throw new PayException(ErrorCodes.SignError, "签名错误");
        if (!await redis.SetOnceAsync($"nonce:{appId}:{nonce}", TimeSpan.FromSeconds(_security.SignToleranceSeconds * 2)))
            throw new PayException(ErrorCodes.Unauthorized, "请求重放（nonce 已使用）");
        return entry.App;
    }
}
