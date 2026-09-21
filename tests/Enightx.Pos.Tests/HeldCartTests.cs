using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class HeldCartTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly HeldCartService _heldCartService;

    public HeldCartTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _heldCartService = new HeldCartService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task HoldCart_EmptyItems_ThrowsPosException()
    {
        var cashier = await _auth.CreateUserAsync("nimal", "Nimal Silva", "Pass#123", Role.Cashier);
        var cmd = new HoldCartCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            CustomerReference: "Customer waiting",
            Items: new List<HeldCartItem>()
        );

        await Assert.ThrowsAsync<PosException>(() => _heldCartService.HoldCartAsync(cmd));
    }

    [Fact]
    public async Task HoldCart_AndRecallCart_RestoresExactItemsAndTotals_AndRemovesFromHeldStorage()
    {
        var cashier = await _auth.CreateUserAsync("kamal", "Kamal Jay", "Pass#123", Role.Cashier);

        var items = new List<HeldCartItem>
        {
            new()
            {
                ProductId = "prod_spark_01",
                Barcode = "8901234567890",
                ProductName = "NGK Spark Plug",
                Quantity = 2.0m,
                UnitPrice = 650.00m,
                DiscountRate = 0.10m,
                DiscountFixed = 0.0m,
                TaxRate = 0.18m,
                LineTotal = 1380.60m
            },
            new()
            {
                ProductId = "prod_oil_01",
                Barcode = "8909876543210",
                ProductName = "Castrol Engine Oil 1L",
                Quantity = 1.0m,
                UnitPrice = 2400.00m,
                DiscountRate = 0.0m,
                DiscountFixed = 0.0m,
                TaxRate = 0.18m,
                LineTotal = 2832.00m
            }
        };

        var holdCmd = new HoldCartCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            CustomerReference: "Customer went to get wallet",
            Items: items
        );

        // 1. Hold cart
        var held = await _heldCartService.HoldCartAsync(holdCmd);
        Assert.NotNull(held);
        Assert.Equal("Customer went to get wallet", held.CustomerReference);
        Assert.Equal(2, held.Items.Count);
        Assert.Equal(3700.00m, held.Subtotal); // (2 * 650) + 2400 = 1300 + 2400
        Assert.True(held.GrandTotal > 0);

        // 2. Query list of held carts for counter
        var activeHeldCarts = await _heldCartService.GetHeldCartsAsync("B01", "C01");
        Assert.Single(activeHeldCarts);
        Assert.Equal(held.HeldCartId, activeHeldCarts[0].HeldCartId);
        Assert.Equal("Customer went to get wallet", activeHeldCarts[0].CustomerReference);

        // 3. Recall cart
        var recalled = await _heldCartService.RecallCartAsync(held.HeldCartId, cashier.UserId);
        Assert.NotNull(recalled);
        Assert.Equal(held.HeldCartId, recalled.HeldCartId);
        Assert.Equal(2, recalled.Items.Count);
        Assert.Equal("prod_spark_01", recalled.Items[0].ProductId);
        Assert.Equal(2.0m, recalled.Items[0].Quantity);
        Assert.Equal(650.00m, recalled.Items[0].UnitPrice);
        Assert.Equal("prod_oil_01", recalled.Items[1].ProductId);

        // 4. Verify held cart is now deleted from held storage
        var remainingHeld = await _heldCartService.GetHeldCartsAsync("B01", "C01");
        Assert.Empty(remainingHeld);

        var directCheck = await _heldCartService.GetHeldCartByIdAsync(held.HeldCartId);
        Assert.Null(directCheck);

        // 5. Verify audit events recorded HOLD_CART and RECALL_CART
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action IN ('HOLD_CART', 'RECALL_CART');";
        Assert.Equal(2, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task HeldCarts_SurviveServiceReinitialization_AcrossRestarts()
    {
        var cashier = await _auth.CreateUserAsync("sarath", "Sarath B", "Pass#123", Role.Cashier);

        var items = new List<HeldCartItem>
        {
            new()
            {
                ProductId = "prod_bulb_01",
                Barcode = "5011223344556",
                ProductName = "12V Bulb",
                Quantity = 4.0m,
                UnitPrice = 350.00m,
                DiscountRate = 0.0m,
                DiscountFixed = 0.0m,
                TaxRate = 0.0m,
                LineTotal = 1400.00m
            }
        };

        var held = await _heldCartService.HoldCartAsync(new HoldCartCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            CustomerReference: "Table 4 Order",
            Items: items
        ));

        // Simulate app restart by instantiating a completely new service instance on the same database
        var newServiceInstance = new HeldCartService(_db);
        var loaded = await newServiceInstance.GetHeldCartByIdAsync(held.HeldCartId);

        Assert.NotNull(loaded);
        Assert.Equal("Table 4 Order", loaded.CustomerReference);
        Assert.Single(loaded.Items);
        Assert.Equal(4.0m, loaded.Items[0].Quantity);
        Assert.Equal(1400.00m, loaded.GrandTotal);
    }

    [Fact]
    public async Task DeleteHeldCart_RemovesFromStorage_AndLogsAudit()
    {
        var cashier = await _auth.CreateUserAsync("chathura", "Chathura K", "Pass#123", Role.Cashier);

        var items = new List<HeldCartItem>
        {
            new()
            {
                ProductId = "prod_fuse",
                Barcode = "9900112233",
                ProductName = "10A Blade Fuse",
                Quantity = 1.0m,
                UnitPrice = 150.00m,
                DiscountRate = 0.0m,
                DiscountFixed = 0.0m,
                TaxRate = 0.0m,
                LineTotal = 150.00m
            }
        };

        var held = await _heldCartService.HoldCartAsync(new HoldCartCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            CustomerReference: "Abandoned Cart",
            Items: items
        ));

        await _heldCartService.DeleteHeldCartAsync(held.HeldCartId, cashier.UserId, "Customer left without paying");

        var check = await _heldCartService.GetHeldCartByIdAsync(held.HeldCartId);
        Assert.Null(check);

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'DISCARD_HELD_CART';";
        Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task MultipleHeldCarts_CanBeMaintainedAndRecalledIndependently()
    {
        var cashier = await _auth.CreateUserAsync("ruwan", "Ruwan P", "Pass#123", Role.Cashier);

        var cart1 = await _heldCartService.HoldCartAsync(new HoldCartCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            CustomerReference: "Customer A",
            Items: new List<HeldCartItem>
            {
                new() { ProductId = "p1", Barcode = "111", ProductName = "Item 1", Quantity = 1m, UnitPrice = 100m, LineTotal = 100m }
            }
        ));

        var cart2 = await _heldCartService.HoldCartAsync(new HoldCartCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            CustomerReference: "Customer B",
            Items: new List<HeldCartItem>
            {
                new() { ProductId = "p2", Barcode = "222", ProductName = "Item 2", Quantity = 2m, UnitPrice = 200m, LineTotal = 400m }
            }
        ));

        var allHeld = await _heldCartService.GetHeldCartsAsync("B01", "C01");
        Assert.Equal(2, allHeld.Count);

        // Recall Cart 2 first (out of order recall)
        var recalled2 = await _heldCartService.RecallCartAsync(cart2.HeldCartId, cashier.UserId);
        Assert.Equal("Customer B", recalled2.CustomerReference);
        Assert.Equal(400m, recalled2.GrandTotal);

        var remaining = await _heldCartService.GetHeldCartsAsync("B01", "C01");
        Assert.Single(remaining);
        Assert.Equal(cart1.HeldCartId, remaining[0].HeldCartId);

        // Recall Cart 1
        var recalled1 = await _heldCartService.RecallCartAsync(cart1.HeldCartId, cashier.UserId);
        Assert.Equal("Customer A", recalled1.CustomerReference);
        Assert.Equal(100m, recalled1.GrandTotal);

        var emptyNow = await _heldCartService.GetHeldCartsAsync("B01", "C01");
        Assert.Empty(emptyNow);
    }
}

