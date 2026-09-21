using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class A01_PrinterFailureTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _sale;
    private readonly MemoryPrinterService _printer;
    private readonly ReceiptService _receipt;

    public A01_PrinterFailureTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _catalog = new CatalogService(_db);
        _shift = new ShiftService(_db);
        _sale = new SaleService(_db, _catalog);
        _printer = new MemoryPrinterService();
        _receipt = new ReceiptService(_db, _sale, _printer);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task A01_CashSaleCommits_PrinterFails_ReprintCreatesNoSecondSale()
    {
        // 1. Setup staff, product, and open shift
        var cashier = await _auth.CreateUserAsync("kasun", "Kasun Perera", "SecurePass123!", Role.Cashier);
        var product = new Product
        {
            ProductId = "prod_plug_01",
            Barcode = "4964336000011",
            Name = "NGK Spark Plug BP6ES",
            UnitPrice = 500.00m,
            CostBasis = 350.00m,
            TaxRate = 0.18m,
            StockOnHand = 20.0m
        };
        await _catalog.AddProductAsync(product);

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        // 2. Commit a complete cash sale
        // 2 units @ 500.00 = 1000.00, 10% disc = 100.00 net 900.00, 18% tax = 162.00, total = 1062.00
        // Tendered: 1500.00 Cash, Expected Change: 438.00
        var saleCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(ProductId: product.ProductId, Quantity: 2.0m, DiscountRate: 0.10m)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType: TenderType.CASH, AmountTendered: 1500.00m)
            }
        );

        var committedSale = await _sale.CommitSaleAsync(saleCmd);

        Assert.NotNull(committedSale);
        Assert.Equal(1062.00m, committedSale.GrandTotal);
        Assert.Equal("B01-C01-000001", committedSale.ReceiptNumber);

        // 3. Printer fails during initial print
        var printResult = await _receipt.PrintSaleReceiptAsync(committedSale.SaleId, simulateHardwareFailure: true);

        Assert.False(printResult.Success);
        Assert.NotNull(printResult.ErrorMessage);
        Assert.True(printResult.CanReprint);
        Assert.Empty(_printer.PrintedJobs); // Hardware did not receive output

        // 4. Verify transaction state remains committed once despite printer failure
        var salesCount = await _sale.GetSaleCountAsync();
        Assert.Equal(1, salesCount);

        var stockOnHand = await _catalog.GetStockOnHandAsync(product.ProductId);
        Assert.Equal(18.0m, stockOnHand); // 20 - 2

        using (var conn = _db.CreateConnection())
        {
            // Verify tenders count
            using var cmdTender = conn.CreateCommand();
            cmdTender.CommandText = "SELECT COUNT(*), change_given FROM tenders WHERE sale_id = $sid;";
            cmdTender.Parameters.AddWithValue("$sid", committedSale.SaleId.ToString());
            using var reader = await cmdTender.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(438.00m, reader.GetDecimal(1));
        }

        using (var conn = _db.CreateConnection())
        {
            // Verify outbox count
            using var cmdOutbox = conn.CreateCommand();
            cmdOutbox.CommandText = "SELECT COUNT(*), source_sequence, status FROM outbox_events WHERE branch_id = 'B01';";
            using var reader = await cmdOutbox.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal("PENDING", reader.GetString(2));
        }

        // 5. Cashier reprints the receipt after clearing paper jam
        var reprintResult = await _receipt.ReprintSaleReceiptAsync(
            committedSale.SaleId,
            cashier.UserId,
            reason: "Paper jam cleared",
            simulateHardwareFailure: false
        );

        Assert.True(reprintResult.Success);
        Assert.Single(_printer.PrintedJobs);
        Assert.Contains("*** DUPLICATE / REPRINT RECEIPT ***", _printer.PrintedJobs[0]);
        Assert.Contains("Reprint Count: 1", _printer.PrintedJobs[0]);
        Assert.Contains("B01-C01-000001", _printer.PrintedJobs[0]);

        // 6. CRITICAL A01 CHECK: Reprint creates NO second sale, duplicate tender, stock, or outbox
        Assert.Equal(1, await _sale.GetSaleCountAsync());

        using (var conn = _db.CreateConnection())
        {
            using var cmdLines = conn.CreateCommand();
            cmdLines.CommandText = "SELECT COUNT(*) FROM sale_lines;";
            Assert.Equal(1, Convert.ToInt32(await cmdLines.ExecuteScalarAsync()));

            using var cmdTenders = conn.CreateCommand();
            cmdTenders.CommandText = "SELECT COUNT(*) FROM tenders;";
            Assert.Equal(1, Convert.ToInt32(await cmdTenders.ExecuteScalarAsync()));

            using var cmdStock = conn.CreateCommand();
            cmdStock.CommandText = "SELECT COUNT(*) FROM stock_movements;";
            Assert.Equal(1, Convert.ToInt32(await cmdStock.ExecuteScalarAsync()));

            using var cmdOutbox = conn.CreateCommand();
            cmdOutbox.CommandText = "SELECT COUNT(*) FROM outbox_events;";
            Assert.Equal(1, Convert.ToInt32(await cmdOutbox.ExecuteScalarAsync()));
        }

        // Stock is still 18.0 (not decremented twice)
        Assert.Equal(18.0m, await _catalog.GetStockOnHandAsync(product.ProductId));
    }
}
