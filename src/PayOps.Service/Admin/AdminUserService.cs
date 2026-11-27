using MySqlConnector;
using PayOps.Core;
using PayOps.Core.Domain;
using PayOps.Core.Security;
using PayOps.Service.Data;

namespace PayOps.Service.Admin;

/// <summary>后台用户、登录校验与操作审计。</summary>
public sealed class AdminUserService(Db db)
{
    private const string UserSelect = "id, username, display_name, role, status, last_login_at, created_at";

    public async Task<SysUser> LoginAsync(string username, string password)
    {
        var user = await db.FirstOrDefaultAsync<SysUser>("SELECT * FROM sys_user WHERE username = @username", new { username = username.Trim() });
        // 登录失败按契约返回 HTTP 200 + code != 0（Unauthorized 会被映射成 HTTP 401，留给令牌失效使用）
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
            throw new PayException(ErrorCodes.InvalidParam, "用户名或密码错误");
        if (user.Status != 1) throw new PayException(ErrorCodes.InvalidParam, "账号已停用");
        await db.ExecuteAsync("UPDATE sys_user SET last_login_at = @now WHERE id = @Id", new { user.Id, now = DateTime.UtcNow });
        return user;
    }

    public Task<SysUser?> FindAsync(long id) => db.GetAsync<SysUser>(id);

    public async Task ChangePasswordAsync(long id, string oldPassword, string newPassword)
    {
        var user = await db.GetAsync<SysUser>(id) ?? throw new PayException(ErrorCodes.NotFound, "用户不存在");
        if (!PasswordHasher.Verify(oldPassword, user.PasswordHash)) throw new PayException(ErrorCodes.InvalidParam, "原密码不正确");
        await SetPasswordAsync(id, newPassword);
    }

    public async Task SetPasswordAsync(long id, string password)
    {
        ValidatePassword(password);
        var rows = await db.ExecuteAsync("UPDATE sys_user SET password_hash = @hash, updated_at = @now WHERE id = @id",
            new { id, hash = PasswordHasher.Hash(password), now = DateTime.UtcNow });
        if (rows == 0) throw new PayException(ErrorCodes.NotFound, "用户不存在");
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length is < 8 or > 64) throw new PayException(ErrorCodes.InvalidParam, "密码长度需在 8~64 位之间");
    }

    public Task<List<UserItem>> ListAsync() => db.QueryAsync<UserItem>($"SELECT {UserSelect} FROM sys_user ORDER BY id");

    public async Task<long> CreateAsync(UserInput input)
    {
        var username = input.Username.Trim();
        if (username.Length is < 2 or > 32) throw new PayException(ErrorCodes.InvalidParam, "用户名长度需在 2~32 之间");
        if (!Roles.All.Contains(input.Role)) throw new PayException(ErrorCodes.InvalidParam, "角色无效");
        ValidatePassword(input.Password);
        var now = DateTime.UtcNow;
        try
        {
            return await db.InsertAsync(new SysUser
            {
                Username = username,
                DisplayName = input.DisplayName.Trim(),
                PasswordHash = PasswordHasher.Hash(input.Password),
                Role = input.Role,
                Status = input.Status == 0 ? 0 : 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        catch (MySqlException ex) when (Db.IsDuplicateKey(ex))
        {
            throw new PayException(ErrorCodes.Conflict, "用户名已存在");
        }
    }

    public async Task UpdateAsync(long id, UserInput input, long operatorId)
    {
        if (!Roles.All.Contains(input.Role)) throw new PayException(ErrorCodes.InvalidParam, "角色无效");
        var status = input.Status == 0 ? 0 : 1;
        if (id == operatorId && (input.Role != Roles.Admin || status != 1))
            throw new PayException(ErrorCodes.InvalidParam, "不能降低或停用自己的账号");
        var rows = await db.ExecuteAsync("UPDATE sys_user SET display_name = @name, role = @role, status = @status, updated_at = @now WHERE id = @id",
            new { id, name = input.DisplayName.Trim(), role = input.Role, status, now = DateTime.UtcNow });
        if (rows == 0) throw new PayException(ErrorCodes.NotFound, "用户不存在");
    }

    public async Task DeleteAsync(long id, long operatorId)
    {
        if (id == operatorId) throw new PayException(ErrorCodes.InvalidParam, "不能删除自己的账号");
        if (await db.ExecuteAsync("DELETE FROM sys_user WHERE id = @id", new { id }) == 0)
            throw new PayException(ErrorCodes.NotFound, "用户不存在");
    }

    // ------------------------------------------------------------------ 审计

    public async Task AuditAsync(long userId, string username, string action, string target, string detail, string ip)
    {
        await db.InsertAsync(new SysAuditLog
        {
            UserId = userId,
            Username = username,
            Action = action,
            Target = target.Length > 128 ? target[..128] : target,
            Detail = detail.Length > 4000 ? detail[..4000] : detail,
            Ip = ip,
            CreatedAt = DateTime.UtcNow,
        });
    }

    public Task<PageResult<SysAuditLog>> ListAuditAsync(AuditQuery q)
    {
        var where = new SqlWhere()
            .Add("username = {0}", q.Username)
            .Like("action", q.Action)
            .Add("created_at >= {0}", q.StartUtc)
            .Add("created_at < {0}", q.EndUtc);
        return db.PageAsync<SysAuditLog>("*", "sys_audit_log", where, "id DESC", q.Page, q.PageSize);
    }
}
