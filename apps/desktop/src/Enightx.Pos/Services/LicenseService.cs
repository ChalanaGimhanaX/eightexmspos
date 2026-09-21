using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Enightx.Pos.Common;

namespace Enightx.Pos.Services;

public enum LicensePlanType
{
    Trial = 1,
    Subscription = 2,
    Permanent = 3
}

public class LicenseEntitlement
{
    public required string TenantId { get; set; }
    public required string DeviceId { get; set; }
    public LicensePlanType PlanType { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsFrozen { get; set; }
    public string? FreezeReason { get; set; }
    public string? Signature { get; set; }
}

public interface ILicenseService
{
    LicenseEntitlement? CurrentEntitlement { get; }
    void LoadLicense(LicenseEntitlement entitlement, string signingKey);
    void VerifyEntitlement(DateTime? currentUtc = null);
    void ApplyRemoteFreeze(string reason);
    void RenewLicense(LicenseEntitlement newEntitlement, string signingKey);
}

public class LicenseService : ILicenseService
{
    private LicenseEntitlement? _currentEntitlement;
    private string? _activeSigningKey;

    public LicenseEntitlement? CurrentEntitlement => _currentEntitlement;

    public static string ComputeSignature(LicenseEntitlement entitlement, string signingKey)
    {
        var rawPayload = $"{entitlement.TenantId}:{entitlement.DeviceId}:{entitlement.PlanType}:" +
                         $"{(entitlement.ExpiresAtUtc.HasValue ? entitlement.ExpiresAtUtc.Value.ToString("o") : "PERMANENT")}";
        
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawPayload));
        return Convert.ToBase64String(hash);
    }

    public void LoadLicense(LicenseEntitlement entitlement, string signingKey)
    {
        if (string.IsNullOrWhiteSpace(signingKey))
            throw new ArgumentException("Signing key cannot be empty.", nameof(signingKey));

        var expectedSig = ComputeSignature(entitlement, signingKey);
        if (entitlement.Signature != expectedSig)
        {
            throw new InvalidLicenseException("Cryptographic signature validation failed. License is invalid or has been tampered with.");
        }

        _currentEntitlement = entitlement;
        _activeSigningKey = signingKey;
    }

    public void VerifyEntitlement(DateTime? currentUtc = null)
    {
        if (_currentEntitlement == null)
        {
            throw new InvalidLicenseException("No active license entitlement is loaded on this counter.");
        }

        if (_currentEntitlement.IsFrozen)
        {
            throw new DeviceFrozenException($"This terminal has been remotely frozen by the provider. Reason: {_currentEntitlement.FreezeReason ?? "Administrative lockout"}.");
        }

        if (_currentEntitlement.PlanType != LicensePlanType.Permanent)
        {
            var checkTime = currentUtc ?? DateTime.UtcNow;
            if (_currentEntitlement.ExpiresAtUtc.HasValue && checkTime > _currentEntitlement.ExpiresAtUtc.Value)
            {
                throw new LicenseExpiredException(
                    $"License entitlement ({_currentEntitlement.PlanType}) expired on {_currentEntitlement.ExpiresAtUtc.Value:yyyy-MM-dd HH:mm:ss} UTC. " +
                    "Business commits are locked until renewed."
                );
            }
        }
    }

    public void ApplyRemoteFreeze(string reason)
    {
        if (_currentEntitlement == null)
            throw new InvalidLicenseException("Cannot freeze a device without an active entitlement.");

        _currentEntitlement.IsFrozen = true;
        _currentEntitlement.FreezeReason = reason;
    }

    public void RenewLicense(LicenseEntitlement newEntitlement, string signingKey)
    {
        LoadLicense(newEntitlement, signingKey);
    }
}

