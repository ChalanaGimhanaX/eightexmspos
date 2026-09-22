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

/// <summary>
/// Empirical and adversarial stress tests for Milestone 3 Multi-Branch Transfers.
/// Verifies:
/// 1. In-Transit Isolation: Dispatched stock is deducted from source, but destination stock MUST NOT increase until explicitly received.
/// 2. Shortage Discrepancy: Dispatched 10, destination receives 7 -> destination stock increases by exactly 7, discrepancy of 3 is recorded, and HasDiscrepancy is true.
/// 3. Cancellation Rollback: Cancelling an in-transit transfer restores the exact quantity to the source branch.
/// 4. Terminal State Guards: Cannot receive an already received or cancelled transfer. Cannot cancel an already received transfer.
/// 5. Wrong Branch Security: Receiving branch ID mismatch must be rejected with an exception.
/// 6. Parameter Guards: Non-positive line quantities or empty lines must throw ArgumentException.
/// Plus concurrency race conditions, multi-database sync isolation, and duplicate product line edge cases.
/// </summary>
public class TransferTestsAdversarial : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly TransferService _transfer;

    public TransferTestsAdversarial()
    {
        _db = PosDatabase.CreateInMemory();
        EnsureTransferTablesExist(_db);
        _auth = new AuthService(_db);
        _catalog = new CatalogService(_db);
        _transfer = new TransferService(_db, _catalog);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private static void EnsureTransferTablesExist(PosDatabase db)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS transfers (
                transfer_id TEXT PRIMARY KEY,
                transfer_number TEXT UNIQUE NOT NULL,
                tenant_id TEXT NOT NULL DEFAULT 'TENANT_LK_01',
                source_branch_id TEXT NOT NULL,
                dest_branch_id TEXT NOT NULL,
                status INTEGER NOT NULL DEFAULT 2,
                dispatched_by TEXT NOT NULL,
                dispatched_at_utc TEXT NOT NULL,
                received_by TEXT,
                received_at_utc TEXT,
                cancelled_by TEXT,
                cancelled_at_utc TEXT,
                cancellation_reason TEXT,
                total_dispatched_quantity NUMERIC NOT NULL DEFAULT 0,
                total_received_quantity NUMERIC,
                has_discrepancy INTEGER NOT NULL DEFAULT 0,
                discrepancy_notes TEXT,
                notes TEXT,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS transfer_items (
                transfer_item_id TEXT PRIMARY KEY,
                transfer_id TEXT NOT NULL,
                product_id TEXT NOT NULL,
                product_name TEXT NOT NULL,
                barcode TEXT NOT NULL,
                dispatched_quantity NUMERIC NOT NULL,
                received_quantity NUMERIC,
                discrepancy_quantity NUMERIC NOT NULL DEFAULT 0,
                unit_cost NUMERIC NOT NULL DEFAULT 0,
                notes TEXT,
                FOREIGN KEY (transfer_id) REFERENCES transfers(transfer_id) ON DELETE CASCADE,
                FOREIGN KEY (product_id) REFERENCES products(product_id)
            );
        ";
        cmd.ExecuteNonQuery();
    }

    private async Task<Product> SeedProductAsync(string productId, string barcode, string name, decimal stockOnHand, decimal costBasis = 1000m)
    {
        var product = new Product
        {
            ProductId = productId,
            Barcode = barcode,
            Name = name,
            UnitPrice = costBasis * 1.35m,
            CostBasis = costBasis,
            StockOnHand = stockOnHand,
            IsActive = true
        };
        await _catalog.AddProductAsync(product);
        return product;
    }

    // =========================================================================
    // 1. IN-TRANSIT ISOLATION ADVERSARIAL TESTS
    // =========================================================================

    [Fact]
    public async Task InTransitIsolation_MultiItemTransfer_DeductsSource_LeavesDestinationUntouched_UntilReceived()
    {
        // Requirement 1: Dispatched stock is deducted from source, but destination stock MUST NOT increase until explicitly received.
        var mgr = await _auth.CreateUserAsync("adv_mgr_iso", "Iso Mgr", "Pass#123", Role.Manager);
        var p1 = await SeedProductAsync("prod_iso_1", "4791001", "Motor Oil 10W-40", 100.0m);
        var p2 = await SeedProductAsync("prod_iso_2", "4791002", "Oil Filter Premium", 50.0m);
        var p3 = await SeedProductAsync("prod_iso_3", "4791003", "Brake Pad Front Set", 25.0m);

        var lines = new List<TransferLineItem>
        {
            new() { ProductId = p1.ProductId, DispatchedQuantity = 30.0m },
            new() { ProductId = p2.ProductId, DispatchedQuantity = 15.0m },
            new() { ProductId = p3.ProductId, DispatchedQuantity = 5.0m }
        };

        // Act: Dispatch from Source B01 to Dest B02
        var transfer = await _transfer.DispatchTransferAsync(
            sourceBranchId: "B01",
            destBranchId: "B02",
            lines: lines,
            dispatchedBy: mgr.UserId,
            tenantId: "TENANT_LK_01"
        );

        // Assert Step 1: In-Transit Verification
        Assert.Equal(TransferStatus.InTransit, transfer.Status);
        Assert.Equal(50.0m, transfer.TotalDispatchedQuantity);
        Assert.Null(transfer.TotalReceivedQuantity);
        Assert.False(transfer.HasDiscrepancy);
        Assert.Null(transfer.ReceivedBy);
        Assert.Null(transfer.ReceivedAtUtc);

        // Assert Step 2: Source stock is strictly reduced
        var p1AfterDispatch = await _catalog.GetProductByIdAsync(p1.ProductId);
        var p2AfterDispatch = await _catalog.GetProductByIdAsync(p2.ProductId);
        var p3AfterDispatch = await _catalog.GetProductByIdAsync(p3.ProductId);
        Assert.Equal(70.0m, p1AfterDispatch!.StockOnHand); // 100 - 30
        Assert.Equal(35.0m, p2AfterDispatch!.StockOnHand); // 50 - 15
        Assert.Equal(20.0m, p3AfterDispatch!.StockOnHand); // 25 - 5

        // Assert Step 3: Stock movements only show TRANSFER_OUT (negative changes)
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM stock_movements WHERE reference_id = $ref AND quantity_change > 0;";
        cmd.Parameters.AddWithValue("$ref", transfer.TransferId);
        var positiveMovementsCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(0, positiveMovementsCount); // Zero incoming movements while in-transit

        // Act Step 4: Now explicitly receive at B02
        var receivedTransfer = await _transfer.ReceiveTransferAsync(transfer.TransferId, "B02", mgr.UserId);

        // Assert Step 5: Destination stock is now incremented
        Assert.Equal(TransferStatus.Received, receivedTransfer.Status);
        Assert.Equal(50.0m, receivedTransfer.TotalReceivedQuantity);
        var p1AfterReceive = await _catalog.GetProductByIdAsync(p1.ProductId);
        var p2AfterReceive = await _catalog.GetProductByIdAsync(p2.ProductId);
        var p3AfterReceive = await _catalog.GetProductByIdAsync(p3.ProductId);
        Assert.Equal(100.0m, p1AfterReceive!.StockOnHand); // 70 + 30
        Assert.Equal(50.0m, p2AfterReceive!.StockOnHand);  // 35 + 15
        Assert.Equal(25.0m, p3AfterReceive!.StockOnHand);  // 20 + 5
    }

    [Fact]
    public async Task InTransitIsolation_TwoIndependentBranchDatabases_SimulatedSync()
    {
        // Real-world offline POS scenario: Branch 1 and Branch 2 have independent databases
        using var dbBranch1 = PosDatabase.CreateInMemory();
        EnsureTransferTablesExist(dbBranch1);
        var catalog1 = new CatalogService(dbBranch1);
        var transferService1 = new TransferService(dbBranch1, catalog1);

        using var dbBranch2 = PosDatabase.CreateInMemory();
        EnsureTransferTablesExist(dbBranch2);
        var catalog2 = new CatalogService(dbBranch2);
        var transferService2 = new TransferService(dbBranch2, catalog2);

        // Seed product in Branch 1 (Stock = 50) and Branch 2 (Stock = 10)
        var p1InB1 = new Product { ProductId = "P_OIL_01", Barcode = "9901", Name = "Castrol 20W50", UnitPrice = 5000m, CostBasis = 4000m, StockOnHand = 50m, IsActive = true };
        await catalog1.AddProductAsync(p1InB1);

        var p1InB2 = new Product { ProductId = "P_OIL_01", Barcode = "9901", Name = "Castrol 20W50", UnitPrice = 5000m, CostBasis = 4000m, StockOnHand = 10m, IsActive = true };
        await catalog2.AddProductAsync(p1InB2);

        // Dispatch 15 units from Branch 1 to Branch 2
        var dispatch = await transferService1.DispatchTransferAsync(
            sourceBranchId: "B01",
            destBranchId: "B02",
            lines: new List<TransferLineItem> { new() { ProductId = "P_OIL_01", DispatchedQuantity = 15m } },
            dispatchedBy: "mgr_b01",
            tenantId: "TENANT_LK_01"
        );

        // In Branch 1: Stock is 35
        var b1Stock = await catalog1.GetProductByIdAsync("P_OIL_01");
        Assert.Equal(35m, b1Stock!.StockOnHand);

        // In Branch 2: Stock MUST REMAIN 10 (untouched, goods in transit)
        var b2StockBeforeReceive = await catalog2.GetProductByIdAsync("P_OIL_01");
        Assert.Equal(10m, b2StockBeforeReceive!.StockOnHand);

        // Simulate sync payload arriving at Branch 2: Replicate transfer header and lines into Branch 2 database
        using (var c2 = dbBranch2.CreateConnection())
        {
            using var insertCmd = c2.CreateCommand();
            insertCmd.CommandText = @"
                INSERT INTO transfers (transfer_id, transfer_number, tenant_id, source_branch_id, dest_branch_id, status, dispatched_by, dispatched_at_utc, total_dispatched_quantity, has_discrepancy, created_at_utc, updated_at_utc)
                VALUES ($id, $num, $tid, $src, $dst, 2, $dby, $dat, 15, 0, $cat, $uat);
                INSERT INTO transfer_items (transfer_item_id, transfer_id, product_id, product_name, barcode, dispatched_quantity, discrepancy_quantity, unit_cost)
                VALUES ($lid, $id, 'P_OIL_01', 'Castrol 20W50', '9901', 15, 0, 4000);
            ";
            insertCmd.Parameters.AddWithValue("$id", dispatch.TransferId);
            insertCmd.Parameters.AddWithValue("$num", dispatch.TransferNumber);
            insertCmd.Parameters.AddWithValue("$tid", dispatch.TenantId);
            insertCmd.Parameters.AddWithValue("$src", dispatch.SourceBranchId);
            insertCmd.Parameters.AddWithValue("$dst", dispatch.DestBranchId);
            insertCmd.Parameters.AddWithValue("$dby", dispatch.DispatchedBy);
            insertCmd.Parameters.AddWithValue("$dat", dispatch.DispatchedAtUtc.ToString("o"));
            insertCmd.Parameters.AddWithValue("$cat", dispatch.CreatedAtUtc.ToString("o"));
            insertCmd.Parameters.AddWithValue("$uat", dispatch.UpdatedAtUtc.ToString("o"));
            insertCmd.Parameters.AddWithValue("$lid", Guid.NewGuid().ToString());
            insertCmd.ExecuteNonQuery();
        }

        // Branch 2 receives the transfer
        var received = await transferService2.ReceiveTransferAsync(dispatch.TransferId, "B02", "mgr_b02");
        Assert.Equal(TransferStatus.Received, received.Status);

        // Branch 2 stock is now 10 + 15 = 25
        var b2StockAfterReceive = await catalog2.GetProductByIdAsync("P_OIL_01");
        Assert.Equal(25m, b2StockAfterReceive!.StockOnHand);

        // Branch 1 stock remains at 35
        var b1StockAfterReceive = await catalog1.GetProductByIdAsync("P_OIL_01");
        Assert.Equal(35m, b1StockAfterReceive!.StockOnHand);
    }

    // =========================================================================
    // 2. SHORTAGE DISCREPANCY ADVERSARIAL TESTS
    // =========================================================================

    [Fact]
    public async Task ShortageDiscrepancy_Dispatched10_Receives7_DestinationIncrementsBy7_DiscrepancyRecorded()
    {
        // Requirement 2: Dispatched 10, destination receives 7 -> destination stock increases by exactly 7, discrepancy of 3 is recorded, and HasDiscrepancy is true.
        var mgr = await _auth.CreateUserAsync("adv_mgr_disc1", "Disc Mgr 1", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_disc_10_7", "4792001", "Transmission Fluid ATF", 50.0m);

        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 10.0m } },
            mgr.UserId, "TENANT_LK_01"
        );

        // Source stock is 40.0m
        var pAfterDispatch = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(40.0m, pAfterDispatch!.StockOnHand);

        // Receive only 7
        var received = await _transfer.ReceiveTransferAsync(
            transferId: transfer.TransferId,
            receivingBranchId: "B02",
            receivedBy: mgr.UserId,
            receivedQuantities: new Dictionary<string, decimal> { { product.ProductId, 7.0m } }
        );

        // Assert Transfer Level
        Assert.Equal(TransferStatus.Received, received.Status);
        Assert.True(received.HasDiscrepancy);
        Assert.Equal(10.0m, received.TotalDispatchedQuantity);
        Assert.Equal(7.0m, received.TotalReceivedQuantity);
        Assert.NotNull(received.DiscrepancyNotes);
        Assert.Contains("diff: -3", received.DiscrepancyNotes);

        // Assert Line Level
        var line = received.Lines.Single();
        Assert.Equal(10.0m, line.DispatchedQuantity);
        Assert.Equal(7.0m, line.ReceivedQuantity);
        Assert.Equal(3.0m, line.DiscrepancyQuantity);

        // Assert Stock Increment is exactly 7 (not 10!)
        var pAfterReceive = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(47.0m, pAfterReceive!.StockOnHand); // 40 + 7 = 47

        // Assert Stock Movement Table
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT quantity_change FROM stock_movements WHERE reference_id = $ref AND quantity_change > 0;";
        cmd.Parameters.AddWithValue("$ref", transfer.TransferId);
        var incomingMovement = Convert.ToDecimal(await cmd.ExecuteScalarAsync());
        Assert.Equal(7.0m, incomingMovement);
    }

    [Fact]
    public async Task ShortageDiscrepancy_TotalLoss_ZeroReceived_StockDoesNotIncrement_DiscrepancyRecorded()
    {
        // Adversarial scenario: All 5 items broken/lost in transit -> received 0
        var mgr = await _auth.CreateUserAsync("adv_mgr_loss", "Loss Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_glass_lens", "4792002", "Headlamp Glass Lens", 20.0m);

        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 5.0m } },
            mgr.UserId, "TENANT_LK_01"
        );

        // Stock dropped to 15.0m
        var pAfterDispatch = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(15.0m, pAfterDispatch!.StockOnHand);

        // Destination receives 0
        var received = await _transfer.ReceiveTransferAsync(
            transferId: transfer.TransferId,
            receivingBranchId: "B02",
            receivedBy: mgr.UserId,
            receivedQuantities: new Dictionary<string, decimal> { { product.ProductId, 0.0m } }
        );

        Assert.Equal(TransferStatus.Received, received.Status);
        Assert.True(received.HasDiscrepancy);
        Assert.Equal(5.0m, received.TotalDispatchedQuantity);
        Assert.Equal(0.0m, received.TotalReceivedQuantity);

        var line = received.Lines.Single();
        Assert.Equal(0.0m, line.ReceivedQuantity);
        Assert.Equal(5.0m, line.DiscrepancyQuantity);

        // Destination stock has NOT incremented (remains 15.0m)
        var pAfterReceive = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(15.0m, pAfterReceive!.StockOnHand);

        // Stock movements: No positive movement created
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM stock_movements WHERE reference_id = $ref AND quantity_change > 0;";
        cmd.Parameters.AddWithValue("$ref", transfer.TransferId);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ShortageDiscrepancy_FractionalHighPrecisionQuantities()
    {
        // Sri Lankan bulk items (petrol, oil, grains) with fractional decimal quantities
        var mgr = await _auth.CreateUserAsync("adv_mgr_frac", "Frac Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_bulk_coolant", "4792003", "Coolant Bulk Drum", 100.000m);

        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 18.750m } },
            mgr.UserId, "TENANT_LK_01"
        );

        // Receive 14.250m (shortage of 4.500m)
        var received = await _transfer.ReceiveTransferAsync(
            transfer.TransferId, "B02", mgr.UserId,
            new Dictionary<string, decimal> { { product.ProductId, 14.250m } }
        );

        Assert.True(received.HasDiscrepancy);
        Assert.Equal(18.750m, received.TotalDispatchedQuantity);
        Assert.Equal(14.250m, received.TotalReceivedQuantity);
        Assert.Equal(4.500m, received.Lines[0].DiscrepancyQuantity);

        var p = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(95.500m, p!.StockOnHand); // 100 - 18.750 + 14.250 = 95.500
    }

    // =========================================================================
    // 3. CANCELLATION ROLLBACK ADVERSARIAL TESTS
    // =========================================================================

    [Fact]
    public async Task CancellationRollback_MultiProduct_ExactQuantityRestoredToSource()
    {
        // Requirement 3: Cancelling an in-transit transfer restores the exact quantity to the source branch.
        var mgr = await _auth.CreateUserAsync("adv_mgr_cancel", "Cancel Mgr", "Pass#123", Role.Manager);
        var p1 = await SeedProductAsync("prod_rb_1", "4793001", "Drive Belt", 80.0m);
        var p2 = await SeedProductAsync("prod_rb_2", "4793002", "Piston Ring Set", 40.0m);
        var p3 = await SeedProductAsync("prod_rb_3", "4793003", "Valve Seal", 120.0m);

        var transfer = await _transfer.DispatchTransferAsync(
            sourceBranchId: "B01",
            destBranchId: "B02",
            lines: new List<TransferLineItem>
            {
                new() { ProductId = p1.ProductId, DispatchedQuantity = 12.5m },
                new() { ProductId = p2.ProductId, DispatchedQuantity = 7.0m },
                new() { ProductId = p3.ProductId, DispatchedQuantity = 35.0m }
            },
            dispatchedBy: mgr.UserId,
            tenantId: "TENANT_LK_01"
        );

        // Verify stock dropped after dispatch
        Assert.Equal(67.5m, (await _catalog.GetProductByIdAsync(p1.ProductId))!.StockOnHand);
        Assert.Equal(33.0m, (await _catalog.GetProductByIdAsync(p2.ProductId))!.StockOnHand);
        Assert.Equal(85.0m, (await _catalog.GetProductByIdAsync(p3.ProductId))!.StockOnHand);

        // Act: Cancel Transfer
        var cancelled = await _transfer.CancelTransferAsync(
            transferId: transfer.TransferId,
            cancelledBy: mgr.UserId,
            reason: "Logistics truck breakdown; returning all parts to shelf"
        );

        // Assert Transfer Header
        Assert.Equal(TransferStatus.Cancelled, cancelled.Status);
        Assert.Equal(mgr.UserId, cancelled.CancelledBy);
        Assert.NotNull(cancelled.CancelledAtUtc);
        Assert.Equal("Logistics truck breakdown; returning all parts to shelf", cancelled.CancellationReason);

        // Assert Exact Stock Restored
        Assert.Equal(80.0m, (await _catalog.GetProductByIdAsync(p1.ProductId))!.StockOnHand);
        Assert.Equal(40.0m, (await _catalog.GetProductByIdAsync(p2.ProductId))!.StockOnHand);
        Assert.Equal(120.0m, (await _catalog.GetProductByIdAsync(p3.ProductId))!.StockOnHand);

        // Assert Compensating Movements in DB
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT movement_type, quantity_change FROM stock_movements
            WHERE reference_id = $ref AND movement_type = 'TRANSFER_CANCEL_RETURN'
            ORDER BY quantity_change;
        ";
        cmd.Parameters.AddWithValue("$ref", transfer.TransferId);
        using var reader = await cmd.ExecuteReaderAsync();
        var movements = new List<decimal>();
        while (await reader.ReadAsync())
        {
            movements.Add(reader.GetDecimal(1));
        }
        Assert.Equal(3, movements.Count);
        Assert.Contains(7.0m, movements);
        Assert.Contains(12.5m, movements);
        Assert.Contains(35.0m, movements);
    }

    // =========================================================================
    // 4. TERMINAL STATE GUARDS ADVERSARIAL TESTS
    // =========================================================================

    [Fact]
    public async Task TerminalStateGuards_CannotReceiveAlreadyReceivedTransfer()
    {
        // Requirement 4: Cannot receive an already received transfer
        var mgr = await _auth.CreateUserAsync("adv_mgr_term1", "Term Mgr 1", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_term_1", "4794001", "Thermostat Valve", 15.0m);

        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 5.0m } },
            mgr.UserId, "TENANT_LK_01"
        );

        // First receive succeeds
        var r1 = await _transfer.ReceiveTransferAsync(transfer.TransferId, "B02", mgr.UserId);
        Assert.Equal(TransferStatus.Received, r1.Status);
        var stockAfterFirstReceive = (await _catalog.GetProductByIdAsync(product.ProductId))!.StockOnHand;
        Assert.Equal(15.0m, stockAfterFirstReceive); // 10 + 5 = 15

        // Second receive must throw InvalidOperationException
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _transfer.ReceiveTransferAsync(transfer.TransferId, "B02", mgr.UserId)
        );
        Assert.Contains("already been received", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Verify stock was NOT double-credited
        var stockAfterFailedSecondReceive = (await _catalog.GetProductByIdAsync(product.ProductId))!.StockOnHand;
        Assert.Equal(15.0m, stockAfterFailedSecondReceive);
    }

    [Fact]
    public async Task TerminalStateGuards_CannotCancelAlreadyReceivedTransfer()
    {
        // Requirement 4: Cannot cancel an already received transfer
        var mgr = await _auth.CreateUserAsync("adv_mgr_term2", "Term Mgr 2", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_term_2", "4794002", "Radiator Cap", 20.0m);

        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 4.0m } },
            mgr.UserId, "TENANT_LK_01"
        );

        // Receive transfer
        await _transfer.ReceiveTransferAsync(transfer.TransferId, "B02", mgr.UserId);

        // Attempt cancel must fail
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _transfer.CancelTransferAsync(transfer.TransferId, mgr.UserId, "Trying to cancel received goods")
        );
        Assert.Contains("already been received and cannot be cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Stock should remain unchanged (20.0m)
        var stock = (await _catalog.GetProductByIdAsync(product.ProductId))!.StockOnHand;
        Assert.Equal(20.0m, stock);
    }

    [Fact]
    public async Task TerminalStateGuards_CannotReceiveAlreadyCancelledTransfer()
    {
        // Requirement 4: Cannot receive an already cancelled transfer
        var mgr = await _auth.CreateUserAsync("adv_mgr_term3", "Term Mgr 3", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_term_3", "4794003", "Wiper Blade 22 inch", 30.0m);

        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 8.0m } },
            mgr.UserId, "TENANT_LK_01"
        );

        // Cancel transfer
        await _transfer.CancelTransferAsync(transfer.TransferId, mgr.UserId, "Branch cancelled request");

        // Attempt receive must fail
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _transfer.ReceiveTransferAsync(transfer.TransferId, "B02", mgr.UserId)
        );
        Assert.Contains("cancelled and cannot be received", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Stock remains at 30.0m (source stock restored upon cancel, dest stock never credited)
        var stock = (await _catalog.GetProductByIdAsync(product.ProductId))!.StockOnHand;
        Assert.Equal(30.0m, stock);
    }

    [Fact]
    public async Task TerminalStateGuards_CannotCancelAlreadyCancelledTransfer()
    {
        // Requirement 4: Cannot cancel an already cancelled transfer
        var mgr = await _auth.CreateUserAsync("adv_mgr_term4", "Term Mgr 4", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_term_4", "4794004", "Wheel Nut 19mm", 50.0m);

        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 10.0m } },
            mgr.UserId, "TENANT_LK_01"
        );

        // First cancel succeeds
        await _transfer.CancelTransferAsync(transfer.TransferId, mgr.UserId, "Order error");

        // Second cancel must fail
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _transfer.CancelTransferAsync(transfer.TransferId, mgr.UserId, "Second cancel attempt")
        );
        Assert.Contains("already cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Stock restored once to 50.0m, not duplicated to 60.0m
        var stock = (await _catalog.GetProductByIdAsync(product.ProductId))!.StockOnHand;
        Assert.Equal(50.0m, stock);
    }

    // =========================================================================
    // 5. WRONG BRANCH SECURITY ADVERSARIAL TESTS
    // =========================================================================

    [Fact]
    public async Task WrongBranchSecurity_MismatchReceivingBranch_MustThrowException_LeavesStateUntouched()
    {
        // Requirement 5: Receiving branch ID mismatch must be rejected with an exception
        var mgr = await _auth.CreateUserAsync("adv_mgr_sec1", "Sec Mgr 1", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_sec_1", "4795001", "Brake Disc Rotor", 20.0m);

        var transfer = await _transfer.DispatchTransferAsync(
            sourceBranchId: "B01_COLOMBO",
            destBranchId: "B02_KANDY",
            lines: new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 6.0m } },
            dispatchedBy: mgr.UserId,
            tenantId: "TENANT_LK_01"
        );

        // Branch B03_GALLE attempts unauthorized receipt of B02's shipment
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _transfer.ReceiveTransferAsync(transfer.TransferId, "B03_GALLE", mgr.UserId)
        );
        Assert.Contains("B02_KANDY", ex.Message);
        Assert.Contains("B03_GALLE", ex.Message);

        // Verify transfer state in DB is STILL InTransit
        var fetched = await _transfer.GetTransferByIdAsync(transfer.TransferId);
        Assert.NotNull(fetched);
        Assert.Equal(TransferStatus.InTransit, fetched.Status);
        Assert.Null(fetched.ReceivedBy);

        // Verify stock remains 14.0m (not credited)
        var stock = (await _catalog.GetProductByIdAsync(product.ProductId))!.StockOnHand;
        Assert.Equal(14.0m, stock);
    }

    [Fact]
    public async Task WrongBranchSecurity_CaseInsensitiveAndWhitespaceTrimmedMatchAllowed()
    {
        // Security check: " b02_kandy " should match "B02_KANDY" cleanly
        var mgr = await _auth.CreateUserAsync("adv_mgr_sec2", "Sec Mgr 2", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_sec_2", "4795002", "Timing Belt Kit", 10.0m);

        var transfer = await _transfer.DispatchTransferAsync(
            sourceBranchId: "B01_COLOMBO",
            destBranchId: "B02_KANDY",
            lines: new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 2.0m } },
            dispatchedBy: mgr.UserId,
            tenantId: "TENANT_LK_01"
        );

        var received = await _transfer.ReceiveTransferAsync(transfer.TransferId, "  b02_kandy  ", mgr.UserId);
        Assert.Equal(TransferStatus.Received, received.Status);
    }

    // =========================================================================
    // 6. PARAMETER GUARDS ADVERSARIAL TESTS
    // =========================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(-0.001)]
    [InlineData(-100)]
    public async Task ParameterGuards_NonPositiveQuantity_ThrowsArgumentException(decimal invalidQty)
    {
        // Requirement 6: Non-positive line quantities must throw ArgumentException
        var mgr = await _auth.CreateUserAsync($"mgr_p1_{Guid.NewGuid():N}", "Param Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync($"prod_p1_{Guid.NewGuid():N}", "4796001", "Test Part", 20.0m);

        var lines = new List<TransferLineItem>
        {
            new() { ProductId = product.ProductId, DispatchedQuantity = invalidQty }
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _transfer.DispatchTransferAsync("B01", "B02", lines, mgr.UserId, "TENANT_LK_01")
        );
        Assert.Contains("Dispatched quantity must be greater than zero", ex.Message);
    }

    [Fact]
    public async Task ParameterGuards_EmptyOrNullLines_ThrowsArgumentException()
    {
        // Requirement 6: Empty lines must throw ArgumentException
        var mgr = await _auth.CreateUserAsync("mgr_p2", "Param Mgr 2", "Pass#123", Role.Manager);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _transfer.DispatchTransferAsync("B01", "B02", new List<TransferLineItem>(), mgr.UserId, "TENANT_LK_01")
        );

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _transfer.DispatchTransferAsync("B01", "B02", null!, mgr.UserId, "TENANT_LK_01")
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ParameterGuards_EmptyBranchIds_ThrowsArgumentException(string? invalidBranch)
    {
        var mgr = await _auth.CreateUserAsync($"mgr_p3_{Guid.NewGuid():N}", "Param Mgr 3", "Pass#123", Role.Manager);
        var product = await SeedProductAsync($"prod_p3_{Guid.NewGuid():N}", "4796002", "Test Part 2", 10.0m);
        var lines = new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 1.0m } };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _transfer.DispatchTransferAsync(invalidBranch!, "B02", lines, mgr.UserId, "TENANT_LK_01")
        );

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _transfer.DispatchTransferAsync("B01", invalidBranch!, lines, mgr.UserId, "TENANT_LK_01")
        );
    }

    [Fact]
    public async Task ParameterGuards_SameSourceAndDestBranch_ThrowsArgumentException()
    {
        var mgr = await _auth.CreateUserAsync("mgr_p4", "Param Mgr 4", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_p4", "4796003", "Test Part 3", 10.0m);
        var lines = new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 1.0m } };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _transfer.DispatchTransferAsync("B01", "  b01  ", lines, mgr.UserId, "TENANT_LK_01")
        );
        Assert.Contains("cannot be the same", ex.Message);
    }

    [Fact]
    public async Task ParameterGuards_ReceiveNegativeQuantity_ThrowsArgumentException()
    {
        var mgr = await _auth.CreateUserAsync("mgr_p5", "Param Mgr 5", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_p5", "4796004", "Test Part 4", 10.0m);
        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 5.0m } },
            mgr.UserId, "TENANT_LK_01"
        );

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _transfer.ReceiveTransferAsync(
                transfer.TransferId, "B02", mgr.UserId,
                new Dictionary<string, decimal> { { product.ProductId, -3.0m } }
            )
        );
        Assert.Contains("Received quantity cannot be negative", ex.Message);
    }

    [Fact]
    public async Task ParameterGuards_NonExistentTransfer_ThrowsKeyNotFoundException()
    {
        var mgr = await _auth.CreateUserAsync("mgr_p6", "Param Mgr 6", "Pass#123", Role.Manager);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _transfer.ReceiveTransferAsync("NON_EXISTENT_TRF_ID", "B02", mgr.UserId)
        );

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _transfer.CancelTransferAsync("NON_EXISTENT_TRF_ID", mgr.UserId, "reason")
        );
    }

    [Fact]
    public async Task ParameterGuards_NonExistentProduct_ThrowsKeyNotFoundException()
    {
        var mgr = await _auth.CreateUserAsync("mgr_p7", "Param Mgr 7", "Pass#123", Role.Manager);

        var lines = new List<TransferLineItem>
        {
            new() { ProductId = "GHOST_PROD_ID", DispatchedQuantity = 5.0m }
        };

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _transfer.DispatchTransferAsync("B01", "B02", lines, mgr.UserId, "TENANT_LK_01")
        );
    }

    // =========================================================================
    // 7. CONCURRENCY & RACE CONDITION ADVERSARIAL STRESS TESTS
    // =========================================================================

    [Fact]
    public async Task Concurrency_DoubleReceiveRace_EmpiricalObservation_ValidatesAtLeastOneSuccess()
    {
        // Stress test: 5 concurrent receive attempts on the same transfer
        var mgr = await _auth.CreateUserAsync("mgr_race1", "Race Mgr 1", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_race_1", "4797001", "Shock Absorber Rear", 50.0m);

        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 10.0m } },
            mgr.UserId, "TENANT_LK_01"
        );

        // Stock after dispatch: 40.0m
        Assert.Equal(40.0m, (await _catalog.GetProductByIdAsync(product.ProductId))!.StockOnHand);

        // Launch 5 concurrent receives
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            try
            {
                await _transfer.ReceiveTransferAsync(transfer.TransferId, "B02", mgr.UserId);
                return true; // Success
            }
            catch (Exception)
            {
                return false; // Rejected due to terminal state guard or busy
            }
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r);

        // At least 1 receive must succeed
        Assert.True(successCount >= 1, "At least one receive must succeed");

        // The final status of the transfer in the database must be Received
        var finalTransfer = await _transfer.GetTransferByIdAsync(transfer.TransferId);
        Assert.NotNull(finalTransfer);
        Assert.Equal(TransferStatus.Received, finalTransfer.Status);
    }

    [Fact]
    public async Task Concurrency_ReceiveVsCancelRace_ConsistentTerminalStateReached()
    {
        // Stress test: A concurrent receive and cancel executed simultaneously
        var mgr = await _auth.CreateUserAsync("mgr_race2", "Race Mgr 2", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_race_2", "4797002", "Tie Rod End", 30.0m);

        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 6.0m } },
            mgr.UserId, "TENANT_LK_01"
        );

        // Launch concurrent Receive and Cancel
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await _transfer.ReceiveTransferAsync(transfer.TransferId, "B02", mgr.UserId);
                return "RECEIVED";
            }
            catch (Exception ex)
            {
                return $"RECV_FAILED: {ex.Message}";
            }
        });

        var cancelTask = Task.Run(async () =>
        {
            try
            {
                await _transfer.CancelTransferAsync(transfer.TransferId, mgr.UserId, "Race cancel");
                return "CANCELLED";
            }
            catch (Exception ex)
            {
                return $"CANCEL_FAILED: {ex.Message}";
            }
        });

        await Task.WhenAll(receiveTask, cancelTask);
        var finalTransfer = await _transfer.GetTransferByIdAsync(transfer.TransferId);

        Assert.NotNull(finalTransfer);
        // Status must be either Received or Cancelled, NEVER InTransit
        Assert.True(finalTransfer.Status == TransferStatus.Received || finalTransfer.Status == TransferStatus.Cancelled);
    }

    [Fact]
    public async Task DuplicateProductLines_DispatchedSuccessfully_DemonstratesDictionaryKeyLimitationInReceive()
    {
        // Edge case: Multiple transfer lines containing the same product ID
        var mgr = await _auth.CreateUserAsync("mgr_dup_lines", "Dup Line Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_dup_line", "4797004", "Spark Plug Box", 50.0m);

        var lines = new List<TransferLineItem>
        {
            new() { ProductId = product.ProductId, DispatchedQuantity = 5.0m, Notes = "Box 1" },
            new() { ProductId = product.ProductId, DispatchedQuantity = 3.0m, Notes = "Box 2" }
        };

        var transfer = await _transfer.DispatchTransferAsync("B01", "B02", lines, mgr.UserId, "TENANT_LK_01");
        Assert.Equal(8.0m, transfer.TotalDispatchedQuantity);
        Assert.Equal(42.0m, (await _catalog.GetProductByIdAsync(product.ProductId))!.StockOnHand);

        // When receiving with default quantities (null receivedQuantities), both lines receive their dispatched quantities
        var received = await _transfer.ReceiveTransferAsync(transfer.TransferId, "B02", mgr.UserId);
        Assert.Equal(8.0m, received.TotalReceivedQuantity);
        Assert.Equal(50.0m, (await _catalog.GetProductByIdAsync(product.ProductId))!.StockOnHand);
    }

    // =========================================================================
    // 8. AUDIT TRAIL VERIFICATION
    // =========================================================================

    [Fact]
    public async Task AuditTrail_LifecycleEventsLogged_DispatchedReceivedCancelled()
    {
        var mgr = await _auth.CreateUserAsync("mgr_audit", "Audit Mgr", "Pass#123", Role.Manager);
        var p1 = await SeedProductAsync("prod_aud_1", "4798001", "Bulb H4", 50.0m);
        var p2 = await SeedProductAsync("prod_aud_2", "4798002", "Bulb H1", 50.0m);

        // 1. Dispatch & Receive
        var t1 = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = p1.ProductId, DispatchedQuantity = 5.0m } },
            mgr.UserId, "TENANT_LK_01"
        );
        await _transfer.ReceiveTransferAsync(t1.TransferId, "B02", mgr.UserId);

        // 2. Dispatch & Cancel
        var t2 = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = p2.ProductId, DispatchedQuantity = 3.0m } },
            mgr.UserId, "TENANT_LK_01"
        );
        await _transfer.CancelTransferAsync(t2.TransferId, mgr.UserId, "Cancel test");

        // Verify audit_events entries
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT action FROM audit_events WHERE tenant_id = 'TENANT_LK_01' ORDER BY occurred_at_utc ASC;";
        using var reader = await cmd.ExecuteReaderAsync();
        var actions = new List<string>();
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetString(0));
        }

        Assert.Contains("TRANSFER_DISPATCHED", actions);
        Assert.Contains("TRANSFER_RECEIVED", actions);
        Assert.Contains("TRANSFER_CANCELLED", actions);
    }
}
