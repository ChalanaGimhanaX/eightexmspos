using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class ShiftOpenDialog : Window
{
    private readonly IShiftService _shiftService;
    private readonly User _user;
    private readonly string _branchId;
    private readonly string _counterId;
    private readonly string _tenantId;

    public CashShift? OpenedShift { get; private set; }

    public ShiftOpenDialog(
        IShiftService shiftService,
        User user,
        string branchId = "B01",
        string counterId = "C01",
        string tenantId = "TENANT_LK_01")
    {
        InitializeComponent();
        _shiftService = shiftService;
        _user = user;
        _branchId = branchId;
        _counterId = counterId;
        _tenantId = tenantId;

        UserInfoText.Text = $"Cashier: {user.DisplayName} ({user.Username}) | Branch: {branchId} | Counter: {counterId}";
        BranchInput.Text = branchId;
        CounterInput.Text = counterId;

        FloatInput.Focus();
        FloatInput.SelectAll();
    }

    private void QuickFloat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            var content = btn.Content?.ToString() ?? "";
            if (content.Contains("2,000")) FloatInput.Text = "2000.00";
            else if (content.Contains("5,000")) FloatInput.Text = "5000.00";
            else if (content.Contains("10,000")) FloatInput.Text = "10000.00";
        }
    }

    private async void OpenShift_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(FloatInput.Text.Trim(), out var openingFloat) || openingFloat < 0)
        {
            MessageBox.Show("Please enter a valid positive opening cash float amount.", "Invalid Float", MessageBoxButton.OK, MessageBoxImage.Warning);
            FloatInput.Focus();
            return;
        }

        try
        {
            OpenedShift = await _shiftService.OpenShiftAsync(
                branchId: _branchId,
                counterId: _counterId,
                cashierId: _user.UserId,
                openingFloat: openingFloat,
                tenantId: _tenantId
            );

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open shift: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

