using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class OpenShiftDialog : Window
{
    private readonly User _currentUser;
    private readonly string _branchId;
    private readonly string _counterId;
    private readonly IShiftService? _shiftService;
    private readonly string _tenantId;

    public decimal OpeningFloat { get; private set; }
    public CashShift? OpenedShift { get; private set; }

    /// <summary>
    /// Standalone constructor: validates and exposes OpeningFloat for MainWindow to open the shift.
    /// </summary>
    public OpenShiftDialog(User currentUser, string branchId = "B01", string counterId = "C01")
        : this(null, currentUser, branchId, counterId, "TENANT_LK_01")
    {
    }

    /// <summary>
    /// Injected constructor: can open the shift directly via IShiftService.
    /// </summary>
    public OpenShiftDialog(IShiftService? shiftService, User currentUser, string branchId = "B01", string counterId = "C01", string tenantId = "TENANT_LK_01")
    {
        InitializeComponent();
        _shiftService = shiftService;
        _currentUser = currentUser;
        _branchId = branchId;
        _counterId = counterId;
        _tenantId = tenantId;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        CashierText.Text = $"{_currentUser.DisplayName} ({_currentUser.Username})";
        StationText.Text = $"Branch: {_branchId} | Counter: {_counterId}";

        FloatBox.Focus();
        FloatBox.SelectAll();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string amountStr)
        {
            FloatBox.Text = amountStr;
            ClearError();
            FloatBox.Focus();
            FloatBox.SelectAll();
        }
    }

    private void FloatBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearError();
    }

    private void FloatBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ConfirmOpen_Click(sender, e);
        }
    }

    private async void ConfirmOpen_Click(object sender, RoutedEventArgs e)
    {
        var rawInput = FloatBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(rawInput) || !decimal.TryParse(rawInput, out var parsedFloat))
        {
            ShowError("Please enter a valid numeric cash float amount.");
            FloatBox.Focus();
            return;
        }

        if (parsedFloat < 0)
        {
            ShowError("Opening float cannot be negative.");
            FloatBox.Focus();
            return;
        }

        OpeningFloat = MoneyCalculator.Round(parsedFloat);

        // If IShiftService was injected, perform shift opening directly inside the dialog
        if (_shiftService != null)
        {
            try
            {
                OpenedShift = await _shiftService.OpenShiftAsync(
                    _branchId,
                    _counterId,
                    _currentUser.UserId,
                    OpeningFloat,
                    _tenantId
                );
            }
            catch (Exception ex)
            {
                ShowError($"Failed to open shift: {ex.Message}");
                return;
            }
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        if (ErrorText != null)
        {
            ErrorText.Text = string.Empty;
            ErrorText.Visibility = Visibility.Collapsed;
        }
    }
}

