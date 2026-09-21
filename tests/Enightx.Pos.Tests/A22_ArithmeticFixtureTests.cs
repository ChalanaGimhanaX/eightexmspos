using System.Text.Json;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class A22_ArithmeticFixtureTests
{
    private class FixtureLineTest
    {
        public string name { get; set; } = "";
        public decimal quantity { get; set; }
        public decimal unit_price { get; set; }
        public decimal discount_rate { get; set; }
        public decimal discount_fixed { get; set; }
        public decimal tax_rate { get; set; }
        public decimal expected_line_subtotal { get; set; }
        public decimal expected_discount_amount { get; set; }
        public decimal expected_tax_amount { get; set; }
        public decimal expected_line_total { get; set; }
    }

    private class FixtureSaleLine
    {
        public decimal quantity { get; set; }
        public decimal unit_price { get; set; }
        public decimal discount_rate { get; set; }
        public decimal discount_fixed { get; set; }
        public decimal tax_rate { get; set; }
    }

    private class FixtureTender
    {
        public string type { get; set; } = "";
        public decimal amount { get; set; }
    }

    private class FixtureSaleTest
    {
        public string name { get; set; } = "";
        public List<FixtureSaleLine> lines { get; set; } = new();
        public decimal? cash_tendered { get; set; }
        public List<FixtureTender>? tenders { get; set; }
        public decimal expected_subtotal { get; set; }
        public decimal expected_discount_total { get; set; }
        public decimal expected_tax_total { get; set; }
        public decimal expected_grand_total { get; set; }
        public decimal expected_change { get; set; }
    }

    private class FixtureRoot
    {
        public string version { get; set; } = "";
        public string currency { get; set; } = "";
        public List<FixtureLineTest> line_tests { get; set; } = new();
        public List<FixtureSaleTest> sale_tests { get; set; } = new();
    }

    [Fact]
    public void A22_SharedArithmeticFixtures_MatchExactRoundingRules()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "../../../../../contracts/fixtures/arithmetic_fixtures.json");
        Assert.True(File.Exists(fixturePath), $"Fixture file not found at {fixturePath}");

        var json = File.ReadAllText(fixturePath);
        var data = JsonSerializer.Deserialize<FixtureRoot>(json);
        Assert.NotNull(data);
        Assert.Equal("LKR", data.currency);

        foreach (var testCase in data.line_tests)
        {
            var result = MoneyCalculator.CalculateLine(
                testCase.quantity,
                testCase.unit_price,
                testCase.discount_rate,
                testCase.discount_fixed,
                testCase.tax_rate
            );

            Assert.True(
                testCase.expected_line_subtotal == result.Subtotal,
                $"Subtotal mismatch in '{testCase.name}': expected {testCase.expected_line_subtotal}, got {result.Subtotal}"
            );

            Assert.True(
                testCase.expected_discount_amount == result.DiscountAmount,
                $"Discount mismatch in '{testCase.name}': expected {testCase.expected_discount_amount}, got {result.DiscountAmount}"
            );

            Assert.True(
                testCase.expected_tax_amount == result.TaxAmount,
                $"Tax mismatch in '{testCase.name}': expected {testCase.expected_tax_amount}, got {result.TaxAmount}"
            );

            Assert.True(
                testCase.expected_line_total == result.LineTotal,
                $"Line total mismatch in '{testCase.name}': expected {testCase.expected_line_total}, got {result.LineTotal}"
            );
        }
    }

    [Fact]
    public async Task A22_SaleLevelFixtures_And_OrderIndependentSplitTender()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "../../../../../contracts/fixtures/arithmetic_fixtures.json");
        var json = File.ReadAllText(fixturePath);
        var data = JsonSerializer.Deserialize<FixtureRoot>(json);
        Assert.NotNull(data);

        using var db = PosDatabase.CreateInMemory();
        var catalog = new CatalogService(db);
        var auth = new AuthService(db);
        var shiftService = new ShiftService(db);
        var saleService = new SaleService(db, catalog);

        var cashier = await auth.CreateUserAsync("kasun", "Kasun Perera", "Pass123!", Role.Cashier);
        var shift = await shiftService.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        int prodIndex = 1;
        foreach (var saleCase in data.sale_tests)
        {
            var items = new List<CreateSaleLineRequest>();
            foreach (var l in saleCase.lines)
            {
                var prodId = $"prod_fixture_{prodIndex++}";
                await catalog.AddProductAsync(new Product
                {
                    ProductId = prodId,
                    Barcode = $"BCODE_{prodId}",
                    Name = $"Fixture Item {prodIndex}",
                    UnitPrice = l.unit_price,
                    CostBasis = l.unit_price * 0.7m,
                    TaxRate = l.tax_rate,
                    StockOnHand = 1000m
                });

                items.Add(new CreateSaleLineRequest(
                    ProductId: prodId,
                    Quantity: l.quantity,
                    DiscountRate: l.discount_rate,
                    DiscountFixed: l.discount_fixed
                ));
            }

            var tenders = new List<CreateTenderRequest>();
            if (saleCase.cash_tendered.HasValue)
            {
                tenders.Add(new CreateTenderRequest(TenderType.CASH, saleCase.cash_tendered.Value));
            }
            else if (saleCase.tenders != null)
            {
                foreach (var t in saleCase.tenders)
                {
                    tenders.Add(new CreateTenderRequest(Enum.Parse<TenderType>(t.type), t.amount));
                }
            }

            var cmd = new CreateSaleCommand(
                TenantId: "TENANT_LK_01",
                BranchId: "B01",
                CounterId: "C01",
                CashierId: cashier.UserId,
                ShiftId: shift.ShiftId,
                Items: items,
                Tenders: tenders
            );

            var sale = await saleService.CommitSaleAsync(cmd);
            Assert.Equal(saleCase.expected_subtotal, sale.Subtotal);
            Assert.Equal(saleCase.expected_discount_total, sale.DiscountTotal);
            Assert.Equal(saleCase.expected_tax_total, sale.TaxTotal);
            Assert.Equal(saleCase.expected_grand_total, sale.GrandTotal);

            var totalChange = sale.Tenders.Sum(t => t.ChangeGiven);
            Assert.Equal(saleCase.expected_change, totalChange);
        }

        // Test CRITICAL fix: Split tender where CASH is passed BEFORE CARD
        // Total = 2450.00. Tenders: [CASH 1500.00, CARD 1000.00]. Change must be 50.00.
        var splitProdId = "prod_split_rev";
        await catalog.AddProductAsync(new Product
        {
            ProductId = splitProdId,
            Barcode = "BCODE_SPLIT_REV",
            Name = "Split Item Reverse",
            UnitPrice = 490.00m,
            CostBasis = 300.00m,
            TaxRate = 0.0m,
            StockOnHand = 50m
        });

        var reverseSplitCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(ProductId: splitProdId, Quantity: 5.0m)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType: TenderType.CASH, AmountTendered: 1500.00m), // CASH first!
                new(TenderType: TenderType.CARD, AmountTendered: 1000.00m)  // CARD second!
            }
        );

        var revSale = await saleService.CommitSaleAsync(reverseSplitCmd);
        Assert.Equal(2450.00m, revSale.GrandTotal);
        Assert.Equal(50.00m, revSale.Tenders.Sum(t => t.ChangeGiven));
    }
}
