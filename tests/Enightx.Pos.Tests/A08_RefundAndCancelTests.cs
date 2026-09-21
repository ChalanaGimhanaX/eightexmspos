using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class A08_RefundAndCancelTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _sale;

    public A08_RefundAndCancelTests()
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
    public async Task A08_RefundSale_RequiresReason_UpdatesShiftCash_RestoresStock_PreservesOriginal()
    {
        var cashier = await _auth.CreateUserAsync("kasun", "Kasun Perera", "Pass#123", Role.Cashier);
        var product = new Product
        {
            ProductId = "prod_chain_01",
            Barcode = "8901112223334",
            Name = "Rolon Drive Chain 428H",
            UnitPrice = 3500.00m,
            CostBasis = 2400.00m,
            TaxRate = 0.0m,
            StockOnHand = 10.0m
        };
        await _catalog.AddProductAsync(product);

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        // 1. Make initial cash sale (2 units @ 3500 = 7000 LKR)
        var saleCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(ProductId: product.ProductId, Quantity: 2.0m)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType: TenderType.CASH, AmountTendered: 7000.00m)
            }
        );

        var sale = await _sale.CommitSaleAsync(saleCmd);
        Assert.Equal(7000.00m, sale.GrandTotal);
        Assert.Equal(8.0m, await _catalog.GetStockOnHandAsync(product.ProductId)); // 10 - 2

        var lineToRefund = sale.Lines[0];

        // 2. Refund WITHOUT reason must throw ArgumentException (A08)
        var noReasonCmd = new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest>
            {
                new(LineId: lineToRefund.LineId, QuantityToRefund: 1.0m)
            },
            Reason: "" // Empty reason rejected!
        );

        await Assert.ThrowsAsync<ArgumentException>(() => _sale.RefundSaleAsync(noReasonCmd));

        // 3. Refund with invalid quantity must throw PosException
        var invalidQtyCmd = new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest>
            {
                new(LineId: lineToRefund.LineId, QuantityToRefund: 5.0m) // Exceeds 2.0
            },
            Reason: "Customer error"
        );

        await Assert.ThrowsAsync<PosException>(() => _sale.RefundSaleAsync(invalidQtyCmd));

        // 4. Valid partial refund (1 unit = 3500 LKR) with reason
        var validRefundCmd = new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest>
            {
                new(LineId: lineToRefund.LineId, QuantityToRefund: 1.0m)
            },
            Reason: "Incorrect part size purchased by customer",
            ReturnStockToInventory: true
        );

        var refund = await _sale.RefundSaleAsync(validRefundCmd);
        Assert.NotNull(refund);
        Assert.Equal(3500.00m, refund.GrandTotal);
        Assert.Contains("-REF", refund.ReceiptNumber);

        // 5. Stock must be restored by 1 unit: 8 + 1 = 9
        Assert.Equal(9.0m, await _catalog.GetStockOnHandAsync(product.ProductId));

        // 6. Original sale must remain in database (never deleted!)
        var originalAfterRefund = await _sale.GetSaleByIdAsync(sale.SaleId);
        Assert.NotNull(originalAfterRefund);
        Assert.Equal(sale.ReceiptNumber, originalAfterRefund.ReceiptNumber);

        // 7. Shift cash refunds must be updated to 3500.00
        var activeShift = await _shift.GetActiveShiftAsync("B01", "C01");
        Assert.NotNull(activeShift);
        Assert.Equal(3500.00m, activeShift.CashRefunds);

        // 8. Verify audit event was logged
        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'REFUND_SALE';";
            Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));

            using var outboxCmd = conn.CreateCommand();
            outboxCmd.CommandText = "SELECT COUNT(*), causal_reference FROM outbox_events WHERE causal_reference = $ref;";
            outboxCmd.Parameters.AddWithValue("$ref", sale.SaleId.ToString());
            using var reader = await outboxCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(sale.SaleId.ToString(), reader.GetString(1));
        }
    }

    [Fact]
    public async Task A08_CancelSale_RequiresReason_PreservesRow_ReversesStockAndCash()
    {
        var cashier = await _auth.CreateUserAsync("amal", "Amal Perera", "Pass#123", Role.Cashier);
        var product = new Product
        {
            ProductId = "prod_cable_01",
            Barcode = "8902223334445",
            Name = "Clutch Cable Pulsar 150",
            UnitPrice = 1200.00m,
            CostBasis = 800.00m,
            TaxRate = 0.0m,
            StockOnHand = 5.0m
        };
        await _catalog.AddProductAsync(product);

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        // 1. Commit sale (2 units = 2400 LKR)
        var saleCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(ProductId: product.ProductId, Quantity: 2.0m)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType: TenderType.CASH, AmountTendered: 3000.00m) // 600 change
            }
        );

        var sale = await _sale.CommitSaleAsync(saleCmd);
        Assert.Equal(3.0m, await _catalog.GetStockOnHandAsync(product.ProductId)); // 5 - 2

        // 2. Cancellation without reason must FAIL
        var blankReasonCmd = new CancelSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            SaleId: sale.SaleId,
            Reason: "   "
        );
        await Assert.ThrowsAsync<ArgumentException>(() => _sale.CancelSaleAsync(blankReasonCmd));

        // 3. Valid cancellation with reason
        var validCancelCmd = new CancelSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            SaleId: sale.SaleId,
            Reason: "Customer card declined on separate terminal, walked out without taking goods"
        );

        var cancelled = await _sale.CancelSaleAsync(validCancelCmd);
        Assert.Equal(SaleStatus.Cancelled, cancelled.Status);

        // 4. ORIGINAL RECORD MUST BE PRESERVED (not deleted!)
        var saleInDb = await _sale.GetSaleByIdAsync(sale.SaleId);
        Assert.NotNull(saleInDb);
        Assert.Equal(SaleStatus.Cancelled, saleInDb.Status);

        // 5. Stock restored to 5.0
        Assert.Equal(5.0m, await _catalog.GetStockOnHandAsync(product.ProductId));

        // 6. Shift cash refunds updated by net cash from sale (2400.00)
        var activeShift = await _shift.GetActiveShiftAsync("B01", "C01");
        Assert.NotNull(activeShift);
        Assert.Equal(2400.00m, activeShift.CashRefunds);

        // 7. Audit event recorded
        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'CANCEL_SALE';";
            Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task A11_CrossCounterRefund_RejectedAtOriginAuthority()
    {
        var cashier = await _auth.CreateUserAsync("sunil", "Sunil Silva", "Pass#123", Role.Cashier);
        var product = new Product
        {
            ProductId = "prod_gasket_01",
            Barcode = "8903334445556",
            Name = "Engine Head Gasket",
            UnitPrice = 1500.00m,
            CostBasis = 1000.00m,
            TaxRate = 0.0m,
            StockOnHand = 10.0m
        };
        await _catalog.AddProductAsync(product);

        var shiftC01 = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        // Sale originated on Counter C01
        var saleCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shiftC01.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(ProductId: product.ProductId, Quantity: 1.0m)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType: TenderType.CASH, AmountTendered: 1500.00m)
            }
        );

        var sale = await _sale.CommitSaleAsync(saleCmd);

        // Attempting to refund from Counter C02 must be REJECTED under origin-authority rule (A11)
        var crossCounterCmd = new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C02", // Different counter!
            CashierId: cashier.UserId,
            ShiftId: shiftC01.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest>
            {
                new(LineId: sale.Lines[0].LineId, QuantityToRefund: 1.0m)
            },
            Reason: "Cross counter return attempt"
        );

        var ex = await Assert.ThrowsAsync<PosException>(() => _sale.RefundSaleAsync(crossCounterCmd));
        Assert.Contains("Cross-counter refund not permitted offline", ex.Message);
    }
}
