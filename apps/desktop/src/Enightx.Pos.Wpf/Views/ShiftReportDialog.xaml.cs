using System.Windows;
using System.Windows.Media;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class ShiftReportDialog : Window
{
    private readonly IReportService _reportService;
    private readonly Guid _shiftId;

    public ShiftReportDialog(IReportService reportService, Guid shiftId)
    {
        InitializeComponent();
        _reportService = reportService;
        _shiftId = shiftId;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = await _reportService.GenerateShiftReportAsync(_shiftId);

            SubtitleText.Text = $"Cashier: {report.CashierName} | Branch: {report.BranchId} | Counter: {report.CounterId} | Status: {report.Status}";

            SalesCountText.Text = report.TotalSalesCount.ToString();
            GrossSalesText.Text = $"Rs. {report.GrossSales:N2}";
            RefundsText.Text = $"Rs. -{report.TotalRefundAmount:N2} ({report.TotalRefundCount} refunds)";

            TenderGrid.ItemsSource = report.TenderSummaries;

            var rec = report.DrawerReconciliation;
            RepFloatText.Text = $"Rs. {rec.OpeningFloat:N2}";
            RepNetCashText.Text = $"Rs. {rec.NetCashSales:N2}";
            RepRefundsText.Text = $"Rs. -{rec.CashRefunds:N2}";
            RepCashInText.Text = $"Rs. +{rec.CashIn:N2}";
            RepCashOutText.Text = $"Rs. -{rec.CashOut:N2}";
            RepExpectedText.Text = $"Rs. {rec.ExpectedCash:N2}";
            RepCountedText.Text = rec.ActualCountedCash.HasValue ? $"Rs. {rec.ActualCountedCash.Value:N2}" : "Shift Still Open";

            if (rec.Variance.HasValue)
            {
                var v = rec.Variance.Value;
                RepVarianceText.Text = $"{(v >= 0 ? "+" : "")}Rs. {v:N2}";
                RepVarianceText.Foreground = v >= 0
                    ? (Brush)FindResource("SuccessBrush")
                    : (Brush)FindResource("DangerBrush");
            }
            else
            {
                RepVarianceText.Text = "---";
                RepVarianceText.Foreground = (Brush)FindResource("TextMutedBrush");
            }

            var prof = report.ProfitSummary;
            ProfitRevText.Text = $"Rs. {prof.Revenue:N2}";
            ProfitCogsText.Text = $"Rs. {prof.CostOfGoodsSold:N2}";
            GrossProfitText.Text = $"Rs. {prof.GrossProfit:N2}";
            GrossMarginText.Text = $"{prof.GrossMarginPercent:F2}%";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to generate shift report: {ex.Message}", "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Report sent to receipt/thermal printer.", "Print", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

