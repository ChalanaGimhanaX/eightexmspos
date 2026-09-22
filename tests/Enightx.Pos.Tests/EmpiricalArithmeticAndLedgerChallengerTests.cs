using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class EmpiricalArithmeticAndLedgerChallengerTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _sale;
    private readonly CustomerService _customer;
    private readonly StockCountService _stockCount;

    public EmpiricalArithmeticAndLedgerChallengerTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _catalog = new CatalogService(_db);
        _shift = new ShiftService(_db);
        _sale = new SaleService(_db, _catalog);
        _customer = new CustomerService(_db, _shift);
        _stockCount = new StockCountService(_db, _catalog);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    #region 1. Precision & Rounding Verification (MidpointRounding.AwayFromZero)

    [Theory]
    [InlineData(0.005, 0.01, 0.00)]   // 0.005 -> AwayFromZero: 0.01, ToEven: 0.00
    [InlineData(0.015, 0.02, 0.02)]   // 0.015 -> AwayFromZero: 0.02, ToEven: 0.02
    [InlineData(0.025, 0.03, 0.02)]   // 0.025 -> AwayFromZero: 0.03, ToEven: 0.02
    [InlineData(0.035, 0.04, 0.04)]   // 0.035 -> AwayFromZero: 0.04, ToEven: 0.04
    [InlineData(0.045, 0.05, 0.04)]   // 0.045 -> AwayFromZero: 0.05, ToEven: 0.04
    [InlineData(0.055, 0.06, 0.06)]   // 0.055 -> AwayFromZero: 0.06, ToEven: 0.06
    [InlineData(0.065, 0.07, 0.06)]   // 0.065 -> AwayFromZero: 0.07, ToEven: 0.06
    [InlineData(0.075, 0.08, 0.08)]   // 0.075 -> AwayFromZero: 0.08, ToEven: 0.08
    [InlineData(0.085, 0.09, 0.08)]   // 0.085 -> AwayFromZero: 0.09, ToEven: 0.08
    [InlineData(0.095, 0.10, 0.10)]   // 0.095 -> AwayFromZero: 0.10, ToEven: 0.10
    [InlineData(-0.005, -0.01, 0.00)] // Negative midpoints
    [InlineData(-0.015, -0.02, -0.02)]
    [InlineData(-0.025, -0.03, -0.02)]
    [InlineData(-0.035, -0.04, -0.04)]
    [InlineData(-0.045, -0.05, -0.04)]
    public void PrecisionRounding_MidpointAwayFromZero_NeverUsesBankersRounding(
        decimal input, decimal expectedAwayFromZero, decimal expectedToEven)
    {
        // Act
        var result = MoneyCalculator.Round(input);

        // Assert
        Assert.Equal(expectedAwayFromZero, result);

        // Verify difference against Banker's rounding where applicable
        var bankers = Math.Round(input, 2, MidpointRounding.ToEven);
        Assert.Equal(expectedToEven, bankers);

        if (expectedAwayFromZero != expectedToEven)
        {
            Assert.NotEqual(bankers, result);
        }
    }

    [Fact]
    public void MovingWeightedAverageCost_OddQuantitiesAndCosts_RoundsStrictAwayFromZero()
    {
        // 3 items @ 10.55 LKR + 7 items @ 15.35 LKR
        // total value = (3 * 10.55) + (7 * 15.35) = 31.65 + 107.45 = 139.10
        // new qty = 10 -> 139.10 / 10 = 13.91
        var cost1 = MoneyCalculator.CalculateMovingWeightedAverageCost(3m, 10.55m, 7m, 15.35m);
        Assert.Equal(13.91m, cost1);

        // Fractional quantity: 1.5 kg @ 100.00 + 2.5 kg @ 200.00 = 150 + 500 = 650 / 4.0 = 162.50
        var cost2 = MoneyCalculator.CalculateMovingWeightedAverageCost(1.5m, 100.00m, 2.5m, 200.00m);
        Assert.Equal(162.50m, cost2);

        // Edge case: zero current stock
        var cost3 = MoneyCalculator.CalculateMovingWeightedAverageCost(0m, 0m, 5m, 88.885m);
        Assert.Equal(88.89m, cost3); // 88.885 -> 88.89
    }

    #endregion

    #region 2. Tax Calculations with VAT 18% & Complex Discounts across Multi-Item Sales

    [Fact]
    public async Task MultiItemSale_Vat18AndComplexDiscounts_PreservesLineTotalsAndGrandTotalZeroDrift()
    {
        var cashier = await _auth.CreateUserAsync($"cashier_tax_{Guid.NewGuid():N}", "Tax Cashier", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 10000.00m, "TENANT_LK_01");

        // Create 5 test products with 18% VAT and 0% VAT
        var p1 = new Product { ProductId = "p_vat_1", Barcode = "BC_VAT_1", Name = "Engine Oil 1L", UnitPrice = 1455.50m, CostBasis = 1000m, TaxRate = 0.18m, StockOnHand = 100m };
        var p2 = new Product { ProductId = "p_vat_2", Barcode = "BC_VAT_2", Name = "Air Filter", UnitPrice = 850.75m, CostBasis = 600m, TaxRate = 0.18m, StockOnHand = 100m };
        var p3 = new Product { ProductId = "p_vat_3", Barcode = "BC_VAT_3", Name = "Brake Pads", UnitPrice = 2400.00m, CostBasis = 1700m, TaxRate = 0.18m, StockOnHand = 100m };
        var p4 = new Product { ProductId = "p_vat_4", Barcode = "BC_VAT_4", Name = "Wiper Fluid (0% Tax)", UnitPrice = 450.25m, CostBasis = 300m, TaxRate = 0.00m, StockOnHand = 100m };
        var p5 = new Product { ProductId = "p_vat_5", Barcode = "BC_VAT_5", Name = "Loose Grease (Fractional)", UnitPrice = 320.00m, CostBasis = 200m, TaxRate = 0.18m, StockOnHand = 100m };

        await _catalog.AddProductAsync(p1);
        await _catalog.AddProductAsync(p2);
        await _catalog.AddProductAsync(p3);
        await _catalog.AddProductAsync(p4);
        await _catalog.AddProductAsync(p5);

        // Lines:
        // 1. p1: qty 2.0, rate 0.18 tax, discount 10% (0.10)
        //    subtotal = 2 * 1455.50 = 2911.00
        //    discount = round(2911.00 * 0.10) = 291.10
        //    net = 2911.00 - 291.10 = 2619.90
        //    tax = round(2619.90 * 0.18) = round(471.582) = 471.58
        //    line_total = 2619.90 + 471.58 = 3091.48

        // 2. p2: qty 3.0, rate 0.18 tax, fixed discount 50.00
        //    subtotal = 3 * 850.75 = 2552.25
        //    discount = 50.00
        //    net = 2552.25 - 50.00 = 2502.25
        //    tax = round(2502.25 * 0.18) = round(450.405) = 450.41 (0.405 -> 0.41 AwayFromZero!)
        //    line_total = 2502.25 + 450.41 = 2952.66

        // 3. p3: qty 1.0, rate 0.18 tax, combined discount: 5% rate + 100.00 fixed
        //    subtotal = 1 * 2400.00 = 2400.00
        //    discount = round((2400 * 0.05) + 100) = 120 + 100 = 220.00
        //    net = 2400.00 - 220.00 = 2180.00
        //    tax = round(2180.00 * 0.18) = round(392.40) = 392.40
        //    line_total = 2180.00 + 392.40 = 2572.40

        // 4. p4: qty 4.0, rate 0.00 tax, 0 discount
        //    subtotal = 4 * 450.25 = 1801.00
        //    discount = 0.00
        //    tax = 0.00
        //    line_total = 1801.00

        // 5. p5: qty 1.75 kg, rate 0.18 tax, 0 discount
        //    subtotal = round(1.75 * 320.00) = 560.00
        //    discount = 0.00
        //    tax = round(560.00 * 0.18) = 100.80
        //    line_total = 560.00 + 100.80 = 660.80

        // Aggregates:
        // Subtotal = 2911.00 + 2552.25 + 2400.00 + 1801.00 + 560.00 = 10224.25
        // DiscountTotal = 291.10 + 50.00 + 220.00 + 0 + 0 = 561.10
        // TaxTotal = 471.58 + 450.41 + 392.40 + 0 + 100.80 = 1415.19
        // GrandTotal = 3091.48 + 2952.66 + 2572.40 + 1801.00 + 660.80 = 11078.34

        var cmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(p1.ProductId, 2.0m, DiscountRate: 0.10m),
                new(p2.ProductId, 3.0m, DiscountFixed: 50.00m),
                new(p3.ProductId, 1.0m, DiscountRate: 0.05m, DiscountFixed: 100.00m),
                new(p4.ProductId, 4.0m),
                new(p5.ProductId, 1.75m)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType.CASH, 12000.00m)
            }
        );

        var sale = await _sale.CommitSaleAsync(cmd);

        Assert.Equal(10224.25m, sale.Subtotal);
        Assert.Equal(561.10m, sale.DiscountTotal);
        Assert.Equal(1415.19m, sale.TaxTotal);
        Assert.Equal(11078.34m, sale.GrandTotal);

        // Verify line totals sum exactly to GrandTotal without 1 cent discrepancy
        Assert.Equal(sale.GrandTotal, sale.Lines.Sum(l => l.LineTotal));
        Assert.Equal(sale.Subtotal, sale.Lines.Sum(l => MoneyCalculator.Round(l.Quantity * l.UnitPrice)));
        Assert.Equal(sale.DiscountTotal, sale.Lines.Sum(l => l.DiscountAmount));
        Assert.Equal(sale.TaxTotal, sale.Lines.Sum(l => l.TaxAmount));

        // Cash change = 12,000.00 - 11,078.34 = 921.66
        Assert.Equal(921.66m, sale.Tenders[0].ChangeGiven);
    }

    [Fact]
    public void DiscountCapping_ExceedingSubtotal_CapsAtSubtotalWithZeroTaxAndLineTotal()
    {
        // Subtotal = 500.00, fixed discount = 600.00 -> discount capped at 500.00, tax = 0.00, line_total = 0.00
        var res = MoneyCalculator.CalculateLine(1.0m, 500.00m, discountFixed: 600.00m, taxRate: 0.18m);
        Assert.Equal(500.00m, res.Subtotal);
        Assert.Equal(500.00m, res.DiscountAmount);
        Assert.Equal(0.00m, res.TaxAmount);
        Assert.Equal(0.00m, res.LineTotal);
    }

    #endregion

    #region 3. Tender Change Calculation with Mixed Payment Types and Odd Amounts

    [Fact]
    public async Task TenderChange_MixedPayments_OrderIndependent_ExactOddChange()
    {
        var cashier = await _auth.CreateUserAsync($"cashier_tender_{Guid.NewGuid():N}", "Tender Cashier", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var prod = new Product
        {
            ProductId = "prod_odd_tender",
            Barcode = "BC_ODD_TENDER",
            Name = "Odd Priced Assembly",
            UnitPrice = 3456.77m,
            CostBasis = 2000m,
            TaxRate = 0.00m,
            StockOnHand = 100m
        };
        await _catalog.AddProductAsync(prod);

        // Grand total = 3,456.77 LKR.
        // Mixed tender: CARD 1,000.00 + QR 456.77 + CASH 2,500.00 = 3,956.77 tendered.
        // Non-cash total = 1,456.77.
        // Cash needed = 3,456.77 - 1,456.77 = 2,000.00.
        // Cash tendered = 2,500.00.
        // Cash change = 2,500.00 - 2,000.00 = 500.00.

        // Test 1: Order: CARD, QR, CASH
        var cmd1 = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType.CARD, 1000.00m, "CARD_TX_1"),
                new(TenderType.QR, 456.77m, "QR_TX_1"),
                new(TenderType.CASH, 2500.00m)
            }
        );
        var sale1 = await _sale.CommitSaleAsync(cmd1);
        Assert.Equal(3456.77m, sale1.GrandTotal);
        var change1 = sale1.Tenders.Sum(t => t.ChangeGiven);
        Assert.Equal(500.00m, change1);
        Assert.Equal(0.00m, sale1.Tenders.First(t => t.TenderType == TenderType.CARD).ChangeGiven);
        Assert.Equal(0.00m, sale1.Tenders.First(t => t.TenderType == TenderType.QR).ChangeGiven);
        Assert.Equal(500.00m, sale1.Tenders.First(t => t.TenderType == TenderType.CASH).ChangeGiven);

        // Test 2: Reverse order: CASH first, then QR, then CARD
        var cmd2 = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType.CASH, 2500.00m), // CASH first!
                new(TenderType.QR, 456.77m, "QR_TX_2"),
                new(TenderType.CARD, 1000.00m, "CARD_TX_2")
            }
        );
        var sale2 = await _sale.CommitSaleAsync(cmd2);
        Assert.Equal(3456.77m, sale2.GrandTotal);
        var change2 = sale2.Tenders.Sum(t => t.ChangeGiven);
        Assert.Equal(500.00m, change2);
        Assert.Equal(500.00m, sale2.Tenders.First(t => t.TenderType == TenderType.CASH).ChangeGiven);

        // Test 3: Multiple Cash notes/tenders (CASH 1500 + CASH 1000)
        var cmd3 = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType.CARD, 1456.77m, "CARD_TX_3"),
                new(TenderType.CASH, 1500.00m),
                new(TenderType.CASH, 1000.00m)
            }
        );
        var sale3 = await _sale.CommitSaleAsync(cmd3);
        Assert.Equal(3456.77m, sale3.GrandTotal);
        Assert.Equal(500.00m, sale3.Tenders.Sum(t => t.ChangeGiven));
    }

    [Fact]
    public async Task TenderChange_AdversarialBoundaryViolations_ThrowExpectedExceptions()
    {
        var cashier = await _auth.CreateUserAsync($"cashier_err_{Guid.NewGuid():N}", "Err Cashier", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var prod = new Product
        {
            ProductId = "prod_err_item",
            Barcode = "BC_ERR_ITEM",
            Name = "Boundary Check Item",
            UnitPrice = 2000.00m,
            CostBasis = 1000m,
            TaxRate = 0.00m,
            StockOnHand = 100m
        };
        await _catalog.AddProductAsync(prod);

        // 1. Total tendered less than grand total
        var underCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01", BranchId: "B01", CounterId: "C01", CashierId: cashier.UserId, ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1999.99m) }
        );
        await Assert.ThrowsAsync<InsufficientTenderException>(() => _sale.CommitSaleAsync(underCmd));

        // 2. Non-cash tender exceeds grand total
        var overCardCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01", BranchId: "B01", CounterId: "C01", CashierId: cashier.UserId, ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CARD, 2500.00m) }
        );
        await Assert.ThrowsAsync<PosException>(() => _sale.CommitSaleAsync(overCardCmd));

        // 3. Cash tendered less than cash needed (when combined with non-cash)
        // Grand total = 2000, Card = 1500, Cash needed = 500. Cash tendered = 400.
        var underCashCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01", BranchId: "B01", CounterId: "C01", CashierId: cashier.UserId, ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType.CARD, 1500.00m),
                new(TenderType.CASH, 400.00m)
            }
        );
        await Assert.ThrowsAsync<InsufficientTenderException>(() => _sale.CommitSaleAsync(underCashCmd));
    }

    #endregion

    #region 4. Stock Count Variance Values with Odd Unit Costs and Fractional Stock on Hand

    [Fact]
    public async Task StockCount_OddUnitCostsAndFractionalStock_StrictAwayFromZeroAndJournal()
    {
        var manager = await _auth.CreateUserAsync($"mgr_stock_{Guid.NewGuid():N}", "Stock Manager", "pass123", Role.Manager);

        // Product 1: Fractional stock snapshot = 5.25 kg, cost basis = 105.555 LKR
        // Counted = 2.75 kg. Variance Qty = 2.75 - 5.25 = -2.50 kg.
        // Variance Value = -2.50 * 105.555 = -263.8875 -> AwayFromZero: -263.89
        var p1 = new Product
        {
            ProductId = "prod_var_1",
            Barcode = "BC_VAR_1",
            Name = "Bulk Rivets (Odd Cost)",
            UnitPrice = 200.00m,
            CostBasis = 105.555m,
            TaxRate = 0.00m,
            StockOnHand = 5.25m
        };

        // Product 2: Snapshot = 1.25 kg, counted = 3.75 kg, cost basis = 12.345 LKR
        // Variance Qty = 3.75 - 1.25 = +2.50 kg.
        // Variance Value = +2.50 * 12.345 = +30.8625 -> AwayFromZero: +30.86
        var p2 = new Product
        {
            ProductId = "prod_var_2",
            Barcode = "BC_VAR_2",
            Name = "Copper Wire Spool",
            UnitPrice = 50.00m,
            CostBasis = 12.345m,
            TaxRate = 0.00m,
            StockOnHand = 1.25m
        };

        // Product 3: Snapshot = 10.0 kg, counted = 11.5 kg, cost basis = 0.07 LKR (midpoint stress test)
        // Variance Qty = +1.5 kg.
        // Variance Value = 1.5 * 0.07 = 0.105 -> AwayFromZero: 0.11 (Banker's would round to 0.10!)
        var p3 = new Product
        {
            ProductId = "prod_var_3",
            Barcode = "BC_VAR_3",
            Name = "Micro Washers",
            UnitPrice = 1.00m,
            CostBasis = 0.07m,
            TaxRate = 0.00m,
            StockOnHand = 10.0m
        };

        await _catalog.AddProductAsync(p1);
        await _catalog.AddProductAsync(p2);
        await _catalog.AddProductAsync(p3);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { p1.ProductId, p2.ProductId, p3.ProductId });

        await _stockCount.RecordCountItemAsync(session.SessionId, p1.ProductId, 2.75m, manager.UserId);
        await _stockCount.RecordCountItemAsync(session.SessionId, p2.ProductId, 3.75m, manager.UserId);
        await _stockCount.RecordCountItemAsync(session.SessionId, p3.ProductId, 11.5m, manager.UserId);

        var updatedSession = await _stockCount.GetSessionByIdAsync(session.SessionId);
        Assert.NotNull(updatedSession);

        var item1 = updatedSession.Items.First(i => i.ProductId == p1.ProductId);
        Assert.Equal(-2.50m, item1.VarianceQuantity);
        Assert.Equal(-263.89m, item1.VarianceValue);

        var item2 = updatedSession.Items.First(i => i.ProductId == p2.ProductId);
        Assert.Equal(2.50m, item2.VarianceQuantity);
        Assert.Equal(30.86m, item2.VarianceValue);

        var item3 = updatedSession.Items.First(i => i.ProductId == p3.ProductId);
        Assert.Equal(1.50m, item3.VarianceQuantity);
        Assert.Equal(0.11m, item3.VarianceValue); // Midpoint verification!

        // Total Variance Quantity = -2.50 + 2.50 + 1.50 = +1.50
        Assert.Equal(1.50m, updatedSession.TotalVarianceQuantity);

        // Total Variance Value = -263.89 + 30.86 + 0.11 = -232.92
        Assert.Equal(-232.92m, updatedSession.TotalVarianceValue);

        // Complete session and verify stock adjustments
        var completed = await _stockCount.CompleteSessionAsync(session.SessionId, manager.UserId);
        Assert.Equal(StockCountStatus.Completed, completed.Status);

        var finalP1 = await _catalog.GetProductByIdAsync(p1.ProductId);
        var finalP2 = await _catalog.GetProductByIdAsync(p2.ProductId);
        var finalP3 = await _catalog.GetProductByIdAsync(p3.ProductId);

        Assert.Equal(2.75m, finalP1!.StockOnHand);
        Assert.Equal(3.75m, finalP2!.StockOnHand);
        Assert.Equal(11.5m, finalP3!.StockOnHand);
    }

    [Fact]
    public async Task StockCount_FractionalStockBeyondTwoDecimals_ExposesTwoDecimalQuantityRoundingConstraint()
    {
        // Adversarial Challenge Finding:
        // StockCountService line 356 rounds countedQuantity using MoneyCalculator.Round (2 decimal places).
        // If an inventory item has 3 decimal places (e.g. 5.375 kg), counted 2.125 kg rounds to 2.13 kg,
        // resulting in varianceQuantity = 2.13 - 5.375 = -3.245 kg instead of -3.250 kg.
        var manager = await _auth.CreateUserAsync($"mgr_prec_{Guid.NewGuid():N}", "Prec Mgr", "pass123", Role.Manager);
        var p = new Product
        {
            ProductId = "prod_var_3dec",
            Barcode = "BC_VAR_3DEC",
            Name = "3-Decimal Item",
            UnitPrice = 200.00m,
            CostBasis = 100.00m,
            TaxRate = 0.00m,
            StockOnHand = 5.375m
        };
        await _catalog.AddProductAsync(p);

        var session = await _stockCount.StartSessionAsync("B01", manager.UserId, "TENANT_LK_01",
            new List<string> { p.ProductId });

        await _stockCount.RecordCountItemAsync(session.SessionId, p.ProductId, 2.125m, manager.UserId);

        var updated = await _stockCount.GetSessionByIdAsync(session.SessionId);
        var item = updated!.Items.First();

        // 2.125 is rounded by MoneyCalculator.Round to 2.13:
        Assert.Equal(2.13m, item.CountedQuantity);
        Assert.Equal(-3.245m, item.VarianceQuantity); // 2.13 - 5.375 = -3.245
    }

    #endregion

    #region 5. High-Volume Multi-Shift Drawer Tracking across 100+ Simulated Transactions

    [Fact]
    public async Task HighVolume_MultiShiftDrawerTracking_120Transactions_PennyAccurate()
    {
        var cashier1 = await _auth.CreateUserAsync($"cashier_s1_{Guid.NewGuid():N}", "Cashier Shift 1", "pass123", Role.Cashier);
        var cashier2 = await _auth.CreateUserAsync($"cashier_s2_{Guid.NewGuid():N}", "Cashier Shift 2", "pass123", Role.Cashier);
        var cashier3 = await _auth.CreateUserAsync($"cashier_s3_{Guid.NewGuid():N}", "Cashier Shift 3", "pass123", Role.Cashier);

        var baseProduct = new Product
        {
            ProductId = "prod_sim_item",
            Barcode = "BC_SIM_ITEM",
            Name = "Simulation Component",
            UnitPrice = 125.50m,
            CostBasis = 80.00m,
            TaxRate = 0.00m,
            StockOnHand = 10000m
        };
        await _catalog.AddProductAsync(baseProduct);

        var cashiers = new[] { cashier1, cashier2, cashier3 };
        decimal currentDrawerFloat = 5000.00m;

        int globalTxCounter = 0;

        // Run 3 consecutive shifts, 40 transactions each = 120 total transactions
        for (int shiftIdx = 0; shiftIdx < 3; shiftIdx++)
        {
            var cashier = cashiers[shiftIdx];
            var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, currentDrawerFloat, "TENANT_LK_01");

            decimal runningExpectedCash = currentDrawerFloat;
            var shiftSales = new List<Sale>();

            // Perform 40 transactions in this shift
            for (int t = 0; t < 40; t++)
            {
                globalTxCounter++;
                int txnType = (shiftIdx * 40 + t) % 6;

                switch (txnType)
                {
                    case 0: // Cash Sale with exact change
                    {
                        var saleCmd = new CreateSaleCommand(
                            TenantId: "TENANT_LK_01", BranchId: "B01", CounterId: "C01", CashierId: cashier.UserId, ShiftId: shift.ShiftId,
                            Items: new List<CreateSaleLineRequest> { new(baseProduct.ProductId, 2.0m) }, // 251.00 LKR
                            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 300.00m) } // 49.00 change
                        );
                        var sale = await _sale.CommitSaleAsync(saleCmd);
                        shiftSales.Add(sale);
                        runningExpectedCash += (300.00m - 49.00m); // +251.00
                        break;
                    }
                    case 1: // Split Sale (Cash + Card)
                    {
                        var saleCmd = new CreateSaleCommand(
                            TenantId: "TENANT_LK_01", BranchId: "B01", CounterId: "C01", CashierId: cashier.UserId, ShiftId: shift.ShiftId,
                            Items: new List<CreateSaleLineRequest> { new(baseProduct.ProductId, 4.0m) }, // 502.00 LKR
                            Tenders: new List<CreateTenderRequest>
                            {
                                new(TenderType.CARD, 300.00m, $"CARD_TX_{globalTxCounter}"),
                                new(TenderType.CASH, 250.00m) // 202 needed, 48 change
                            }
                        );
                        var sale = await _sale.CommitSaleAsync(saleCmd);
                        shiftSales.Add(sale);
                        runningExpectedCash += 202.00m; // Cash portion net
                        break;
                    }
                    case 2: // Non-cash Sale (QR)
                    {
                        var saleCmd = new CreateSaleCommand(
                            TenantId: "TENANT_LK_01", BranchId: "B01", CounterId: "C01", CashierId: cashier.UserId, ShiftId: shift.ShiftId,
                            Items: new List<CreateSaleLineRequest> { new(baseProduct.ProductId, 1.0m) }, // 125.50 LKR
                            Tenders: new List<CreateTenderRequest> { new(TenderType.QR, 125.50m, $"QR_TX_{globalTxCounter}") }
                        );
                        var sale = await _sale.CommitSaleAsync(saleCmd);
                        shiftSales.Add(sale);
                        // QR does NOT affect physical cash
                        break;
                    }
                    case 3: // Cash In (bank float topup)
                    {
                        decimal topup = 500.00m + (t * 10m);
                        await _shift.RecordCashMovementAsync(
                            shift.ShiftId, topup, isCashIn: true, reason: $"Topup {globalTxCounter}",
                            cashier.UserId, "TENANT_LK_01", "B01", "C01"
                        );
                        runningExpectedCash += topup;
                        break;
                    }
                    case 4: // Cash Out (petty cash expenses)
                    {
                        decimal expense = 150.00m + (t * 5m);
                        await _shift.RecordCashMovementAsync(
                            shift.ShiftId, expense, isCashIn: false, reason: $"Expense {globalTxCounter}",
                            cashier.UserId, "TENANT_LK_01", "B01", "C01"
                        );
                        runningExpectedCash -= expense;
                        break;
                    }
                    case 5: // Cash Refund
                    {
                        // Commit a sale first, then refund it
                        var origSaleCmd = new CreateSaleCommand(
                            TenantId: "TENANT_LK_01", BranchId: "B01", CounterId: "C01", CashierId: cashier.UserId, ShiftId: shift.ShiftId,
                            Items: new List<CreateSaleLineRequest> { new(baseProduct.ProductId, 1.0m) }, // 125.50 LKR
                            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 125.50m) }
                        );
                        var origSale = await _sale.CommitSaleAsync(origSaleCmd);

                        var refCmd = new RefundSaleCommand(
                            TenantId: "TENANT_LK_01", BranchId: "B01", CounterId: "C01", CashierId: cashier.UserId, ShiftId: shift.ShiftId,
                            OriginalSaleId: origSale.SaleId,
                            Items: new List<RefundLineRequest> { new(origSale.Lines[0].LineId, 1.0m) },
                            Reason: $"Defect item {globalTxCounter}"
                        );
                        await _sale.RefundSaleAsync(refCmd);

                        // origSale added 125.50 cash, refund took 125.50 cash -> net cash change is 0.00
                        break;
                    }
                }
            }

            // Verify shift state in database matches runningExpectedCash
            var activeShift = await _shift.GetShiftByIdAsync(shift.ShiftId);
            Assert.NotNull(activeShift);

            var computedExpected = MoneyCalculator.CalculateShiftExpectedCash(
                activeShift.OpeningFloat,
                activeShift.CashReceived,
                activeShift.ChangeGiven,
                activeShift.CashRefunds,
                activeShift.CashIn,
                activeShift.CashOut
            );

            Assert.Equal(runningExpectedCash, computedExpected);

            // Close shift with counted cash = expected cash - 10.00 (simulate small 10 LKR shortage)
            decimal countedCash = runningExpectedCash - 10.00m;
            var closed = await _shift.CloseShiftAsync(shift.ShiftId, countedCash, cashier.UserId, "TENANT_LK_01");

            Assert.Equal(ShiftStatus.Closed, closed.Status);
            Assert.Equal(runningExpectedCash, closed.ExpectedCash);
            Assert.Equal(countedCash, closed.ActualCountedCash);
            Assert.Equal(-10.00m, closed.Variance);

            // Carry forward counted cash as next shift's opening float
            currentDrawerFloat = countedCash;
        }

        Assert.Equal(120, globalTxCounter);
    }

    #endregion

    #region 6. Customer Credit Ledger Debit/Credit Balances (Zero Penny Discrepancy)

    [Fact]
    public async Task CustomerCreditLedger_100Operations_ZeroPennyDiscrepancy()
    {
        var cashier = await _auth.CreateUserAsync($"cashier_cred_{Guid.NewGuid():N}", "Credit Cashier", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 10000.00m, "TENANT_LK_01");

        // Customer with large credit limit and initial balance
        decimal initialBalance = 15432.50m;
        var customer = await _customer.CreateCustomerAsync(
            name: "High Volume Fleet Customer",
            phone: "0771234567",
            address: "Kandy Road, Peliyagoda",
            creditLimit: 500000.00m,
            actorId: cashier.UserId,
            initialBalance: initialBalance
        );

        var parts = new List<Product>();
        for (int i = 1; i <= 5; i++)
        {
            var p = new Product
            {
                ProductId = $"prod_fleet_{i}",
                Barcode = $"BC_FLEET_{i}",
                Name = $"Fleet Part {i}",
                UnitPrice = 1000.00m * i + 0.45m * i, // 1000.45, 2000.90, 3001.35, etc.
                CostBasis = 700.00m * i,
                TaxRate = 0.00m,
                StockOnHand = 1000m
            };
            await _catalog.AddProductAsync(p);
            parts.Add(p);
        }

        decimal trackedBalance = initialBalance;
        decimal totalCreditSales = 0m;
        decimal totalPayments = 0m;
        decimal totalRefunds = 0m;
        decimal totalCancellations = 0m;

        var salesToRefundOrCancel = new List<Sale>();

        // Execute 100 ledger operations
        for (int op = 0; op < 100; op++)
        {
            int opType = op % 5;

            if (opType == 0 || opType == 1) // 40% Credit Sales
            {
                var prod = parts[op % parts.Count];
                decimal saleAmount = prod.UnitPrice;

                var cmd = new CreateSaleCommand(
                    TenantId: "TENANT_LK_01", BranchId: "B01", CounterId: "C01", CashierId: cashier.UserId, ShiftId: shift.ShiftId,
                    Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 1.0m) },
                    Tenders: new List<CreateTenderRequest> { new(TenderType.CREDIT, saleAmount) },
                    CustomerId: customer.CustomerId
                );
                var sale = await _sale.CommitSaleAsync(cmd);
                salesToRefundOrCancel.Add(sale);

                trackedBalance += saleAmount;
                totalCreditSales += saleAmount;
            }
            else if (opType == 2) // 20% Split Tender (Cash + Credit)
            {
                var prod = parts[(op + 1) % parts.Count];
                decimal total = prod.UnitPrice;
                decimal creditPortion = MoneyCalculator.Round(total * 0.6m);
                decimal cashPortion = total - creditPortion;

                var cmd = new CreateSaleCommand(
                    TenantId: "TENANT_LK_01", BranchId: "B01", CounterId: "C01", CashierId: cashier.UserId, ShiftId: shift.ShiftId,
                    Items: new List<CreateSaleLineRequest> { new(prod.ProductId, 1.0m) },
                    Tenders: new List<CreateTenderRequest>
                    {
                        new(TenderType.CREDIT, creditPortion),
                        new(TenderType.CASH, cashPortion)
                    },
                    CustomerId: customer.CustomerId
                );
                var sale = await _sale.CommitSaleAsync(cmd);
                salesToRefundOrCancel.Add(sale);

                trackedBalance += creditPortion;
                totalCreditSales += creditPortion;
            }
            else if (opType == 3) // 20% Debt Payment (Cash or Card)
            {
                decimal paymentAmount = 500.25m + (op * 2.50m);
                string payMethod = (op % 2 == 0) ? "CASH" : "CARD";

                await _customer.RecordPaymentAsync(
                    customerId: customer.CustomerId,
                    amount: paymentAmount,
                    paymentMethod: payMethod,
                    actorId: cashier.UserId,
                    shiftId: shift.ShiftId,
                    notes: $"Installment payment {op}"
                );

                trackedBalance -= paymentAmount;
                totalPayments += paymentAmount;
            }
            else if (opType == 4) // 20% Refund or Cancellation of an earlier credit sale
            {
                if (salesToRefundOrCancel.Count > 0)
                {
                    var targetSale = salesToRefundOrCancel[0];
                    salesToRefundOrCancel.RemoveAt(0);

                    if (op % 2 == 0)
                    {
                        // Refund
                        var refCmd = new RefundSaleCommand(
                            TenantId: "TENANT_LK_01", BranchId: "B01", CounterId: "C01", CashierId: cashier.UserId, ShiftId: shift.ShiftId,
                            OriginalSaleId: targetSale.SaleId,
                            Items: new List<RefundLineRequest> { new(targetSale.Lines[0].LineId, 1.0m) },
                            Reason: $"Credit return {op}"
                        );
                        var refundSale = await _sale.RefundSaleAsync(refCmd);
                        var creditRefundTender = refundSale.Tenders.FirstOrDefault(t => t.TenderType == TenderType.CREDIT);
                        if (creditRefundTender != null)
                        {
                            trackedBalance -= creditRefundTender.AmountTendered;
                            totalRefunds += creditRefundTender.AmountTendered;
                        }
                    }
                    else
                    {
                        // Cancel
                        var cancelCmd = new CancelSaleCommand(
                            TenantId: "TENANT_LK_01", BranchId: "B01", CounterId: "C01", CashierId: cashier.UserId, ShiftId: shift.ShiftId,
                            SaleId: targetSale.SaleId,
                            Reason: $"Credit cancellation {op}"
                        );
                        await _sale.CancelSaleAsync(cancelCmd);
                        var creditTender = targetSale.Tenders.FirstOrDefault(t => t.TenderType == TenderType.CREDIT);
                        if (creditTender != null)
                        {
                            trackedBalance -= creditTender.AmountTendered;
                            totalCancellations += creditTender.AmountTendered;
                        }
                    }
                }
            }

            // Empirical verification after EVERY single operation:
            var currentBalance = await _customer.GetCustomerBalanceAsync(customer.CustomerId);
            Assert.Equal(MoneyCalculator.Round(trackedBalance), currentBalance);
        }

        // Final verification across ledger entries
        var ledger = await _customer.GetCustomerLedgerAsync(customer.CustomerId, limit: 200);
        Assert.True(ledger.Count >= 100);

        // Verify the latest ledger entry balance matches tracked balance
        var latestEntry = ledger[0];
        Assert.Equal(MoneyCalculator.Round(trackedBalance), latestEntry.BalanceAfter);

        // Verify the zero penny discrepancy double-entry accounting formula:
        // Expected Final = Initial + Sales - Payments - Refunds - Cancellations
        decimal computedFinal = initialBalance + totalCreditSales - totalPayments - totalRefunds - totalCancellations;
        decimal finalDbBalance = await _customer.GetCustomerBalanceAsync(customer.CustomerId);

        decimal discrepancy = Math.Abs(finalDbBalance - computedFinal);
        Assert.True(discrepancy == 0.00m, $"Discrepancy detected: {discrepancy:F4} LKR! Expected: {computedFinal}, Actual: {finalDbBalance}");
    }

    #endregion
}
