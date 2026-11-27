namespace PayOps.Core;

public sealed class PayOpsOptions
{
    public const string Section = "PayOps";

    /// <summary>进程角色，逗号分隔：Gateway（下单与上游回调）、Admin（管理后台）、Worker（后台任务）；All = 全部（单体）。</summary>
    public string Roles { get; set; } = "All";

    /// <summary>对外可访问的基础地址，用于拼接上游回调地址。</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>业务时区（小时偏移），用于日限额、可用时段与报表按天统计。</summary>
    public int TimeZoneOffsetHours { get; set; } = 8;

    /// <summary>开启演示端点（模拟业务方通知接收端）。生产环境保持 false。</summary>
    public bool EnableDemoEndpoints { get; set; }

    public DatabaseOptions Database { get; set; } = new();
    public RedisOptions Redis { get; set; } = new();
    public RabbitMqOptions RabbitMq { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public OrderOptions Order { get; set; } = new();
    public NotifyOptions Notify { get; set; } = new();
    public BreakerOptions Breaker { get; set; } = new();
    public ReconOptions Recon { get; set; } = new();

    public bool HasRole(string role) =>
        Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(r => r.Equals("All", StringComparison.OrdinalIgnoreCase) || r.Equals(role, StringComparison.OrdinalIgnoreCase));

    public string[] RoleList() => new[] { "Gateway", "Admin", "Worker" }.Where(HasRole).ToArray();

    public TimeSpan TimeZoneOffset => TimeSpan.FromHours(TimeZoneOffsetHours);
}

public sealed class DatabaseOptions
{
    public string ConnectionString { get; set; } = "";
    /// <summary>启动时自动建库并执行版本化迁移脚本（多副本通过 MySQL GET_LOCK 互斥）。</summary>
    public bool AutoMigrate { get; set; } = true;
}

public sealed class RedisOptions
{
    /// <summary>StackExchange.Redis 连接串，支持单机 / Sentinel（serviceName=）/ Cluster。</summary>
    public string ConnectionString { get; set; } = "localhost:6379";
    public string KeyPrefix { get; set; } = "payops:";
}

public sealed class RabbitMqOptions
{
    /// <summary>逗号分隔的 host[:port] 列表，集群时填多个节点。</summary>
    public string Hosts { get; set; } = "localhost:5672";
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    /// <summary>交换机与队列名前缀。</summary>
    public string Prefix { get; set; } = "payops";
    /// <summary>quorum（多副本集群推荐）或 classic。</summary>
    public string QueueType { get; set; } = "quorum";
    public ushort NotifyPrefetch { get; set; } = 20;
    public ushort CheckPrefetch { get; set; } = 10;
}

public sealed class SecurityOptions
{
    /// <summary>通道凭证与应用密钥的落库加密主密钥，所有副本必须一致，丢失后密文不可恢复。</summary>
    public string MasterKey { get; set; } = "";
    public string JwtKey { get; set; } = "";
    public int JwtExpireMinutes { get; set; } = 480;
    /// <summary>首次启动创建 admin 账号使用的初始密码。</summary>
    public string InitAdminPassword { get; set; } = "Admin@123456";
    /// <summary>业务签名时间戳容忍秒数。</summary>
    public int SignToleranceSeconds { get; set; } = 300;
}

public sealed class OrderOptions
{
    public int DefaultExpireSeconds { get; set; } = 1800;
    /// <summary>单次下单最多尝试的通道数（故障转移）。</summary>
    public int MaxRouteAttempts { get; set; } = 3;
    /// <summary>待支付订单主动查单间隔（秒），按次数递进，最后一档重复使用。</summary>
    public int[] CheckIntervals { get; set; } = [30, 60, 120, 300, 600];
    public int RefundCheckMaxCount { get; set; } = 30;
}

public sealed class NotifyOptions
{
    public int TimeoutSeconds { get; set; } = 10;
    /// <summary>失败后的重试间隔（秒）；总尝试次数 = 长度 + 1。</summary>
    public int[] RetryIntervals { get; set; } = [15, 30, 60, 180, 600, 1800, 3600, 7200, 21600];
}

public sealed class BreakerOptions
{
    public int FailureThreshold { get; set; } = 5;
    public int WindowSeconds { get; set; } = 60;
    public int OpenSeconds { get; set; } = 30;
}

public sealed class ReconOptions
{
    /// <summary>是否每天自动为支持账单的通道生成昨日对账批次。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>业务时区几点之后开始对昨天的账（微信/抖音账单 10 点后可取）。</summary>
    public int Hour { get; set; } = 10;
    /// <summary>账单未就绪时的最大尝试次数与重试间隔。</summary>
    public int MaxAttempts { get; set; } = 6;
    public int RetryMinutes { get; set; } = 60;
}