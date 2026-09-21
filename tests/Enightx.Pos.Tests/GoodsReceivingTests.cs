using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class GoodsReceivingTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly GoodsReceivingService _receiving;

    public GoodsReceivingTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _catalog = new CatalogService(_db);
        _receiving = new GoodsReceivingService(_db, _catalog);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task Cashier_AttemptingGoodsReceiving_IsRejectedAtServiceLayer()
    {
        var cashier = await _auth.CreateUserAsync("tharindu", "Tharindu K", "Pass#123", Role.Cashier);
        var product = new Product
        {
            ProductId = "p_grip",
            Barcode = "9988776655",
            Name = "Handlebar Grips",
            UnitPrice = 850.00m,
            CostBasis = 500.00m,
            StockOnHand = 5m
        };
        await _catalog.AddProductAsync(product);

        var cmd = new GoodsReceivingCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            SupplierName: "DSI Rubber Ltd",
            InvoiceReference: "INV-2026-001",
            Actor: cashier,
            Items: new List<GoodsReceivingItemRequest>
            {
                new(product.ProductId, Quantity: 10m, UnitCost: 520.00m)
            }
        );

        // A09: Cashier attempting stock receiving MUST throw UnauthorizedActionException
        await Assert.ThrowsAsync<UnauthorizedActionException>(() => _receiving.ReceiveGoodsAsync(cmd));

        // Stock and cost basis remain unchanged
        var p = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.NotNull(p);
        Assert.Equal(5m, p.StockOnHand);
        Assert.Equal(500.00m, p.CostBasis);
    }

    [Fact]
    public async Task Manager_ReceivesGoods_UpdatesStockOnHand_AndCalculatesMovingWeightedAverageCost()
    {
        var manager = await _auth.CreateUserAsync("rohan", "Rohan Perera", "Manager#123", Role.Manager);

        // Initial state:
        // Stock On Hand: 10 units
        // Cost Basis: 1,000.00 LKR
        var product = new Product
        {
            ProductId = "p_sprocket",
            Barcode = "5544332211",
            Name = "Rear Sprocket 42T",
            UnitPrice = 2200.00m,
            CostBasis = 1000.00m,
            StockOnHand = 10.0m
        };
        await _catalog.AddProductAsync(product);

        // Receive 10 units @ 1,200.00 LKR
        // Moving weighted average cost:
        // Total value = (10 * 1000) + (10 * 1200) = 10000 + 12000 = 22000
        // Total qty = 10 + 10 = 20
        // New Cost Basis = 22000 / 20 = 1,100.00 LKR
        var cmd = new GoodsReceivingCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            SupplierName: "Rolon Lanka Distributors",
            InvoiceReference: "INV-ROLON-8891",
            Actor: manager,
            Items: new List<GoodsReceivingItemRequest>
            {
                new(product.ProductId, Quantity: 10.0m, UnitCost: 1200.00m)
            },
            Notes: "Delivered via freight courier"
        );

        var receipt = await _receiving.ReceiveGoodsAsync(cmd);
        Assert.NotNull(receipt);
        Assert.Equal("Rolon Lanka Distributors", receipt.SupplierName);
        Assert.Equal("INV-ROLON-8891", receipt.InvoiceReference);
        Assert.Equal(12000.00m, receipt.TotalCost); // 10 * 1200
        Assert.Single(receipt.Lines);

        // Verify product stock on hand updated to 20
        var updatedProduct = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.NotNull(updatedProduct);
        Assert.Equal(20.0m, updatedProduct.StockOnHand);
        Assert.Equal(1100.00m, updatedProduct.CostBasis); // 1,100.00 LKR

        // Verify receipt persisted in SQLite
        var loadedReceipt = await _receiving.GetGoodsReceiptByIdAsync(receipt.ReceiptId);
        Assert.NotNull(loadedReceipt);
        Assert.Equal(receipt.ReceiptId, loadedReceipt.ReceiptId);
        Assert.Single(loadedReceipt.Lines);
        Assert.Equal(10.0m, loadedReceipt.Lines[0].Quantity);
        Assert.Equal(1200.00m, loadedReceipt.Lines[0].UnitCost);
        Assert.Equal(12000.00m, loadedReceipt.Lines[0].LineTotalCost);

        // Verify stock movement was recorded with type 'RECEIVING'
        var movements = await _receiving.GetStockMovementsAsync(product.ProductId, "RECEIVING");
        Assert.Single(movements);
        Assert.Equal("RECEIVING", movements[0].MovementType);
        Assert.Equal(10.0m, movements[0].QuantityChange);
        Assert.Equal(receipt.ReceiptId.ToString(), movements[0].ReferenceId);
    }

    [Fact]
    public async Task Manager_ReceivesGoods_WhenPreviousStockWasNegative_SetsCostBasisToNewUnitCost()
    {
        var manager = await _auth.CreateUserAsync("saman", "Saman K", "Manager#123", Role.Manager);

        // Suppose counter oversold goods offline (A06 negative stock):
        // Stock on hand: -3 units, historical cost basis: 500.00 LKR
        var product = new Product
        {
            ProductId = "p_cable",
            Barcode = "6677889900",
            Name = "Clutch Cable Hero",
            UnitPrice = 850.00m,
            CostBasis = 500.00m,
            StockOnHand = -3.0m
        };
        await _catalog.AddProductAsync(product);

        // Receive 10 units @ 580.00 LKR
        // New stock on hand: -3 + 10 = 7.0m
        // Since old stock was negative, the new purchase price 580.00 becomes the active cost basis!
        var cmd = new GoodsReceivingCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            SupplierName: "Hero Spares Colombo",
            InvoiceReference: "INV-HERO-102",
            Actor: manager,
            Items: new List<GoodsReceivingItemRequest>
            {
                new(product.ProductId, Quantity: 10.0m, UnitCost: 580.00m)
            }
        );

        await _receiving.ReceiveGoodsAsync(cmd);

        var updated = await _catalog.GetProductByIdAsync(product.ProductId);
        Assert.NotNull(updated);
        Assert.Equal(7.0m, updated.StockOnHand);
        Assert.Equal(580.00m, updated.CostBasis);
    }

    [Fact]
    public async Task ReceiveGoods_WithMultipleProducts_UpdatesAllStocksAndCostBasesAtomically()
    {
        var owner = await _auth.CreateUserAsync("owner1", "Shop Owner", "Owner#123", Role.Owner);

        var prodA = new Product { ProductId = "p_a", Barcode = "001", Name = "Part A", UnitPrice = 100m, CostBasis = 60m, StockOnHand = 5m };
        var prodB = new Product { ProductId = "p_b", Barcode = "002", Name = "Part B", UnitPrice = 200m, CostBasis = 120m, StockOnHand = 2m };
        await _catalog.AddProductAsync(prodA);
        await _catalog.AddProductAsync(prodB);

        var cmd = new GoodsReceivingCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            SupplierName: "Universal Auto Parts",
            InvoiceReference: "INV-UNI-55",
            Actor: owner,
            Items: new List<GoodsReceivingItemRequest>
            {
                new(prodA.ProductId, Quantity: 5m, UnitCost: 70m),   // (5*60 + 5*70)/10 = 650/10 = 65m
                new(prodB.ProductId, Quantity: 8m, UnitCost: 130m)   // (2*120 + 8*130)/10 = (240 + 1040)/10 = 128m
            }
        );

        var receipt = await _receiving.ReceiveGoodsAsync(cmd);
        Assert.Equal(2, receipt.Lines.Count);
        Assert.Equal((5m * 70m) + (8m * 130m), receipt.TotalCost); // 350 + 1040 = 1390m

        var updatedA = await _catalog.GetProductByIdAsync(prodA.ProductId);
        var updatedB = await _catalog.GetProductByIdAsync(prodB.ProductId);

        Assert.Equal(10m, updatedA!.StockOnHand);
        Assert.Equal(65.00m, updatedA.CostBasis);

        Assert.Equal(10m, updatedB!.StockOnHand);
        Assert.Equal(128.00m, updatedB.CostBasis);
    }

    [Fact]
    public async Task ReceiveGoods_EmitsDurableOutboxEvent()
    {
        var manager = await _auth.CreateUserAsync("outbox_mgr", "Outbox Manager", "Manager#123", Role.Manager);
        var product = new Product
        {
            ProductId = "p_outbox_test",
            Barcode = "9988112233",
            Name = "Testing Outbox Product",
            UnitPrice = 500m,
            CostBasis = 300m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(product);

        var cmd = new GoodsReceivingCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            SupplierName: "Outbox Supplier Ltd",
            InvoiceReference: "INV-OUTBOX-01",
            Actor: manager,
            Items: new List<GoodsReceivingItemRequest>
            {
                new(product.ProductId, 5m, 320m)
            }
        );

        var receipt = await _receiving.ReceiveGoodsAsync(cmd);

        using var conn = _db.CreateConnection();
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*), payload_json FROM outbox_events WHERE causal_reference = $ref;";
        checkCmd.Parameters.AddWithValue("$ref", receipt.ReceiptId.ToString());
        using var reader = await checkCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        var payload = reader.GetString(1);
        Assert.Contains("INV-OUTBOX-01", payload);
        Assert.Contains("Outbox Supplier Ltd", payload);
    }
}

