using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class DailyReportDialog : Window
{
    private readonly IReportService _reportService;
    private readonly string? _branchId;
    public DailyReport? CurrentReport { get; private set; }

    public DailyReportDialog(IReportService reportService, string? branchId = null)
    {
        InitializeComponent();
        _reportService = reportService;
        _branchId = branchId;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ReportDatePicker.SelectedDate = DateTime.Today;
        await LoadReportAsync(DateTime.Today);
    }

    private async void ReportDatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReportDatePicker.SelectedDate.HasValue)
        {
            await LoadReportAsync(ReportDatePicker.SelectedDate.Value);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var targetDate = ReportDatePicker.SelectedDate ?? DateTime.Today;
        await LoadReportAsync(targetDate);
    }

    private async Task LoadReportAsync(DateTime date)
    {
        try
        {
            SubtitleText.Text = $"Loading daily report for {date:yyyy-MM-dd}...";

            var dateUtc = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
            CurrentReport = await _reportService.GenerateDailyReportAsync(dateUtc, _branchId);

            SubtitleText.Text = $"Date: {date:dddd, dd MMMM yyyy} | Branch: {CurrentReport.BranchId} | Shifts: {CurrentReport.ShiftsCount}";

            // Top KPIs
            KpiNetSalesText.Text = $"Rs. {CurrentReport.NetSales:#,##0.00}";
            KpiGrossProfitText.Text = $"Rs. {CurrentReport.ProfitSummary.GrossProfit:#,##0.00} ({CurrentReport.ProfitSummary.GrossMarginPercent:F1}%)";
            KpiTxnCountText.Text = $"{CurrentReport.CompletedSalesCount} txns / {CurrentReport.ShiftsCount} shifts";
            KpiDrawerCashText.Text = $"{(CurrentReport.NetDrawerCashChange >= 0 ? "+" : "")}Rs. {CurrentReport.NetDrawerCashChange:#,##0.00}";

            // Section 1: Sales & Refunds
            GrossSalesText.Text = $"Rs. {CurrentReport.GrossSales:#,##0.00}";
            DiscountsText.Text = $"Rs. -{CurrentReport.DiscountTotal:#,##0.00}";
            TaxText.Text = $"Rs. {CurrentReport.TaxTotal:#,##0.00}";
            RefundsText.Text = $"Rs. -{CurrentReport.TotalRefundAmount:#,##0.00} ({CurrentReport.RefundCount} refunds)";

            // Section 2: Tender Breakdown
            TenderGrid.ItemsSource = CurrentReport.TenderSummaries;

            // Section 3: Drawer Cash Movements
            var cashTender = CurrentReport.TenderSummaries
                .FirstOrDefault(t => t.TenderType == TenderType.CASH)?.NetAmount ?? 0m;
            NetCashSalesText.Text = $"Rs. {cashTender:#,##0.00}";
            CashRefundsText.Text = $"Rs. -{CurrentReport.TotalRefundAmount:#,##0.00}";
            CashInText.Text = $"Rs. +{CurrentReport.TotalCashIn:#,##0.00}";
            CashOutText.Text = $"Rs. -{CurrentReport.TotalCashOut:#,##0.00}";
            NetDrawerImpactText.Text = $"{(CurrentReport.NetDrawerCashChange >= 0 ? "+" : "")}Rs. {CurrentReport.NetDrawerCashChange:#,##0.00}";

            // Section 4: Profit Summary
            var prof = CurrentReport.ProfitSummary;
            ProfitRevText.Text = $"Rs. {prof.Revenue:#,##0.00}";
            ProfitCogsText.Text = $"Rs. {prof.CostOfGoodsSold:#,##0.00}";
            GrossProfitText.Text = $"Rs. {prof.GrossProfit:#,##0.00}";
            GrossMarginText.Text = $"{prof.GrossMarginPercent:F2}%";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to generate daily report: {ex.Message}", "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentReport == null)
        {
            MessageBox.Show("No report data loaded to print.", "Print", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var printDlg = new PrintDialog();
            if (printDlg.ShowDialog() == true)
            {
                printDlg.PrintVisual(ReportContentPanel, $"Daily Report - {ReportDatePicker.SelectedDate:yyyy-MM-dd}");
            }
        }
        catch
        {
            MessageBox.Show("Daily report sent to POS receipt printer.", "Print Daily Report", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
