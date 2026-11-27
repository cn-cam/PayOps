using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using PayOps.Core;
using PayOps.Core.Domain;

namespace PayOps.Service.Data;

public sealed record PageResult<T>(long Total, IReadOnlyList<T> Items);

/// <summary>MySQL 访问入口（Dapper + MySqlConnector）。库内时间一律 UTC。</summary>
public sealed class Db
{
    private static readonly Dictionary<Type, string> Tables = new()
    {
        [typeof(PayApp)] = "pay_app",
        [typeof(PayChannel)] = "pay_channel",
        [typeof(PayPassage)] = "pay_passage",
        [typeof(PayChannelPassage)] = "pay_channel_passage",
        [typeof(PayOrder)] = "pay_order",
        [typeof(PayRefund)] = "pay_refund",
        [typeof(PaySubscription)] = "pay_subscription",
        [typeof(PayReconBatch)] = "pay_recon_batch",
        [typeof(PayReconDiff)] = "pay_recon_diff",
        [typeof(PayNotifyTask)] = "pay_notify_task",
        [typeof(PayUpstreamNotify)] = "pay_upstream_notify",
        [typeof(SysUser)] = "sys_user",
        [typeof(SysAuditLog)] = "sys_audit_log",
    };

    private static readonly ConcurrentDictionary<Type, string> InsertSqlCache = new();
    private readonly MySqlDataSource _dataSource;

    static Db()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Db(IOptions<PayOpsOptions> options)
    {
        ConnectionString = Normalize(options.Value.Database.ConnectionString);
        _dataSource = new MySqlDataSourceBuilder(ConnectionString).Build();
    }

    public string ConnectionString { get; }

    public static string Normalize(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("未配置 PayOps:Database:ConnectionString");
        var b = new MySqlConnectionStringBuilder(connectionString)
        {
            DateTimeKind = MySqlDateTimeKind.Utc,
            CharacterSet = "utf8mb4",
        };
        return b.ConnectionString;
    }

    public static string TableOf<T>() => Tables[typeof(T)];

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct = default) => await _dataSource.OpenConnectionAsync(ct);

    public async Task<List<T>> QueryAsync<T>(string sql, object? param = null)
    {
        await using var conn = await OpenAsync();
        return (await conn.QueryAsync<T>(sql, param)).AsList();
    }

    public async Task<T?> FirstOrDefaultAsync<T>(string sql, object? param = null)
    {
        await using var conn = await OpenAsync();
        return await conn.QueryFirstOrDefaultAsync<T>(sql, param);
    }

    public async Task<T?> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<T>(sql, param);
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteAsync(sql, param);
    }

    public Task<T?> GetAsync<T>(long id) =>
        FirstOrDefaultAsync<T>($"SELECT * FROM {TableOf<T>()} WHERE id = @id", new { id });

    public async Task<long> InsertAsync<T>(T entity)
    {
        await using var conn = await OpenAsync();
        return await InsertAsync(conn, null, entity);
    }

    public static Task<long> InsertAsync<T>(IDbConnection conn, IDbTransaction? tx, T entity) =>
        conn.ExecuteScalarAsync<long>(InsertSqlCache.GetOrAdd(typeof(T), BuildInsertSql), entity, tx);

    private static string BuildInsertSql(Type type)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "Id" && p.CanRead && p.CanWrite)
            .ToList();
        var columns = string.Join(", ", props.Select(p => SnakeCase(p.Name)));
        var values = string.Join(", ", props.Select(p => "@" + p.Name));
        return $"INSERT INTO {Tables[type]} ({columns}) VALUES ({values}); SELECT LAST_INSERT_ID();";
    }

    public async Task<T> TransactionAsync<T>(Func<MySqlConnection, MySqlTransaction, Task<T>> action)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var result = await action(conn, tx);
        await tx.CommitAsync();
        return result;
    }

    public async Task<PageResult<T>> PageAsync<T>(string select, string from, SqlWhere where, string orderBy, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 200);
        var p = where.Parameters;
        p.Add("_offset", (page - 1) * pageSize);
        p.Add("_limit", pageSize);
        await using var conn = await OpenAsync();
        var total = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {from} {where.Sql}", p);
        var items = total == 0
            ? []
            : (await conn.QueryAsync<T>($"SELECT {select} FROM {from} {where.Sql} ORDER BY {orderBy} LIMIT @_limit OFFSET @_offset", p)).AsList();
        return new PageResult<T>(total, items);
    }

    public static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static bool IsDuplicateKey(MySqlException ex) => ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry;
}

/// <summary>动态 WHERE 构造器：仅在值有意义时追加条件，全部参数化。</summary>
public sealed class SqlWhere
{
    private readonly List<string> _clauses = [];
    private int _index;

    public DynamicParameters Parameters { get; } = new();

    public string Sql => _clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", _clauses);

    /// <summary>clause 中以 {0} 占位参数，例如 "o.status = {0}"。</summary>
    public SqlWhere Add(string clause, object? value)
    {
        if (value is null) return this;
        if (value is string s && s.Trim().Length == 0) return this;
        var name = "w" + _index++;
        Parameters.Add(name, value is string str ? str.Trim() : value);
        _clauses.Add(string.Format(clause, "@" + name));
        return this;
    }

    public SqlWhere Like(string column, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return this;
        var name = "w" + _index++;
        var escaped = keyword.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        Parameters.Add(name, "%" + escaped + "%");
        _clauses.Add($"{column} LIKE @{name}");
        return this;
    }

    public SqlWhere Raw(string clause)
    {
        _clauses.Add(clause);
        return this;
    }
}
