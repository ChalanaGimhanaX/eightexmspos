using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class StockCountTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _sale;
    private readonly StockCountService _stockCount;

    public StockCountTests()
    {
        _db = PosDatabase.CreateInMemory();
        EnsureStockCountTablesExist(_db);
        _auth = new AuthService(_db);
        _catalog = new CatalogService(_db);
        _shift = new ShiftService(_db);
        _sale = new SaleService(_db, _catalog);
        _stockCount = new StockCountService(_db, _catalog);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private static void EnsureStockCountTablesExist(PosDatabase db)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS stock_count_sessions (
                session_id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                branch_id TEXT NOT NULL,
                status INTEGER NOT NULL DEFAULT 1,
                started_by TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                completed_by TEXT,
                completed_at_utc TEXT,
                cancelled_by TEXT,
                cancelled_at_utc TEXT,
                cancellation_reason TEXT,
                notes TEXT,
                total_items_counted INTEGER NOT NULL DEFAULT 0,
                total_variance_quantity NUMERIC NOT NULL DEFAULT 0.0,
                total_variance_value NUMERIC NOT NULL DEFAULT 0.0,
                lines_with_variance_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS stock_count_items (
                item_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                product_id TEXT NOT NULL,
                product_name TEXT NOT NULL,
                barcode TEXT NOT NULL,
                snapshot_stock NUMERIC NOT NULL,
                counted_quantity NUMERIC,
                variance_quantity NUMERIC NOT NULL DEFAULT 0.0,
                cost_basis NUMERIC NOT NULL DEFAULT 0.0,
                variance_value NUMERIC NOT NULL DEFAULT 0.0,
                is_counted INTEGER NOT NULL DEFAULT 0,
                counted_by TEXT,
                counted_at_utc TEXT,
                notes TEXT,
                FOREIGN KEY (session_id) REFERENCES stock_count_sessions(session_id) ON DELETE CASCADE,
                FOREIGN KEY (product_id) REFERENCES products(product_id)
            );
        ";
        cmd.ExecuteNonQuery();
    }

    private async Task<Product> SeedProductAsync(string productId, string barcode, string name, decimal stockOnHand, decimal costBasis)
    {
        var product = new Product
        {
            ProductId = productId,
            Barcode = barcode,
            Name = name,
            UnitPrice = costBasis * 1.40m,
            CostBasis = costBasis,
            StockOnHand = stockOnHand,
            IsActive = true
        };
        await _catalog.AddProductAsync(product);
        return product;
    }

    [Fact]
    public async Task StartSession_SnapshotsCurrentSystemStock_FrozenCutoffIntact()
    {
        // Arrange
        var manager = await _auth.CreateUserAsync("mgr_count1", "Count Manager", "Pass#123", Role.Manager);
        var p1 = await SeedProductAsync("prod_sc_1", "479101", "Air Filter Primary", 25.0m, 1450.00m);
        var p2 = await SeedProductAsync("prod_sc_2", "479102", "Fuel Filter Diesel", 12.0m, 2100.00m);

        // Act: Start full branch count session
        var session = await _stockCount.StartSessionAsync(
            branchId: "B01",
            startedBy: manager.UserId,
            tenantId: "TENANT_LK_01",
            notes: "End-of-month stock count"
        );

        // Assert Header
        Assert.NotNull(session);
        Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
        Assert.Equal(StockCountStatus.InProgress, session.Status);
        Assert.Equal("B01", session.BranchId);
        Assert.Equal("TENANT_LK_01", session.TenantId);
        Assert.Equal(manager.UserId, session.StartedBy);
        Assert.Null(session.CompletedAtUtc);

        // Assert Frozen Snapshot Items
        Assert.True(session.Items.Count >= 2);
        var item1 = session.Items.First(i => i.ProductId == p1.ProductId);
        Assert.Equal(25.0m, item1.SnapshotStock);
        Assert.Equal(1450.00m, item1.CostBasis);
        Assert.Null(item1.CountedQuantity);
        Assert.False(item1.IsCounted);
        Assert.Equal(0.0m, item1.VarianceQuantity);

        var item2 = session.Items.First(i => i.ProductId == p2.ProductId);
        Assert.Equal(12.0m, item2.SnapshotStock);
        Assert.Equal(2100.00m, item2.CostBasis);

        // Assert Direct SQLite Persistence
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, started_by FROM stock_count_sessions WHERE session_id = $id;";
        cmd.Parameters.AddWithValue("$id", session.SessionId);
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0)); // 1 = InProgress
        Assert.Equal(manager.UserId, reader.GetString(1));
    }

    [Fact]
    public async Task ActiveSession_SalesOccurringDuringCount_DoNotAlterFrozenSnapshotStock()
    {
        // Core Guardrail: Sales made while count is in progress must NOT mutate the frozen snapshot
        var manager = await _auth.CreateUserAsync("mgr_active_sale", "Active Sale Mgr", "Pass#123", Role.Manager);
        var cashier = await _auth.CreateUserAsync("cashier_active", "Active Cashier", "Pass#123", Role.Cashier);
        var product = await SeedProductAsync("prod_active_part", "479103", "Brake Shoe Set", 30.0m, 1800.00m);

        // 1. Session starts when product has 30 units
        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01");
        var initialItem = session.Items.First(i => i.ProductId == product.ProductId);
        Assert.Equal(30.0m, initialItem.SnapshotStock);

        // 2. Open shift and commit sale of 6 units at counter during active counting
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");
        var saleCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 6.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 20000.00m) }
        );
        var sale = await _sale.CommitSaleAsync(saleCmd);
        Assert.NotNull(sale);

        // Verify product stock on hand decreased from 30 to 24
        var currentProd = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(24.0m, currentProd!.StockOnHand);

        // 3. Re-query the active stock count session from database
        var reloadedSession = await _stockCount.GetSessionByIdAsync(session.SessionId);
        Assert.NotNull(reloadedSession);
        var reloadedItem = reloadedSession.Items.First(i => i.ProductId == product.ProductId);

        // CRITICAL ASSERTION: Snapshot stock MUST STILL equal 30.0m (unaffected by sales!)
        Assert.Equal(30.0m, reloadedItem.SnapshotStock);
    }

    [Fact]
    public async Task RecordCountItem_ComputesAccurateVarianceQuantity_AndVarianceValue()
    {
        // Arrange
        var manager = await _auth.CreateUserAsync("mgr_record_count", "Record Mgr", "Pass#123", Role.Manager);
        var pShort = await SeedProductAsync("prod_shortage", "479104", "Chain 520 Pitch", 20.0m, 3200.00m);
        var pSurplus = await SeedProductAsync("prod_surplus", "479105", "Bulb 12V 10W", 10.0m, 150.00m);
        var pExact = await SeedProductAsync("prod_exact", "479106", "Fuse Blade 15A", 50.0m, 40.00m);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01");

        // Act 1: Record shortage (Counted 17, Snapshot 20 -> Variance -3, Value -9,600 LKR)
        await _stockCount.RecordCountItemAsync(session.SessionId, pShort.ProductId, 17.0m, manager.UserId);

        // Act 2: Record surplus (Counted 14, Snapshot 10 -> Variance +4, Value +600 LKR)
        await _stockCount.RecordCountItemAsync(session.SessionId, pSurplus.ProductId, 14.0m, manager.UserId);

        // Act 3: Record exact match (Counted 50, Snapshot 50 -> Variance 0, Value 0 LKR)
        await _stockCount.RecordCountItemAsync(session.SessionId, pExact.ProductId, 50.0m, manager.UserId);

        // Assert Item Variance Calculations
        var updatedSession = await _stockCount.GetSessionByIdAsync(session.SessionId);
        Assert.NotNull(updatedSession);

        var itemShort = updatedSession.Items.First(i => i.ProductId == pShort.ProductId);
        Assert.True(itemShort.IsCounted);
        Assert.Equal(17.0m, itemShort.CountedQuantity);
        Assert.Equal(-3.0m, itemShort.VarianceQuantity); // 17 - 20 = -3
        Assert.Equal(-9600.00m, itemShort.VarianceValue); // -3 * 3200 = -9600

        var itemSurplus = updatedSession.Items.First(i => i.ProductId == pSurplus.ProductId);
        Assert.True(itemSurplus.IsCounted);
        Assert.Equal(14.0m, itemSurplus.CountedQuantity);
        Assert.Equal(4.0m, itemSurplus.VarianceQuantity); // 14 - 10 = +4
        Assert.Equal(600.00m, itemSurplus.VarianceValue); // +4 * 150 = +600

        var itemExact = updatedSession.Items.First(i => i.ProductId == pExact.ProductId);
        Assert.True(itemExact.IsCounted);
        Assert.Equal(50.0m, itemExact.CountedQuantity);
        Assert.Equal(0.0m, itemExact.VarianceQuantity);
        Assert.Equal(0.0m, itemExact.VarianceValue);

        // Assert Session Aggregate Header
        Assert.Equal(3, updatedSession.TotalItemsCounted);
        Assert.Equal(1.0m, updatedSession.TotalVarianceQuantity); // -3 + 4 + 0 = +1
        Assert.Equal(-9000.00m, updatedSession.TotalVarianceValue); // -9600 + 600 = -9000
        Assert.Equal(2, updatedSession.LinesWithVarianceCount); // 2 lines with non-zero variance
    }

    [Theory]
    [InlineData(7.0, 10.0, 105.555, -316.67)] // -3 * 105.555 = -316.665 -> -316.67 (AwayFromZero)
    [InlineData(13.0, 10.0, 105.555, 316.67)] // +3 * 105.555 = +316.665 -> +316.67 (AwayFromZero)
    [InlineData(9.0, 10.0, 12.345, -12.35)]   // -1 * 12.345 = -12.345 -> -12.35 (AwayFromZero)
    [InlineData(11.0, 10.0, 12.345, 12.35)]   // +1 * 12.345 = +12.345 -> +12.35 (AwayFromZero)
    public async Task RecordCountItem_HighPrecisionCostBasis_RoundsVarianceValueAwayFromZero(
        decimal countedQty, decimal snapshotQty, decimal costBasis, decimal expectedVarianceValue)
    {
        var manager = await _auth.CreateUserAsync($"mgr_round_{Guid.NewGuid():N}", "Round Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync($"prod_pr_{Guid.NewGuid():N}", $"BAR_{Guid.NewGuid():N}", "Precision Part", snapshotQty, costBasis);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });

        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, countedQty, manager.UserId);

        var updated = await _stockCount.GetSessionByIdAsync(session.SessionId);
        var item = updated!.Items.First(i => i.ProductId == product.ProductId);

        Assert.Equal(expectedVarianceValue, item.VarianceValue);
    }

    [Fact]
    public async Task CompleteSession_AppliesVarianceDeltaToStockOnHand_PreservingInterveningSales()
    {
        // Hand-calculated integration proof:
        // Initial stock at start: 30 units
        // Intervening sale during count: 4 units sold -> Current stock = 26 units
        // Physical count conducted: 28 units found on shelves
        // Variance delta = Counted (28) - Snapshot (30) = -2 units (shortage)
        // Complete session: Current stock (26) + Variance (-2) = 24 units!
        // Physical reality: 28 units were on shelf before 4 were sold at counter -> 24 remaining on shelf!
        var manager = await _auth.CreateUserAsync("mgr_recon", "Reconciliation Manager", "Pass#123", Role.Manager);
        var cashier = await _auth.CreateUserAsync("cashier_recon", "Recon Cashier", "Pass#123", Role.Cashier);
        var product = await SeedProductAsync("prod_recon_part", "479107", "Battery Terminal Clamp", 30.0m, 250.00m);

        // 1. Start session
        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });

        // 2. Intervening sale of 4 units
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");
        await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 4.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 5000.00m) }
        ));

        // System stock is now 26.0m
        var postSale = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(26.0m, postSale!.StockOnHand);

        // 3. Record count of 28 units
        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, 28.0m, manager.UserId);

        // 4. Complete session
        var completed = await _stockCount.CompleteSessionAsync(session.SessionId, manager.UserId);

        // Assert Session Completion State
        Assert.Equal(StockCountStatus.Completed, completed.Status);
        Assert.Equal(manager.UserId, completed.CompletedBy);
        Assert.NotNull(completed.CompletedAtUtc);

        // Assert Stock On Hand is adjusted to exactly 24.0m!
        var finalProduct = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.NotNull(finalProduct);
        Assert.Equal(24.0m, finalProduct.StockOnHand);

        // Assert stock_movements logged delta adjustment of -2.0m
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT movement_type, quantity_change FROM stock_movements WHERE product_id = $pid AND quantity_change = -2.0;";
        cmd.Parameters.AddWithValue("$pid", product.ProductId);
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "STOCK_COUNT_ADJUSTMENT movement must be logged.");
        Assert.Equal(-2.0m, reader.GetDecimal(1));
    }

    [Fact]
    public async Task CancelSession_LeavesProductStockUnchanged_AndMarksCancelled()
    {
        // Arrange
        var manager = await _auth.CreateUserAsync("mgr_cancel_sess", "Cancel Session Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_cancel_sess", "479108", "Ignition Coil Assembly", 15.0m, 4200.00m);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });

        // Record a count showing variance
        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, 10.0m, manager.UserId); // -5 variance

        // Act: Cancel session
        var cancelled = await _stockCount.CancelSessionAsync(
            sessionId: session.SessionId,
            cancelledBy: manager.UserId,
            reason: "Audit postponed due to network outage"
        );

        // Assert
        Assert.Equal(StockCountStatus.Cancelled, cancelled.Status);
        Assert.Equal(manager.UserId, cancelled.CancelledBy);
        Assert.NotNull(cancelled.CancelledAtUtc);
        Assert.Equal("Audit postponed due to network outage", cancelled.CancellationReason);

        // Product stock MUST remain 15.0m (no adjustments applied!)
        var unchangedProd = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(15.0m, unchangedProd!.StockOnHand);

        // Assert NO stock movement was posted for session
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM stock_movements WHERE reference_id LIKE $ref;";
        cmd.Parameters.AddWithValue("$ref", $"%{session.SessionId}%");
        var movCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(0, movCount);
    }

    [Fact]
    public async Task RecordCountItem_NegativeQuantity_ThrowsException()
    {
        var manager = await _auth.CreateUserAsync("mgr_neg_cnt", "Neg Count Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_neg_cnt", "479109", "Spark Plug Cap", 10.0m, 200.00m);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });

        // Act & Assert: Physical count cannot be negative
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, -1.0m, manager.UserId)
        );
    }

    [Fact]
    public async Task CompleteSession_DoubleComplete_ThrowsException()
    {
        var manager = await _auth.CreateUserAsync("mgr_dup_comp", "Dup Comp Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_dup_comp", "479110", "Handlebar Grip L", 8.0m, 350.00m);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });
        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, 8.0m, manager.UserId);

        // First completion
        await _stockCount.CompleteSessionAsync(session.SessionId, manager.UserId);

        // Second completion attempt must throw
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _stockCount.CompleteSessionAsync(session.SessionId, manager.UserId)
        );
    }

    [Fact]
    public async Task CompleteSession_OnCancelledSession_ThrowsException()
    {
        var manager = await _auth.CreateUserAsync("mgr_comp_canc", "Comp Canc Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_comp_canc", "479111", "Fuel Hose 1m", 12.0m, 500.00m);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });

        await _stockCount.CancelSessionAsync(session.SessionId, manager.UserId, "Cancelled session");

        // Attempting to complete a cancelled session must throw
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _stockCount.CompleteSessionAsync(session.SessionId, manager.UserId)
        );
    }

    [Fact]
    public async Task CompleteSession_CashierAttemptingCompletion_ThrowsUnauthorizedActionException()
    {
        // A09 Security Rule: Only Manager or Owner can authorize stock adjustments
        var manager = await _auth.CreateUserAsync("mgr_auth_test", "Auth Mgr", "Pass#123", Role.Manager);
        var cashier = await _auth.CreateUserAsync("cashier_unauth", "Unauth Cashier", "Pass#123", Role.Cashier);
        var product = await SeedProductAsync("prod_unauth_test", "479112", "Exhaust Gasket Copper", 10.0m, 150.00m);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });
        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, 8.0m, cashier.UserId);

        // Cashier attempts to complete session
        await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _stockCount.CompleteSessionAsync(session.SessionId, cashier.UserId)
        );
    }

    [Fact]
    public async Task StartSession_WithSubsetOfProductIds_CycleCount_OnlySnapshotsSelectedProducts()
    {
        var manager = await _auth.CreateUserAsync("mgr_cycle", "Cycle Mgr", "Pass#123", Role.Manager);
        var p1 = await SeedProductAsync("prod_cyc_1", "479113", "Target Item 1", 10.0m, 500m);
        var p2 = await SeedProductAsync("prod_cyc_2", "479114", "Target Item 2", 15.0m, 800m);
        var pOther = await SeedProductAsync("prod_cyc_other", "479115", "Other Item", 50.0m, 100m);

        var session = await _stockCount.StartSessionAsync(
            branchId: "B01",
            startedBy: manager.UserId,
            tenantId: "TENANT_LK_01",
            productIds: new List<string> { p1.ProductId, p2.ProductId }
        );

        Assert.Equal(2, session.Items.Count);
        Assert.Contains(session.Items, i => i.ProductId == p1.ProductId);
        Assert.Contains(session.Items, i => i.ProductId == p2.ProductId);
        Assert.DoesNotContain(session.Items, i => i.ProductId == pOther.ProductId);
    }

    [Fact]
    public async Task GenerateVarianceJournal_ReturnsAccurateAdjustmentLines()
    {
        var manager = await _auth.CreateUserAsync("mgr_journal", "Journal Mgr", "Pass#123", Role.Manager);
        var pA = await SeedProductAsync("prod_jnl_a", "479116", "Journal Item A", 20.0m, 1000.00m);
        var pB = await SeedProductAsync("prod_jnl_b", "479117", "Journal Item B", 10.0m, 500.00m);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { pA.ProductId, pB.ProductId });

        await _stockCount.RecordCountItemAsync(session.SessionId, pA.ProductId, 18.0m, manager.UserId); // -2 delta
        await _stockCount.RecordCountItemAsync(session.SessionId, pB.ProductId, 13.0m, manager.UserId); // +3 delta

        var journal = await _stockCount.GenerateVarianceJournalAsync(session.SessionId);

        Assert.NotNull(journal);
        Assert.Equal(session.SessionId, journal.SessionId);
        Assert.Equal(2, journal.Lines.Count);

        var lineA = journal.Lines.First(l => l.ProductId == pA.ProductId);
        Assert.Equal(20.0m, lineA.SnapshotStock);
        Assert.Equal(18.0m, lineA.CountedQuantity);
        Assert.Equal(-2.0m, lineA.VarianceQuantity);
        Assert.Equal(-2000.00m, lineA.VarianceValue);

        var lineB = journal.Lines.First(l => l.ProductId == pB.ProductId);
        Assert.Equal(10.0m, lineB.SnapshotStock);
        Assert.Equal(13.0m, lineB.CountedQuantity);
        Assert.Equal(3.0m, lineB.VarianceQuantity);
        Assert.Equal(1500.00m, lineB.VarianceValue);

        Assert.Equal(1.0m, journal.TotalVarianceQuantity); // -2 + 3 = +1
        Assert.Equal(-500.00m, journal.TotalVarianceValue); // -2000 + 1500 = -500
    }

    [Fact]
    public async Task EmpiricalChallenge_FrozenSnapshotCutoff_Start10_Sale3_SnapshotRemains10()
    {
        // 1. Frozen Snapshot Cutoff: Start session with 10 units.
        // Simulate concurrent customer sales reducing stock to 7.
        // Verify session SnapshotStock remains 10.
        var manager = await _auth.CreateUserAsync($"mgr_snap_{Guid.NewGuid():N}", "Snapshot Mgr", "Pass#123", Role.Manager);
        var cashier = await _auth.CreateUserAsync($"cashier_snap_{Guid.NewGuid():N}", "Snapshot Cashier", "Pass#123", Role.Cashier);
        var product = await SeedProductAsync($"prod_snap_{Guid.NewGuid():N}", $"BAR_{Guid.NewGuid():N}", "Snapshot Oil Filter", 10.0m, 1200.00m);

        // Start session with 10 units
        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });

        var initialItem = session.Items.First(i => i.ProductId == product.ProductId);
        Assert.Equal(10.0m, initialItem.SnapshotStock);

        // Simulate concurrent customer sales reducing stock to 7
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");
        await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 3.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 10000.00m) }
        ));

        // Verify current stock is now 7
        var postSaleProd = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(7.0m, postSaleProd!.StockOnHand);

        // Verify session SnapshotStock remains 10
        var reloadedSession = await _stockCount.GetSessionByIdAsync(session.SessionId);
        Assert.NotNull(reloadedSession);
        var reloadedItem = reloadedSession.Items.First(i => i.ProductId == product.ProductId);
        Assert.Equal(10.0m, reloadedItem.SnapshotStock);
    }

    [Fact]
    public async Task EmpiricalChallenge_DeltaReconciliation_Start10_Sale3_Count8_FinalStockIs5()
    {
        // 2. Delta Reconciliation: Physical count entered is 8. Intervening sales were 3 (current stock 7).
        // Upon completion, final stock must be 7 + (8 - 10) = 5, NOT 8. Intervening sales must be preserved.
        var manager = await _auth.CreateUserAsync($"mgr_recon_{Guid.NewGuid():N}", "Recon Mgr", "Pass#123", Role.Manager);
        var cashier = await _auth.CreateUserAsync($"cashier_recon_{Guid.NewGuid():N}", "Recon Cashier", "Pass#123", Role.Cashier);
        var product = await SeedProductAsync($"prod_recon_{Guid.NewGuid():N}", $"BAR_{Guid.NewGuid():N}", "Brake Pad Set", 10.0m, 1500.00m);

        // Start session with 10 units
        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });

        // Concurrent sales of 3 units
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");
        await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 3.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 10000.00m) }
        ));

        // Current stock is now 7
        var currentProd = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(7.0m, currentProd!.StockOnHand);

        // Physical count entered is 8
        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, 8.0m, manager.UserId);

        var countedSession = await _stockCount.GetSessionByIdAsync(session.SessionId);
        var countedItem = countedSession!.Items.First(i => i.ProductId == product.ProductId);
        Assert.Equal(8.0m, countedItem.CountedQuantity);
        Assert.Equal(-2.0m, countedItem.VarianceQuantity); // 8 - 10 = -2

        // Complete session
        var completed = await _stockCount.CompleteSessionAsync(session.SessionId, manager.UserId);
        Assert.Equal(StockCountStatus.Completed, completed.Status);

        // Upon completion, final stock must be 7 + (8 - 10) = 5, NOT 8. Intervening sales must be preserved.
        var finalProduct = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.NotNull(finalProduct);
        Assert.Equal(5.0m, finalProduct.StockOnHand);
        Assert.NotEqual(8.0m, finalProduct.StockOnHand);
    }

    [Fact]
    public async Task EmpiricalChallenge_RoleAuthorization_CashierAttemptingCompletion_ThrowsUnauthorizedActionException()
    {
        // 3. Role Authorization Guard: Cashier attempting CompleteSessionAsync must be rejected with UnauthorizedActionException.
        var manager = await _auth.CreateUserAsync($"mgr_guard_{Guid.NewGuid():N}", "Guard Mgr", "Pass#123", Role.Manager);
        var cashier = await _auth.CreateUserAsync($"cashier_guard_{Guid.NewGuid():N}", "Guard Cashier", "Pass#123", Role.Cashier);
        var owner = await _auth.CreateUserAsync($"owner_guard_{Guid.NewGuid():N}", "Guard Owner", "Pass#123", Role.Owner);
        var product = await SeedProductAsync($"prod_guard_{Guid.NewGuid():N}", $"BAR_{Guid.NewGuid():N}", "Guard Spark Plug", 20.0m, 350.00m);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });
        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, 18.0m, cashier.UserId);

        // Cashier attempts to complete -> throws UnauthorizedActionException
        var ex = await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _stockCount.CompleteSessionAsync(session.SessionId, cashier.UserId)
        );
        Assert.Contains("Cashier role", ex.Message);
        Assert.Contains("Owner or Manager", ex.Message);

        // Verify session remains InProgress after unauthorized attempt
        var reloaded = await _stockCount.GetSessionByIdAsync(session.SessionId);
        Assert.Equal(StockCountStatus.InProgress, reloaded!.Status);

        // Verify Owner CAN complete session
        var ownerCompleted = await _stockCount.CompleteSessionAsync(session.SessionId, owner.UserId);
        Assert.Equal(StockCountStatus.Completed, ownerCompleted.Status);
        Assert.Equal(owner.UserId, ownerCompleted.CompletedBy);
    }

    [Fact]
    public async Task EmpiricalChallenge_CancellationIntegrity_LeavesStockIntactWithZeroAdjustments()
    {
        // 4. Cancellation Integrity: Cancelling session leaves stock intact with 0 adjustments.
        var manager = await _auth.CreateUserAsync($"mgr_cancel_{Guid.NewGuid():N}", "Cancel Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync($"prod_cancel_{Guid.NewGuid():N}", $"BAR_{Guid.NewGuid():N}", "Cancel Radiator Hose", 50.0m, 850.00m);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });

        // Record a physical count that has a huge variance (e.g., 20 instead of 50)
        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, 20.0m, manager.UserId);

        // Cancel session
        var cancelled = await _stockCount.CancelSessionAsync(session.SessionId, manager.UserId, "Discrepancy under investigation");
        Assert.Equal(StockCountStatus.Cancelled, cancelled.Status);
        Assert.Equal(manager.UserId, cancelled.CancelledBy);
        Assert.Equal("Discrepancy under investigation", cancelled.CancellationReason);

        // Product stock MUST remain 50.0m (0 adjustments!)
        var finalProduct = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(50.0m, finalProduct!.StockOnHand);

        // Verify 0 stock_movements records created for this session
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM stock_movements WHERE reference_id LIKE $sid;";
        cmd.Parameters.AddWithValue("$sid", $"%{session.SessionId}%");
        var movCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(0, movCount);
    }

    [Theory]
    // Tests where Banker's rounding (ToEven) would round towards even digit,
    // but MidpointRounding.AwayFromZero strictly rounds away from zero (half-up).
    // positive midpoint:
    [InlineData(10.0, 11.5, 0.07, 0.11)]    // variance = +1.5; 1.5 * 0.07 = 0.105 -> AwayFromZero: 0.11 (ToEven: 0.10)
    [InlineData(10.0, 12.5, 0.01, 0.03)]    // variance = +2.5; 2.5 * 0.01 = 0.025 -> AwayFromZero: 0.03 (ToEven: 0.02)
    [InlineData(10.0, 10.5, 0.09, 0.05)]    // variance = +0.5; 0.5 * 0.09 = 0.045 -> AwayFromZero: 0.05 (ToEven: 0.04)
    [InlineData(10.0, 10.5, 0.13, 0.07)]    // variance = +0.5; 0.5 * 0.13 = 0.065 -> AwayFromZero: 0.07 (ToEven: 0.06)
    // negative midpoint:
    [InlineData(10.0, 8.5, 0.07, -0.11)]    // variance = -1.5; -1.5 * 0.07 = -0.105 -> AwayFromZero: -0.11 (ToEven: -0.10)
    [InlineData(10.0, 7.5, 0.01, -0.03)]    // variance = -2.5; -2.5 * 0.01 = -0.025 -> AwayFromZero: -0.03 (ToEven: -0.02)
    [InlineData(10.0, 9.5, 0.09, -0.05)]    // variance = -0.5; -0.5 * 0.09 = -0.045 -> AwayFromZero: -0.05 (ToEven: -0.04)
    [InlineData(10.0, 9.5, 0.13, -0.07)]    // variance = -0.5; -0.5 * 0.13 = -0.065 -> AwayFromZero: -0.07 (ToEven: -0.06)
    public async Task EmpiricalChallenge_AwayFromZeroRounding_FractionalVarianceQuantities_StrictHalfUp(
        decimal snapshotStock, decimal countedQty, decimal costBasis, decimal expectedVarianceValue)
    {
        // 5. AwayFromZero Rounding: Fractional variance quantities multiplied by cost basis must strictly round half-up (MidpointRounding.AwayFromZero).
        var manager = await _auth.CreateUserAsync($"mgr_away_{Guid.NewGuid():N}", "Away Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync($"prod_away_{Guid.NewGuid():N}", $"BAR_{Guid.NewGuid():N}", "HalfUp Test Item", snapshotStock, costBasis);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });

        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, countedQty, manager.UserId);

        var updated = await _stockCount.GetSessionByIdAsync(session.SessionId);
        var item = updated!.Items.First(i => i.ProductId == product.ProductId);

        Assert.Equal(expectedVarianceValue, item.VarianceValue);
    }

    [Fact]
    public async Task EmpiricalChallenge_ConcurrentRestockAndSale_DeltaReconciliationPreservesBoth()
    {
        // Stress test: Concurrent restock (+10) and sale (-5) during count session
        var manager = await _auth.CreateUserAsync($"mgr_mix_{Guid.NewGuid():N}", "Mix Mgr", "Pass#123", Role.Manager);
        var cashier = await _auth.CreateUserAsync($"cashier_mix_{Guid.NewGuid():N}", "Mix Cashier", "Pass#123", Role.Cashier);
        var product = await SeedProductAsync($"prod_mix_{Guid.NewGuid():N}", $"BAR_{Guid.NewGuid():N}", "Mix Test Item", 20.0m, 500.00m);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });

        // Concurrent restock (+10)
        await _catalog.AdjustStockAsync(product.ProductId, 10.0m, "RESTOCK", manager, "TENANT_LK_01", "B01", "MAIN");

        // Concurrent sale (-5)
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");
        await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 5.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 10000.00m) }
        ));

        // Intervening stock = 20 + 10 - 5 = 25
        var currentProd = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(25.0m, currentProd!.StockOnHand);

        // Record physical count = 22
        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, 22.0m, manager.UserId);

        // Complete session: delta is 22 - 20 = +2
        await _stockCount.CompleteSessionAsync(session.SessionId, manager.UserId);

        var finalProduct = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(27.0m, finalProduct!.StockOnHand); // 25 + 2 = 27
    }

    [Fact]
    public async Task EmpiricalChallenge_RecountItem_OverwritesPreviousCountAndRecalculatesTotals()
    {
        var manager = await _auth.CreateUserAsync($"mgr_rc_{Guid.NewGuid():N}", "Recount Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync($"prod_rc_{Guid.NewGuid():N}", $"BAR_{Guid.NewGuid():N}", "Recount Item", 15.0m, 200.00m);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { product.ProductId });

        // First count: 12 (-3 variance)
        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, 12.0m, manager.UserId);
        var s1 = await _stockCount.GetSessionByIdAsync(session.SessionId);
        Assert.Equal(1, s1!.TotalItemsCounted);
        Assert.Equal(-3.0m, s1.TotalVarianceQuantity);
        Assert.Equal(-600.00m, s1.TotalVarianceValue);

        // Recount: 14 (-1 variance)
        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, 14.0m, manager.UserId);
        var s2 = await _stockCount.GetSessionByIdAsync(session.SessionId);
        Assert.Equal(1, s2!.TotalItemsCounted);
        Assert.Equal(-1.0m, s2.TotalVarianceQuantity);
        Assert.Equal(-200.00m, s2.TotalVarianceValue);
        Assert.Equal(1, s2.LinesWithVarianceCount);

        // Recount to exact match: 15 (0 variance)
        await _stockCount.RecordCountItemAsync(session.SessionId, product.ProductId, 15.0m, manager.UserId);
        var s3 = await _stockCount.GetSessionByIdAsync(session.SessionId);
        Assert.Equal(1, s3!.TotalItemsCounted);
        Assert.Equal(0.0m, s3.TotalVarianceQuantity);
        Assert.Equal(0.0m, s3.TotalVarianceValue);
        Assert.Equal(0, s3.LinesWithVarianceCount);
    }
}

