using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface IAuthService
{
    Task<User> CreateUserAsync(string username, string displayName, string password, Role role);
    Task<User> AuthenticateAsync(string username, string password, string tenantId, string branchId, string counterId);
    Task<List<User>> GetAllUsersAsync();
    Task UpdateUserRoleAsync(string userId, Role newRole, string actorId, string tenantId, string branchId, string counterId);
    Task SetUserActiveAsync(string userId, bool isActive, string actorId, string tenantId, string branchId, string counterId);
    Task ResetPasswordAsync(string userId, string newPassword, string actorId, string tenantId, string branchId, string counterId);
}

public class AuthService : IAuthService
{
    private readonly PosDatabase _db;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 100_000;

    public AuthService(PosDatabase db)
    {
        _db = db;
    }

    public async Task<User> CreateUserAsync(string username, string displayName, string password, Role role)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltSize);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            password,
            saltBytes,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize
        );

        var salt = Convert.ToBase64String(saltBytes);
        var hash = Convert.ToBase64String(hashBytes);

        var user = new User
        {
            UserId = $"usr_{Guid.NewGuid():N}",
            Username = username.ToLowerInvariant().Trim(),
            DisplayName = displayName.Trim(),
            Role = role,
            PasswordHash = hash,
            PasswordSalt = salt,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO users (user_id, username, display_name, role, password_hash, password_salt, is_active, created_at_utc)
            VALUES ($id, $uname, $dname, $role, $hash, $salt, $active, $created);
        ";
        cmd.Parameters.AddWithValue("$id", user.UserId);
        cmd.Parameters.AddWithValue("$uname", user.Username);
        cmd.Parameters.AddWithValue("$dname", user.DisplayName);
        cmd.Parameters.AddWithValue("$role", (int)user.Role);
        cmd.Parameters.AddWithValue("$hash", user.PasswordHash);
        cmd.Parameters.AddWithValue("$salt", user.PasswordSalt);
        cmd.Parameters.AddWithValue("$active", user.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", user.CreatedAtUtc.ToString("o"));

        await cmd.ExecuteNonQueryAsync();
        return user;
    }

    public async Task<User> AuthenticateAsync(string username, string password, string tenantId, string branchId, string counterId)
    {
        var normUser = username.ToLowerInvariant().Trim();
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT user_id, username, display_name, role, password_hash, password_salt, is_active, created_at_utc
            FROM users WHERE username = $uname;
        ";
        cmd.Parameters.AddWithValue("$uname", normUser);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            await LogLoginAuditAsync(tenantId, branchId, counterId, normUser, success: false, "User not found");
            throw new AuthenticationException("Invalid username or password.");
        }

        var user = new User
        {
            UserId = reader.GetString(0),
            Username = reader.GetString(1),
            DisplayName = reader.GetString(2),
            Role = (Role)reader.GetInt32(3),
            PasswordHash = reader.GetString(4),
            PasswordSalt = reader.GetString(5),
            IsActive = reader.GetInt32(6) == 1,
            CreatedAtUtc = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
        };

        if (!user.IsActive)
        {
            await LogLoginAuditAsync(tenantId, branchId, counterId, user.UserId, success: false, "User deactivated");
            throw new AuthenticationException("User account is inactive.");
        }

        var saltBytes = Convert.FromBase64String(user.PasswordSalt);
        var computedHashBytes = Rfc2898DeriveBytes.Pbkdf2(
            password,
            saltBytes,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize
        );
        var computedHash = Convert.ToBase64String(computedHashBytes);

        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(computedHash),
            Convert.FromBase64String(user.PasswordHash)))
        {
            await LogLoginAuditAsync(tenantId, branchId, counterId, user.UserId, success: false, "Invalid password");
            throw new AuthenticationException("Invalid username or password.");
        }

        await LogLoginAuditAsync(tenantId, branchId, counterId, user.UserId, success: true, "Login successful");
        return user;
    }

    private async Task LogLoginAuditAsync(string tenantId, string branchId, string counterId, string actorId, bool success, string reason)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, $action, $details, $occurred);
        ";
        cmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$tid", tenantId);
        cmd.Parameters.AddWithValue("$bid", branchId);
        cmd.Parameters.AddWithValue("$cid", counterId);
        cmd.Parameters.AddWithValue("$actor", actorId);
        cmd.Parameters.AddWithValue("$action", success ? "USER_LOGIN_SUCCESS" : "USER_LOGIN_FAILED");
        cmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new { Reason = reason }));
        cmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<User>> GetAllUsersAsync()
    {
        var users = new List<User>();
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT user_id, username, display_name, role, password_hash, password_salt, is_active, created_at_utc
            FROM users
            ORDER BY display_name ASC;
        ";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            users.Add(new User
            {
                UserId = reader.GetString(0),
                Username = reader.GetString(1),
                DisplayName = reader.GetString(2),
                Role = (Role)reader.GetInt32(3),
                PasswordHash = reader.GetString(4),
                PasswordSalt = reader.GetString(5),
                IsActive = reader.GetInt32(6) == 1,
                CreatedAtUtc = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
            });
        }
        return users;
    }

    public async Task UpdateUserRoleAsync(string userId, Role newRole, string actorId, string tenantId, string branchId, string counterId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE users SET role = $role WHERE user_id = $id;
        ";
        cmd.Parameters.AddWithValue("$role", (int)newRole);
        cmd.Parameters.AddWithValue("$id", userId);
        await cmd.ExecuteNonQueryAsync();

        // Audit log
        using var auditCmd = conn.CreateCommand();
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, 'USER_ROLE_UPDATED', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", tenantId);
        auditCmd.Parameters.AddWithValue("$bid", branchId);
        auditCmd.Parameters.AddWithValue("$cid", counterId);
        auditCmd.Parameters.AddWithValue("$actor", actorId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new { TargetUserId = userId, NewRole = newRole.ToString() }));
        auditCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();
    }

    public async Task SetUserActiveAsync(string userId, bool isActive, string actorId, string tenantId, string branchId, string counterId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE users SET is_active = $active WHERE user_id = $id;
        ";
        cmd.Parameters.AddWithValue("$active", isActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", userId);
        await cmd.ExecuteNonQueryAsync();

        // Audit log
        using var auditCmd = conn.CreateCommand();
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, 'USER_STATUS_UPDATED', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", tenantId);
        auditCmd.Parameters.AddWithValue("$bid", branchId);
        auditCmd.Parameters.AddWithValue("$cid", counterId);
        auditCmd.Parameters.AddWithValue("$actor", actorId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new { TargetUserId = userId, IsActive = isActive }));
        auditCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();
    }

    public async Task ResetPasswordAsync(string userId, string newPassword, string actorId, string tenantId, string branchId, string counterId)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltSize);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            newPassword,
            saltBytes,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize
        );
        var salt = Convert.ToBase64String(saltBytes);
        var hash = Convert.ToBase64String(hashBytes);

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE users SET password_hash = $hash, password_salt = $salt WHERE user_id = $id;
        ";
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$salt", salt);
        cmd.Parameters.AddWithValue("$id", userId);
        await cmd.ExecuteNonQueryAsync();

        // Audit log
        using var auditCmd = conn.CreateCommand();
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, 'USER_PASSWORD_RESET', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", tenantId);
        auditCmd.Parameters.AddWithValue("$bid", branchId);
        auditCmd.Parameters.AddWithValue("$cid", counterId);
        auditCmd.Parameters.AddWithValue("$actor", actorId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new { TargetUserId = userId }));
        auditCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();
    }
}
