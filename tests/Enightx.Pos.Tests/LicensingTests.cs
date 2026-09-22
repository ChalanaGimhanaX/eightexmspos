using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class LicensingTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly CatalogService _catalog;
    private readonly AuthService _auth;
    private readonly ShiftService _shift;
    private readonly CustomerService _customer;
    private readonly ReportService _report;
    private const string SecretKey = "Enightx-Pos-Licensing-Secret-Key-2026-Secure";
    private const string TenantId = "TENANT_LK_01";
    private const string BranchId = "B001";
    private const string CounterId = "C01";

    public LicensingTests()
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

    private static string CreateToken(
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
            secretKey: secretKey ?? SecretKey,
            entitlementId: entitlementId
        );
    }

    private async Task<(User Cashier, CashShift Shift, Product Product)> SetupStoreEnvironmentAsync()
    {
        var cashier = await _auth.CreateUserAsync("cashier1", "Cashier 01", "pass123", Role.Cashier, "1234");
        var shift = await _shift.OpenShiftAsync(BranchId, CounterId, cashier.UserId, 5000m, TenantId);
        var product = new Product
        {
            ProductId = "PROD-001",
            Name = "Keells Samba Rice 5kg",
            Barcode = "4790001001",
            UnitPrice = 1250.00m,
            CostBasis = 1050.00m,
            StockOnHand = 100m,
            TaxRate = 0.08m,
            MinStockThreshold = 10m
        };
        await _catalog.AddProductAsync(product);
        return (cashier, shift, product);
    }

    // -------------------------------------------------------------
    // Test 1: Valid Signed Token Activation & Audit Logging
    // -------------------------------------------------------------
    [Fact]
    public async Task ApplyEntitlement_ValidSignedToken_ActivatesSuccessfullyAndLogsAudit()
    {
        var licenseService = new LicenseService(_db, secretKey: SecretKey);
        var token = CreateToken(TenantId, plan: "Pro", expiresAtUtc: DateTime.UtcNow.AddDays(45));

        var entitlement = await licenseService.ApplyEntitlementAsync(token, TenantId);

        Assert.NotNull(entitlement);
        Assert.Equal(TenantId, entitlement.TenantId);
        Assert.Equal("Pro", entitlement.Plan);
        Assert.True(entitlement.ExpiresAtUtc > DateTime.UtcNow);
        Assert.Equal(1, entitlement.Revision);

        var active = await licenseService.GetActiveEntitlementAsync(TenantId);
        Assert.NotNull(active);
        Assert.Equal("Pro", active.Plan);
        Assert.Equal(TenantId, active.TenantId);

        var status = await licenseService.EvaluateLicenseStatusAsync(TenantId);
        Assert.Equal(LicenseStatus.Active, status.Status);
        Assert.True(status.IsSaleAllowed);
        Assert.Contains("License active", status.Message);

        // Verify audit event ENTITLEMENT_APPLIED is recorded
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'ENTITLEMENT_APPLIED' AND tenant_id = $tenantId;";
        cmd.Parameters.AddWithValue("$tenantId", TenantId);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.True(count >= 1);
    }

    // -------------------------------------------------------------
    // Test 2: Forged Signature Rejection
    // -------------------------------------------------------------
    [Fact]
    public async Task ApplyEntitlement_ForgedSignature_ThrowsAndRejects()
    {
        var licenseService = new LicenseService(_db, secretKey: SecretKey);
        // Signed with an unauthorized key
        var forgedToken = CreateToken(TenantId, secretKey: "Attacker-Unauthorized-Key");

        await Assert.ThrowsAnyAsync<PosException>(() => licenseService.ApplyEntitlementAsync(forgedToken, TenantId));

        var active = await licenseService.GetActiveEntitlementAsync(TenantId);
        Assert.Null(active);
    }

    // -------------------------------------------------------------
    // Test 3: Tampered Payload Rejection
    // -------------------------------------------------------------
    [Fact]
    public async Task ApplyEntitlement_TamperedPayload_ThrowsSignatureVerificationFailure()
    {
        var licenseService = new LicenseService(_db, secretKey: SecretKey);
        var validToken = CreateToken(TenantId, expiresAtUtc: DateTime.UtcNow.AddDays(10));

        var parts = validToken.Split('.');
        var rawJson = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
        var tamperedJson = rawJson.Replace("10", "365"); // attempt expiry extension
        var tamperedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(tamperedJson));
        var tamperedToken = $"{tamperedPayload}.{parts[1]}";

        await Assert.ThrowsAnyAsync<PosException>(() => licenseService.ApplyEntitlementAsync(tamperedToken, TenantId));
    }

    // -------------------------------------------------------------
    // Test 4: Cross-Tenant Token Replay Rejection
    // -------------------------------------------------------------
    [Fact]
    public async Task ApplyEntitlement_CrossTenantToken_ThrowsPosException()
    {
        var licenseService = new LicenseService(_db, secretKey: SecretKey);
        var tokenForOtherTenant = CreateToken("TENANT_OTHER");

        var ex = await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync(tokenForOtherTenant, TenantId));
        Assert.Contains("TENANT_OTHER", ex.Message);
    }

    // -------------------------------------------------------------
    // Test 5: Anti-Downgrade Check
    // -------------------------------------------------------------
    [Fact]
    public async Task ApplyEntitlement_AntiDowngrade_RejectsOlderRevision()
    {
        var licenseService = new LicenseService(_db, secretKey: SecretKey);
        var tokenRev2 = CreateToken(TenantId, plan: "Enterprise", revision: 2);
        await licenseService.ApplyEntitlementAsync(tokenRev2, TenantId);

        var tokenRev1 = CreateToken(TenantId, plan: "Standard", revision: 1);
        var ex = await Assert.ThrowsAsync<PosException>(() => licenseService.ApplyEntitlementAsync(tokenRev1, TenantId));
        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);

        var active = await licenseService.GetActiveEntitlementAsync(TenantId);
        Assert.NotNull(active);
        Assert.Equal(2, active.Revision);
        Assert.Equal("Enterprise", active.Plan);
    }

    // -------------------------------------------------------------
    // Test 6: Active License Allows Checkout
    // -------------------------------------------------------------
    [Fact]
    public async Task CommitSale_DuringActivePeriod_AllowsCheckout()
    {
        var licenseService = new LicenseService(_db, secretKey: SecretKey);
        await licenseService.ApplyEntitlementAsync(CreateToken(TenantId, expiresAtUtc: DateTime.UtcNow.AddDays(30)), TenantId);
        var saleService = new SaleService(_db, _catalog, licenseService);

        var (cashier, shift, product) = await SetupStoreEnvironmentAsync();
        var command = new CreateSaleCommand(
            TenantId: TenantId,
            BranchId: BranchId,
            CounterId: CounterId,
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(product.ProductId, Quantity: 2m)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType.CASH, AmountTendered: 3000.00m)
            }
        );

        var sale = await saleService.CommitSaleAsync(command);

        Assert.NotNull(sale);
        Assert.Equal(SaleStatus.Completed, sale.Status);
        Assert.Equal(1, await saleService.GetSaleCountAsync());

        var stock = await _catalog.GetStockOnHandAsync(product.ProductId);
        Assert.Equal(98m, stock);
    }

    // -------------------------------------------------------------
    // Test 7: Grace Period Allows Checkout with Warning Status
    // -------------------------------------------------------------
    [Fact]
    public async Task CommitSale_DuringGracePeriod_AllowsCheckoutWithWarning()
    {
        // Expired 2 days ago, but 7-day grace period means 5 days remain
        var expiredAt = DateTime.UtcNow.AddDays(-2);
        var licenseService = new LicenseService(_db, secretKey: SecretKey);
        await licenseService.ApplyEntitlementAsync(CreateToken(TenantId, expiresAtUtc: expiredAt, gracePeriodDays: 7), TenantId);

        var status = await licenseService.EvaluateLicenseStatusAsync(TenantId);
        Assert.Equal(LicenseStatus.GracePeriod, status.Status);
        Assert.True(status.IsSaleAllowed);
        Assert.Contains("grace period", status.Message, StringComparison.OrdinalIgnoreCase);

        var saleService = new SaleService(_db, _catalog, licenseService);
        var (cashier, shift, product) = await SetupStoreEnvironmentAsync();
        var command = new CreateSaleCommand(
            TenantId: TenantId,
            BranchId: BranchId,
            CounterId: CounterId,
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 2000.00m) }
        );

        var sale = await saleService.CommitSaleAsync(command);
        Assert.NotNull(sale);
        Assert.Equal(SaleStatus.Completed, sale.Status);
    }

    // -------------------------------------------------------------
    // Test 8: Expired Beyond Grace Period Throws LicenseLockoutException
    // -------------------------------------------------------------
    [Fact]
    public async Task CommitSale_ExpiredBeyondGracePeriod_ThrowsLicenseLockoutExceptionAndPreservesState()
    {
        // Expired 15 days ago with 7 days grace period -> lockout active
        var expiredAt = DateTime.UtcNow.AddDays(-15);
        var licenseService = new LicenseService(_db, secretKey: SecretKey);
        await licenseService.ApplyEntitlementAsync(CreateToken(TenantId, expiresAtUtc: expiredAt, gracePeriodDays: 7), TenantId);

        var status = await licenseService.EvaluateLicenseStatusAsync(TenantId);
        Assert.Equal(LicenseStatus.ExpiredLockedOut, status.Status);
        Assert.False(status.IsSaleAllowed);

        var saleService = new SaleService(_db, _catalog, licenseService);
        var (cashier, shift, product) = await SetupStoreEnvironmentAsync();
        var initialStock = await _catalog.GetStockOnHandAsync(product.ProductId);

        var command = new CreateSaleCommand(
            TenantId: TenantId,
            BranchId: BranchId,
            CounterId: CounterId,
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 2m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 3000.00m) }
        );

        var ex = await Assert.ThrowsAsync<LicenseLockoutException>(() => saleService.CommitSaleAsync(command));
        Assert.Contains("Checkout is locked", ex.Message);

        // Verify database state is untouched
        Assert.Equal(0, await saleService.GetSaleCountAsync());
        Assert.Equal(initialStock, await _catalog.GetStockOnHandAsync(product.ProductId));
    }

    // -------------------------------------------------------------
    // Test 9: Unlicensed Tenant Throws LicenseLockoutException
    // -------------------------------------------------------------
    [Fact]
    public async Task CommitSale_UnlicensedTenant_ThrowsLicenseLockoutException()
    {
        var licenseService = new LicenseService(_db, secretKey: SecretKey);
        var saleService = new SaleService(_db, _catalog, licenseService);

        var (cashier, shift, product) = await SetupStoreEnvironmentAsync();
        var command = new CreateSaleCommand(
            TenantId: "TENANT_UNLICENSED",
            BranchId: BranchId,
            CounterId: CounterId,
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 2000.00m) }
        );

        var ex = await Assert.ThrowsAsync<LicenseLockoutException>(() => saleService.CommitSaleAsync(command));
        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------
    // Test 10: Historical Reads Work Flawlessly Under Lockout
    // -------------------------------------------------------------
    [Fact]
    public async Task HistoricalReads_UnderLockout_AllReadMethodsSucceedCompletely()
    {
        // 1. Arrange: Commit historical sale and ledger while license is active
        var licenseService = new LicenseService(_db, secretKey: SecretKey);
        await licenseService.ApplyEntitlementAsync(CreateToken(TenantId, expiresAtUtc: DateTime.UtcNow.AddDays(10)), TenantId);
        var saleService = new SaleService(_db, _catalog, licenseService);

        var (cashier, shift, product) = await SetupStoreEnvironmentAsync();
        var customer = await _customer.CreateCustomerAsync("Nimal Perera", "0771234567", "Colombo", 50000m, cashier.UserId);

        var committedSale = await saleService.CommitSaleAsync(new CreateSaleCommand(
            TenantId: TenantId,
            BranchId: BranchId,
            CounterId: CounterId,
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 2000.00m) },
            CustomerId: customer.CustomerId
        ));

        // 2. Transition into expired lockout
        await licenseService.ApplyEntitlementAsync(CreateToken(TenantId, expiresAtUtc: DateTime.UtcNow.AddDays(-30), gracePeriodDays: 7, revision: 2), TenantId);

        // Verify checkout is indeed blocked
        await Assert.ThrowsAsync<LicenseLockoutException>(() => saleService.CommitSaleAsync(new CreateSaleCommand(
            TenantId: TenantId,
            BranchId: BranchId,
            CounterId: CounterId,
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 2000.00m) }
        )));

        // 3. Act & Assert: All historical read queries succeed 100% without exception
        var saleById = await saleService.GetSaleByIdAsync(committedSale.SaleId);
        Assert.NotNull(saleById);
        Assert.Equal(committedSale.ReceiptNumber, saleById.ReceiptNumber);
        Assert.Single(saleById.Lines);
        Assert.Single(saleById.Tenders);

        var saleByRcpt = await saleService.GetSaleByReceiptNumberAsync(committedSale.ReceiptNumber);
        Assert.NotNull(saleByRcpt);
        Assert.Equal(committedSale.SaleId, saleByRcpt.SaleId);

        var count = await saleService.GetSaleCountAsync();
        Assert.Equal(1, count);

        var recentSales = await saleService.GetRecentSalesAsync(50);
        Assert.Single(recentSales);
        Assert.Equal(committedSale.SaleId, recentSales[0].SaleId);

        // Reports succeed
        var shiftReport = await _report.GenerateShiftReportAsync(shift.ShiftId);
        Assert.NotNull(shiftReport);
        Assert.Equal(1, shiftReport.TotalSalesCount);

        var dailyReport = await _report.GenerateDailyReportAsync(DateTime.UtcNow);
        Assert.NotNull(dailyReport);

        // Customer statements and ledger succeed
        var ledger = await _customer.GetCustomerLedgerAsync(customer.CustomerId);
        Assert.NotNull(ledger);
        var balance = await _customer.GetCustomerBalanceAsync(customer.CustomerId);
        Assert.Equal(0m, balance);

        // Shift queries succeed
        var activeShift = await _shift.GetShiftByIdAsync(shift.ShiftId);
        Assert.NotNull(activeShift);
        var shifts = await _shift.GetShiftsAsync(BranchId);
        Assert.NotEmpty(shifts);
    }

    // -------------------------------------------------------------
    // Test 11: Backward Compatibility Without LicenseService
    // -------------------------------------------------------------
    [Fact]
    public async Task SaleService_ConstructorWithoutLicenseService_PreservesExistingBehavior()
    {
        var legacySaleService = new SaleService(_db, _catalog);
        var (cashier, shift, product) = await SetupStoreEnvironmentAsync();

        var sale = await legacySaleService.CommitSaleAsync(new CreateSaleCommand(
            TenantId: TenantId,
            BranchId: BranchId,
            CounterId: CounterId,
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 2000.00m) }
        ));

        Assert.NotNull(sale);
        Assert.Equal(SaleStatus.Completed, sale.Status);
    }

    // -------------------------------------------------------------
    // Test 12: Feature Check
    // -------------------------------------------------------------
    [Fact]
    public async Task HasFeature_ReturnsCorrectStatus()
    {
        var licenseService = new LicenseService(_db, secretKey: SecretKey);
        var token = CreateToken(TenantId, features: new List<string> { "billing", "inventory", "transfers" });
        await licenseService.ApplyEntitlementAsync(token, TenantId);

        Assert.True(licenseService.HasFeature(TenantId, "billing"));
        Assert.True(licenseService.HasFeature(TenantId, "transfers"));
        Assert.False(licenseService.HasFeature(TenantId, "ai_forecasting"));
        Assert.False(licenseService.HasFeature(TenantId, ""));
    }
}
