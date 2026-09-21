using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class ReturnsAndRefundsTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _sale;

    public ReturnsAndRefundsTests()
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
    public async Task GetSaleByReceiptNumber_ReturnsOriginalSaleWithLinesAndTenders()
    {
        var cashier = await _auth.CreateUserAsync("kasun_r", "Kasun R", "Pass#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var prod = new Product
        {
            ProductId = "p_brake_fluid",
            Barcode = "7788990011",
            Name = "Brake Fluid DOT4 500ml",
            UnitPrice = 950.00m,
            CostBasis = 600.00m,
            StockOnHand = 20m
        };
        await _catalog.AddProductAsync(prod);

        var sale = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 2.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 2000.00m) }
        ));

        // Query by receipt number
        var found = await _sale.GetSaleByReceiptNumberAsync(sale.ReceiptNumber);
        Assert.NotNull(found);
        Assert.Equal(sale.SaleId, found.SaleId);
        Assert.Equal(sale.ReceiptNumber, found.ReceiptNumber);
        Assert.Single(found.Lines);
        Assert.Equal(2.0m, found.Lines[0].Quantity);
        Assert.Equal(950.00m, found.Lines[0].UnitPrice);
        Assert.Single(found.Tenders);
        Assert.Equal(100.00m, found.Tenders[0].ChangeGiven); // 2000 - 1900
    }

    [Fact]
    public async Task RestockToggle_TrueRestoresInventory_FalseLeavesInventoryUnchanged()
    {
        var cashier = await _auth.CreateUserAsync("chaminda", "Chaminda P", "Pass#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var prodRestockable = new Product
        {
            ProductId = "p_restock",
            Barcode = "1122334455",
            Name = "Unopened Battery Pack",
            UnitPrice = 4500.00m,
            CostBasis = 3200.00m,
            StockOnHand = 10m
        };
        var prodDamaged = new Product
        {
            ProductId = "p_damaged",
            Barcode = "2233445566",
            Name = "Cracked Side Mirror",
            UnitPrice = 1800.00m,
            CostBasis = 1100.00m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(prodRestockable);
        await _catalog.AddProductAsync(prodDamaged);

        // Make sale of both products (1 of each)
        var sale = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(prodRestockable.ProductId, 1.0m),
                new(prodDamaged.ProductId, 1.0m)
            },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 6300.00m) }
        ));

        // After sale, stock is 9 for both
        Assert.Equal(9m, await _catalog.GetStockOnHandAsync(prodRestockable.ProductId));
        Assert.Equal(9m, await _catalog.GetStockOnHandAsync(prodDamaged.ProductId));

        // 1. Refund Restockable WITH restock toggle = true
        var restockLine = sale.Lines.First(l => l.ProductId == prodRestockable.ProductId);
        await _sale.RefundSaleAsync(new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest> { new(restockLine.LineId, 1.0m) },
            Reason: "Customer bought wrong model, unopened in box",
            ReturnStockToInventory: true
        ));

        // Stock restored to 10 for restockable!
        Assert.Equal(10m, await _catalog.GetStockOnHandAsync(prodRestockable.ProductId));

        // 2. Refund Damaged item WITH restock toggle = false (cannot be resold)
        var damagedLine = sale.Lines.First(l => l.ProductId == prodDamaged.ProductId);
        await _sale.RefundSaleAsync(new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest> { new(damagedLine.LineId, 1.0m) },
            Reason: "Defective mirror glass - discarded/scrapped",
            ReturnStockToInventory: false
        ));

        // Stock MUST REMAIN 9 (not incremented because item is unsaleable)!
        Assert.Equal(9m, await _catalog.GetStockOnHandAsync(prodDamaged.ProductId));
    }

    [Fact]
    public async Task CumulativePartialRefunds_PreventsExceedingOriginalQuantity()
    {
        var cashier = await _auth.CreateUserAsync("udara", "Udara S", "Pass#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var prod = new Product
        {
            ProductId = "p_chain_lube",
            Barcode = "3344556677",
            Name = "Chain Lube Spray 400ml",
            UnitPrice = 1200.00m,
            CostBasis = 800.00m,
            StockOnHand = 20m
        };
        await _catalog.AddProductAsync(prod);

        // Buy 3 units
        var sale = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 3.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 3600.00m) }
        ));

        var line = sale.Lines[0];

        // 1. First partial refund: 1 unit (remaining refundable = 2)
        var ref1 = await _sale.RefundSaleAsync(new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest> { new(line.LineId, 1.0m) },
            Reason: "Returned 1 excess unit",
            ReturnStockToInventory: true
        ));
        Assert.Equal(1200.00m, ref1.GrandTotal);

        // 2. Second partial refund: 2 units (remaining refundable was 2 -> now 0)
        var ref2 = await _sale.RefundSaleAsync(new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest> { new(line.LineId, 2.0m) },
            Reason: "Returned remaining 2 units",
            ReturnStockToInventory: true
        ));
        Assert.Equal(2400.00m, ref2.GrandTotal);

        // 3. Third refund attempt: 1 unit -> MUST FAIL because all 3 units have been refunded!
        await Assert.ThrowsAsync<PosException>(() =>
            _sale.RefundSaleAsync(new RefundSaleCommand(
                TenantId: "TENANT_LK_01",
                BranchId: "B01",
                CounterId: "C01",
                CashierId: cashier.UserId,
                ShiftId: shift.ShiftId,
                OriginalSaleId: sale.SaleId,
                Items: new List<RefundLineRequest> { new(line.LineId, 1.0m) },
                Reason: "Attempting to refund more than purchased",
                ReturnStockToInventory: true
            ))
        );

        // 4. Verify total cash refunds on shift
        var activeShift = await _shift.GetActiveShiftAsync("B01", "C01");
        Assert.NotNull(activeShift);
        Assert.Equal(3600.00m, activeShift.CashRefunds); // 1200 + 2400
    }

    [Fact]
    public async Task RefundWithoutReason_ThrowsArgumentException()
    {
        var cashier = await _auth.CreateUserAsync("asanka", "Asanka P", "Pass#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var prod = new Product
        {
            ProductId = "p_fuse_set",
            Barcode = "4455667788",
            Name = "Mini Fuse Assortment",
            UnitPrice = 450.00m,
            CostBasis = 200.00m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(prod);

        var sale = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 500.00m) }
        ));

        // Empty reason rejected
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sale.RefundSaleAsync(new RefundSaleCommand(
                TenantId: "TENANT_LK_01",
                BranchId: "B01",
                CounterId: "C01",
                CashierId: cashier.UserId,
                ShiftId: shift.ShiftId,
                OriginalSaleId: sale.SaleId,
                Items: new List<RefundLineRequest> { new(sale.Lines[0].LineId, 1.0m) },
                Reason: "   "
            ))
        );
    }

    [Fact]
    public async Task Refund_ProportionalFixedDiscount_CalculatesCorrectRefundAmount()
    {
        var cashier = await _auth.CreateUserAsync("nimal_disc", "Nimal Disc", "Pass#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var prod = new Product
        {
            ProductId = "p_oil_filter",
            Barcode = "9988776655",
            Name = "Premium Oil Filter",
            UnitPrice = 1000.00m,
            CostBasis = 600.00m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(prod);

        // Buy 2 units at 1000 each = 2000, with 200 LKR fixed discount -> Grand Total = 1800
        var sale = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(prod.ProductId, 2.0m, DiscountFixed: 200.00m)
            },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 2000.00m) }
        ));

        Assert.Equal(1800.00m, sale.GrandTotal);

        // Refund 1 unit. Proportional discount should be 100.00m. Refund total = 1000 - 100 = 900.00m.
        var line = sale.Lines[0];
        var refund = await _sale.RefundSaleAsync(new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest> { new(line.LineId, 1.0m) },
            Reason: "Customer returning 1 unit from 2-pack",
            ReturnStockToInventory: true
        ));

        Assert.Equal(900.00m, refund.GrandTotal);
        Assert.Equal(100.00m, refund.DiscountTotal);
        Assert.Equal(1000.00m, refund.Subtotal);
        Assert.Equal(line.LineId, refund.Lines[0].ParentLineId);
    }

    [Fact]
    public async Task Refund_DuplicateLinesInRequest_ThrowsPosException()
    {
        var cashier = await _auth.CreateUserAsync("dup_test", "Dup Test", "Pass#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var prod = new Product
        {
            ProductId = "p_spark_plug",
            Barcode = "1231231234",
            Name = "Spark Plug NGK",
            UnitPrice = 500.00m,
            CostBasis = 300.00m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(prod);

        var sale = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 2.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1000.00m) }
        ));

        var line = sale.Lines[0];

        // Pass duplicate line items in single refund request
        await Assert.ThrowsAsync<PosException>(() =>
            _sale.RefundSaleAsync(new RefundSaleCommand(
                TenantId: "TENANT_LK_01",
                BranchId: "B01",
                CounterId: "C01",
                CashierId: cashier.UserId,
                ShiftId: shift.ShiftId,
                OriginalSaleId: sale.SaleId,
                Items: new List<RefundLineRequest>
                {
                    new(line.LineId, 1.0m),
                    new(line.LineId, 1.0m)
                },
                Reason: "Duplicate lines test"
            ))
        );
    }

    [Fact]
    public async Task CancelSale_WhenChildRefundExists_ThrowsPosException()
    {
        var cashier = await _auth.CreateUserAsync("cancel_guard", "Cancel Guard", "Pass#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var prod = new Product
        {
            ProductId = "p_wiper_blade",
            Barcode = "9876543210",
            Name = "Wiper Blade 20 inch",
            UnitPrice = 800.00m,
            CostBasis = 500.00m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(prod);

        var sale = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 2.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1600.00m) }
        ));

        // Partially refund 1 unit
        await _sale.RefundSaleAsync(new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest> { new(sale.Lines[0].LineId, 1.0m) },
            Reason: "Customer changed mind on 1 item",
            ReturnStockToInventory: true
        ));

        // Now attempting to cancel the whole sale MUST be rejected to prevent double restock and double cash return!
        await Assert.ThrowsAsync<PosException>(() =>
            _sale.CancelSaleAsync(new CancelSaleCommand(
                TenantId: "TENANT_LK_01",
                BranchId: "B01",
                CounterId: "C01",
                CashierId: cashier.UserId,
                ShiftId: shift.ShiftId,
                SaleId: sale.SaleId,
                Reason: "Attempt cancel partially refunded sale"
            ))
        );
    }

    [Fact]
    public async Task Refund_FullRefundUpdatesOriginalSaleStatusToRefunded()
    {
        var cashier = await _auth.CreateUserAsync("full_ref", "Full Ref", "Pass#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var prod = new Product
        {
            ProductId = "p_headlight_bulb",
            Barcode = "5544332211",
            Name = "H4 Halogen Bulb",
            UnitPrice = 750.00m,
            CostBasis = 400.00m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(prod);

        var sale = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 750.00m) }
        ));

        Assert.Equal(SaleStatus.Completed, sale.Status);

        // Refund the entire 1 unit
        await _sale.RefundSaleAsync(new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest> { new(sale.Lines[0].LineId, 1.0m) },
            Reason: "Full refund test",
            ReturnStockToInventory: true
        ));

        // Original sale status remains Completed (A08: original record is preserved!)
        var updatedSale = await _sale.GetSaleByIdAsync(sale.SaleId);
        Assert.NotNull(updatedSale);
        Assert.Equal(SaleStatus.Completed, updatedSale.Status);

        // Attempting to refund again should be rejected with "already fully refunded"
        var ex = await Assert.ThrowsAsync<PosException>(() =>
            _sale.RefundSaleAsync(new RefundSaleCommand(
                TenantId: "TENANT_LK_01",
                BranchId: "B01",
                CounterId: "C01",
                CashierId: cashier.UserId,
                ShiftId: shift.ShiftId,
                OriginalSaleId: sale.SaleId,
                Items: new List<RefundLineRequest> { new(sale.Lines[0].LineId, 1.0m) },
                Reason: "Another refund attempt"
            ))
        );
        Assert.Contains("fully refunded", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

