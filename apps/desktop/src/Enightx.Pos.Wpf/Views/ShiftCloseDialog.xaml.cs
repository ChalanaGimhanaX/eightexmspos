using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Common;

namespace Enightx.Pos.Wpf.Views;

public partial class ShiftCloseDialog : Window
{
    private readonly CashShift _shift;
    private readonly IShiftService _shiftService;
    private readonly string _actorId;
    public CashShift? ReconciledShift { get; private set; }

    public ShiftCloseDialog(CashShift shift, IShiftService shiftService, string actorId)
    {
        InitializeComponent();
        _shift = shift;
        _shiftService = shiftService;
        _actorId = actorId;

        PopulateShiftData();
    }

    private void PopulateShiftData()
    {
        ShiftInfoText.Text = $"Shift ID: {_shift.ShiftId:N} | Cashier: {_shift.CashierId} | Opened: {_shift.OpenedAtUtc:yyyy-MM-dd HH:mm}";
        OpeningFloatText.Text = $"LKR {_shift.OpeningFloat:F2}";
        CashReceivedText.Text = $"LKR {_shift.CashReceived:F2}";
        ChangeGivenText.Text = $"LKR {_shift.ChangeGiven:F2}";
        CashRefundsText.Text = $"LKR {_shift.CashRefunds:F2}";
        CashInText.Text = $"LKR {_shift.CashIn:F2}";
        CashOutText.Text = $"LKR {_shift.CashOut:F2}";
        ExpectedCashText.Text = $"LKR {_shift.ExpectedCash:F2}";

        ActualCashInput.Text = _shift.ExpectedCash.ToString("F2");
        UpdateVariance();
    }

    private void ActualCashInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateVariance();
    }

    private void UpdateVariance()
    {
        if (decimal.TryParse(ActualCashInput.Text.Trim(), out var actual))
        {
            var expected = _shift.ExpectedCash;
            var variance = MoneyCalculator.Round(actual - expected);

            if (variance < 0)
            {
                VarianceText.Text = $"LKR {variance:F2} (Shortage)";
                VarianceText.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40)); // Red
            }
            else if (variance > 0)
            {
                VarianceText.Text = $"LKR +{variance:F2} (Overage)";
                VarianceText.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50)); // Green
            }
            else
            {
                VarianceText.Text = "LKR 0.00 (Balanced)";
                VarianceText.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50)); // Green
            }
        }
        else
        {
            VarianceText.Text = "--";
            VarianceText.Foreground = Brushes.Gray;
        }
    }

    private async void CloseShift_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(ActualCashInput.Text.Trim(), out var actualCash) || actualCash < 0)
        {
            MessageBox.Show("Please enter a valid positive counted cash amount.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            CloseShiftBtn.IsEnabled = false;
            var notes = string.IsNullOrWhiteSpace(NotesInput.Text) ? null : NotesInput.Text.Trim();
            ReconciledShift = await _shiftService.CloseShiftAsync(_shift.ShiftId, actualCash, notes, _actorId);

            var variance = ReconciledShift.Variance ?? 0;
            var summaryMsg = $"Shift closed successfully!\n\nExpected Cash: LKR {ReconciledShift.ExpectedCash:F2}\nActual Counted: LKR {actualCash:F2}\nVariance: LKR {variance:F2}";
            
            if (variance != 0)
            {
                summaryMsg += $"\n\nAudit notice: A cash variance of LKR {variance:F2} was recorded and flagged for manager review.";
            }

            MessageBox.Show(summaryMsg, "Shift Reconciled", MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error reconciling shift: {ex.Message}", "Reconciliation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            CloseShiftBtn.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

