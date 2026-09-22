using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface ILicenseService
{
    Task<LicenceEntitlement> ApplyEntitlementAsync(string signedToken, string tenantId);
    Task<LicenceEntitlement?> GetActiveEntitlementAsync(string tenantId);
    Task<LicenseStatusInfo> EvaluateLicenseStatusAsync(string tenantId, DateTime? asOfUtc = null);
    void ValidateSaleAllowed(string tenantId, DateTime? asOfUtc = null);
    bool HasFeature(string tenantId, string featureName);
}

public class LicenseService : ILicenseService
{
    public const string DefaultSigningKey = "Enightx-Pos-Master-Licensing-Secret-LK-2026!#$";
    private readonly PosDatabase _db;
    private readonly string _secretKey;

    public LicenseService(PosDatabase db, string? secretKey = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _secretKey = secretKey ?? Environment.GetEnvironmentVariable("ENIGHTX_LICENSE_SECRET") ?? DefaultSigningKey;
    }

    public async Task<LicenceEntitlement> ApplyEntitlementAsync(string signedToken, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(signedToken))
            throw new PosException("License entitlement token cannot be empty.");
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new PosException("Tenant ID cannot be empty.");

        string payloadBase64;
        string signatureStr;

        var trimmed = signedToken.Trim();
        if (trimmed.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (!doc.RootElement.TryGetProperty("payload", out var payloadProp) ||
                    !doc.RootElement.TryGetProperty("signature", out var sigProp))
                {
                    throw new PosException("Invalid entitlement token JSON. Expected 'payload' and 'signature' properties.");
                }
                payloadBase64 = payloadProp.GetString() ?? throw new PosException("Token payload is null.");
                signatureStr = sigProp.GetString() ?? throw new PosException("Token signature is null.");
            }
            catch (JsonException ex)
            {
                throw new PosException($"Malformed license entitlement token JSON: {ex.Message}", ex);
            }
        }
        else
        {
            var parts = trimmed.Split('.');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                throw new PosException("Invalid entitlement token format. Expected <payload_base64>.<signature_hex>.");
            }
            payloadBase64 = parts[0];
            signatureStr = parts[1];
        }

        // 1. Parse signature
        byte[] providedSig;
        try
        {
            providedSig = Convert.FromHexString(signatureStr);
        }
        catch (FormatException)
        {
            try
            {
                providedSig = Convert.FromBase64String(signatureStr);
            }
            catch (FormatException)
            {
                throw new PosException("Invalid signature encoding in entitlement token.");
            }
        }

        // 2. Cryptographic HMAC-SHA256 signature verification (constant-time)
        byte[] expectedSigOverBase64;
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secretKey)))
        {
            expectedSigOverBase64 = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadBase64));
        }

        bool signatureValid = CryptographicOperations.FixedTimeEquals(expectedSigOverBase64, providedSig);
        if (!signatureValid)
        {
            // Fallback check: signature computed over decoded payload bytes
            try
            {
                var rawBytes = Convert.FromBase64String(payloadBase64);
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secretKey));
                var expectedSigOverRaw = hmac.ComputeHash(rawBytes);
                signatureValid = CryptographicOperations.FixedTimeEquals(expectedSigOverRaw, providedSig);
            }
            catch { }
        }

        if (!signatureValid)
        {
            throw new PosException("Invalid cryptographic signature on license entitlement token.");
        }

        // 3. Decode JSON payload
        EntitlementPayloadDto dto;
        try
        {
            var jsonBytes = Convert.FromBase64String(payloadBase64);
            var json = Encoding.UTF8.GetString(jsonBytes);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            dto = JsonSerializer.Deserialize<EntitlementPayloadDto>(json, options)
                  ?? throw new PosException("Failed to deserialize entitlement payload.");
        }
        catch (Exception ex) when (ex is not PosException)
        {
            throw new PosException($"Malformed license entitlement payload: {ex.Message}", ex);
        }

        // 4. Tenant matching check
        if (!dto.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new PosException($"Entitlement token is issued for tenant '{dto.TenantId}', but target tenant is '{tenantId}'.");
        }

        // 5. Date validation
        if (!DateTime.TryParse(dto.IssuedAt, null, DateTimeStyles.AdjustToUniversal, out var issuedAt))
            throw new PosException("Invalid issued_at date in entitlement payload.");
        if (!DateTime.TryParse(dto.ExpiresAt, null, DateTimeStyles.AdjustToUniversal, out var expiresAt))
            throw new PosException("Invalid expires_at date in entitlement payload.");
        if (expiresAt < issuedAt)
            throw new PosException("Entitlement expires_at cannot precede issued_at.");

        // 6. Anti-downgrade check against existing stored entitlement
        var current = await GetActiveEntitlementAsync(tenantId);
        if (current != null && dto.Revision < current.Revision)
        {
            throw new PosException($"Cannot apply entitlement with revision {dto.Revision} because existing revision {current.Revision} is newer.");
        }

        var entitlement = new LicenceEntitlement
        {
            EntitlementId = string.IsNullOrWhiteSpace(dto.EntitlementId) ? Guid.NewGuid().ToString() : dto.EntitlementId,
            TenantId = dto.TenantId,
            Plan = dto.Plan,
            IssuedAtUtc = issuedAt,
            ExpiresAtUtc = expiresAt,
            GracePeriodDays = dto.GracePeriodDays >= 0 ? dto.GracePeriodDays : 0,
            MaxDevices = dto.MaxDevices > 0 ? dto.MaxDevices : 1,
            Features = dto.Features ?? new List<string>(),
            Revision = dto.Revision,
            SignatureHex = Convert.ToHexString(providedSig).ToLowerInvariant(),
            RawPayloadBase64 = payloadBase64,
            RawToken = signedToken,
            AppliedAtUtc = DateTime.UtcNow
        };

        // 7. Persist to SQLite
        using (var conn = _db.CreateConnection())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT INTO license_entitlements (
                        entitlement_id, tenant_id, plan, issued_at_utc, expires_at_utc,
                        grace_period_days, max_devices, features_json, revision, signature_hex,
                        raw_payload_base64, applied_at_utc
                    ) VALUES (
                        $entitlementId, $tenantId, $plan, $issuedAt, $expiresAt,
                        $graceDays, $maxDevices, $featuresJson, $revision, $signatureHex,
                        $rawPayload, $appliedAt
                    );
                ";
                cmd.Parameters.AddWithValue("$entitlementId", entitlement.EntitlementId);
                cmd.Parameters.AddWithValue("$tenantId", entitlement.TenantId);
                cmd.Parameters.AddWithValue("$plan", entitlement.Plan);
                cmd.Parameters.AddWithValue("$issuedAt", entitlement.IssuedAtUtc.ToString("o"));
                cmd.Parameters.AddWithValue("$expiresAt", entitlement.ExpiresAtUtc.ToString("o"));
                cmd.Parameters.AddWithValue("$graceDays", entitlement.GracePeriodDays);
                cmd.Parameters.AddWithValue("$maxDevices", entitlement.MaxDevices);
                cmd.Parameters.AddWithValue("$featuresJson", JsonSerializer.Serialize(entitlement.Features));
                cmd.Parameters.AddWithValue("$revision", entitlement.Revision);
                cmd.Parameters.AddWithValue("$signatureHex", entitlement.SignatureHex);
                cmd.Parameters.AddWithValue("$rawPayload", entitlement.RawPayloadBase64);
                cmd.Parameters.AddWithValue("$appliedAt", entitlement.AppliedAtUtc.ToString("o"));

                await cmd.ExecuteNonQueryAsync();
            }

            // 8. Log audit event ENTITLEMENT_APPLIED
            using (var auditCmd = conn.CreateCommand())
            {
                auditCmd.CommandText = @"
                    INSERT INTO audit_events (
                        event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc
                    ) VALUES (
                        $eventId, $tenantId, $branchId, $counterId, $actorId, $action, $details, $occurredAt
                    );
                ";
                auditCmd.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString());
                auditCmd.Parameters.AddWithValue("$tenantId", entitlement.TenantId);
                auditCmd.Parameters.AddWithValue("$branchId", "SYSTEM");
                auditCmd.Parameters.AddWithValue("$counterId", "SYSTEM");
                auditCmd.Parameters.AddWithValue("$actorId", "SYSTEM");
                auditCmd.Parameters.AddWithValue("$action", "ENTITLEMENT_APPLIED");
                auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
                {
                    entitlement_id = entitlement.EntitlementId,
                    plan = entitlement.Plan,
                    revision = entitlement.Revision,
                    expires_at = entitlement.ExpiresAtUtc.ToString("o")
                }));
                auditCmd.Parameters.AddWithValue("$occurredAt", DateTime.UtcNow.ToString("o"));

                await auditCmd.ExecuteNonQueryAsync();
            }
        }

        return entitlement;
    }

    public async Task<LicenceEntitlement?> GetActiveEntitlementAsync(string tenantId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT entitlement_id, tenant_id, plan, issued_at_utc, expires_at_utc,
                   grace_period_days, max_devices, features_json, revision, signature_hex,
                   raw_payload_base64, applied_at_utc
            FROM license_entitlements
            WHERE tenant_id = $tenantId
            ORDER BY revision DESC, applied_at_utc DESC
            LIMIT 1;
        ";
        cmd.Parameters.AddWithValue("$tenantId", tenantId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return MapEntitlement(reader);
    }

    public async Task<LicenseStatusInfo> EvaluateLicenseStatusAsync(string tenantId, DateTime? asOfUtc = null)
    {
        var now = asOfUtc ?? DateTime.UtcNow;
        var entitlement = await GetActiveEntitlementAsync(tenantId);

        if (entitlement == null)
        {
            return new LicenseStatusInfo
            {
                Status = LicenseStatus.Unlicensed,
                IsSaleAllowed = false,
                TenantId = tenantId,
                DaysRemaining = 0,
                Message = $"No active license entitlement found for tenant '{tenantId}'."
            };
        }

        var graceExpiresAt = entitlement.ExpiresAtUtc.AddDays(entitlement.GracePeriodDays);

        if (now <= entitlement.ExpiresAtUtc)
        {
            var daysRemaining = Math.Max(0, (int)Math.Ceiling((entitlement.ExpiresAtUtc - now).TotalDays));
            return new LicenseStatusInfo
            {
                Status = LicenseStatus.Active,
                IsSaleAllowed = true,
                TenantId = tenantId,
                Plan = entitlement.Plan,
                ExpiresAtUtc = entitlement.ExpiresAtUtc,
                GracePeriodExpiresAtUtc = graceExpiresAt,
                DaysRemaining = daysRemaining,
                Message = $"License active. {daysRemaining} day(s) remaining.",
                ActiveEntitlement = entitlement
            };
        }

        if (now <= graceExpiresAt)
        {
            var graceDaysRemaining = Math.Max(0, (int)Math.Ceiling((graceExpiresAt - now).TotalDays));
            return new LicenseStatusInfo
            {
                Status = LicenseStatus.GracePeriod,
                IsSaleAllowed = true,
                TenantId = tenantId,
                Plan = entitlement.Plan,
                ExpiresAtUtc = entitlement.ExpiresAtUtc,
                GracePeriodExpiresAtUtc = graceExpiresAt,
                DaysRemaining = graceDaysRemaining,
                Message = $"Subscription expired on {entitlement.ExpiresAtUtc:yyyy-MM-dd}. Operating in offline grace period ({graceDaysRemaining} day(s) remaining).",
                ActiveEntitlement = entitlement
            };
        }

        return new LicenseStatusInfo
        {
            Status = LicenseStatus.ExpiredLockedOut,
            IsSaleAllowed = false,
            TenantId = tenantId,
            Plan = entitlement.Plan,
            ExpiresAtUtc = entitlement.ExpiresAtUtc,
            GracePeriodExpiresAtUtc = graceExpiresAt,
            DaysRemaining = 0,
            Message = $"Terminal lockout: Subscription expired on {entitlement.ExpiresAtUtc:yyyy-MM-dd} and {entitlement.GracePeriodDays}-day grace period ended on {graceExpiresAt:yyyy-MM-dd}.",
            ActiveEntitlement = entitlement
        };
    }

    public void ValidateSaleAllowed(string tenantId, DateTime? asOfUtc = null)
    {
        var now = asOfUtc ?? DateTime.UtcNow;
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT expires_at_utc, grace_period_days, plan
            FROM license_entitlements
            WHERE tenant_id = $tenantId
            ORDER BY revision DESC, applied_at_utc DESC
            LIMIT 1;
        ";
        cmd.Parameters.AddWithValue("$tenantId", tenantId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new LicenseLockoutException($"Terminal checkout locked: No license entitlement installed for tenant '{tenantId}'.");
        }

        var expiresAt = DateTime.Parse(reader.GetString(0), null, DateTimeStyles.AdjustToUniversal);
        var graceDays = reader.GetInt32(1);
        var graceExpiresAt = expiresAt.AddDays(graceDays);

        if (now > graceExpiresAt)
        {
            throw new LicenseLockoutException(
                $"License has expired and offline grace period has ended. Checkout is locked.");
        }
    }

    public bool HasFeature(string tenantId, string featureName)
    {
        if (string.IsNullOrWhiteSpace(featureName)) return false;
        var entitlement = GetActiveEntitlementAsync(tenantId).GetAwaiter().GetResult();
        return entitlement != null && entitlement.Features.Contains(featureName, StringComparer.OrdinalIgnoreCase);
    }

    public static string GenerateToken(
        string tenantId,
        string plan,
        DateTime issuedAtUtc,
        DateTime expiresAtUtc,
        int gracePeriodDays = 7,
        int maxDevices = 1,
        List<string>? features = null,
        int revision = 1,
        string? secretKey = null,
        string? entitlementId = null)
    {
        var dto = new EntitlementPayloadDto
        {
            EntitlementId = entitlementId ?? Guid.NewGuid().ToString(),
            TenantId = tenantId,
            Plan = plan,
            IssuedAt = issuedAtUtc.ToString("o"),
            ExpiresAt = expiresAtUtc.ToString("o"),
            GracePeriodDays = gracePeriodDays,
            MaxDevices = maxDevices,
            Features = features ?? new List<string> { "billing", "inventory" },
            Revision = revision
        };

        var json = JsonSerializer.Serialize(dto);
        var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var key = secretKey ?? DefaultSigningKey;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadBase64));
        var sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();

        return $"{payloadBase64}.{sigHex}";
    }

    private static LicenceEntitlement MapEntitlement(SqliteDataReader reader)
    {
        var featuresJson = reader.GetString(7);
        var features = string.IsNullOrWhiteSpace(featuresJson)
            ? new List<string>()
            : (JsonSerializer.Deserialize<List<string>>(featuresJson) ?? new List<string>());

        var sigHex = reader.GetString(9);
        var rawPayload = reader.GetString(10);

        return new LicenceEntitlement
        {
            EntitlementId = reader.GetString(0),
            TenantId = reader.GetString(1),
            Plan = reader.GetString(2),
            IssuedAtUtc = DateTime.Parse(reader.GetString(3), null, DateTimeStyles.AdjustToUniversal),
            ExpiresAtUtc = DateTime.Parse(reader.GetString(4), null, DateTimeStyles.AdjustToUniversal),
            GracePeriodDays = reader.GetInt32(5),
            MaxDevices = reader.GetInt32(6),
            Features = features,
            Revision = reader.GetInt32(8),
            SignatureHex = sigHex,
            RawPayloadBase64 = rawPayload,
            RawToken = $"{rawPayload}.{sigHex}",
            AppliedAtUtc = DateTime.Parse(reader.GetString(11), null, DateTimeStyles.AdjustToUniversal)
        };
    }
}
