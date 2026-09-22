using System.Windows;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class CashMovementDialog : Window
{
    private readonly IShiftService _shiftService;
    private readonly User _currentUser;
    private readonly CashShift _currentShift;

    public CashMovementDialog(IShiftService shiftService, User currentUser, CashShift currentShift, bool isCashIn = true)
    {
        InitializeComponent();
        _shiftService = shiftService;
        _currentUser = currentUser;
        _currentShift = currentShift;

        if (isCashIn)
        {
            CashInRadio.IsChecked = true;
            DialogTitleText.Text = "💵 Record Cash In (Deposit)";
        }
        else
        {
            CashOutRadio.IsChecked = true;
            DialogTitleText.Text = "💸 Record Cash Out (Payout)";
        }
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(AmountBox.Text, out var amount) || amount <= 0)
        {
            MessageBox.Show("Please enter a valid positive amount.", "Invalid Amount", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var reason = ReasonBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            MessageBox.Show("A reason is mandatory for drawer cash movements. Please provide a reason.", "Reason Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool isCashIn = CashInRadio.IsChecked == true;

        try
        {
            await _shiftService.RecordCashMovementAsync(
                _currentShift.ShiftId,
                amount,
                isCashIn,
                reason,
                _currentUser.UserId,
                "TENANT_LK_01",
                _currentShift.BranchId,
                _currentShift.CounterId
            );

            MessageBox.Show(
                $"Cash {(isCashIn ? "In" : "Out")} of Rs. {amount:N2} recorded successfully.",
                "Cash Movement Recorded",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to record cash movement: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

