using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

/// <summary>
/// Empirical integration and stress-test harness for the End-to-End Multi-Module Lifecycle.
/// Covers all 9 stages:
/// 1. Open shift with non-default float (LKR 7,500.50).
/// 2. Dispatch inventory transfer from Branch A to Branch B -> source deducted, dest unaffected.
/// 3. Receive inventory transfer at Branch B -> dest stock incremented, status Received.
/// 4. Start stock physical count session -> freeze snapshot cutoff.
/// 5. Process sale under active license -> tender, change, and receipt verification.
/// 6. Complete stock physical count session -> delta reconciliation preserves intervening sale.
/// 7. Close shift -> exact expected cash matching drawer down to the cent.
/// 8. Apply expired license -> checkout lockout enforced while historical sales & reports remain 100% accessible.
/// 9. Adversarial stress-testing of boundaries, roles, and invalid state transitions across the lifecycle.
/// </summary>
public class EndToEndMultiModuleLifecycleTests : IDisposable
{
    private readonly PosDatabase _dbBranchA;
    private readonly PosDatabase _dbBranchB;

    private readonly CatalogService _catalogA;
    private readonly CatalogService _catalogB;

    private readonly AuthService _authA;
    private readonly AuthService _authB;

    private readonly ShiftService _shiftB;
    private readonly TransferService _transferA;
    private readonly TransferService _transferB;
    private readonly StockCountService _stockCountB;
    private readonly LicenseService _licenseB;
    private readonly SaleService _saleB;
    private readonly ReportService _reportB;

    private const string SecretKey = "Enightx-Pos-Licensing-Secret-Key-2026-Secure";
    private const string TenantId = "TENANT_E2E_LIFECYCLE_01";
    private const string BranchAId = "BRANCH_COLOMBO_01";
    private const string BranchBId = "BRANCH_KANDY_02";
    private const string CounterBId = "COUNTER_KANDY_POS01";

    public EndToEndMultiModuleLifecycleTests()
    {
        _dbBranchA = PosDatabase.CreateInMemory();
        _dbBranchB = PosDatabase.CreateInMemory();

        _catalogA = new CatalogService(_dbBranchA);
        _catalogB = new CatalogService(_dbBranchB);

        _authA = new AuthService(_dbBranchA);
        _authB = new AuthService(_dbBranchB);

        _shiftB = new ShiftService(_dbBranchB);
        _transferA = new TransferService(_dbBranchA, _catalogA);
        _transferB = new TransferService(_dbBranchB, _catalogB);
        _stockCountB = new StockCountService(_dbBranchB, _catalogB);
        _licenseB = new LicenseService(_dbBranchB, SecretKey);
        _saleB = new SaleService(_dbBranchB, _catalogB, _licenseB);
        _reportB = new ReportService(_dbBranchB);
    }

    public void Dispose()
    {
        _dbBranchA.Dispose();
        _dbBranchB.Dispose();
    }

    [Fact]
    public async Task FullMultiModuleLifecycle_ExecuteAllNineStages_EmpiricallyVerified()
    {
        // -------------------------------------------------------------------------
        // SETUP: Seed Users, Roles, Products in Branch A and Branch B
        // -------------------------------------------------------------------------
        var mgrA = await _authA.CreateUserAsync("mgr_colombo", "Colombo Manager", "Pass#123", Role.Manager);
        var mgrB = await _authB.CreateUserAsync("mgr_kandy", "Kandy Manager", "Pass#123", Role.Manager);
        var cashierB = await _authB.CreateUserAsync("cashier_kandy", "Kandy Cashier", "Pass#123", Role.Cashier);

        const string productId = "PROD_CEYLON_TEA_500G";
        const string barcode = "4791234567890";
        const string productName = "Pure Ceylon Black Tea 500g";
        const decimal unitPrice = 1250.00m;
        const decimal costBasis = 800.00m;

        // Branch A starts with 100 units; Branch B starts with 20 units
        await _catalogA.AddProductAsync(new Product
        {
            ProductId = productId,
            Barcode = barcode,
            Name = productName,
            UnitPrice = unitPrice,
            CostBasis = costBasis,
            StockOnHand = 100.0m,
            IsActive = true,
            MinStockThreshold = 10.0m
        });

        await _catalogB.AddProductAsync(new Product
        {
            ProductId = productId,
            Barcode = barcode,
            Name = productName,
            UnitPrice = unitPrice,
            CostBasis = costBasis,
            StockOnHand = 20.0m,
            IsActive = true,
            MinStockThreshold = 10.0m
        });

        // =========================================================================
        // STAGE 1: Open shift with non-default float (LKR 7,500.50)
        // =========================================================================
        const decimal nonDefaultFloat = 7500.50m;
        var shift = await _shiftB.OpenShiftAsync(
            branchId: BranchBId,
            counterId: CounterBId,
            cashierId: cashierB.UserId,
            openingFloat: nonDefaultFloat,
            tenantId: TenantId
        );

        Assert.NotNull(shift);
        Assert.NotEqual(Guid.Empty, shift.ShiftId);
        Assert.Equal(ShiftStatus.Open, shift.Status);
        Assert.Equal(nonDefaultFloat, shift.OpeningFloat);
        Assert.Equal(nonDefaultFloat, shift.ExpectedCash);
        Assert.Equal(0m, shift.CashReceived);
        Assert.Equal(0m, shift.ChangeGiven);

        // =========================================================================
        // STAGE 2: Dispatch inventory transfer from Branch A to Branch B
        // Verify: Source stock deducted, Dest stock unaffected while In-Transit
        // =========================================================================
        const decimal transferQuantity = 15.0m;
        var dispatchLines = new List<TransferLineItem>
        {
            new()
            {
                ProductId = productId,
                ProductName = productName,
                Barcode = barcode,
                DispatchedQuantity = transferQuantity,
                UnitCost = costBasis
            }
        };

        var transfer = await _transferA.DispatchTransferAsync(
            sourceBranchId: BranchAId,
            destBranchId: BranchBId,
            lines: dispatchLines,
            dispatchedBy: mgrA.UserId,
            tenantId: TenantId,
            notes: "Emergency replenishment for Kandy Branch"
        );

        Assert.NotNull(transfer);
        Assert.Equal(TransferStatus.InTransit, transfer.Status);
        Assert.Equal(transferQuantity, transfer.TotalDispatchedQuantity);

        // Verify Branch A stock deducted: 100 - 15 = 85
        var prodA = await _catalogA.GetProductByIdAsync(productId);
        Assert.NotNull(prodA);
        Assert.Equal(85.0m, prodA.StockOnHand);

        // Verify Branch B stock is COMPLETELY UNAFFECTED: remains 20.0
        var prodBBeforeReceive = await _catalogB.GetProductByIdAsync(productId);
        Assert.NotNull(prodBBeforeReceive);
        Assert.Equal(20.0m, prodBBeforeReceive.StockOnHand);

        // =========================================================================
        // STAGE 3: Receive inventory transfer at Branch B
        // Verify: Dest stock updated to 20 + 15 = 35.0, status Received
        // =========================================================================
        // Simulate transfer record arrival at Branch B DB
        using (var connB = _dbBranchB.CreateConnection())
        {
            using var insTrf = connB.CreateCommand();
            insTrf.CommandText = @"
                INSERT INTO transfers (
                    transfer_id, transfer_number, tenant_id, source_branch_id, dest_branch_id,
                    status, dispatched_by, dispatched_at_utc, total_dispatched_quantity,
                    has_discrepancy, created_at_utc, updated_at_utc
                ) VALUES (
                    $id, $num, $tid, $src, $dst,
                    $st, $dby, $dat, $totd,
                    0, $cat, $uat
                );
                INSERT INTO transfer_items (
                    transfer_item_id, transfer_id, product_id, product_name, barcode,
                    dispatched_quantity, unit_cost
                ) VALUES (
                    $lid, $id, $pid, $pname, $bcode,
                    $dqty, $ucost
                );
            ";
            insTrf.Parameters.AddWithValue("$id", transfer.TransferId);
            insTrf.Parameters.AddWithValue("$num", transfer.TransferNumber);
            insTrf.Parameters.AddWithValue("$tid", TenantId);
            insTrf.Parameters.AddWithValue("$src", BranchAId);
            insTrf.Parameters.AddWithValue("$dst", BranchBId);
            insTrf.Parameters.AddWithValue("$st", (int)TransferStatus.InTransit);
            insTrf.Parameters.AddWithValue("$dby", mgrA.UserId);
            insTrf.Parameters.AddWithValue("$dat", transfer.DispatchedAtUtc.ToString("o"));
            insTrf.Parameters.AddWithValue("$totd", transferQuantity);
            insTrf.Parameters.AddWithValue("$cat", DateTime.UtcNow.ToString("o"));
            insTrf.Parameters.AddWithValue("$uat", DateTime.UtcNow.ToString("o"));

            insTrf.Parameters.AddWithValue("$lid", Guid.NewGuid().ToString());
            insTrf.Parameters.AddWithValue("$pid", productId);
            insTrf.Parameters.AddWithValue("$pname", productName);
            insTrf.Parameters.AddWithValue("$bcode", barcode);
            insTrf.Parameters.AddWithValue("$dqty", transferQuantity);
            insTrf.Parameters.AddWithValue("$ucost", costBasis);
            await insTrf.ExecuteNonQueryAsync();
        }

        var receivedTransfer = await _transferB.ReceiveTransferAsync(
            transferId: transfer.TransferId,
            receivingBranchId: BranchBId,
            receivedBy: mgrB.UserId
        );

        Assert.NotNull(receivedTransfer);
        Assert.Equal(TransferStatus.Received, receivedTransfer.Status);
        Assert.Equal(mgrB.UserId, receivedTransfer.ReceivedBy);

        // Verify Branch B stock is incremented: 20 + 15 = 35.0
        var prodBAfterReceive = await _catalogB.GetProductByIdAsync(productId);
        Assert.NotNull(prodBAfterReceive);
        Assert.Equal(35.0m, prodBAfterReceive.StockOnHand);

        // =========================================================================
        // STAGE 4: Start stock physical count session -> freeze snapshot cutoff
        // =========================================================================
        var countSession = await _stockCountB.StartSessionAsync(
            branchId: BranchBId,
            startedBy: mgrB.UserId,
            tenantId: TenantId,
            productIds: new List<string> { productId }
        );

        Assert.NotNull(countSession);
        Assert.Equal(StockCountStatus.InProgress, countSession.Status);
        Assert.Single(countSession.Items);
        var snapshotItem = countSession.Items[0];
        Assert.Equal(productId, snapshotItem.ProductId);
        Assert.Equal(35.0m, snapshotItem.SnapshotStock); // Frozen cutoff at 35.0
        Assert.False(snapshotItem.IsCounted);

        // =========================================================================
        // STAGE 5: Process sale under active license -> verify tender and receipt
        // Intervening sale during physical count session!
        // =========================================================================
        // 5a. Install active valid cryptographic license
        var activeToken = LicenseService.GenerateToken(
            tenantId: TenantId,
            plan: "Enterprise",
            issuedAtUtc: DateTime.UtcNow.AddDays(-5),
            expiresAtUtc: DateTime.UtcNow.AddDays(30),
            gracePeriodDays: 7,
            maxDevices: 10,
            features: new List<string> { "POS_BILLING", "INVENTORY", "REPORTS" },
            revision: 1,
            secretKey: SecretKey
        );
        var appliedLicense = await _licenseB.ApplyEntitlementAsync(activeToken, TenantId);
        Assert.NotNull(appliedLicense);
        var statusActive = await _licenseB.EvaluateLicenseStatusAsync(TenantId);
        Assert.True(statusActive.IsSaleAllowed);
        Assert.Equal(LicenseStatus.Active, statusActive.Status);

        // 5b. Process sale of 4 units of Ceylon Tea
        // 4 units @ LKR 1,250.00 = LKR 5,000.00
        const decimal saleQty = 4.0m;
        const decimal saleTotal = 5000.00m;
        const decimal tenderCash = 6000.00m; // Customer gives 6,000 LKR
        const decimal expectedChange = 1000.00m; // Change = 1,000 LKR

        var saleCommand = new CreateSaleCommand(
            TenantId: TenantId,
            BranchId: BranchBId,
            CounterId: CounterBId,
            CashierId: cashierB.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(ProductId: productId, Quantity: saleQty)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType: TenderType.CASH, AmountTendered: tenderCash)
            }
        );

        var sale = await _saleB.CommitSaleAsync(saleCommand);

        Assert.NotNull(sale);
        Assert.NotEqual(Guid.Empty, sale.SaleId);
        Assert.StartsWith($"{BranchBId}-{CounterBId}-", sale.ReceiptNumber);
        Assert.Equal(saleTotal, sale.GrandTotal);
        Assert.Equal(tenderCash, sale.Tenders[0].AmountTendered);
        Assert.Equal(expectedChange, sale.Tenders[0].ChangeGiven);

        // Verify stock in Branch B after sale: 35.0 - 4.0 = 31.0
        var prodBAfterSale = await _catalogB.GetProductByIdAsync(productId);
        Assert.NotNull(prodBAfterSale);
        Assert.Equal(31.0m, prodBAfterSale.StockOnHand);

        // Verify shift drawer updated:
        // CashReceived += 6,000, ChangeGiven += 1,000 -> Net Cash += 5,000
        // Expected Cash = 7,500.50 + 5,000.00 = 12,500.50
        var updatedShift = await _shiftB.GetActiveShiftAsync(BranchBId, CounterBId);
        Assert.NotNull(updatedShift);
        Assert.Equal(tenderCash, updatedShift.CashReceived);
        Assert.Equal(expectedChange, updatedShift.ChangeGiven);
        var dynamicExpectedCash = MoneyCalculator.CalculateShiftExpectedCash(
            updatedShift.OpeningFloat,
            updatedShift.CashReceived,
            updatedShift.ChangeGiven,
            updatedShift.CashRefunds,
            updatedShift.CashIn,
            updatedShift.CashOut
        );
        Assert.Equal(nonDefaultFloat + saleTotal, dynamicExpectedCash);
        Assert.Equal(12500.50m, dynamicExpectedCash);

        // =========================================================================
        // STAGE 6: Complete stock physical count session
        // Verify: Delta reconciliation preserves the intervening sale!
        // =========================================================================
        // Staff counted 34 units physically present on shelf at snapshot cutoff
        // (i.e. 1 unit damaged/missing relative to the 35 snapshot: variance = 34 - 35 = -1)
        await _stockCountB.RecordCountItemAsync(
            sessionId: countSession.SessionId,
            productId: productId,
            countedQuantity: 34.0m,
            countedBy: mgrB.UserId
        );

        // Manager completes session
        var completedSession = await _stockCountB.CompleteSessionAsync(
            sessionId: countSession.SessionId,
            completedBy: mgrB.UserId
        );

        Assert.NotNull(completedSession);
        Assert.Equal(StockCountStatus.Completed, completedSession.Status);
        Assert.Equal(-1.0m, completedSession.TotalVarianceQuantity);
        Assert.Equal(-800.00m, completedSession.TotalVarianceValue); // -1 * 800.00 cost basis

        // CRUCIAL VERIFICATION:
        // Current stock was 31.0 (after the 4-unit sale).
        // Delta adjustment applies variance (-1.0) to current stock:
        // 31.0 + (-1.0) = 30.0!
        // The intervening sale of 4 units was PRESERVED and not overwritten!
        var prodBFinalStock = await _catalogB.GetProductByIdAsync(productId);
        Assert.NotNull(prodBFinalStock);
        Assert.Equal(30.0m, prodBFinalStock.StockOnHand);

        // =========================================================================
        // STAGE 7: Close shift -> verify drawer balance
        // =========================================================================
        // Exact expected cash = 12,500.50
        const decimal actualCountedCash = 12500.50m;
        var closedShift = await _shiftB.CloseShiftAsync(
            shiftId: shift.ShiftId,
            actualCountedCash: actualCountedCash,
            actorId: cashierB.UserId,
            tenantId: TenantId
        );

        Assert.NotNull(closedShift);
        Assert.Equal(ShiftStatus.Closed, closedShift.Status);
        Assert.Equal(actualCountedCash, closedShift.ActualCountedCash);
        Assert.Equal(0m, closedShift.Variance);
        Assert.Equal(12500.50m, closedShift.ExpectedCash);
        Assert.NotNull(closedShift.ClosedAtUtc);

        // =========================================================================
        // STAGE 8: Apply expired license -> verify checkout lockout
        // while historical sales and reports remain 100% accessible!
        // =========================================================================
        // 8a. Generate and apply expired entitlement (expired 40 days ago, grace 7 days)
        var expiredToken = LicenseService.GenerateToken(
            tenantId: TenantId,
            plan: "Enterprise",
            issuedAtUtc: DateTime.UtcNow.AddDays(-60),
            expiresAtUtc: DateTime.UtcNow.AddDays(-40),
            gracePeriodDays: 7,
            maxDevices: 10,
            features: new List<string> { "POS_BILLING" },
            revision: 2, // Higher revision replaces active license
            secretKey: SecretKey
        );
        var appliedExpired = await _licenseB.ApplyEntitlementAsync(expiredToken, TenantId);
        Assert.NotNull(appliedExpired);
        var statusExpired = await _licenseB.EvaluateLicenseStatusAsync(TenantId);
        Assert.False(statusExpired.IsSaleAllowed);
        Assert.Equal(LicenseStatus.ExpiredLockedOut, statusExpired.Status);

        // 8b. Open a new shift to attempt checkout
        var shift2 = await _shiftB.OpenShiftAsync(
            branchId: BranchBId,
            counterId: CounterBId,
            cashierId: cashierB.UserId,
            openingFloat: 5000.00m,
            tenantId: TenantId
        );

        // 8c. Checkout lockout verification: CommitSaleAsync throws LicenseLockoutException
        var lockedSaleCommand = new CreateSaleCommand(
            TenantId: TenantId,
            BranchId: BranchBId,
            CounterId: CounterBId,
            CashierId: cashierB.UserId,
            ShiftId: shift2.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(ProductId: productId, Quantity: 1.0m)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType: TenderType.CASH, AmountTendered: 1250.00m)
            }
        );

        var ex = await Assert.ThrowsAsync<LicenseLockoutException>(() =>
            _saleB.CommitSaleAsync(lockedSaleCommand)
        );
        Assert.Contains("grace period", ex.Message, StringComparison.OrdinalIgnoreCase);

        // 8d. Historical sales read preservation verification:
        // GetSaleByIdAsync and GetSaleByReceiptNumberAsync remain 100% accessible!
        var historicalSaleById = await _saleB.GetSaleByIdAsync(sale.SaleId);
        Assert.NotNull(historicalSaleById);
        Assert.Equal(sale.SaleId, historicalSaleById.SaleId);
        Assert.Equal(sale.ReceiptNumber, historicalSaleById.ReceiptNumber);
        Assert.Equal(saleTotal, historicalSaleById.GrandTotal);
        Assert.Single(historicalSaleById.Lines);
        Assert.Equal(saleQty, historicalSaleById.Lines[0].Quantity);

        var historicalSaleByReceipt = await _saleB.GetSaleByReceiptNumberAsync(sale.ReceiptNumber);
        Assert.NotNull(historicalSaleByReceipt);
        Assert.Equal(sale.SaleId, historicalSaleByReceipt.SaleId);

        // 8e. Reports read preservation verification:
        // Daily Report and Inventory Valuation Report remain 100% accessible!
        var dailyReport = await _reportB.GenerateDailyReportAsync(DateTime.UtcNow, BranchBId);
        Assert.NotNull(dailyReport);
        Assert.Equal(1, dailyReport.CompletedSalesCount);
        Assert.Equal(saleTotal, dailyReport.GrossSales);
        Assert.Equal(saleTotal, dailyReport.NetSales);

        var valuationReport = await _reportB.GenerateInventoryValuationReportAsync();
        Assert.NotNull(valuationReport);
        Assert.True(valuationReport.TotalProducts >= 1);
        Assert.True(valuationReport.TotalValuationAtCost > 0);
    }

    [Fact]
    public async Task MultiModuleAdversarialChallenges_StressTestBoundaryConditions()
    {
        // -------------------------------------------------------------------------
        // ADVERSARIAL CHALLENGE 1: Negative float or zero float validation
        // -------------------------------------------------------------------------
        var cashier = await _authB.CreateUserAsync("adv_cashier", "Adversarial Cashier", "Pass#123", Role.Cashier);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _shiftB.OpenShiftAsync(BranchBId, CounterBId, cashier.UserId, -100.00m, TenantId)
        );

        // Open legitimate shift with non-default float
        var shift = await _shiftB.OpenShiftAsync(BranchBId, CounterBId, cashier.UserId, 7500.50m, TenantId);

        // Attempt opening second concurrent shift on same counter -> rejected
        await Assert.ThrowsAsync<PosException>(() =>
            _shiftB.OpenShiftAsync(BranchBId, CounterBId, cashier.UserId, 5000.00m, TenantId)
        );

        // -------------------------------------------------------------------------
        // ADVERSARIAL CHALLENGE 2: Transfer to identical branch -> rejected
        // -------------------------------------------------------------------------
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _transferA.DispatchTransferAsync(
                sourceBranchId: BranchAId,
                destBranchId: BranchAId, // Same branch
                lines: new List<TransferLineItem> { new() { ProductId = "P1", DispatchedQuantity = 5 } },
                dispatchedBy: "mgr1",
                tenantId: TenantId
            )
        );

        // -------------------------------------------------------------------------
        // ADVERSARIAL CHALLENGE 3: Unauthorized role completing stock count -> rejected
        // -------------------------------------------------------------------------
        var p = new Product
        {
            ProductId = "PROD_RICE_5KG",
            Barcode = "4795551112223",
            Name = "Samba Rice 5kg",
            UnitPrice = 1100.00m,
            CostBasis = 900.00m,
            StockOnHand = 50.0m,
            IsActive = true
        };
        await _catalogB.AddProductAsync(p);

        var mgr = await _authB.CreateUserAsync("adv_mgr", "Adversarial Mgr", "Pass#123", Role.Manager);
        var session = await _stockCountB.StartSessionAsync(BranchBId, mgr.UserId, TenantId, new List<string> { p.ProductId });

        await _stockCountB.RecordCountItemAsync(session.SessionId, p.ProductId, 48.0m, cashier.UserId);

        // Cashier attempts to complete count -> unauthorized action
        await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _stockCountB.CompleteSessionAsync(session.SessionId, cashier.UserId)
        );

        // Manager completes count -> succeeds
        var comp = await _stockCountB.CompleteSessionAsync(session.SessionId, mgr.UserId);
        Assert.Equal(StockCountStatus.Completed, comp.Status);

        // Attempt to re-complete already completed session -> rejected
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _stockCountB.CompleteSessionAsync(session.SessionId, mgr.UserId)
        );

        // Close shift
        await _shiftB.CloseShiftAsync(shift.ShiftId, 7500.50m, cashier.UserId, TenantId);
    }
}
