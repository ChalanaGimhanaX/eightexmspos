using System.Text.Json;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class A06_NegativeStockTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _sale;

    public A06_NegativeStockTests()
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
    public async Task A06_SellingMoreThanStock_AllowsSale_FlagsNegativeStock_And_RecordsWarning()
    {
        // Rule 26: Stock shortage: Warn, allow sale, record the warning, and flag negative stock
        var cashier = await _auth.CreateUserAsync("ranil", "Ranil Wickrama", "Pass#123", Role.Cashier);
        var product = new Product
        {
            ProductId = "prod_battery_01",
            Barcode = "8905556667778",
            Name = "Maintenance Free Battery 12V 5Ah",
            UnitPrice = 6500.00m,
            CostBasis = 4800.00m,
            TaxRate = 0.0m,
            StockOnHand = 1.0m // Only 1 unit in stock!
        };
        await _catalog.AddProductAsync(product);

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        // Customer wants 3 units (2 units short!)
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
                new(TenderType: TenderType.CASH, AmountTendered: 19500.00m)
            }
        );

        // Sale must NOT be rejected; local autonomous billing allows sale
        var sale = await _sale.CommitSaleAsync(saleCmd);
        Assert.NotNull(sale);
        Assert.Equal(19500.00m, sale.GrandTotal);

        // Stock on hand becomes NEGATIVE (-2.0)
        var stockAfterSale = await _catalog.GetStockOnHandAsync(product.ProductId);
        Assert.Equal(-2.0m, stockAfterSale);

        // Verify audit event records StockShortageWarning
        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT details_json FROM audit_events WHERE action = 'COMMIT_SALE';";
            var detailsJson = (string)(await cmd.ExecuteScalarAsync())!;
            using var doc = JsonDocument.Parse(detailsJson);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("StockShortageWarning").GetBoolean());
            var shortages = root.GetProperty("Shortages");
            Assert.Equal(1, shortages.GetArrayLength());
            Assert.Equal(product.ProductId, shortages[0].GetProperty("ProductId").GetString());
            Assert.Equal(1.0m, shortages[0].GetProperty("StockOnHand").GetDecimal());
            Assert.Equal(3.0m, shortages[0].GetProperty("RequestedQuantity").GetDecimal());
        }

        // Verify outbox payload records stock shortage flag
        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload_json FROM outbox_events WHERE branch_id = 'B01';";
            var payloadJson = (string)(await cmd.ExecuteScalarAsync())!;
            using var doc = JsonDocument.Parse(payloadJson);
            Assert.True(doc.RootElement.GetProperty("stock_shortage_warning").GetBoolean());
        }
    }
}

