using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class ShiftAndDailyReportsTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _sale;
    private readonly ReportService _report;

    public ShiftAndDailyReportsTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _catalog = new CatalogService(_db);
        _shift = new ShiftService(_db);
        _sale = new SaleService(_db, _catalog);
        _report = new ReportService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task ShiftReport_MatchesHandCalculatedTenderSummary_DrawerReconciliation_AndGrossProfit()
    {
        // 1. Setup Cashier & Open Shift (Opening Float: 5,000.00)
        var cashier = await _auth.CreateUserAsync("anura_rep", "Anura Senanayake", "Pass#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        // 2. Setup Products with explicit unit price and cost basis
        var p1 = new Product { ProductId = "rep_p1", Barcode = "1001", Name = "Brake Shoes", UnitPrice = 1000.00m, CostBasis = 600.00m, StockOnHand = 50m };
        var p2 = new Product { ProductId = "rep_p2", Barcode = "1002", Name = "Air Filter", UnitPrice = 1500.00m, CostBasis = 900.00m, StockOnHand = 50m };
        var p3 = new Product { ProductId = "rep_p3", Barcode = "1003", Name = "Tyre Tubeless", UnitPrice = 3000.00m, CostBasis = 2000.00m, StockOnHand = 50m };
        var p4 = new Product { ProductId = "rep_p4", Barcode = "1004", Name = "LED Light Bulb", UnitPrice = 800.00m, CostBasis = 500.00m, StockOnHand = 50m };
        var p5 = new Product { ProductId = "rep_p5", Barcode = "1005", Name = "Mirror Assembly", UnitPrice = 1200.00m, CostBasis = 700.00m, StockOnHand = 50m };

        await _catalog.AddProductAsync(p1);
        await _catalog.AddProductAsync(p2);
        await _catalog.AddProductAsync(p3);
        await _catalog.AddProductAsync(p4);
        await _catalog.AddProductAsync(p5);

        // 3. Cash Sale 1: 2 units of p1 = 2,000.00 LKR. Tendered Cash: 2,500.00. Change: 500.00.
        var sale1 = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(p1.ProductId, 2.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 2500.00m) }
        ));
        Assert.Equal(500.00m, sale1.Tenders[0].ChangeGiven);

        // 4. Cash Sale 2: 1 unit of p2 = 1,500.00 LKR. Tendered Cash: 1,500.00. Change: 0.
        var sale2 = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(p2.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1500.00m) }
        ));

        // 5. Card Sale: 1 unit of p3 = 3,000.00 LKR. Tendered Card: 3,000.00.
        await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(p3.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CARD, 3000.00m, "TXN_CARD_999") }
        ));

        // 6. QR Sale: 1 unit of p4 = 800.00 LKR. Tendered QR: 800.00.
        await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(p4.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.QR, 800.00m, "LANKAQR_888") }
        ));

        // 7. Credit Sale: 1 unit of p5 = 1,200.00 LKR. Tendered Credit: 1,200.00.
        await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(p5.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CREDIT, 1200.00m, "ACC_CUST_777") }
        ));

        // 8. Partial Refund: Refund 1 unit of p1 from Sale 1 (1,000.00 LKR) with restock = true.
        var refundSale = await _sale.RefundSaleAsync(new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale1.SaleId,
            Items: new List<RefundLineRequest> { new(sale1.Lines[0].LineId, 1.0m) },
            Reason: "Customer returned 1 unit of brake shoes",
            ReturnStockToInventory: true
        ));
        Assert.Equal(1000.00m, refundSale.GrandTotal);

        // 9. Cash In: 2,000.00 LKR (Bank deposit addition)
        await _shift.RecordCashMovementAsync(
            shift.ShiftId,
            2000.00m,
            isCashIn: true,
            reason: "Bank deposit addition",
            cashier.UserId,
            "TENANT_LK_01",
            "B01",
            "C01"
        );

        // 10. Cash Out: 1,000.00 LKR (Refreshment purchase)
        await _shift.RecordCashMovementAsync(
            shift.ShiftId,
            1000.00m,
            isCashIn: false,
            reason: "Staff refreshments and tea",
            cashier.UserId,
            "TENANT_LK_01",
            "B01",
            "C01"
        );

        // 11. Close Shift with counted cash: 8,450.00 LKR
        // Hand calculations:
        // Opening Float: 5,000.00
        // Cash Received: 2,500 + 1,500 = 4,000.00
        // Change Given: 500.00
        // Net Cash from sales: 3,500.00
        // Cash Refunds: 1,000.00
        // Cash In: 2,000.00
        // Cash Out: 1,000.00
        // Expected Cash = 5,000 + 4,000 - 500 - 1,000 + 2,000 - 1,000 = 8,500.00 LKR
        // Counted Cash = 8,450.00 LKR
        // Variance = 8,450 - 8,500 = -50.00 LKR (shortage)
        var closedShift = await _shift.CloseShiftAsync(shift.ShiftId, 8450.00m, cashier.UserId, "TENANT_LK_01");
        Assert.Equal(8500.00m, closedShift.ExpectedCash);
        Assert.Equal(-50.00m, closedShift.Variance);

        // 12. Generate Shift Report
        var report = await _report.GenerateShiftReportAsync(shift.ShiftId);
        Assert.NotNull(report);
        Assert.Equal("Anura Senanayake", report.CashierName);
        Assert.Equal(ShiftStatus.Closed, report.Status);

        // Sales totals
        Assert.Equal(5, report.TotalSalesCount);
        Assert.Equal(8500.00m, report.GrossSales); // 2000 + 1500 + 3000 + 800 + 1200
        Assert.Equal(8500.00m, report.NetSales);
        Assert.Equal(1, report.TotalRefundCount);
        Assert.Equal(1000.00m, report.TotalRefundAmount);

        // Tender summaries check
        var cashTender = report.TenderSummaries.First(t => t.TenderType == TenderType.CASH);
        Assert.Equal(2, cashTender.TransactionCount);
        Assert.Equal(4000.00m, cashTender.TotalTendered);
        Assert.Equal(500.00m, cashTender.ChangeGiven);
        Assert.Equal(3500.00m, cashTender.NetAmount);

        var cardTender = report.TenderSummaries.First(t => t.TenderType == TenderType.CARD);
        Assert.Equal(1, cardTender.TransactionCount);
        Assert.Equal(3000.00m, cardTender.NetAmount);

        var qrTender = report.TenderSummaries.First(t => t.TenderType == TenderType.QR);
        Assert.Equal(1, qrTender.TransactionCount);
        Assert.Equal(800.00m, qrTender.NetAmount);

        var creditTender = report.TenderSummaries.First(t => t.TenderType == TenderType.CREDIT);
        Assert.Equal(1, creditTender.TransactionCount);
        Assert.Equal(1200.00m, creditTender.NetAmount);

        // Cash Drawer Reconciliation check
        var rec = report.DrawerReconciliation;
        Assert.Equal(5000.00m, rec.OpeningFloat);
        Assert.Equal(4000.00m, rec.CashReceived);
        Assert.Equal(500.00m, rec.ChangeGiven);
        Assert.Equal(3500.00m, rec.NetCashSales);
        Assert.Equal(1000.00m, rec.CashRefunds);
        Assert.Equal(2000.00m, rec.CashIn);
        Assert.Equal(1000.00m, rec.CashOut);
        Assert.Equal(8500.00m, rec.ExpectedCash);
        Assert.Equal(8450.00m, rec.ActualCountedCash);
        Assert.Equal(-50.00m, rec.Variance);
        Assert.Equal("Closed", rec.Status);

        // Cash Movements list check
        Assert.Equal(2, report.CashMovements.Count);

        // Gross Profit Breakdown check:
        // Net Revenue = 8,500 - 1,000 = 7,500.00 LKR
        // Sales COGS:
        // p1: 2 * 600 = 1200
        // p2: 1 * 900 = 900
        // p3: 1 * 2000 = 2000
        // p4: 1 * 500 = 500
        // p5: 1 * 700 = 700
        // Total Sales COGS = 5,300.00 LKR
        // Restocked refund COGS: 1 * 600 = 600.00 LKR
        // Net COGS = 5,300 - 600 = 4,700.00 LKR
        // Gross Profit = 7,500 - 4,700 = 2,800.00 LKR
        // Gross Margin % = (2,800 / 7,500) * 100 = 37.33%
        var profit = report.ProfitSummary;
        Assert.Equal(7500.00m, profit.Revenue);
        Assert.Equal(4700.00m, profit.CostOfGoodsSold);
        Assert.Equal(2800.00m, profit.GrossProfit);
        Assert.Equal(37.33m, profit.GrossMarginPercent);
        Assert.Contains("Gross Profit only", profit.Disclaimer);
    }

    [Fact]
    public async Task DailyReport_AggregatesSalesAndTendersAcrossMultipleShifts()
    {
        var cashier1 = await _auth.CreateUserAsync("c1", "Cashier 1", "Pass#123", Role.Cashier);
        var cashier2 = await _auth.CreateUserAsync("c2", "Cashier 2", "Pass#123", Role.Cashier);

        var product = new Product
        {
            ProductId = "p_daily",
            Barcode = "889900",
            Name = "Spark Plug",
            UnitPrice = 500.00m,
            CostBasis = 300.00m,
            StockOnHand = 100m
        };
        await _catalog.AddProductAsync(product);

        // Shift 1
        var shift1 = await _shift.OpenShiftAsync("B01", "C01", cashier1.UserId, 2000.00m, "TENANT_LK_01");
        await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier1.UserId,
            ShiftId: shift1.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 2m) }, // 1000 LKR
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1000.00m) }
        ));
        await _shift.CloseShiftAsync(shift1.ShiftId, 3000.00m, cashier1.UserId, "TENANT_LK_01");

        // Shift 2 (on counter 02)
        var shift2 = await _shift.OpenShiftAsync("B01", "C02", cashier2.UserId, 3000.00m, "TENANT_LK_01");
        await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C02",
            CashierId: cashier2.UserId,
            ShiftId: shift2.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 4m) }, // 2000 LKR
            Tenders: new List<CreateTenderRequest> { new(TenderType.CARD, 2000.00m) }
        ));
        await _shift.CloseShiftAsync(shift2.ShiftId, 3000.00m, cashier2.UserId, "TENANT_LK_01");

        // Generate daily report for today
        var dailyReport = await _report.GenerateDailyReportAsync(DateTime.UtcNow, "B01");
        Assert.Equal(2, dailyReport.ShiftsCount);
        Assert.Equal(2, dailyReport.CompletedSalesCount);
        Assert.Equal(3000.00m, dailyReport.GrossSales); // 1000 + 2000
        Assert.Equal(3000.00m, dailyReport.NetSales);

        var cashTender = dailyReport.TenderSummaries.First(t => t.TenderType == TenderType.CASH);
        Assert.Equal(1000.00m, cashTender.NetAmount);

        var cardTender = dailyReport.TenderSummaries.First(t => t.TenderType == TenderType.CARD);
        Assert.Equal(2000.00m, cardTender.NetAmount);

        // Gross Profit:
        // Revenue = 3,000 LKR
        // COGS = (2 * 300) + (4 * 300) = 600 + 1200 = 1,800 LKR
        // Gross Profit = 3,000 - 1,800 = 1,200 LKR
        Assert.Equal(3000.00m, dailyReport.ProfitSummary.Revenue);
        Assert.Equal(1800.00m, dailyReport.ProfitSummary.CostOfGoodsSold);
        Assert.Equal(1200.00m, dailyReport.ProfitSummary.GrossProfit);
        Assert.Equal(40.00m, dailyReport.ProfitSummary.GrossMarginPercent);
    }

    [Fact]
    public async Task InventorySummaryReport_AccuratelyCalculatesValuationAndStockThresholds()
    {
        var pGood = new Product { ProductId = "p_g", Barcode = "01", Name = "Ample Stock", UnitPrice = 1000m, CostBasis = 600m, StockOnHand = 25m };
        var pLow = new Product { ProductId = "p_l", Barcode = "02", Name = "Low Stock", UnitPrice = 500m, CostBasis = 300m, StockOnHand = 3m };
        var pNeg = new Product { ProductId = "p_n", Barcode = "03", Name = "Oversold Stock", UnitPrice = 200m, CostBasis = 100m, StockOnHand = -2m };

        await _catalog.AddProductAsync(pGood);
        await _catalog.AddProductAsync(pLow);
        await _catalog.AddProductAsync(pNeg);

        var invReport = await _report.GenerateInventorySummaryReportAsync();
        Assert.Equal(3, invReport.TotalProducts);
        Assert.Equal(1, invReport.LowStockProducts); // pLow has 3 <= 5
        Assert.Equal(1, invReport.NegativeStockProducts); // pNeg has -2 < 0

        // Valuation at cost = (25 * 600) + (3 * 300) + (-2 * 100) = 15,000 + 900 - 200 = 15,700 LKR
        Assert.Equal(15700.00m, invReport.TotalValuationAtCost);

        // Valuation at retail = (25 * 1000) + (3 * 500) + (-2 * 200) = 25,000 + 1500 - 400 = 26,100 LKR
        Assert.Equal(26100.00m, invReport.TotalValuationAtRetail);
    }

    [Fact]
    public async Task ShiftReport_MultiItemRefund_DoesNotProduceCartesianProductOnCOGS()
    {
        var cashier = await _auth.CreateUserAsync("cartesian_cashier", "Cartesian Cashier", "Pass#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 10000.00m, "TENANT_LK_01");

        // Product A: Unit Price 1000, Cost 400
        var prodA = new Product { ProductId = "cart_pA", Barcode = "CA01", Name = "Part A", UnitPrice = 1000.00m, CostBasis = 400.00m, StockOnHand = 10m };
        // Product B: Unit Price 2000, Cost 700
        var prodB = new Product { ProductId = "cart_pB", Barcode = "CB01", Name = "Part B", UnitPrice = 2000.00m, CostBasis = 700.00m, StockOnHand = 10m };

        await _catalog.AddProductAsync(prodA);
        await _catalog.AddProductAsync(prodB);

        // Commit sale with 1 unit of A and 1 unit of B = Grand Total 3000
        // COGS = 400 + 700 = 1100
        var sale = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(prodA.ProductId, 1.0m),
                new(prodB.ProductId, 1.0m)
            },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 3000.00m) }
        ));

        // Refund BOTH items in a single refund transaction with restock = true
        var refund = await _sale.RefundSaleAsync(new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest>
            {
                new(sale.Lines.First(l => l.ProductId == prodA.ProductId).LineId, 1.0m),
                new(sale.Lines.First(l => l.ProductId == prodB.ProductId).LineId, 1.0m)
            },
            Reason: "Customer return both items",
            ReturnStockToInventory: true
        ));

        // Generate shift report
        var report = await _report.GenerateShiftReportAsync(shift.ShiftId);

        // Net Revenue: Net Sales (3000) - Refund (3000) = 0.00 LKR
        // Total Sales COGS = 400 + 700 = 1100.00 LKR
        // Restocked Refund COGS MUST be exactly 400 + 700 = 1100.00 LKR (NOT 2200 from a Cartesian product!)
        // Net COGS = 1100 - 1100 = 0.00 LKR
        // Gross Profit = 0.00 LKR
        Assert.Equal(0.00m, report.ProfitSummary.Revenue);
        Assert.Equal(0.00m, report.ProfitSummary.CostOfGoodsSold);
        Assert.Equal(0.00m, report.ProfitSummary.GrossProfit);
    }

    [Fact]
    public async Task DailyReport_BranchFilter_ExcludesCrossBranchCashMovements()
    {
        var cashier1 = await _auth.CreateUserAsync("br1_cashier", "B01 Cashier", "Pass#123", Role.Cashier);
        var cashier2 = await _auth.CreateUserAsync("br2_cashier", "B02 Cashier", "Pass#123", Role.Cashier);

        // Branch 1 shift
        var shiftB1 = await _shift.OpenShiftAsync("B01", "C01", cashier1.UserId, 5000.00m, "TENANT_LK_01");
        await _shift.RecordCashMovementAsync(shiftB1.ShiftId, 500.00m, true, "B01 Cash In", cashier1.UserId, "TENANT_LK_01", "B01", "C01");

        // Branch 2 shift (same day, different branch)
        var shiftB2 = await _shift.OpenShiftAsync("B02", "C01", cashier2.UserId, 5000.00m, "TENANT_LK_01");
        await _shift.RecordCashMovementAsync(shiftB2.ShiftId, 9999.00m, true, "B02 Large Cash In", cashier2.UserId, "TENANT_LK_01", "B02", "C01");

        // Daily report for B01 only
        var reportB1 = await _report.GenerateDailyReportAsync(DateTime.UtcNow, "B01");
        Assert.Equal(1, reportB1.ShiftsCount);
        // Total cash change on drawer for B01 must only include the 500.00 LKR from B01, NOT 9999.00 from B02!
        Assert.Equal(500.00m, reportB1.NetDrawerCashChange);
    }
}

