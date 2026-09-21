using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class A02_CrashRecoveryTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _sale;

    public A02_CrashRecoveryTests()
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
    public async Task A02_ProcessTerminatesDuringCommit_ZeroPartialStateRemains()
    {
        // 1. Setup user, catalog, and shift
        var cashier = await _auth.CreateUserAsync("nimal", "Nimal Silva", "Passw0rd!123", Role.Cashier);
        var product = new Product
        {
            ProductId = "prod_oil_01",
            Barcode = "8901234567890",
            Name = "Castrol Actevo 4T 20W-40 1L",
            UnitPrice = 2450.00m,
            CostBasis = 1900.00m,
            TaxRate = 0.0m,
            StockOnHand = 15.0m
        };
        await _catalog.AddProductAsync(product);

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 10000.00m, "TENANT_LK_01");

        // 2. Prepare sale command
        var saleCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(ProductId: product.ProductId, Quantity: 3.0m)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType: TenderType.CASH, AmountTendered: 10000.00m)
            }
        );

        // 3. Inject simulated process termination / unhandled crash right before tx.Commit()
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _sale.CommitSaleAsync(saleCmd, failureHook: (conn, tx) =>
            {
                throw new InvalidOperationException("FATAL: Simulated kernel panic / sudden power outage during commit!");
            });
        });

        // 4. Verify ZERO partial transaction effects exist in database
        Assert.Equal(0, await _sale.GetSaleCountAsync());

        using (var conn = _db.CreateConnection())
        {
            using var cmdLines = conn.CreateCommand();
            cmdLines.CommandText = "SELECT COUNT(*) FROM sale_lines;";
            Assert.Equal(0, Convert.ToInt32(await cmdLines.ExecuteScalarAsync()));

            using var cmdTenders = conn.CreateCommand();
            cmdTenders.CommandText = "SELECT COUNT(*) FROM tenders;";
            Assert.Equal(0, Convert.ToInt32(await cmdTenders.ExecuteScalarAsync()));

            using var cmdStock = conn.CreateCommand();
            cmdStock.CommandText = "SELECT COUNT(*) FROM stock_movements WHERE movement_type = 'SALE';";
            Assert.Equal(0, Convert.ToInt32(await cmdStock.ExecuteScalarAsync()));

            using var cmdOutbox = conn.CreateCommand();
            cmdOutbox.CommandText = "SELECT COUNT(*) FROM outbox_events;";
            Assert.Equal(0, Convert.ToInt32(await cmdOutbox.ExecuteScalarAsync()));

            using var cmdAudit = conn.CreateCommand();
            cmdAudit.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'COMMIT_SALE';";
            Assert.Equal(0, Convert.ToInt32(await cmdAudit.ExecuteScalarAsync()));

            using var cmdSeq = conn.CreateCommand();
            cmdSeq.CommandText = "SELECT COUNT(*) FROM receipt_sequences;";
            Assert.Equal(0, Convert.ToInt32(await cmdSeq.ExecuteScalarAsync()));
        }

        // Product stock on hand must remain unaffected (15.0)
        Assert.Equal(15.0m, await _catalog.GetStockOnHandAsync(product.ProductId));

        // Shift cash totals must remain unaffected
        var activeShift = await _shift.GetActiveShiftAsync("B01", "C01");
        Assert.NotNull(activeShift);
        Assert.Equal(0m, activeShift.CashReceived);
        Assert.Equal(0m, activeShift.ChangeGiven);

        // 5. Subsequent valid sale commits without issue
        var successfulSale = await _sale.CommitSaleAsync(saleCmd);
        Assert.NotNull(successfulSale);
        Assert.Equal(1, await _sale.GetSaleCountAsync());
        Assert.Equal("B01-C01-000001", successfulSale.ReceiptNumber);
        Assert.Equal(12.0m, await _catalog.GetStockOnHandAsync(product.ProductId));
    }
}
