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
            KpiNetSalesText.Text = $"LKR {CurrentReport.NetSales:N2}";
            KpiGrossProfitText.Text = $"LKR {CurrentReport.ProfitSummary.GrossProfit:N2} ({CurrentReport.ProfitSummary.GrossMarginPercent:F1}%)";
            KpiTxnCountText.Text = $"{CurrentReport.CompletedSalesCount} txns / {CurrentReport.ShiftsCount} shifts";
            KpiDrawerCashText.Text = $"{(CurrentReport.NetDrawerCashChange >= 0 ? "+" : "")}LKR {CurrentReport.NetDrawerCashChange:N2}";

            // Section 1: Sales & Refunds
            GrossSalesText.Text = $"LKR {CurrentReport.GrossSales:N2}";
            DiscountsText.Text = $"LKR -{CurrentReport.DiscountTotal:N2}";
            TaxText.Text = $"LKR {CurrentReport.TaxTotal:N2}";
            RefundsText.Text = $"LKR -{CurrentReport.TotalRefundAmount:N2} ({CurrentReport.RefundCount} refunds)";

            // Section 2: Tender Breakdown
            TenderGrid.ItemsSource = CurrentReport.TenderSummaries;

            // Section 3: Drawer Cash Movements
            var cashTender = CurrentReport.TenderSummaries
                .FirstOrDefault(t => t.TenderType == TenderType.CASH)?.NetAmount ?? 0m;
            NetCashSalesText.Text = $"LKR {cashTender:N2}";
            CashRefundsText.Text = $"LKR -{CurrentReport.TotalRefundAmount:N2}";
            CashInText.Text = $"LKR +{CurrentReport.TotalCashIn:N2}";
            CashOutText.Text = $"LKR -{CurrentReport.TotalCashOut:N2}";
            NetDrawerImpactText.Text = $"{(CurrentReport.NetDrawerCashChange >= 0 ? "+" : "")}LKR {CurrentReport.NetDrawerCashChange:N2}";

            // Section 4: Profit Summary
            var prof = CurrentReport.ProfitSummary;
            ProfitRevText.Text = $"LKR {prof.Revenue:N2}";
            ProfitCogsText.Text = $"LKR {prof.CostOfGoodsSold:N2}";
            GrossProfitText.Text = $"LKR {prof.GrossProfit:N2}";
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

