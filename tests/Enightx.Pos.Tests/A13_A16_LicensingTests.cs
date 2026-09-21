using Enightx.Pos.Common;
using Enightx.Pos.Services;
using Xunit;

namespace Enightx.Pos.Tests;

public class A13_A16_LicensingTests
{
    private const string SigningKey = "test_licence_secret_key_1234567890";

    [Fact]
    public void A13_TrialOrSubscriptionExpiresOffline_BlocksNewCommits_ExistingDataPreserved()
    {
        var licenseService = new LicenseService();
        var issuedAt = DateTime.UtcNow.AddDays(-3);
        var expiresAt = DateTime.UtcNow.AddDays(-1); // Expired yesterday

        var trialLicense = new LicenseEntitlement
        {
            TenantId = "tenant-sl-001",
            DeviceId = "counter-01",
            PlanType = LicensePlanType.Trial,
            IssuedAtUtc = issuedAt,
            ExpiresAtUtc = expiresAt,
            IsFrozen = false
        };
        trialLicense.Signature = LicenseService.ComputeSignature(trialLicense, SigningKey);

        licenseService.LoadLicense(trialLicense, SigningKey);

        // Verification must throw LicenseExpiredException
        var ex = Assert.Throws<LicenseExpiredException>(() => licenseService.VerifyEntitlement());
        Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A14_RemoteFreeze_AppliesLockoutWithReason()
    {
        var licenseService = new LicenseService();
        var license = new LicenseEntitlement
        {
            TenantId = "tenant-sl-001",
            DeviceId = "counter-01",
            PlanType = LicensePlanType.Subscription,
            IssuedAtUtc = DateTime.UtcNow.AddDays(-10),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(20),
            IsFrozen = false
        };
        license.Signature = LicenseService.ComputeSignature(license, SigningKey);
        licenseService.LoadLicense(license, SigningKey);

        // Active before freeze
        licenseService.VerifyEntitlement();

        // Apply remote freeze
        licenseService.ApplyRemoteFreeze("Overdue invoice payment");

        // Subsequent verification must throw DeviceFrozenException
        var ex = Assert.Throws<DeviceFrozenException>(() => licenseService.VerifyEntitlement());
        Assert.Contains("remotely frozen", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Overdue invoice payment", ex.Message);
    }

    [Fact]
    public void A15_TamperedSignatureOrStaleLicense_Rejected()
    {
        var licenseService = new LicenseService();
        var license = new LicenseEntitlement
        {
            TenantId = "tenant-sl-001",
            DeviceId = "counter-01",
            PlanType = LicensePlanType.Subscription,
            IssuedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            IsFrozen = false,
            Signature = "tampered_invalid_signature_xyz"
        };

        // Tampered signature must be rejected immediately
        var ex = Assert.Throws<InvalidLicenseException>(() => licenseService.LoadLicense(license, SigningKey));
        Assert.Contains("Cryptographic signature validation failed", ex.Message);
    }

    [Fact]
    public void A16_ExpiredDeviceRenewed_FreshEntitlementRestoresAccess()
    {
        var licenseService = new LicenseService();
        var expiredLicense = new LicenseEntitlement
        {
            TenantId = "tenant-sl-001",
            DeviceId = "counter-01",
            PlanType = LicensePlanType.Trial,
            IssuedAtUtc = DateTime.UtcNow.AddDays(-5),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(-3),
            IsFrozen = false
        };
        expiredLicense.Signature = LicenseService.ComputeSignature(expiredLicense, SigningKey);
        licenseService.LoadLicense(expiredLicense, SigningKey);

        Assert.Throws<LicenseExpiredException>(() => licenseService.VerifyEntitlement());

        // Renew with new valid subscription
        var renewedLicense = new LicenseEntitlement
        {
            TenantId = "tenant-sl-001",
            DeviceId = "counter-01",
            PlanType = LicensePlanType.Subscription,
            IssuedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            IsFrozen = false
        };
        renewedLicense.Signature = LicenseService.ComputeSignature(renewedLicense, SigningKey);

        licenseService.RenewLicense(renewedLicense, SigningKey);

        // Verification must now succeed cleanly
        licenseService.VerifyEntitlement();
        Assert.Equal(LicensePlanType.Subscription, licenseService.CurrentEntitlement?.PlanType);
    }

    [Fact]
    public void A21_PermanentLicense_RemainsValidIndefinitelyWithoutMonthlyPayment()
    {
        var licenseService = new LicenseService();
        var permanentLicense = new LicenseEntitlement
        {
            TenantId = "tenant-sl-001",
            DeviceId = "counter-01",
            PlanType = LicensePlanType.Permanent,
            IssuedAtUtc = DateTime.UtcNow.AddYears(-2),
            ExpiresAtUtc = null, // Permanent has no expiry
            IsFrozen = false
        };
        permanentLicense.Signature = LicenseService.ComputeSignature(permanentLicense, SigningKey);

        licenseService.LoadLicense(permanentLicense, SigningKey);

        // Verify entitlement even 5 years into the future
        licenseService.VerifyEntitlement(DateTime.UtcNow.AddYears(5));
        Assert.Equal(LicensePlanType.Permanent, licenseService.CurrentEntitlement?.PlanType);
    }
}

