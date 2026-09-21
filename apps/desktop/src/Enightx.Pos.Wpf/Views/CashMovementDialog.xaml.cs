using System.Windows;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class CashMovementDialog : Window
{
    private readonly IShiftService _shiftService;
    private readonly CashShift _shift;
    private readonly User _user;

    public CashMovementDialog(IShiftService shiftService, CashShift shift, User user)
    {
        InitializeComponent();
        _shiftService = shiftService;
        _shift = shift;
        _user = user;

        ShiftInfoText.Text = $"Shift: {shift.ShiftId.ToString()[..8]}... | Cashier: {user.DisplayName} | Counter: {shift.CounterId}";
        AmountInput.Focus();
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(AmountInput.Text.Trim(), out var amount) || amount <= 0)
        {
            MessageBox.Show("Please enter a valid positive cash amount.", "Invalid Amount", MessageBoxButton.OK, MessageBoxImage.Warning);
            AmountInput.Focus();
            return;
        }

        var reason = ReasonInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            MessageBox.Show("A reason is mandatory for drawer cash movements.", "Reason Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            ReasonInput.Focus();
            return;
        }

        var isCashIn = RadioCashIn.IsChecked == true;

        try
        {
            await _shiftService.RecordCashMovementAsync(
                shiftId: _shift.ShiftId,
                amount: amount,
                isCashIn: isCashIn,
                reason: reason,
                actorId: _user.UserId,
                tenantId: "TENANT_LK_01",
                branchId: _shift.BranchId,
                counterId: _shift.CounterId
            );

            var actionLabel = isCashIn ? "Cash In" : "Cash Out";
            MessageBox.Show($"{actionLabel} of LKR {amount:N2} recorded successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
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
        DialogResult = false;
        Close();
    }
}

