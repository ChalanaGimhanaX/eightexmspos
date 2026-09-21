using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class CloseShiftDialog : Window
{
    private readonly IShiftService _shiftService;
    private readonly User _currentUser;
    private readonly CashShift _currentShift;
    private decimal _expectedCash;

    public CashShift? ClosedShift { get; private set; }

    public CloseShiftDialog(IShiftService shiftService, User currentUser, CashShift currentShift)
    {
        InitializeComponent();
        _shiftService = shiftService;
        _currentUser = currentUser;
        _currentShift = currentShift;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Load latest shift ledger state
        var shift = await _shiftService.GetActiveShiftAsync(_currentShift.BranchId, _currentShift.CounterId);
        var s = shift ?? _currentShift;

        var netCashSales = MoneyCalculator.Round(s.CashReceived - s.ChangeGiven);
        _expectedCash = MoneyCalculator.CalculateShiftExpectedCash(
            s.OpeningFloat,
            s.CashReceived,
            s.ChangeGiven,
            s.CashRefunds,
            s.CashIn,
            s.CashOut
        );

        OpeningFloatText.Text = $"LKR {s.OpeningFloat:N2}";
        NetCashSalesText.Text = $"LKR {netCashSales:N2} (Rec: {s.CashReceived:N2}, Chg: {s.ChangeGiven:N2})";
        CashRefundsText.Text = $"LKR -{s.CashRefunds:N2}";
        CashInText.Text = $"LKR +{s.CashIn:N2}";
        CashOutText.Text = $"LKR -{s.CashOut:N2}";
        ExpectedCashText.Text = $"LKR {_expectedCash:N2}";

        CountedCashBox.Text = _expectedCash.ToString("F2");
        UpdateVariance();
    }

    private void CountedCashBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateVariance();
    }

    private void UpdateVariance()
    {
        if (decimal.TryParse(CountedCashBox?.Text, out var counted))
        {
            var variance = MoneyCalculator.Round(counted - _expectedCash);
            if (variance == 0)
            {
                VarianceText.Text = "LKR 0.00";
                VarianceText.Foreground = new SolidColorBrush(Color.FromRgb(56, 161, 105)); // Green
                VarianceNote.Text = "Balanced (No discrepancy)";
                VarianceNote.Foreground = new SolidColorBrush(Color.FromRgb(113, 128, 150));
            }
            else if (variance > 0)
            {
                VarianceText.Text = $"+LKR {variance:N2}";
                VarianceText.Foreground = new SolidColorBrush(Color.FromRgb(49, 130, 206)); // Blue
                VarianceNote.Text = $"Cash Overage of LKR {variance:N2}";
                VarianceNote.Foreground = new SolidColorBrush(Color.FromRgb(49, 130, 206));
            }
            else
            {
                VarianceText.Text = $"-LKR {Math.Abs(variance):N2}";
                VarianceText.Foreground = new SolidColorBrush(Color.FromRgb(229, 62, 62)); // Red
                VarianceNote.Text = $"Cash Shortage of LKR {Math.Abs(variance):N2}";
                VarianceNote.Foreground = new SolidColorBrush(Color.FromRgb(229, 62, 62));
            }
        }
        else
        {
            VarianceText.Text = "---";
            VarianceNote.Text = "Enter a valid counted cash amount.";
        }
    }

    private async void ConfirmClose_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(CountedCashBox.Text, out var counted) || counted < 0)
        {
            MessageBox.Show("Please enter a valid non-negative counted cash amount.", "Invalid Counted Cash", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var variance = MoneyCalculator.Round(counted - _expectedCash);
        var message = $"Expected Cash: LKR {_expectedCash:N2}\nCounted Cash: LKR {counted:N2}\nVariance: {(variance >= 0 ? "+" : "")}LKR {variance:N2}\n\nConfirm closing shift?";
        var confirm = MessageBox.Show(message, "Confirm Shift Closing", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var closed = await _shiftService.CloseShiftAsync(
                _currentShift.ShiftId,
                counted,
                _currentUser.UserId,
                "TENANT_LK_01"
            );

            ClosedShift = closed;
            MessageBox.Show(
                $"Shift closed successfully!\n\nFinal Expected: LKR {closed.ExpectedCash:N2}\nFinal Counted: LKR {closed.ActualCountedCash:N2}\nFinal Variance: {(closed.Variance >= 0 ? "+" : "")}LKR {closed.Variance:N2}",
                "Shift Closed",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to close shift: {ex.Message}", "Closing Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

