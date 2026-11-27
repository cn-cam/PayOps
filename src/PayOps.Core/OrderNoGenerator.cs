namespace PayOps.Core;

/// <summary>
/// 订单号生成：前缀(1) + yyMMddHHmmss(12) + 节点号(3) + 秒内序列(5) = 21 位。
/// 节点号由各副本通过 Redis 租约独占，保证多副本下不冲突；时钟回拨时沿用上一秒继续递增。
/// </summary>
public sealed class OrderNoGenerator
{
    private const int MaxSequence = 99_999;
    private readonly Lock _lock = new();
    private long _lastSecond;
    private int _sequence;

    /// <summary>0~999，由 WorkerIdLease 写入。</summary>
    public int WorkerId { get; set; }

    public string Next(char prefix)
    {
        long second;
        int seq;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now > _lastSecond)
            {
                _lastSecond = now;
                _sequence = Random.Shared.Next(0, 100);
            }
            else if (++_sequence > MaxSequence)
            {
                _lastSecond++;
                _sequence = 0;
            }
            second = _lastSecond;
            seq = _sequence;
        }

        var time = DateTimeOffset.FromUnixTimeSeconds(second).ToOffset(TimeSpan.FromHours(8));
        return string.Create(21, (prefix, time, WorkerId, seq), static (span, s) =>
        {
            span[0] = s.prefix;
            s.time.TryFormat(span[1..], out _, "yyMMddHHmmss");
            (s.WorkerId % 1000).TryFormat(span[13..], out _, "000");
            s.seq.TryFormat(span[16..], out _, "00000");
        });
    }
}
