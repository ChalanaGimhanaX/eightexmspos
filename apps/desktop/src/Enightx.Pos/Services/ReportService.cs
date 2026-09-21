using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface IReportService
{
    Task<ShiftReport> GenerateShiftReportAsync(Guid shiftId);
    Task<DailyReport> GenerateDailyReportAsync(DateTime dateUtc, string? branchId = null);
    Task<InventorySummaryReport> GenerateInventorySummaryReportAsync();
}

public class ReportService : IReportService
{
    private readonly PosDatabase _db;

    public ReportService(PosDatabase db)
    {
        _db = db;
    }

    public async Task<ShiftReport> GenerateShiftReportAsync(Guid shiftId)
    {
        using var conn = _db.CreateConnection();

        // 1. Fetch shift
        using var shiftCmd = conn.CreateCommand();
        shiftCmd.CommandText = @"
            SELECT s.shift_id, s.branch_id, s.counter_id, s.cashier_id, u.display_name,
                   s.opened_at_utc, s.closed_at_utc, s.opening_float, s.cash_received,
                   s.change_given, s.cash_refunds, s.cash_in, s.cash_out, s.expected_cash,
                   s.actual_counted_cash, s.variance, s.status
            FROM shifts s
            JOIN users u ON s.cashier_id = u.user_id
            WHERE s.shift_id = $id;
        ";
        shiftCmd.Parameters.AddWithValue("$id", shiftId.ToString());

        using var sReader = await shiftCmd.ExecuteReaderAsync();
        if (!await sReader.ReadAsync())
        {
            throw new PosException($"Shift '{shiftId}' not found.");
        }

        var shift = new CashShift
        {
            ShiftId = Guid.Parse(sReader.GetString(0)),
            BranchId = sReader.GetString(1),
            CounterId = sReader.GetString(2),
            CashierId = sReader.GetString(3),
            OpenedAtUtc = DateTime.Parse(sReader.GetString(5), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            ClosedAtUtc = sReader.IsDBNull(6) ? null : DateTime.Parse(sReader.GetString(6), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            OpeningFloat = sReader.GetDecimal(7),
            CashReceived = sReader.GetDecimal(8),
            ChangeGiven = sReader.GetDecimal(9),
            CashRefunds = sReader.GetDecimal(10),
            CashIn = sReader.GetDecimal(11),
            CashOut = sReader.GetDecimal(12),
            ExpectedCash = sReader.GetDecimal(13),
            ActualCountedCash = sReader.IsDBNull(14) ? null : sReader.GetDecimal(14),
            Variance = sReader.IsDBNull(15) ? null : sReader.GetDecimal(15),
            Status = (ShiftStatus)sReader.GetInt32(16)
        };
        var cashierDisplayName = sReader.GetString(4);
        sReader.Close();

        // 2. Sales Summary (Status = Completed (1))
        using var salesSumCmd = conn.CreateCommand();
        salesSumCmd.CommandText = @"
            SELECT COUNT(*),
                   COALESCE(SUM(subtotal), 0),
                   COALESCE(SUM(discount_total), 0),
                   COALESCE(SUM(tax_total), 0),
                   COALESCE(SUM(grand_total), 0)
            FROM sales
            WHERE shift_id = $sid AND status = 1;
        ";
        salesSumCmd.Parameters.AddWithValue("$sid", shiftId.ToString());

        int totalSalesCount = 0;
        decimal grossSales = 0m, discountTotal = 0m, taxTotal = 0m, netSales = 0m;
        using (var reader = await salesSumCmd.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                totalSalesCount = reader.GetInt32(0);
                grossSales = MoneyCalculator.Round(reader.GetDecimal(1));
                discountTotal = MoneyCalculator.Round(reader.GetDecimal(2));
                taxTotal = MoneyCalculator.Round(reader.GetDecimal(3));
                netSales = MoneyCalculator.Round(reader.GetDecimal(4));
            }
        }

        // 3. Refunds Summary (Status = Refunded (3))
        using var refSumCmd = conn.CreateCommand();
        refSumCmd.CommandText = @"
            SELECT COUNT(*), COALESCE(SUM(grand_total), 0)
            FROM sales
            WHERE shift_id = $sid AND status = 3;
        ";
        refSumCmd.Parameters.AddWithValue("$sid", shiftId.ToString());

        int totalRefundCount = 0;
        decimal totalRefundAmount = 0m;
        using (var reader = await refSumCmd.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                totalRefundCount = reader.GetInt32(0);
                totalRefundAmount = MoneyCalculator.Round(reader.GetDecimal(1));
            }
        }

        // 4. Tender Breakdown
        var tenderSummaries = new List<TenderSummary>();
        var tenderTypes = new[] { TenderType.CASH, TenderType.CARD, TenderType.QR, TenderType.CREDIT };

        foreach (var tType in tenderTypes)
        {
            using var tCmd = conn.CreateCommand();
            tCmd.CommandText = @"
                SELECT COUNT(t.tender_id),
                       COALESCE(SUM(t.amount_tendered), 0),
                       COALESCE(SUM(t.change_given), 0)
                FROM tenders t
                JOIN sales s ON t.sale_id = s.sale_id
                WHERE s.shift_id = $sid AND s.status = 1 AND t.tender_type = $ttype;
            ";
            tCmd.Parameters.AddWithValue("$sid", shiftId.ToString());
            tCmd.Parameters.AddWithValue("$ttype", tType.ToString());

            using var reader = await tCmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var count = reader.GetInt32(0);
                var tendered = MoneyCalculator.Round(reader.GetDecimal(1));
                var change = MoneyCalculator.Round(reader.GetDecimal(2));
                var netTender = MoneyCalculator.Round(tendered - change);

                tenderSummaries.Add(new TenderSummary
                {
                    TenderType = tType,
                    TransactionCount = count,
                    TotalTendered = tendered,
                    ChangeGiven = change,
                    NetAmount = netTender
                });
            }
        }

        // 5. Drawer Reconciliation
        var netCashSales = MoneyCalculator.Round(shift.CashReceived - shift.ChangeGiven);
        var expectedCash = MoneyCalculator.CalculateShiftExpectedCash(
            shift.OpeningFloat,
            shift.CashReceived,
            shift.ChangeGiven,
            shift.CashRefunds,
            shift.CashIn,
            shift.CashOut
        );

        decimal? variance = shift.ActualCountedCash.HasValue
            ? MoneyCalculator.Round(shift.ActualCountedCash.Value - expectedCash)
            : null;

        var reconciliation = new CashDrawerReconciliation
        {
            OpeningFloat = shift.OpeningFloat,
            CashReceived = shift.CashReceived,
            ChangeGiven = shift.ChangeGiven,
            NetCashSales = netCashSales,
            CashRefunds = shift.CashRefunds,
            CashIn = shift.CashIn,
            CashOut = shift.CashOut,
            ExpectedCash = expectedCash,
            ActualCountedCash = shift.ActualCountedCash,
            Variance = variance,
            Status = shift.Status == ShiftStatus.Open ? "Open" : "Closed"
        };

        // 6. Cash Movements
        using var moveCmd = conn.CreateCommand();
        moveCmd.CommandText = @"
            SELECT movement_id, shift_id, movement_type, amount, reason, actor_id, occurred_at_utc
            FROM shift_cash_movements
            WHERE shift_id = $sid
            ORDER BY occurred_at_utc ASC;
        ";
        moveCmd.Parameters.AddWithValue("$sid", shiftId.ToString());

        var cashMovements = new List<ShiftCashMovement>();
        using (var reader = await moveCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                cashMovements.Add(new ShiftCashMovement
                {
                    MovementId = Guid.Parse(reader.GetString(0)),
                    ShiftId = Guid.Parse(reader.GetString(1)),
                    MovementType = reader.GetString(2),
                    Amount = reader.GetDecimal(3),
                    Reason = reader.GetString(4),
                    ActorId = reader.GetString(5),
                    OccurredAtUtc = DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
                });
            }
        }

        // 7. Gross Profit Breakdown
        // Revenue = net sales - refund amount
        var netRevenue = MoneyCalculator.Round(netSales - totalRefundAmount);

        // Sales COGS = sum of (line.quantity * line.cost_basis) for completed sales
        using var cogsCmd = conn.CreateCommand();
        cogsCmd.CommandText = @"
            SELECT COALESCE(SUM(sl.quantity * sl.cost_basis), 0)
            FROM sale_lines sl
            JOIN sales s ON sl.sale_id = s.sale_id
            WHERE s.shift_id = $sid AND s.status = 1;
        ";
        cogsCmd.Parameters.AddWithValue("$sid", shiftId.ToString());
        var salesCogs = MoneyCalculator.Round(Convert.ToDecimal(await cogsCmd.ExecuteScalarAsync()));

        // Refunded COGS for restocked items = sum of (sl.quantity * sl.cost_basis) where stock was restored
        using var refCogsCmd = conn.CreateCommand();
        refCogsCmd.CommandText = @"
            SELECT COALESCE(SUM(sl.quantity * sl.cost_basis), 0)
            FROM sale_lines sl
            JOIN sales s ON sl.sale_id = s.sale_id
            WHERE s.shift_id = $sid AND s.status = 3
              AND EXISTS (
                  SELECT 1 FROM stock_movements sm
                  WHERE sm.reference_id = s.sale_id
                    AND sm.product_id = sl.product_id
                    AND sm.movement_type = 'REFUND'
              );
        ";
        refCogsCmd.Parameters.AddWithValue("$sid", shiftId.ToString());
        var refundedRestockedCogs = MoneyCalculator.Round(Convert.ToDecimal(await refCogsCmd.ExecuteScalarAsync()));

        var netCogs = MoneyCalculator.Round(salesCogs - refundedRestockedCogs);
        var grossProfit = MoneyCalculator.Round(netRevenue - netCogs);
        var marginPct = netRevenue > 0 ? MoneyCalculator.Round((grossProfit / netRevenue) * 100m) : 0m;

        var profitSummary = new GrossProfitSummary
        {
            Revenue = netRevenue,
            CostOfGoodsSold = netCogs,
            GrossProfit = grossProfit,
            GrossMarginPercent = marginPct,
            Disclaimer = "Gross Profit only (excludes operating and store expenses)."
        };

        return new ShiftReport
        {
            ShiftId = shift.ShiftId,
            BranchId = shift.BranchId,
            CounterId = shift.CounterId,
            CashierId = shift.CashierId,
            CashierName = cashierDisplayName,
            OpenedAtUtc = shift.OpenedAtUtc,
            ClosedAtUtc = shift.ClosedAtUtc,
            Status = shift.Status,
            TotalSalesCount = totalSalesCount,
            GrossSales = grossSales,
            DiscountTotal = discountTotal,
            TaxTotal = taxTotal,
            NetSales = netSales,
            TotalRefundCount = totalRefundCount,
            TotalRefundAmount = totalRefundAmount,
            TenderSummaries = tenderSummaries,
            DrawerReconciliation = reconciliation,
            CashMovements = cashMovements,
            ProfitSummary = profitSummary
        };
    }

    public async Task<DailyReport> GenerateDailyReportAsync(DateTime dateUtc, string? branchId = null)
    {
        var startOfDay = DateTime.SpecifyKind(dateUtc.Date, DateTimeKind.Utc);
        var endOfDay = startOfDay.AddDays(1);

        using var conn = _db.CreateConnection();

        var branchFilter = string.IsNullOrEmpty(branchId) ? "" : "AND branch_id = $bid";
        var sBranchFilter = string.IsNullOrEmpty(branchId) ? "" : "AND s.branch_id = $bid";

        // Count shifts
        using var shiftCntCmd = conn.CreateCommand();
        shiftCntCmd.CommandText = $@"
            SELECT COUNT(*) FROM shifts
            WHERE opened_at_utc >= $start AND opened_at_utc < $end {branchFilter};
        ";
        shiftCntCmd.Parameters.AddWithValue("$start", startOfDay.ToString("o"));
        shiftCntCmd.Parameters.AddWithValue("$end", endOfDay.ToString("o"));
        if (!string.IsNullOrEmpty(branchId)) shiftCntCmd.Parameters.AddWithValue("$bid", branchId);
        int shiftsCount = Convert.ToInt32(await shiftCntCmd.ExecuteScalarAsync());

        // Completed sales
        using var salesCmd = conn.CreateCommand();
        salesCmd.CommandText = $@"
            SELECT COUNT(*),
                   COALESCE(SUM(subtotal), 0),
                   COALESCE(SUM(discount_total), 0),
                   COALESCE(SUM(tax_total), 0),
                   COALESCE(SUM(grand_total), 0)
            FROM sales
            WHERE created_at_utc >= $start AND created_at_utc < $end AND status = 1 {branchFilter};
        ";
        salesCmd.Parameters.AddWithValue("$start", startOfDay.ToString("o"));
        salesCmd.Parameters.AddWithValue("$end", endOfDay.ToString("o"));
        if (!string.IsNullOrEmpty(branchId)) salesCmd.Parameters.AddWithValue("$bid", branchId);

        int completedSales = 0;
        decimal grossSales = 0m, discTotal = 0m, taxTotal = 0m, netSales = 0m;
        using (var reader = await salesCmd.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                completedSales = reader.GetInt32(0);
                grossSales = MoneyCalculator.Round(reader.GetDecimal(1));
                discTotal = MoneyCalculator.Round(reader.GetDecimal(2));
                taxTotal = MoneyCalculator.Round(reader.GetDecimal(3));
                netSales = MoneyCalculator.Round(reader.GetDecimal(4));
            }
        }

        // Refunds
        using var refCmd = conn.CreateCommand();
        refCmd.CommandText = $@"
            SELECT COUNT(*), COALESCE(SUM(grand_total), 0)
            FROM sales
            WHERE created_at_utc >= $start AND created_at_utc < $end AND status = 3 {branchFilter};
        ";
        refCmd.Parameters.AddWithValue("$start", startOfDay.ToString("o"));
        refCmd.Parameters.AddWithValue("$end", endOfDay.ToString("o"));
        if (!string.IsNullOrEmpty(branchId)) refCmd.Parameters.AddWithValue("$bid", branchId);

        int refundCount = 0;
        decimal totalRefundAmount = 0m;
        using (var reader = await refCmd.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                refundCount = reader.GetInt32(0);
                totalRefundAmount = MoneyCalculator.Round(reader.GetDecimal(1));
            }
        }

        // Tenders
        var tenderSummaries = new List<TenderSummary>();
        var tenderTypes = new[] { TenderType.CASH, TenderType.CARD, TenderType.QR, TenderType.CREDIT };
        foreach (var tType in tenderTypes)
        {
            using var tCmd = conn.CreateCommand();
            tCmd.CommandText = $@"
                SELECT COUNT(t.tender_id),
                       COALESCE(SUM(t.amount_tendered), 0),
                       COALESCE(SUM(t.change_given), 0)
                FROM tenders t
                JOIN sales s ON t.sale_id = s.sale_id
                WHERE s.created_at_utc >= $start AND s.created_at_utc < $end
                  AND s.status = 1 AND t.tender_type = $ttype {sBranchFilter};
            ";
            tCmd.Parameters.AddWithValue("$start", startOfDay.ToString("o"));
            tCmd.Parameters.AddWithValue("$end", endOfDay.ToString("o"));
            tCmd.Parameters.AddWithValue("$ttype", tType.ToString());
            if (!string.IsNullOrEmpty(branchId)) tCmd.Parameters.AddWithValue("$bid", branchId);

            using var reader = await tCmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var count = reader.GetInt32(0);
                var tendered = MoneyCalculator.Round(reader.GetDecimal(1));
                var change = MoneyCalculator.Round(reader.GetDecimal(2));
                tenderSummaries.Add(new TenderSummary
                {
                    TenderType = tType,
                    TransactionCount = count,
                    TotalTendered = tendered,
                    ChangeGiven = change,
                    NetAmount = MoneyCalculator.Round(tendered - change)
                });
            }
        }

        // Cash In & Cash Out totals (scoped to branch via shifts join)
        using var moveCmd = conn.CreateCommand();
        moveCmd.CommandText = $@"
            SELECT COALESCE(SUM(CASE WHEN scm.movement_type = 'CASH_IN' THEN scm.amount ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN scm.movement_type = 'CASH_OUT' THEN scm.amount ELSE 0 END), 0)
            FROM shift_cash_movements scm
            JOIN shifts s ON scm.shift_id = s.shift_id
            WHERE scm.occurred_at_utc >= $start AND scm.occurred_at_utc < $end
              AND ($bid IS NULL OR s.branch_id = $bid);
        ";
        moveCmd.Parameters.AddWithValue("$start", startOfDay.ToString("o"));
        moveCmd.Parameters.AddWithValue("$end", endOfDay.ToString("o"));
        moveCmd.Parameters.AddWithValue("$bid", (object?)branchId ?? DBNull.Value);

        decimal totalCashIn = 0m, totalCashOut = 0m;
        using (var reader = await moveCmd.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                totalCashIn = MoneyCalculator.Round(reader.GetDecimal(0));
                totalCashOut = MoneyCalculator.Round(reader.GetDecimal(1));
            }
        }

        var cashTender = tenderSummaries.FirstOrDefault(t => t.TenderType == TenderType.CASH)?.NetAmount ?? 0m;
        var netDrawerCashChange = MoneyCalculator.Round(cashTender - totalRefundAmount + totalCashIn - totalCashOut);

        // Gross Profit
        var netRevenue = MoneyCalculator.Round(netSales - totalRefundAmount);

        using var cogsCmd = conn.CreateCommand();
        cogsCmd.CommandText = $@"
            SELECT COALESCE(SUM(sl.quantity * sl.cost_basis), 0)
            FROM sale_lines sl
            JOIN sales s ON sl.sale_id = s.sale_id
            WHERE s.created_at_utc >= $start AND s.created_at_utc < $end AND s.status = 1 {sBranchFilter};
        ";
        cogsCmd.Parameters.AddWithValue("$start", startOfDay.ToString("o"));
        cogsCmd.Parameters.AddWithValue("$end", endOfDay.ToString("o"));
        if (!string.IsNullOrEmpty(branchId)) cogsCmd.Parameters.AddWithValue("$bid", branchId);
        var salesCogs = MoneyCalculator.Round(Convert.ToDecimal(await cogsCmd.ExecuteScalarAsync()));

        using var refCogsCmd = conn.CreateCommand();
        refCogsCmd.CommandText = $@"
            SELECT COALESCE(SUM(sl.quantity * sl.cost_basis), 0)
            FROM sale_lines sl
            JOIN sales s ON sl.sale_id = s.sale_id
            WHERE s.created_at_utc >= $start AND s.created_at_utc < $end AND s.status = 3 {sBranchFilter}
              AND EXISTS (
                  SELECT 1 FROM stock_movements sm
                  WHERE sm.reference_id = s.sale_id
                    AND sm.product_id = sl.product_id
                    AND sm.movement_type = 'REFUND'
              );
        ";
        refCogsCmd.Parameters.AddWithValue("$start", startOfDay.ToString("o"));
        refCogsCmd.Parameters.AddWithValue("$end", endOfDay.ToString("o"));
        if (!string.IsNullOrEmpty(branchId)) refCogsCmd.Parameters.AddWithValue("$bid", branchId);
        var restockedCogs = MoneyCalculator.Round(Convert.ToDecimal(await refCogsCmd.ExecuteScalarAsync()));

        var netCogs = MoneyCalculator.Round(salesCogs - restockedCogs);
        var grossProfit = MoneyCalculator.Round(netRevenue - netCogs);
        var marginPct = netRevenue > 0 ? MoneyCalculator.Round((grossProfit / netRevenue) * 100m) : 0m;

        return new DailyReport
        {
            DateUtc = startOfDay,
            BranchId = branchId ?? "ALL",
            ShiftsCount = shiftsCount,
            CompletedSalesCount = completedSales,
            GrossSales = grossSales,
            DiscountTotal = discTotal,
            TaxTotal = taxTotal,
            NetSales = netSales,
            RefundCount = refundCount,
            TotalRefundAmount = totalRefundAmount,
            TenderSummaries = tenderSummaries,
            TotalCashIn = totalCashIn,
            TotalCashOut = totalCashOut,
            NetDrawerCashChange = netDrawerCashChange,
            ProfitSummary = new GrossProfitSummary
            {
                Revenue = netRevenue,
                CostOfGoodsSold = netCogs,
                GrossProfit = grossProfit,
                GrossMarginPercent = marginPct,
                Disclaimer = "Gross Profit only (excludes operating and store expenses)."
            }
        };
    }

    public async Task<InventorySummaryReport> GenerateInventorySummaryReportAsync()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT product_id, barcode, name, stock_on_hand, cost_basis, unit_price
            FROM products
            WHERE is_active = 1
            ORDER BY name ASC;
        ";

        var items = new List<InventoryValuationItem>();
        int lowStock = 0, negativeStock = 0;
        decimal totalValCost = 0m, totalValRetail = 0m;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var pid = reader.GetString(0);
            var barcode = reader.GetString(1);
            var name = reader.GetString(2);
            var soh = reader.GetDecimal(3);
            var cost = reader.GetDecimal(4);
            var retail = reader.GetDecimal(5);

            if (soh < 0) negativeStock++;
            else if (soh <= 5) lowStock++;

            var valCost = MoneyCalculator.Round(soh * cost);
            var valRetail = MoneyCalculator.Round(soh * retail);

            totalValCost += valCost;
            totalValRetail += valRetail;

            items.Add(new InventoryValuationItem
            {
                ProductId = pid,
                Barcode = barcode,
                Name = name,
                StockOnHand = soh,
                CostBasis = cost,
                UnitPrice = retail,
                ValuationAtCost = valCost,
                ValuationAtRetail = valRetail
            });
        }

        return new InventorySummaryReport
        {
            GeneratedAtUtc = DateTime.UtcNow,
            TotalProducts = items.Count,
            LowStockProducts = lowStock,
            NegativeStockProducts = negativeStock,
            TotalValuationAtCost = MoneyCalculator.Round(totalValCost),
            TotalValuationAtRetail = MoneyCalculator.Round(totalValRetail),
            Items = items
        };
    }
}

