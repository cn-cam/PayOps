using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Core.Security;

namespace PayOps.Service.Data;

/// <summary>
/// 版本化 SQL 迁移：脚本内嵌在程序集（Data/Migrations/V###__name.sql），已执行版本记录在 schema_version。
/// 多副本同时启动时用 MySQL GET_LOCK 互斥，保证每个脚本只执行一次。
/// </summary>
public sealed class Migrator(Db db, IOptions<PayOpsOptions> options, ILogger<Migrator> logger)
{
    private const string LockName = "payops_migrate";

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await EnsureDatabaseAsync(ct);

        await using var conn = await db.OpenAsync(ct);
        var locked = await conn.ExecuteScalarAsync<int?>("SELECT GET_LOCK(@name, 120)", new { name = LockName });
        if (locked != 1) throw new InvalidOperationException("获取数据库迁移锁超时");
        try
        {
            await conn.ExecuteAsync(
                """
                CREATE TABLE IF NOT EXISTS schema_version (
                  version    VARCHAR(128) NOT NULL,
                  applied_at DATETIME(3)  NOT NULL,
                  PRIMARY KEY (version)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """);
            var applied = (await conn.QueryAsync<string>("SELECT version FROM schema_version")).ToHashSet();

            var assembly = typeof(Migrator).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                var version = ScriptName(resource);
                if (applied.Contains(version)) continue;
                logger.LogInformation("执行数据库迁移 {Version}", version);
                await conn.ExecuteAsync(await ReadAsync(assembly, resource), commandTimeout: 300);
                await conn.ExecuteAsync("INSERT INTO schema_version (version, applied_at) VALUES (@version, UTC_TIMESTAMP(3))", new { version });
            }

            await SeedAdminAsync(conn);
        }
        finally
        {
            await conn.ExecuteAsync("SELECT RELEASE_LOCK(@name)", new { name = LockName });
        }
    }

    private async Task EnsureDatabaseAsync(CancellationToken ct)
    {
        var builder = new MySqlConnectionStringBuilder(db.ConnectionString);
        var database = builder.Database;
        if (string.IsNullOrEmpty(database)) throw new InvalidOperationException("连接串必须指定 Database");
        builder.Database = "";
        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{database.Replace("`", "")}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
    }

    private async Task SeedAdminAsync(MySqlConnection conn)
    {
        if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sys_user") > 0) return;
        var now = DateTime.UtcNow;
        await Db.InsertAsync(conn, null, new SysUser
        {
            Username = "admin",
            DisplayName = "超级管理员",
            PasswordHash = PasswordHasher.Hash(options.Value.Security.InitAdminPassword),
            Role = Roles.Admin,
            CreatedAt = now,
            UpdatedAt = now,
        });
        logger.LogWarning("已创建初始管理员账号 admin，请登录后立即修改密码");
    }

    private static string ScriptName(string resource)
    {
        var withoutExt = resource[..^4];
        var idx = withoutExt.LastIndexOf(".V", StringComparison.Ordinal);
        return idx >= 0 ? withoutExt[(idx + 1)..] : withoutExt;
    }

    private static async Task<string> ReadAsync(Assembly assembly, string resource)
    {
        await using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
