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

public class TransferTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly TransferService _transfer;

    public TransferTests()
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

    [Fact]
    public async Task DispatchTransfer_ValidLines_CreatesInTransitRecord_DeductsSourceStock_AndLogsStockMovement()
    {
        // Arrange
        var manager = await _auth.CreateUserAsync("mgr_colombo", "Colombo Manager", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_engine_oil", "4790001", "Synthetic Engine Oil 4L", 50.0m, 6500.00m);

        var lines = new List<TransferLineItem>
        {
            new()
            {
                ProductId = product.ProductId,
                DispatchedQuantity = 10.0m
            }
        };

        // Act
        var transfer = await _transfer.DispatchTransferAsync(
            sourceBranchId: "B01",
            destBranchId: "B02",
            lines: lines,
            dispatchedBy: manager.UserId,
            tenantId: "TENANT_LK_01",
            notes: "Inter-branch weekly replenishment"
        );

        // Assert Domain Model
        Assert.NotNull(transfer);
        Assert.False(string.IsNullOrWhiteSpace(transfer.TransferId));
        Assert.StartsWith("TRF-", transfer.TransferNumber);
        Assert.Equal("B01", transfer.SourceBranchId);
        Assert.Equal("B02", transfer.DestBranchId);
        Assert.Equal(TransferStatus.InTransit, transfer.Status);
        Assert.Equal(manager.UserId, transfer.DispatchedBy);
        Assert.Null(transfer.ReceivedBy);
        Assert.Null(transfer.ReceivedAtUtc);
        Assert.Equal(10.0m, transfer.TotalDispatchedQuantity);
        Assert.Single(transfer.Lines);
        Assert.Equal(product.ProductId, transfer.Lines[0].ProductId);
        Assert.Equal(10.0m, transfer.Lines[0].DispatchedQuantity);

        // Assert Source Stock Deducted
        var updatedProduct = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.NotNull(updatedProduct);
        Assert.Equal(40.0m, updatedProduct.StockOnHand); // 50 - 10 = 40

        // Assert SQLite Database Persistence
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, total_dispatched_quantity FROM transfers WHERE transfer_id = $id;";
        cmd.Parameters.AddWithValue("$id", transfer.TransferId);
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((int)TransferStatus.InTransit, reader.GetInt32(0));
        Assert.Equal(10.0m, reader.GetDecimal(1));

        // Assert stock_movements logged TRANSFER_OUT
        using var smCmd = conn.CreateCommand();
        smCmd.CommandText = "SELECT movement_type, quantity_change FROM stock_movements WHERE product_id = $pid AND reference_id = $ref;";
        smCmd.Parameters.AddWithValue("$pid", product.ProductId);
        smCmd.Parameters.AddWithValue("$ref", transfer.TransferId);
        using var smReader = await smCmd.ExecuteReaderAsync();
        Assert.True(await smReader.ReadAsync(), "Stock movement must be logged for dispatch.");
        var mType = smReader.GetString(0);
        Assert.True(mType == "TRANSFER_OUT" || mType == "TRANSFER_DISPATCH");
        Assert.Equal(-10.0m, smReader.GetDecimal(1));
    }

    [Fact]
    public async Task DispatchTransfer_FunctionalGuardrail_TransferRemainsInTransit_UntilDestinationReceives()
    {
        // Guardrail: "Transfers stay in in-transit status until explicitly received at destination branch."
        var manager = await _auth.CreateUserAsync("mgr_kandy", "Kandy Manager", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_brake_fluid", "4790002", "Brake Fluid DOT4", 30.0m, 1200.00m);

        var transfer = await _transfer.DispatchTransferAsync(
            sourceBranchId: "B01",
            destBranchId: "B02",
            lines: new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 5.0m } },
            dispatchedBy: manager.UserId,
            tenantId: "TENANT_LK_01"
        );

        // Verification: Transfer remains InTransit
        var fetched = await _transfer.GetTransferByIdAsync(transfer.TransferId);
        Assert.NotNull(fetched);
        Assert.Equal(TransferStatus.InTransit, fetched.Status);
        Assert.Null(fetched.ReceivedAtUtc);
        Assert.Null(fetched.ReceivedBy);
        Assert.False(fetched.HasDiscrepancy);

        // Destination stock has NOT received the 5 units prematurely
        // In local DB catalog, stock remains at 25.0m (deducted from source, not yet accepted at destination)
        var sourceProd = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(25.0m, sourceProd!.StockOnHand);
    }

    [Fact]
    public async Task ReceiveTransfer_AtDestinationBranch_UpdatesStatusToReceived_AndIncrementsStock()
    {
        // Arrange
        var mgrSource = await _auth.CreateUserAsync("mgr_src", "Source Mgr", "Pass#123", Role.Manager);
        var mgrDest = await _auth.CreateUserAsync("mgr_dest", "Dest Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_spark_plug", "4790003", "Spark Plug NGK", 40.0m, 850.00m);

        var transfer = await _transfer.DispatchTransferAsync(
            sourceBranchId: "B01",
            destBranchId: "B02",
            lines: new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 12.0m } },
            dispatchedBy: mgrSource.UserId,
            tenantId: "TENANT_LK_01"
        );

        // Act: Destination branch explicitly receives transfer
        var received = await _transfer.ReceiveTransferAsync(
            transferId: transfer.TransferId,
            receivingBranchId: "B02",
            receivedBy: mgrDest.UserId
        );

        // Assert
        Assert.NotNull(received);
        Assert.Equal(TransferStatus.Received, received.Status);
        Assert.Equal(mgrDest.UserId, received.ReceivedBy);
        Assert.NotNull(received.ReceivedAtUtc);
        Assert.Equal(12.0m, received.TotalReceivedQuantity);
        Assert.False(received.HasDiscrepancy);
        Assert.Equal(12.0m, received.Lines[0].ReceivedQuantity);

        // Verify stock movement logged TRANSFER_IN
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT movement_type, quantity_change FROM stock_movements WHERE product_id = $pid AND reference_id = $ref AND quantity_change > 0;";
        cmd.Parameters.AddWithValue("$pid", product.ProductId);
        cmd.Parameters.AddWithValue("$ref", transfer.TransferId);
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "TRANSFER_IN movement must be recorded.");
        var mType = reader.GetString(0);
        Assert.True(mType == "TRANSFER_IN" || mType == "TRANSFER_RECEIVE");
        Assert.Equal(12.0m, reader.GetDecimal(1));
    }

    [Fact]
    public async Task ReceiveTransfer_DiscrepancyShortage_RecordsVariance_AndOnlyIncrementsActuallyReceivedQuantity()
    {
        // Scenario: Dispatched 20 units, but delivery truck only delivered 17 units (3 damaged/lost in transit)
        var mgrSource = await _auth.CreateUserAsync("mgr_src2", "Source Mgr 2", "Pass#123", Role.Manager);
        var mgrDest = await _auth.CreateUserAsync("mgr_dest2", "Dest Mgr 2", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_light_bulb", "4790004", "Halogen Bulb H7", 50.0m, 450.00m);

        var transfer = await _transfer.DispatchTransferAsync(
            sourceBranchId: "B01",
            destBranchId: "B02",
            lines: new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 20.0m } },
            dispatchedBy: mgrSource.UserId,
            tenantId: "TENANT_LK_01"
        );

        // Act: Receive with shortage discrepancy (17 received)
        var receivedQuantities = new Dictionary<string, decimal>
        {
            { product.ProductId, 17.0m }
        };

        var received = await _transfer.ReceiveTransferAsync(
            transferId: transfer.TransferId,
            receivingBranchId: "B02",
            receivedBy: mgrDest.UserId,
            receivedQuantities: receivedQuantities
        );

        // Assert Discrepancy Flagging
        Assert.Equal(TransferStatus.Received, received.Status);
        Assert.True(received.HasDiscrepancy);
        Assert.Equal(17.0m, received.TotalReceivedQuantity);
        Assert.Equal(20.0m, received.TotalDispatchedQuantity);

        var line = received.Lines.First(l => l.ProductId == product.ProductId);
        Assert.Equal(20.0m, line.DispatchedQuantity);
        Assert.Equal(17.0m, line.ReceivedQuantity);
        Assert.Equal(3.0m, line.DiscrepancyQuantity); // 20 - 17 = 3 missing

        // Verify only 17 units credited in TRANSFER_IN movement
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT quantity_change FROM stock_movements WHERE product_id = $pid AND reference_id = $ref AND quantity_change > 0;";
        cmd.Parameters.AddWithValue("$pid", product.ProductId);
        cmd.Parameters.AddWithValue("$ref", transfer.TransferId);
        var increment = Convert.ToDecimal(await cmd.ExecuteScalarAsync());
        Assert.Equal(17.0m, increment);
    }

    [Fact]
    public async Task CancelTransfer_InTransit_RestoresSourceStock_AndLogsCancelMovement()
    {
        // Scenario: 15 units dispatched. Customer cancelled order before dispatch left branch.
        var manager = await _auth.CreateUserAsync("mgr_cancel", "Cancel Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_clutch_plate", "4790005", "Clutch Plate Bajaj", 30.0m, 3500.00m);

        var transfer = await _transfer.DispatchTransferAsync(
            sourceBranchId: "B01",
            destBranchId: "B02",
            lines: new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 15.0m } },
            dispatchedBy: manager.UserId,
            tenantId: "TENANT_LK_01"
        );

        // Verify source stock dropped from 30 to 15
        var postDispatch = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(15.0m, postDispatch!.StockOnHand);

        // Act: Cancel the in-transit transfer
        var cancelled = await _transfer.CancelTransferAsync(
            transferId: transfer.TransferId,
            cancelledBy: manager.UserId,
            reason: "Vehicle breakdown, return goods to store shelf"
        );

        // Assert Status & Metadata
        Assert.Equal(TransferStatus.Cancelled, cancelled.Status);
        Assert.Equal(manager.UserId, cancelled.CancelledBy);
        Assert.NotNull(cancelled.CancelledAtUtc);
        Assert.Equal("Vehicle breakdown, return goods to store shelf", cancelled.CancellationReason);

        // Assert Source Stock Restored back to 30!
        var postCancel = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.Equal(30.0m, postCancel!.StockOnHand);

        // Assert Compensating Movement Logged
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT movement_type, quantity_change FROM stock_movements WHERE product_id = $pid AND reference_id = $ref AND quantity_change > 0;";
        cmd.Parameters.AddWithValue("$pid", product.ProductId);
        cmd.Parameters.AddWithValue("$ref", transfer.TransferId);
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Compensating return movement must be logged.");
        var mType = reader.GetString(0);
        Assert.True(mType == "TRANSFER_CANCEL_RETURN" || mType == "TRANSFER_CANCEL");
        Assert.Equal(15.0m, reader.GetDecimal(1));
    }

    [Fact]
    public async Task ReceiveTransfer_AlreadyReceived_ThrowsException()
    {
        var mgr = await _auth.CreateUserAsync("mgr_dup_recv", "Dup Recv Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_horn", "4790006", "12V Horn Disc", 20.0m, 1500.00m);

        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 5.0m } },
            mgr.UserId, "TENANT_LK_01"
        );

        // Receive once
        await _transfer.ReceiveTransferAsync(transfer.TransferId, "B02", mgr.UserId);

        // Act & Assert: Second receive attempt must throw
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _transfer.ReceiveTransferAsync(transfer.TransferId, "B02", mgr.UserId)
        );
    }

    [Fact]
    public async Task ReceiveTransfer_AlreadyCancelled_ThrowsException()
    {
        var mgr = await _auth.CreateUserAsync("mgr_cancelled_recv", "Cancelled Recv Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_mirror", "4790007", "Rearview Mirror Set", 15.0m, 1800.00m);

        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 4.0m } },
            mgr.UserId, "TENANT_LK_01"
        );

        // Cancel
        await _transfer.CancelTransferAsync(transfer.TransferId, mgr.UserId, "Cancelled by manager");

        // Act & Assert: Attempting to receive a cancelled transfer must throw
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _transfer.ReceiveTransferAsync(transfer.TransferId, "B02", mgr.UserId)
        );
    }

    [Fact]
    public async Task ReceiveTransfer_WrongDestinationBranch_ThrowsException()
    {
        var mgr = await _auth.CreateUserAsync("mgr_wrong_dest", "Wrong Dest Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_gasket", "4790008", "Cylinder Head Gasket", 25.0m, 600.00m);

        var transfer = await _transfer.DispatchTransferAsync(
            sourceBranchId: "B01",
            destBranchId: "B02", // Explicit destination is B02
            lines: new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 5.0m } },
            dispatchedBy: mgr.UserId,
            tenantId: "TENANT_LK_01"
        );

        // Act & Assert: Branch B03 attempts to receive goods intended for B02
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            _transfer.ReceiveTransferAsync(transfer.TransferId, receivingBranchId: "B03", receivedBy: mgr.UserId)
        );
        Assert.Contains("B02", ex.Message);
    }

    [Fact]
    public async Task CancelTransfer_AlreadyReceived_ThrowsException()
    {
        var mgr = await _auth.CreateUserAsync("mgr_cancel_recv", "Cancel Recv Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_cable_inner", "4790009", "Inner Throttle Cable", 20.0m, 250.00m);

        var transfer = await _transfer.DispatchTransferAsync(
            "B01", "B02",
            new List<TransferLineItem> { new() { ProductId = product.ProductId, DispatchedQuantity = 5.0m } },
            mgr.UserId, "TENANT_LK_01"
        );

        await _transfer.ReceiveTransferAsync(transfer.TransferId, "B02", mgr.UserId);

        // Act & Assert: Cannot cancel transfer that has already been received
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _transfer.CancelTransferAsync(transfer.TransferId, mgr.UserId, "Attempt late cancel")
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10.5)]
    public async Task DispatchTransfer_NonPositiveLineQuantity_ThrowsArgumentException(decimal invalidQty)
    {
        var mgr = await _auth.CreateUserAsync($"mgr_inv_{Guid.NewGuid():N}", "Invalid Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync($"prod_neg_{Guid.NewGuid():N}", "479999", "Test Prod", 20.0m);

        var lines = new List<TransferLineItem>
        {
            new() { ProductId = product.ProductId, DispatchedQuantity = invalidQty }
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _transfer.DispatchTransferAsync("B01", "B02", lines, mgr.UserId, "TENANT_LK_01")
        );
    }

    [Fact]
    public async Task DispatchTransfer_EmptyLineItems_ThrowsArgumentException()
    {
        var mgr = await _auth.CreateUserAsync("mgr_empty_lines", "Empty Mgr", "Pass#123", Role.Manager);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _transfer.DispatchTransferAsync("B01", "B02", new List<TransferLineItem>(), mgr.UserId, "TENANT_LK_01")
        );
    }

    [Fact]
    public async Task DispatchTransfer_SameSourceAndDestBranch_ThrowsArgumentException()
    {
        var mgr = await _auth.CreateUserAsync("mgr_same_branch", "Same Branch Mgr", "Pass#123", Role.Manager);
        var product = await SeedProductAsync("prod_same_br", "4790010", "Chain Lube Spray", 10.0m);

        var lines = new List<TransferLineItem>
        {
            new() { ProductId = product.ProductId, DispatchedQuantity = 2.0m }
        };

        // Transfer from B01 to B01 is invalid
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _transfer.DispatchTransferAsync("B01", "B01", lines, mgr.UserId, "TENANT_LK_01")
        );
    }

    [Fact]
    public async Task GetTransfers_FilterByBranchAndStatus_ReturnsMatchingRecords()
    {
        var mgr = await _auth.CreateUserAsync("mgr_query", "Query Mgr", "Pass#123", Role.Manager);
        var p1 = await SeedProductAsync("prod_q1", "4790011", "Part Q1", 100m);
        var p2 = await SeedProductAsync("prod_q2", "4790012", "Part Q2", 100m);

        // Transfer 1: B01 -> B02, InTransit
        var t1 = await _transfer.DispatchTransferAsync("B01", "B02",
            new List<TransferLineItem> { new() { ProductId = p1.ProductId, DispatchedQuantity = 5m } },
            mgr.UserId, "TENANT_LK_01");

        // Transfer 2: B01 -> B03, InTransit -> Received
        var t2 = await _transfer.DispatchTransferAsync("B01", "B03",
            new List<TransferLineItem> { new() { ProductId = p2.ProductId, DispatchedQuantity = 8m } },
            mgr.UserId, "TENANT_LK_01");
        await _transfer.ReceiveTransferAsync(t2.TransferId, "B03", mgr.UserId);

        // Filter by branch B02
        var listB02 = await _transfer.GetTransfersAsync(branchId: "B02");
        Assert.Single(listB02);
        Assert.Equal(t1.TransferId, listB02[0].TransferId);

        // Filter by status Received
        var listReceived = await _transfer.GetTransfersAsync(status: TransferStatus.Received);
        Assert.Contains(listReceived, t => t.TransferId == t2.TransferId);
        Assert.DoesNotContain(listReceived, t => t.TransferId == t1.TransferId);
    }
}
