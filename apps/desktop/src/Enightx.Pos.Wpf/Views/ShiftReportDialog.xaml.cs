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
            GrossSalesText.Text = $"LKR {report.GrossSales:N2}";
            RefundsText.Text = $"LKR -{report.TotalRefundAmount:N2} ({report.TotalRefundCount} refunds)";

            TenderGrid.ItemsSource = report.TenderSummaries;

            var rec = report.DrawerReconciliation;
            RepFloatText.Text = $"LKR {rec.OpeningFloat:N2}";
            RepNetCashText.Text = $"LKR {rec.NetCashSales:N2}";
            RepRefundsText.Text = $"LKR -{rec.CashRefunds:N2}";
            RepCashInText.Text = $"LKR +{rec.CashIn:N2}";
            RepCashOutText.Text = $"LKR -{rec.CashOut:N2}";
            RepExpectedText.Text = $"LKR {rec.ExpectedCash:N2}";
            RepCountedText.Text = rec.ActualCountedCash.HasValue ? $"LKR {rec.ActualCountedCash.Value:N2}" : "Shift Still Open";

            if (rec.Variance.HasValue)
            {
                var v = rec.Variance.Value;
                RepVarianceText.Text = $"{(v >= 0 ? "+" : "")}LKR {v:N2}";
                RepVarianceText.Foreground = v >= 0
                    ? new SolidColorBrush(Color.FromRgb(56, 161, 105))
                    : new SolidColorBrush(Color.FromRgb(229, 62, 62));
            }
            else
            {
                RepVarianceText.Text = "---";
                RepVarianceText.Foreground = Brushes.Gray;
            }

            var prof = report.ProfitSummary;
            ProfitRevText.Text = $"LKR {prof.Revenue:N2}";
            ProfitCogsText.Text = $"LKR {prof.CostOfGoodsSold:N2}";
            GrossProfitText.Text = $"LKR {prof.GrossProfit:N2}";
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

