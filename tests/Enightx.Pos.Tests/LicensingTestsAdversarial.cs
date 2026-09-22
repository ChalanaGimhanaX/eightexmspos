using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

/// <summary>
/// Comprehensive Empirical Adversarial Stress Test Suite for Milestone 4:
/// Desktop Licensing & Lockout Guardrails.
///
/// Tests cover:
/// 1. Cryptographic Tampering:
///    - Bit flips in payload base64
///    - Field modifications (plan, expires_at, tenant_id, revision, max_devices, features)
///    - Signature tampering (bit flips, length corruption, non-hex, wrong HMAC key, empty sig)
///    - Format malformations (garbage tokens, multiple dots, missing JSON fields, inverted dates)
/// 2. Anti-Downgrade:
///    - Direct downgrade rejection (rev N < current)
///    - Multi-revision leap and cascade downgrade attack
///    - Post-downgrade legitimate upgrade acceptance
///    - Active entitlement preservation across rejected attempts
/// 3. Cross-Tenant Replay:
///    - Replaying token of Tenant A against Tenant B
///    - Isolated multi-tenant entitlements in same database
///    - Scoped checkout guardrail: Tenant A (active) succeeds, Tenant B (expired) locked out
/// 4. Grace Period Behavior:
///    - Exact boundary checks (T-1, T, T+1, T+grace, T+grace+1)
///    - Successful sale commits during grace period without lockout
///    - Grace period warning status evaluation
/// 5. Hard Lockout Guardrail:
///    - Strict lockout enforcement when expired beyond grace period
///    - Zero grace period immediate lockout
///    - Zero uncommitted state guarantee (stock, tenders, drawer untouched)
///    - Repeated checkout assault under lockout (concurrency and multi-attempt)
///    - Dynamic lockout recovery when higher-revision valid license applied
/// 6. Historical Read Preservation:
///    - All historical sale queries (by ID, receipt, recent, count) succeed 100% under lockout
///    - Reports (Daily, Shift, Inventory valuation) succeed 100% under lockout
///    - Customer ledger, balance, shift records succeed 100% under lockout
/// </summary>
public class LicensingTestsAdversarial : IDisposable
{
    private readonly PosDatabase _db;
    private readonly CatalogService _catalog;
    private readonly AuthService _auth;
    private readonly ShiftService _shift;
    private readonly CustomerService _customer;
    private readonly ReportService _report;

    private const string MasterSecretKey = "Enightx-Pos-Master-Licensing-Secret-LK-2026!#$";
    private const string CustomSecretKey = "Adversarial-Custom-Test-Secret-LK-999!";
    private const string TenantAlpha = "TENANT_ALPHA";
    private const string TenantBeta = "TENANT_BETA";
    private const string TenantGamma = "TENANT_GAMMA";
    private const string BranchCol = "BRANCH_COLOMBO_01";
    private const string CounterMain = "COUNTER_MAIN";

    public LicensingTestsAdversarial()
    {
        _db = PosDatabase.CreateInMemory();
        _catalog = new CatalogService(_db);
        _auth = new AuthService(_db);
        _shift = new ShiftService(_db);
        _customer = new CustomerService(_db, _shift);
        _report = new ReportService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private static string GenerateToken(
        string tenantId,
        string plan = "Standard",
        DateTime? issuedAtUtc = null,
        DateTime? expiresAtUtc = null,
        int gracePeriodDays = 7,
        int maxDevices = 1,
        List<string>? features = null,
        int revision = 1,
        string? secretKey = null,
        string? entitlementId = null)
    {
        var expires = expiresAtUtc ?? DateTime.UtcNow.AddDays(30);
        var issued = issuedAtUtc ?? expires.AddDays(-30);

        return LicenseService.GenerateToken(
            tenantId: tenantId,
            plan: plan,
            issuedAtUtc: issued,
            expiresAtUtc: expires,
            gracePeriodDays: gracePeriodDays,
            maxDevices: maxDevices,
            features: features,
            revision: revision,
            secretKey: secretKey ?? CustomSecretKey,
            entitlementId: entitlementId
        );
    }

    private async Task<(User Cashier, CashShift Shift, Product Product)> SetupStoreEnvironmentAsync(string tenantId = TenantAlpha, string? counterId = null)
    {
        var cashier = await _auth.CreateUserAsync($"cashier_{Guid.NewGuid():N}", "Cashier Sri Lanka", "pass123", Role.Cashier, "1234");
        var actualCounter = counterId ?? $"COUNTER_{tenantId}_{Guid.NewGuid().ToString("N")[..6]}";
        var shift = await _shift.OpenShiftAsync(BranchCol, actualCounter, cashier.UserId, 5000m, tenantId);
        var product = new Product
        {
            ProductId = $"PROD_{Guid.NewGuid():N}",
            Name = "Ceylon Black Tea 500g",
            Barcode = $"479{Guid.NewGuid().ToString("N")[..7]}",
            UnitPrice = 850.00m,
            CostBasis = 650.00m,
            StockOnHand = 500m,
            TaxRate = 0.08m,
            MinStockThreshold = 20m
        };
        await _catalog.AddProductAsync(product);
        return (cashier, shift, product);
    }

    // =========================================================================
    // 1. CRYPTOGRAPHIC TAMPERING ADVERSARIAL STRESS TESTS
    // =========================================================================

    [Fact]
    public async Task CryptoTamper_PayloadBitFlips_StrictlyRejected()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);
        var token = GenerateToken(TenantAlpha, plan: "Enterprise");
        var parts = token.Split('.');
        var payloadBase64 = parts[0];
        var signature = parts[1];

        // Flip character at beginning, middle, and end
        var tamperIndices = new[] { 0, payloadBase64.Length / 2, payloadBase64.Length - 1 };
        foreach (var idx in tamperIndices)
        {
            var chars = payloadBase64.ToCharArray();
            chars[idx] = chars[idx] == 'A' ? 'B' : 'A';
            var tamperedToken = $"{new string(chars)}.{signature}";

            await Assert.ThrowsAnyAsync<PosException>(() =>
                licenseService.ApplyEntitlementAsync(tamperedToken, TenantAlpha));
        }

        // Ensure database remains completely untouched
        Assert.Null(await licenseService.GetActiveEntitlementAsync(TenantAlpha));
    }

    [Fact]
    public async Task CryptoTamper_InnerPayloadFieldModifications_StrictlyRejected()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);
        var originalToken = GenerateToken(TenantAlpha, plan: "Standard", revision: 1);
        var parts = originalToken.Split('.');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));

        // Attack 1: Privilege escalation from "Standard" to "Enterprise"
        var tamperedPlanJson = json.Replace("\"Standard\"", "\"Enterprise\"");
        var tamperedPlanToken = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(tamperedPlanJson))}.{parts[1]}";
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync(tamperedPlanToken, TenantAlpha));

        // Attack 2: Expiry extension by 10 years
        var tamperedExpiryJson = json.Replace("2026", "2036");
        var tamperedExpiryToken = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(tamperedExpiryJson))}.{parts[1]}";
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync(tamperedExpiryToken, TenantAlpha));

        // Attack 3: Device limit boost
        var tamperedDevicesJson = json.Replace("\"max_devices\":1", "\"max_devices\":9999");
        var tamperedDevicesToken = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(tamperedDevicesJson))}.{parts[1]}";
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync(tamperedDevicesToken, TenantAlpha));

        // Attack 4: Revision inflation
        var tamperedRevJson = json.Replace("\"revision\":1", "\"revision\":100");
        var tamperedRevToken = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(tamperedRevJson))}.{parts[1]}";
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync(tamperedRevToken, TenantAlpha));
    }

    [Fact]
    public async Task CryptoTamper_SignatureMutations_StrictlyRejected()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);
        var validToken = GenerateToken(TenantAlpha, plan: "Pro");
        var parts = validToken.Split('.');
        var payload = parts[0];
        var sig = parts[1];

        // 1. Flip single hex character in signature
        var flippedSigChars = sig.ToCharArray();
        flippedSigChars[0] = flippedSigChars[0] == 'a' ? 'b' : 'a';
        await Assert.ThrowsAsync<PosException>(() =>
            licenseService.ApplyEntitlementAsync($"{payload}.{new string(flippedSigChars)}", TenantAlpha));

        // 2. Truncated signature
        await Assert.ThrowsAsync<PosException>(() =>
            licenseService.ApplyEntitlementAsync($"{payload}.{sig[..16]}", TenantAlpha));

        // 3. Extended signature
        await Assert.ThrowsAsync<PosException>(() =>
            licenseService.ApplyEntitlementAsync($"{payload}.{sig}{sig}", TenantAlpha));

        // 4. Non-hex characters
        await Assert.ThrowsAsync<PosException>(() =>
            licenseService.ApplyEntitlementAsync($"{payload}.ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ", TenantAlpha));

        // 5. Signed with wrong HMAC secret
        var forgedToken = GenerateToken(TenantAlpha, secretKey: "Attacker-Fake-Secret-Key-12345");
        await Assert.ThrowsAsync<PosException>(() =>
            licenseService.ApplyEntitlementAsync(forgedToken, TenantAlpha));
    }

    [Fact]
    public async Task CryptoTamper_StructuralMalformations_StrictlyRejected()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);

        // Null / whitespace
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync("", TenantAlpha));
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync("   ", TenantAlpha));

        // No dot delimiter
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync("RandomStringWithoutDelimiter", TenantAlpha));

        // Multiple dots
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync("part1.part2.part3", TenantAlpha));

        // Empty payload or empty signature
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync(".abc123", TenantAlpha));
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync("abc123.", TenantAlpha));

        // JSON format with missing fields
        var missingFieldsJson = "{\"payload\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")) + "\"}";
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync(missingFieldsJson, TenantAlpha));

        // Inverted dates: expires_at before issued_at (properly signed with genuine secret)
        var invertedToken = GenerateToken(
            tenantId: TenantAlpha,
            issuedAtUtc: DateTime.UtcNow.AddDays(10),
            expiresAtUtc: DateTime.UtcNow.AddDays(-10),
            secretKey: CustomSecretKey
        );
        var dateEx = await Assert.ThrowsAsync<PosException>(() =>
            licenseService.ApplyEntitlementAsync(invertedToken, TenantAlpha));
        Assert.Contains("expires_at cannot precede issued_at", dateEx.Message);
    }

    // =========================================================================
    // 2. ANTI-DOWNGRADE ADVERSARIAL STRESS TESTS
    // =========================================================================

    [Fact]
    public async Task AntiDowngrade_StrictMonotonicity_BlocksAllPreviousRevisions()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);

        // Step 1: Install base revision 1
        var rev1 = GenerateToken(TenantAlpha, plan: "Standard", revision: 1);
        await licenseService.ApplyEntitlementAsync(rev1, TenantAlpha);

        // Step 2: Upgrade to revision 5
        var rev5 = GenerateToken(TenantAlpha, plan: "Enterprise", revision: 5);
        await licenseService.ApplyEntitlementAsync(rev5, TenantAlpha);

        // Active entitlement must be revision 5
        var active = await licenseService.GetActiveEntitlementAsync(TenantAlpha);
        Assert.NotNull(active);
        Assert.Equal(5, active.Revision);
        Assert.Equal("Enterprise", active.Plan);

        // Step 3: Adversarial downgrade cascade (attempting rev 4, 3, 2, 1, 0, -1)
        var downgradeCandidates = new[] { 4, 3, 2, 1, 0, -1 };
        foreach (var rev in downgradeCandidates)
        {
            var token = GenerateToken(TenantAlpha, plan: "DowngradedPlan", revision: rev);
            var ex = await Assert.ThrowsAsync<PosException>(() =>
                licenseService.ApplyEntitlementAsync(token, TenantAlpha));
            Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Active entitlement must STILL be revision 5 with 0 corruption
        var activeAfterAttacks = await licenseService.GetActiveEntitlementAsync(TenantAlpha);
        Assert.NotNull(activeAfterAttacks);
        Assert.Equal(5, activeAfterAttacks.Revision);
        Assert.Equal("Enterprise", activeAfterAttacks.Plan);

        // Step 4: Legitimate upgrade to revision 6 must succeed
        var rev6 = GenerateToken(TenantAlpha, plan: "Ultimate", revision: 6);
        var appliedRev6 = await licenseService.ApplyEntitlementAsync(rev6, TenantAlpha);
        Assert.Equal(6, appliedRev6.Revision);

        var activeAfterRev6 = await licenseService.GetActiveEntitlementAsync(TenantAlpha);
        Assert.NotNull(activeAfterRev6);
        Assert.Equal(6, activeAfterRev6.Revision);
        Assert.Equal("Ultimate", activeAfterRev6.Plan);
    }

    [Fact]
    public async Task AntiDowngrade_DowngradeAttemptDoesNotPolluteDatabase()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);
        await licenseService.ApplyEntitlementAsync(GenerateToken(TenantAlpha, revision: 3), TenantAlpha);

        // Attempt downgrade to revision 2
        var rev2Token = GenerateToken(TenantAlpha, revision: 2);
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync(rev2Token, TenantAlpha));

        // Verify count of entitlements in SQLite table is exactly 1
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM license_entitlements WHERE tenant_id = $t;";
        cmd.Parameters.AddWithValue("$t", TenantAlpha);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    // =========================================================================
    // 3. CROSS-TENANT REPLAY ADVERSARIAL STRESS TESTS
    // =========================================================================

    [Fact]
    public async Task CrossTenant_TokenReplayAcrossTenants_StrictlyRejectedAndIsolated()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);

        // Generate genuine valid token for Tenant Alpha
        var alphaToken = GenerateToken(TenantAlpha, plan: "Enterprise", revision: 1);

        // Apply legitimately to Tenant Alpha
        await licenseService.ApplyEntitlementAsync(alphaToken, TenantAlpha);

        // Replay Attack 1: Attempt to apply Tenant Alpha token to Tenant Beta
        var exBeta = await Assert.ThrowsAsync<PosException>(() =>
            licenseService.ApplyEntitlementAsync(alphaToken, TenantBeta));
        Assert.Contains(TenantAlpha, exBeta.Message);
        Assert.Contains(TenantBeta, exBeta.Message);

        // Replay Attack 2: Attempt to apply Tenant Alpha token to Tenant Gamma
        var exGamma = await Assert.ThrowsAsync<PosException>(() =>
            licenseService.ApplyEntitlementAsync(alphaToken, TenantGamma));
        Assert.Contains(TenantAlpha, exGamma.Message);

        // Verify Tenant Beta and Gamma remain completely unlicensed
        Assert.Null(await licenseService.GetActiveEntitlementAsync(TenantBeta));
        Assert.Null(await licenseService.GetActiveEntitlementAsync(TenantGamma));

        // Status evaluation for Beta and Gamma must be Unlicensed and IsSaleAllowed = false
        var betaStatus = await licenseService.EvaluateLicenseStatusAsync(TenantBeta);
        Assert.Equal(LicenseStatus.Unlicensed, betaStatus.Status);
        Assert.False(betaStatus.IsSaleAllowed);

        // Status evaluation for Alpha must remain Active
        var alphaStatus = await licenseService.EvaluateLicenseStatusAsync(TenantAlpha);
        Assert.Equal(LicenseStatus.Active, alphaStatus.Status);
        Assert.True(alphaStatus.IsSaleAllowed);
    }

    [Fact]
    public async Task CrossTenant_MultiTenantDatabaseIsolation_MaintainsSeparateStates()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);

        // Tenant Alpha: Active Enterprise license (expires in 60 days)
        var alphaToken = GenerateToken(TenantAlpha, plan: "Enterprise", expiresAtUtc: DateTime.UtcNow.AddDays(60), revision: 3);
        await licenseService.ApplyEntitlementAsync(alphaToken, TenantAlpha);

        // Tenant Beta: Expired Standard license (expired 30 days ago, 7-day grace -> locked out)
        var betaToken = GenerateToken(TenantBeta, plan: "Standard", expiresAtUtc: DateTime.UtcNow.AddDays(-30), gracePeriodDays: 7, revision: 1);
        await licenseService.ApplyEntitlementAsync(betaToken, TenantBeta);

        // Validate individual active entitlements
        var activeAlpha = await licenseService.GetActiveEntitlementAsync(TenantAlpha);
        var activeBeta = await licenseService.GetActiveEntitlementAsync(TenantBeta);

        Assert.NotNull(activeAlpha);
        Assert.Equal("Enterprise", activeAlpha.Plan);
        Assert.Equal(3, activeAlpha.Revision);

        Assert.NotNull(activeBeta);
        Assert.Equal("Standard", activeBeta.Plan);
        Assert.Equal(1, activeBeta.Revision);

        // Validate checkout guardrail per tenant
        licenseService.ValidateSaleAllowed(TenantAlpha); // Should not throw

        var exLockout = Assert.Throws<LicenseLockoutException>(() =>
            licenseService.ValidateSaleAllowed(TenantBeta));
        Assert.Contains("Checkout is locked", exLockout.Message);

        // SaleService commit checkout scoping
        var saleService = new SaleService(_db, _catalog, licenseService);

        // Setup store environments
        var (cashierAlpha, shiftAlpha, prodAlpha) = await SetupStoreEnvironmentAsync(TenantAlpha);
        var (cashierBeta, shiftBeta, prodBeta) = await SetupStoreEnvironmentAsync(TenantBeta);

        // Tenant Alpha commits sale successfully
        var saleAlpha = await saleService.CommitSaleAsync(new CreateSaleCommand(
            TenantId: TenantAlpha,
            BranchId: BranchCol,
            CounterId: shiftAlpha.CounterId,
            CashierId: cashierAlpha.UserId,
            ShiftId: shiftAlpha.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prodAlpha.ProductId, 1m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1000m) }
        ));
        Assert.NotNull(saleAlpha);
        Assert.Equal(SaleStatus.Completed, saleAlpha.Status);

        // Tenant Beta checkout is strictly blocked
        await Assert.ThrowsAsync<LicenseLockoutException>(() => saleService.CommitSaleAsync(new CreateSaleCommand(
            TenantId: TenantBeta,
            BranchId: BranchCol,
            CounterId: shiftBeta.CounterId,
            CashierId: cashierBeta.UserId,
            ShiftId: shiftBeta.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prodBeta.ProductId, 1m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1000m) }
        )));
    }

    [Fact]
    public async Task CrossTenant_CaseInsensitiveTenantMatch_HandledProperly()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);
        var token = GenerateToken("tenant_lowercase_01");

        // Should succeed because StringComparison.OrdinalIgnoreCase is used
        var entitlement = await licenseService.ApplyEntitlementAsync(token, "TENANT_LOWERCASE_01");
        Assert.NotNull(entitlement);
    }

    // =========================================================================
    // 4. GRACE PERIOD BEHAVIOR ADVERSARIAL STRESS TESTS
    // =========================================================================

    [Fact]
    public async Task GracePeriod_ExactBoundaryTransitions_AccuratelyEvaluated()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);
        var expiry = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        const int graceDays = 7;
        var graceExpiry = expiry.AddDays(graceDays); // 2026-09-27 12:00:00 UTC

        var token = GenerateToken(TenantAlpha, expiresAtUtc: expiry, gracePeriodDays: graceDays);
        await licenseService.ApplyEntitlementAsync(token, TenantAlpha);

        // Boundary 1: 1 second before expiry -> Active, Sale Allowed
        var t1 = expiry.AddSeconds(-1);
        var s1 = await licenseService.EvaluateLicenseStatusAsync(TenantAlpha, asOfUtc: t1);
        Assert.Equal(LicenseStatus.Active, s1.Status);
        Assert.True(s1.IsSaleAllowed);
        licenseService.ValidateSaleAllowed(TenantAlpha, asOfUtc: t1);

        // Boundary 2: Exactly at expiry -> Active (now <= expiresAt), Sale Allowed
        var s2 = await licenseService.EvaluateLicenseStatusAsync(TenantAlpha, asOfUtc: expiry);
        Assert.Equal(LicenseStatus.Active, s2.Status);
        Assert.True(s2.IsSaleAllowed);
        licenseService.ValidateSaleAllowed(TenantAlpha, asOfUtc: expiry);

        // Boundary 3: 1 second after expiry -> GracePeriod, Sale Allowed
        var t3 = expiry.AddSeconds(1);
        var s3 = await licenseService.EvaluateLicenseStatusAsync(TenantAlpha, asOfUtc: t3);
        Assert.Equal(LicenseStatus.GracePeriod, s3.Status);
        Assert.True(s3.IsSaleAllowed);
        Assert.Contains("grace period", s3.Message, StringComparison.OrdinalIgnoreCase);
        licenseService.ValidateSaleAllowed(TenantAlpha, asOfUtc: t3);

        // Boundary 4: Mid-grace period (3.5 days after expiry) -> GracePeriod, Sale Allowed
        var t4 = expiry.AddDays(3.5);
        var s4 = await licenseService.EvaluateLicenseStatusAsync(TenantAlpha, asOfUtc: t4);
        Assert.Equal(LicenseStatus.GracePeriod, s4.Status);
        Assert.True(s4.IsSaleAllowed);
        licenseService.ValidateSaleAllowed(TenantAlpha, asOfUtc: t4);

        // Boundary 5: Exactly at grace period expiry -> GracePeriod (now <= graceExpiresAt), Sale Allowed
        var s5 = await licenseService.EvaluateLicenseStatusAsync(TenantAlpha, asOfUtc: graceExpiry);
        Assert.Equal(LicenseStatus.GracePeriod, s5.Status);
        Assert.True(s5.IsSaleAllowed);
        licenseService.ValidateSaleAllowed(TenantAlpha, asOfUtc: graceExpiry);

        // Boundary 6: 1 second past grace period expiry -> ExpiredLockedOut, Sale Disallowed
        var t6 = graceExpiry.AddSeconds(1);
        var s6 = await licenseService.EvaluateLicenseStatusAsync(TenantAlpha, asOfUtc: t6);
        Assert.Equal(LicenseStatus.ExpiredLockedOut, s6.Status);
        Assert.False(s6.IsSaleAllowed);
        Assert.Throws<LicenseLockoutException>(() => licenseService.ValidateSaleAllowed(TenantAlpha, asOfUtc: t6));
    }

    [Fact]
    public async Task GracePeriod_MultipleSaleCommitsDuringGracePeriod_SucceedSeamlessly()
    {
        // Expired 3 days ago, 7-day grace period (4 days remaining in grace period)
        var expiredAt = DateTime.UtcNow.AddDays(-3);
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);
        await licenseService.ApplyEntitlementAsync(GenerateToken(TenantAlpha, expiresAtUtc: expiredAt, gracePeriodDays: 7), TenantAlpha);

        var saleService = new SaleService(_db, _catalog, licenseService);
        var (cashier, shift, product) = await SetupStoreEnvironmentAsync(TenantAlpha);

        var initialStock = await _catalog.GetStockOnHandAsync(product.ProductId);

        // Commit 3 distinct sales during grace period
        for (int i = 1; i <= 3; i++)
        {
            var sale = await saleService.CommitSaleAsync(new CreateSaleCommand(
                TenantId: TenantAlpha,
                BranchId: BranchCol,
                CounterId: CounterMain,
                CashierId: cashier.UserId,
                ShiftId: shift.ShiftId,
                Items: new List<CreateSaleLineRequest> { new(product.ProductId, 2m) },
                Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 2000m) }
            ));

            Assert.NotNull(sale);
            Assert.Equal(SaleStatus.Completed, sale.Status);
            Assert.Equal(i, await saleService.GetSaleCountAsync());
        }

        // Verify stock was deducted by exactly 6 units (3 sales * 2 units)
        var currentStock = await _catalog.GetStockOnHandAsync(product.ProductId);
        Assert.Equal(initialStock - 6m, currentStock);
    }

    // =========================================================================
    // 5. HARD LOCKOUT GUARDRAIL ADVERSARIAL STRESS TESTS
    // =========================================================================

    [Fact]
    public async Task HardLockout_ImmediateLockoutWithZeroGracePeriod()
    {
        // Zero grace period token expired 1 minute ago
        var expiredAt = DateTime.UtcNow.AddMinutes(-1);
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);
        await licenseService.ApplyEntitlementAsync(GenerateToken(TenantAlpha, expiresAtUtc: expiredAt, gracePeriodDays: 0), TenantAlpha);

        var status = await licenseService.EvaluateLicenseStatusAsync(TenantAlpha);
        Assert.Equal(LicenseStatus.ExpiredLockedOut, status.Status);
        Assert.False(status.IsSaleAllowed);

        var saleService = new SaleService(_db, _catalog, licenseService);
        var (cashier, shift, product) = await SetupStoreEnvironmentAsync(TenantAlpha);

        await Assert.ThrowsAsync<LicenseLockoutException>(() => saleService.CommitSaleAsync(new CreateSaleCommand(
            TenantId: TenantAlpha,
            BranchId: BranchCol,
            CounterId: CounterMain,
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1000m) }
        )));
    }

    [Fact]
    public async Task HardLockout_ZeroUncommittedStateSideEffects_DatabaseRemainsPristine()
    {
        // Expired 20 days ago with 7-day grace period -> strictly locked out
        var expiredAt = DateTime.UtcNow.AddDays(-20);
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);
        await licenseService.ApplyEntitlementAsync(GenerateToken(TenantAlpha, expiresAtUtc: expiredAt, gracePeriodDays: 7), TenantAlpha);

        var saleService = new SaleService(_db, _catalog, licenseService);
        var (cashier, shift, product) = await SetupStoreEnvironmentAsync(TenantAlpha);

        var initialStock = await _catalog.GetStockOnHandAsync(product.ProductId);
        var initialSaleCount = await saleService.GetSaleCountAsync();

        // Check SQLite tables before attempt
        using (var conn = _db.CreateConnection())
        {
            using var cmdLines = conn.CreateCommand();
            cmdLines.CommandText = "SELECT COUNT(*) FROM sale_lines;";
            var initialLineCount = Convert.ToInt32(await cmdLines.ExecuteScalarAsync());

            using var cmdTenders = conn.CreateCommand();
            cmdTenders.CommandText = "SELECT COUNT(*) FROM tenders;";
            var initialTenderCount = Convert.ToInt32(await cmdTenders.ExecuteScalarAsync());

            using var cmdMovements = conn.CreateCommand();
            cmdMovements.CommandText = "SELECT COUNT(*) FROM stock_movements;";
            var initialMovementsCount = Convert.ToInt32(await cmdMovements.ExecuteScalarAsync());

            // Attempt checkout
            var ex = await Assert.ThrowsAsync<LicenseLockoutException>(() => saleService.CommitSaleAsync(new CreateSaleCommand(
                TenantId: TenantAlpha,
                BranchId: BranchCol,
                CounterId: CounterMain,
                CashierId: cashier.UserId,
                ShiftId: shift.ShiftId,
                Items: new List<CreateSaleLineRequest> { new(product.ProductId, 5m) },
                Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 5000m) }
            )));
            Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Re-verify all tables to ensure zero side-effects
            Assert.Equal(initialSaleCount, await saleService.GetSaleCountAsync());
            Assert.Equal(initialStock, await _catalog.GetStockOnHandAsync(product.ProductId));

            var finalLineCount = Convert.ToInt32(await cmdLines.ExecuteScalarAsync());
            var finalTenderCount = Convert.ToInt32(await cmdTenders.ExecuteScalarAsync());
            var finalMovementsCount = Convert.ToInt32(await cmdMovements.ExecuteScalarAsync());

            Assert.Equal(initialLineCount, finalLineCount);
            Assert.Equal(initialTenderCount, finalTenderCount);
            Assert.Equal(initialMovementsCount, finalMovementsCount);
        }
    }

    [Fact]
    public async Task HardLockout_RepeatedCheckoutAssault_FailsDeterministicallyWithoutDegradation()
    {
        var expiredAt = DateTime.UtcNow.AddDays(-10);
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);
        await licenseService.ApplyEntitlementAsync(GenerateToken(TenantAlpha, expiresAtUtc: expiredAt, gracePeriodDays: 3), TenantAlpha);

        var saleService = new SaleService(_db, _catalog, licenseService);
        var (cashier, shift, product) = await SetupStoreEnvironmentAsync(TenantAlpha);

        // Execute 25 consecutive checkout calls
        for (int i = 0; i < 25; i++)
        {
            await Assert.ThrowsAsync<LicenseLockoutException>(() => saleService.CommitSaleAsync(new CreateSaleCommand(
                TenantId: TenantAlpha,
                BranchId: BranchCol,
                CounterId: CounterMain,
                CashierId: cashier.UserId,
                ShiftId: shift.ShiftId,
                Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1m) },
                Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1000m) }
            )));
        }

        Assert.Equal(0, await saleService.GetSaleCountAsync());
    }

    [Fact]
    public async Task HardLockout_DynamicRecovery_HigherRevisionClearsLockoutImmediately()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);

        // Step 1: Place in hard lockout with Rev 1
        var expiredRev1 = GenerateToken(TenantAlpha, expiresAtUtc: DateTime.UtcNow.AddDays(-15), gracePeriodDays: 7, revision: 1);
        await licenseService.ApplyEntitlementAsync(expiredRev1, TenantAlpha);

        var saleService = new SaleService(_db, _catalog, licenseService);
        var (cashier, shift, product) = await SetupStoreEnvironmentAsync(TenantAlpha);

        // Verify locked out
        await Assert.ThrowsAsync<LicenseLockoutException>(() => saleService.CommitSaleAsync(new CreateSaleCommand(
            TenantId: TenantAlpha,
            BranchId: BranchCol,
            CounterId: CounterMain,
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1000m) }
        )));

        // Step 2: Customer renews subscription and receives Rev 2 with active expiry
        var renewedRev2 = GenerateToken(TenantAlpha, expiresAtUtc: DateTime.UtcNow.AddDays(365), gracePeriodDays: 14, revision: 2);
        var applied = await licenseService.ApplyEntitlementAsync(renewedRev2, TenantAlpha);
        Assert.Equal(2, applied.Revision);

        // Status becomes Active immediately
        var status = await licenseService.EvaluateLicenseStatusAsync(TenantAlpha);
        Assert.Equal(LicenseStatus.Active, status.Status);
        Assert.True(status.IsSaleAllowed);

        // Step 3: Checkout now commits with 100% success
        var sale = await saleService.CommitSaleAsync(new CreateSaleCommand(
            TenantId: TenantAlpha,
            BranchId: BranchCol,
            CounterId: CounterMain,
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 3m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 3000m) }
        ));

        Assert.NotNull(sale);
        Assert.Equal(SaleStatus.Completed, sale.Status);
        Assert.Equal(1, await saleService.GetSaleCountAsync());
    }

    // =========================================================================
    // 6. HISTORICAL READ PRESERVATION UNDER LOCKOUT ADVERSARIAL STRESS TESTS
    // =========================================================================

    [Fact]
    public async Task HistoricalReads_UnderSevereHardLockout_AllQueriesRemain100PercentAccessible()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);

        // 1. Arrange: Start with active license
        await licenseService.ApplyEntitlementAsync(GenerateToken(TenantAlpha, expiresAtUtc: DateTime.UtcNow.AddDays(15), revision: 1), TenantAlpha);
        var saleService = new SaleService(_db, _catalog, licenseService);

        var (cashier, shift, product) = await SetupStoreEnvironmentAsync(TenantAlpha);

        // Add a second product
        var product2 = new Product
        {
            ProductId = $"PROD_SUGAR_{Guid.NewGuid():N}",
            Name = "White Sugar 1kg",
            Barcode = "4790002001",
            UnitPrice = 240.00m,
            CostBasis = 190.00m,
            StockOnHand = 200m,
            TaxRate = 0.00m,
            MinStockThreshold = 15m
        };
        await _catalog.AddProductAsync(product2);

        // Create customer with account credit
        var customer = await _customer.CreateCustomerAsync("Sunil Wickramasinghe", "0719876543", "Kandy, Sri Lanka", 25000m, cashier.UserId);

        // Commit multiple historical sales with various lines and payment tenders
        var historicalSales = new List<Sale>();
        for (int i = 1; i <= 4; i++)
        {
            var committed = await saleService.CommitSaleAsync(new CreateSaleCommand(
                TenantId: TenantAlpha,
                BranchId: BranchCol,
                CounterId: CounterMain,
                CashierId: cashier.UserId,
                ShiftId: shift.ShiftId,
                Items: new List<CreateSaleLineRequest>
                {
                    new(product.ProductId, Quantity: i),
                    new(product2.ProductId, Quantity: 2m)
                },
                Tenders: new List<CreateTenderRequest>
                {
                    new(TenderType.CASH, AmountTendered: 5000m)
                },
                CustomerId: customer.CustomerId
            ));
            historicalSales.Add(committed);
        }

        // 2. Act: Trigger severe hard lockout via expired Rev 2
        var expiredToken = GenerateToken(TenantAlpha, expiresAtUtc: DateTime.UtcNow.AddDays(-60), gracePeriodDays: 7, revision: 2);
        await licenseService.ApplyEntitlementAsync(expiredToken, TenantAlpha);

        // Verify that checkout is strictly blocked
        await Assert.ThrowsAsync<LicenseLockoutException>(() => saleService.CommitSaleAsync(new CreateSaleCommand(
            TenantId: TenantAlpha,
            BranchId: BranchCol,
            CounterId: CounterMain,
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1000m) }
        )));

        // 3. Assert: Historical Read Preservation
        // A. GetSaleByIdAsync for all historical sales
        foreach (var orig in historicalSales)
        {
            var retrieved = await saleService.GetSaleByIdAsync(orig.SaleId);
            Assert.NotNull(retrieved);
            Assert.Equal(orig.SaleId, retrieved.SaleId);
            Assert.Equal(orig.ReceiptNumber, retrieved.ReceiptNumber);
            Assert.Equal(orig.GrandTotal, retrieved.GrandTotal);
            Assert.Equal(2, retrieved.Lines.Count);
            Assert.Single(retrieved.Tenders);
        }

        // B. GetSaleByReceiptNumberAsync
        foreach (var orig in historicalSales)
        {
            var byReceipt = await saleService.GetSaleByReceiptNumberAsync(orig.ReceiptNumber);
            Assert.NotNull(byReceipt);
            Assert.Equal(orig.SaleId, byReceipt.SaleId);
        }

        // C. GetSaleCountAsync
        var count = await saleService.GetSaleCountAsync();
        Assert.Equal(4, count);

        // D. GetRecentSalesAsync
        var recent = await saleService.GetRecentSalesAsync(50, BranchCol);
        Assert.Equal(4, recent.Count);

        // E. Daily and Shift Reporting
        var dailyReport = await _report.GenerateDailyReportAsync(DateTime.UtcNow);
        Assert.NotNull(dailyReport);
        Assert.True(dailyReport.CompletedSalesCount >= 4);

        var shiftReport = await _report.GenerateShiftReportAsync(shift.ShiftId);
        Assert.NotNull(shiftReport);
        Assert.Equal(4, shiftReport.TotalSalesCount);

        // F. Inventory Valuation Report
        var invReport = await _report.GenerateInventorySummaryReportAsync();
        Assert.NotNull(invReport);
        Assert.True(invReport.TotalProducts > 0);

        // G. Customer ledger and balance queries
        var ledger = await _customer.GetCustomerLedgerAsync(customer.CustomerId);
        Assert.NotNull(ledger);
        var balance = await _customer.GetCustomerBalanceAsync(customer.CustomerId);
        Assert.Equal(0m, balance);

        // H. Shift inspection queries
        var currentShift = await _shift.GetShiftByIdAsync(shift.ShiftId);
        Assert.NotNull(currentShift);
        var branchShifts = await _shift.GetShiftsAsync(BranchCol);
        Assert.NotEmpty(branchShifts);

        // I. Product catalog read queries
        var prod1 = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.NotNull(prod1);
        var stock1 = await _catalog.GetStockOnHandAsync(product.ProductId);
        Assert.True(stock1 > 0);
        var activeProducts = await _catalog.GetAllActiveProductsAsync();
        Assert.NotEmpty(activeProducts);
    }

    [Fact]
    public async Task HasFeature_GracefullyHandlesUnknownAndUnlicensedTenants()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);

        // Unlicensed tenant
        Assert.False(licenseService.HasFeature("UNLICENSED_TENANT_XYZ", "billing"));
        Assert.False(licenseService.HasFeature("UNLICENSED_TENANT_XYZ", ""));

        // Licensed tenant with specific features
        var token = GenerateToken(TenantAlpha, features: new List<string> { "reports", "transfers", "stock_count" });
        await licenseService.ApplyEntitlementAsync(token, TenantAlpha);

        Assert.True(licenseService.HasFeature(TenantAlpha, "reports"));
        Assert.True(licenseService.HasFeature(TenantAlpha, "transfers"));
        Assert.True(licenseService.HasFeature(TenantAlpha, "stock_count"));
        Assert.True(licenseService.HasFeature(TenantAlpha, "REPORTS")); // case-insensitive
        Assert.False(licenseService.HasFeature(TenantAlpha, "cloud_ai"));
    }

    // =========================================================================
    // 7. ADDITIONAL DEEP ADVERSARIAL STRESS TESTS
    // =========================================================================

    [Fact]
    public async Task Concurrency_MultiThreadedLockoutAssault_AllRejectDeterministically()
    {
        var expiredAt = DateTime.UtcNow.AddDays(-20);
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);
        await licenseService.ApplyEntitlementAsync(GenerateToken(TenantAlpha, expiresAtUtc: expiredAt, gracePeriodDays: 3), TenantAlpha);

        var saleService = new SaleService(_db, _catalog, licenseService);
        var (cashier, shift, product) = await SetupStoreEnvironmentAsync(TenantAlpha);

        const int concurrentRequests = 20;
        var tasks = new Task[concurrentRequests];

        for (int i = 0; i < concurrentRequests; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await Assert.ThrowsAsync<LicenseLockoutException>(() => saleService.CommitSaleAsync(new CreateSaleCommand(
                    TenantId: TenantAlpha,
                    BranchId: BranchCol,
                    CounterId: shift.CounterId,
                    CashierId: cashier.UserId,
                    ShiftId: shift.ShiftId,
                    Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1m) },
                    Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1000m) }
                )));
            });
        }

        await Task.WhenAll(tasks);

        // Verify zero sales recorded
        Assert.Equal(0, await saleService.GetSaleCountAsync());
    }

    [Fact]
    public async Task PreCommitLockout_PrecedesCommandValidation_FailsAtStepZero()
    {
        var expiredAt = DateTime.UtcNow.AddDays(-15);
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);
        await licenseService.ApplyEntitlementAsync(GenerateToken(TenantAlpha, expiresAtUtc: expiredAt, gracePeriodDays: 5), TenantAlpha);

        var saleService = new SaleService(_db, _catalog, licenseService);

        // Command with invalid items (empty) and invalid tenders (empty)
        var invalidCommand = new CreateSaleCommand(
            TenantId: TenantAlpha,
            BranchId: BranchCol,
            CounterId: "C1",
            CashierId: "U1",
            ShiftId: Guid.NewGuid(),
            Items: new List<CreateSaleLineRequest>(),
            Tenders: new List<CreateTenderRequest>()
        );

        // Must throw LicenseLockoutException BEFORE throwing PosException for empty items/tenders
        var ex = await Assert.ThrowsAsync<LicenseLockoutException>(() =>
            saleService.CommitSaleAsync(invalidCommand));
        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DateHandling_SriLankaTimeZoneOffset_CorrectlyNormalizedToUtc()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);

        // Issued at 2026-09-01 10:00:00 +05:30 (Sri Lanka time)
        // Expires at 2026-10-01 10:00:00 +05:30
        var issuedDto = "2026-09-01T10:00:00+05:30";
        var expiresDto = "2026-10-01T10:00:00+05:30";

        var dto = new EntitlementPayloadDto
        {
            EntitlementId = Guid.NewGuid().ToString(),
            TenantId = TenantAlpha,
            Plan = "Enterprise",
            IssuedAt = issuedDto,
            ExpiresAt = expiresDto,
            GracePeriodDays = 7,
            MaxDevices = 5,
            Features = new List<string> { "billing" },
            Revision = 1
        };

        var json = JsonSerializer.Serialize(dto);
        var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(CustomSecretKey));
        var sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadBase64));
        var sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();
        var token = $"{payloadBase64}.{sigHex}";

        var entitlement = await licenseService.ApplyEntitlementAsync(token, TenantAlpha);

        // Expected UTC: 10:00 - 05:30 = 04:30 UTC
        Assert.Equal(DateTimeKind.Utc, entitlement.IssuedAtUtc.Kind);
        Assert.Equal(4, entitlement.IssuedAtUtc.Hour);
        Assert.Equal(30, entitlement.IssuedAtUtc.Minute);

        Assert.Equal(DateTimeKind.Utc, entitlement.ExpiresAtUtc.Kind);
        Assert.Equal(4, entitlement.ExpiresAtUtc.Hour);
        Assert.Equal(30, entitlement.ExpiresAtUtc.Minute);
    }

    [Fact]
    public async Task NegativeGracePeriodAndZeroDevices_SafelyClamped()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);

        var dto = new EntitlementPayloadDto
        {
            EntitlementId = Guid.NewGuid().ToString(),
            TenantId = TenantAlpha,
            Plan = "Starter",
            IssuedAt = DateTime.UtcNow.AddDays(-1).ToString("o"),
            ExpiresAt = DateTime.UtcNow.AddDays(30).ToString("o"),
            GracePeriodDays = -10, // Negative grace period
            MaxDevices = 0,        // Zero devices
            Features = new List<string> { "billing" },
            Revision = 1
        };

        var json = JsonSerializer.Serialize(dto);
        var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(CustomSecretKey));
        var sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadBase64));
        var token = $"{payloadBase64}.{Convert.ToHexString(sigBytes).ToLowerInvariant()}";

        var entitlement = await licenseService.ApplyEntitlementAsync(token, TenantAlpha);

        // Clamped safely
        Assert.Equal(0, entitlement.GracePeriodDays);
        Assert.Equal(1, entitlement.MaxDevices);
    }

    [Fact]
    public async Task AuditLogging_EntitlementAppliedLoggedOnlyOnSuccess()
    {
        var licenseService = new LicenseService(_db, secretKey: CustomSecretKey);

        // Step 1: Failed tamper attempt
        var forgedToken = GenerateToken(TenantAlpha, secretKey: "FakeSecret");
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync(forgedToken, TenantAlpha));

        // Step 2: Failed cross-tenant attempt
        var crossTenantToken = GenerateToken(TenantBeta);
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync(crossTenantToken, TenantAlpha));

        // Step 3: Legitimate application
        var validToken = GenerateToken(TenantAlpha, plan: "Pro", revision: 1);
        var entitlement = await licenseService.ApplyEntitlementAsync(validToken, TenantAlpha);

        // Step 4: Failed downgrade attempt
        var downgradeToken = GenerateToken(TenantAlpha, plan: "Pro", revision: 0);
        await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync(downgradeToken, TenantAlpha));

        // Verify audit_events table contains EXACTLY ONE event (the successful one)
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT details_json, occurred_at_utc FROM audit_events WHERE action = 'ENTITLEMENT_APPLIED';";
        using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        var detailsJson = reader.GetString(0);
        Assert.Contains(entitlement.EntitlementId, detailsJson);
        Assert.Contains("Pro", detailsJson);

        // No more rows!
        Assert.False(await reader.ReadAsync());
    }
}
