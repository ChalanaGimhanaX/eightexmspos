using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface IAuthService
{
    Task<User> CreateUserAsync(string username, string displayName, string password, Role role, string? pin = null);
    Task<User> AuthenticateAsync(string username, string password, string tenantId, string branchId, string counterId);
    Task SetUserPinAsync(string userId, string pin);
    Task<User> VerifyPinAsync(string pin, Role minimumRole = Role.Manager, string? tenantId = null, string? branchId = null, string? counterId = null, string? actionDescription = null, string? cashierId = null);
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

    public async Task<User> CreateUserAsync(string username, string displayName, string password, Role role, string? pin = null)
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

        string? pinSalt = null;
        string? pinHash = null;

        if (!string.IsNullOrWhiteSpace(pin))
        {
            var pSaltBytes = RandomNumberGenerator.GetBytes(SaltSize);
            var pHashBytes = Rfc2898DeriveBytes.Pbkdf2(
                pin.Trim(),
                pSaltBytes,
                Iterations,
                HashAlgorithmName.SHA256,
                KeySize
            );
            pinSalt = Convert.ToBase64String(pSaltBytes);
            pinHash = Convert.ToBase64String(pHashBytes);
        }

        var user = new User
        {
            UserId = $"usr_{Guid.NewGuid():N}",
            Username = username.ToLowerInvariant().Trim(),
            DisplayName = displayName.Trim(),
            Role = role,
            PasswordHash = hash,
            PasswordSalt = salt,
            PinHash = pinHash,
            PinSalt = pinSalt,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO users (user_id, username, display_name, role, password_hash, password_salt, pin_hash, pin_salt, is_active, created_at_utc)
            VALUES ($id, $uname, $dname, $role, $hash, $salt, $phash, $psalt, $active, $created);
        ";
        cmd.Parameters.AddWithValue("$id", user.UserId);
        cmd.Parameters.AddWithValue("$uname", user.Username);
        cmd.Parameters.AddWithValue("$dname", user.DisplayName);
        cmd.Parameters.AddWithValue("$role", (int)user.Role);
        cmd.Parameters.AddWithValue("$hash", user.PasswordHash);
        cmd.Parameters.AddWithValue("$salt", user.PasswordSalt);
        cmd.Parameters.AddWithValue("$phash", (object?)user.PinHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$psalt", (object?)user.PinSalt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$active", user.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", user.CreatedAtUtc.ToString("o"));

        await cmd.ExecuteNonQueryAsync();
        return user;
    }

    public async Task SetUserPinAsync(string userId, string pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            throw new ArgumentException("PIN cannot be empty.", nameof(pin));
        }

        var pSaltBytes = RandomNumberGenerator.GetBytes(SaltSize);
        var pHashBytes = Rfc2898DeriveBytes.Pbkdf2(
            pin.Trim(),
            pSaltBytes,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize
        );
        var pinSalt = Convert.ToBase64String(pSaltBytes);
        var pinHash = Convert.ToBase64String(pHashBytes);

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE users SET pin_hash = $phash, pin_salt = $psalt WHERE user_id = $id;
        ";
        cmd.Parameters.AddWithValue("$phash", pinHash);
        cmd.Parameters.AddWithValue("$psalt", pinSalt);
        cmd.Parameters.AddWithValue("$id", userId);

        var affected = await cmd.ExecuteNonQueryAsync();
        if (affected == 0)
        {
            throw new PosException($"User '{userId}' not found.");
        }
    }

    public async Task<User> AuthenticateAsync(string username, string password, string tenantId, string branchId, string counterId)
    {
        var normUser = username.ToLowerInvariant().Trim();
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT user_id, username, display_name, role, password_hash, password_salt, is_active, created_at_utc, pin_hash, pin_salt
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
            CreatedAtUtc = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            PinHash = reader.IsDBNull(8) ? null : reader.GetString(8),
            PinSalt = reader.IsDBNull(9) ? null : reader.GetString(9)
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

    public async Task<User> VerifyPinAsync(
        string pin,
        Role minimumRole = Role.Manager,
        string? tenantId = null,
        string? branchId = null,
        string? counterId = null,
        string? actionDescription = null,
        string? cashierId = null)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            throw new ArgumentException("PIN cannot be empty.", nameof(pin));
        }

        var trimmedPin = pin.Trim();
        var effectiveTenant = tenantId ?? "TENANT_LK_01";
        var effectiveBranch = branchId ?? "B01";
        var effectiveCounter = counterId ?? "C01";

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT user_id, username, display_name, role, password_hash, password_salt, is_active, created_at_utc, pin_hash, pin_salt
            FROM users
            WHERE is_active = 1 AND role >= $minRole;
        ";
        cmd.Parameters.AddWithValue("$minRole", (int)minimumRole);

        var candidateUsers = new List<User>();
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                candidateUsers.Add(new User
                {
                    UserId = reader.GetString(0),
                    Username = reader.GetString(1),
                    DisplayName = reader.GetString(2),
                    Role = (Role)reader.GetInt32(3),
                    PasswordHash = reader.GetString(4),
                    PasswordSalt = reader.GetString(5),
                    IsActive = reader.GetInt32(6) == 1,
                    CreatedAtUtc = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
                    PinHash = reader.IsDBNull(8) ? null : reader.GetString(8),
                    PinSalt = reader.IsDBNull(9) ? null : reader.GetString(9)
                });
            }
        }

        foreach (var user in candidateUsers)
        {
            bool isMatch = false;

            // 1. Check against PIN if set
            if (!string.IsNullOrEmpty(user.PinHash) && !string.IsNullOrEmpty(user.PinSalt))
            {
                var pSaltBytes = Convert.FromBase64String(user.PinSalt);
                var pHashBytes = Rfc2898DeriveBytes.Pbkdf2(
                    trimmedPin,
                    pSaltBytes,
                    Iterations,
                    HashAlgorithmName.SHA256,
                    KeySize
                );
                var computedPinHash = Convert.ToBase64String(pHashBytes);

                if (CryptographicOperations.FixedTimeEquals(
                    Convert.FromBase64String(computedPinHash),
                    Convert.FromBase64String(user.PinHash)))
                {
                    isMatch = true;
                }
            }

            // 2. Fallback: check against user password (allows manager password to authorize)
            if (!isMatch && !string.IsNullOrEmpty(user.PasswordHash) && !string.IsNullOrEmpty(user.PasswordSalt))
            {
                var saltBytes = Convert.FromBase64String(user.PasswordSalt);
                var computedHashBytes = Rfc2898DeriveBytes.Pbkdf2(
                    trimmedPin,
                    saltBytes,
                    Iterations,
                    HashAlgorithmName.SHA256,
                    KeySize
                );
                var computedHash = Convert.ToBase64String(computedHashBytes);

                if (CryptographicOperations.FixedTimeEquals(
                    Convert.FromBase64String(computedHash),
                    Convert.FromBase64String(user.PasswordHash)))
                {
                    isMatch = true;
                }
            }

            if (isMatch)
            {
                // Log successful PIN authorization audit
                using var auditCmd = conn.CreateCommand();
                auditCmd.CommandText = @"
                    INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                    VALUES ($eid, $tid, $bid, $cid, $actor, 'MANAGER_PIN_VERIFIED', $details, $occurred);
                ";
                auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
                auditCmd.Parameters.AddWithValue("$tid", effectiveTenant);
                auditCmd.Parameters.AddWithValue("$bid", effectiveBranch);
                auditCmd.Parameters.AddWithValue("$cid", effectiveCounter);
                auditCmd.Parameters.AddWithValue("$actor", user.UserId);
                auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
                {
                    AuthorizerId = user.UserId,
                    AuthorizerName = user.DisplayName,
                    Role = user.Role.ToString(),
                    CashierId = cashierId,
                    SensitiveAction = actionDescription ?? "UNKNOWN"
                }));
                auditCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
                await auditCmd.ExecuteNonQueryAsync();

                return user;
            }
        }

        // Failed authorization
        using var failCmd = conn.CreateCommand();
        failCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, 'SYSTEM', 'MANAGER_PIN_FAILED', $details, $occurred);
        ";
        failCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        failCmd.Parameters.AddWithValue("$tid", effectiveTenant);
        failCmd.Parameters.AddWithValue("$bid", effectiveBranch);
        failCmd.Parameters.AddWithValue("$cid", effectiveCounter);
        failCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            SensitiveAction = actionDescription ?? "UNKNOWN",
            MinimumRole = minimumRole.ToString(),
            CashierId = cashierId,
            Reason = "Invalid PIN or user lacks required role."
        }));
        failCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await failCmd.ExecuteNonQueryAsync();

        throw new UnauthorizedActionException("Invalid PIN or user lacks required manager/owner authorization.");
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
}
