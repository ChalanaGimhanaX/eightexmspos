using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class A08_A09_SecurityAuditTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _sale;

    public A08_A09_SecurityAuditTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _catalog = new CatalogService(_db);
        _shift = new ShiftService(_db);
        _sale = new SaleService(_db, _catalog);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task A08_CashierPriceOverride_RequiresReasonAndLogsActor()
    {
        var cashier = await _auth.CreateUserAsync("anura", "Anura Kumara", "Pass#123", Role.Cashier);
        var product = new Product
        {
            ProductId = "prod_bulb_01",
            Barcode = "5011223344556",
            Name = "Headlight Bulb 12V 35W",
            UnitPrice = 850.00m,
            CostBasis = 500.00m,
            TaxRate = 0.0m,
            StockOnHand = 50.0m
        };
        await _catalog.AddProductAsync(product);

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 2000.00m, "TENANT_LK_01");

        // Price override without reason must be REJECTED
        var invalidCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(ProductId: product.ProductId, Quantity: 1.0m, PriceOverride: 750.00m, OverrideReason: null)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType: TenderType.CASH, AmountTendered: 800.00m)
            }
        );

        await Assert.ThrowsAsync<ArgumentException>(() => _sale.CommitSaleAsync(invalidCmd));

        // Price override WITH reason is accepted
        var validCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(ProductId: product.ProductId, Quantity: 1.0m, PriceOverride: 750.00m, OverrideReason: "Customer loyalty promotion approved")
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType: TenderType.CASH, AmountTendered: 800.00m)
            }
        );

        var sale = await _sale.CommitSaleAsync(validCmd);
        Assert.NotNull(sale);
        Assert.Equal(750.00m, sale.GrandTotal);

        // Check tender change
        Assert.Equal(50.00m, sale.Tenders[0].ChangeGiven);
    }

    [Fact]
    public async Task A09_CashierAttemptsManualStockAdjustment_RejectedAtServiceLayer()
    {
        var cashier = await _auth.CreateUserAsync("malik", "Malik De Silva", "Pass#123", Role.Cashier);
        var manager = await _auth.CreateUserAsync("kamal", "Kamal Perera", "Manager#123", Role.Manager);

        var product = new Product
        {
            ProductId = "prod_tyre_01",
            Barcode = "8908877665544",
            Name = "DSI Tyre 90/90-18 Tubeless",
            UnitPrice = 8500.00m,
            CostBasis = 6200.00m,
            TaxRate = 0.0m,
            StockOnHand = 10.0m
        };
        await _catalog.AddProductAsync(product);

        // A09: Cashier attempting manual stock adjustment MUST throw UnauthorizedActionException
        await Assert.ThrowsAsync<UnauthorizedActionException>(async () =>
        {
            await _catalog.AdjustStockAsync(
                product.ProductId,
                quantityChange: 5.0m,
                reason: "Direct inventory correction attempt",
                actor: cashier,
                tenantId: "TENANT_LK_01",
                branchId: "B01",
                counterId: "C01"
            );
        });

        // Stock remains unchanged
        Assert.Equal(10.0m, await _catalog.GetStockOnHandAsync(product.ProductId));

        // Manager CAN adjust stock with reason
        await _catalog.AdjustStockAsync(
            product.ProductId,
            quantityChange: 5.0m,
            reason: "Stock audit discrepancy resolved",
            actor: manager,
            tenantId: "TENANT_LK_01",
            branchId: "B01",
            counterId: "C01"
        );

        // Stock successfully adjusted to 15.0
        Assert.Equal(15.0m, await _catalog.GetStockOnHandAsync(product.ProductId));
    }
}
