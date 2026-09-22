using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class ManagerPinDialog : Window
{
    private readonly IAuthService _authService;
    private readonly string _actionDescription;
    private readonly Role _requiredRole;
    private readonly string _tenantId;
    private readonly string _branchId;
    private readonly string _counterId;

    public User? AuthorizedUser { get; private set; }

    public ManagerPinDialog(
        IAuthService authService,
        string actionDescription,
        Role requiredRole = Role.Manager,
        string tenantId = "TENANT_LK_01",
        string branchId = "B01",
        string counterId = "C01")
    {
        InitializeComponent();
        _authService = authService;
        _actionDescription = actionDescription;
        _requiredRole = requiredRole;
        _tenantId = tenantId;
        _branchId = branchId;
        _counterId = counterId;

        var roleTitle = requiredRole == Role.Owner ? "Owner" : "Manager or Owner";
        ActionDescriptionText.Text = $"Action: {actionDescription}\nPlease enter a valid {roleTitle} PIN to proceed.";
        PinBox.Focus();
    }

    public ManagerPinDialog(
        IAuthService authService,
        string actionDescription,
        string tenantId,
        string branchId = "B01",
        string counterId = "C01")
        : this(authService, actionDescription, Role.Manager, tenantId, branchId, counterId)
    {
    }

    private async void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await AttemptAuthorizeAsync();
        }
    }

    private async void Authorize_Click(object sender, RoutedEventArgs e)
    {
        await AttemptAuthorizeAsync();
    }

    private void Keypad_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string digit)
        {
            PinBox.Password += digit;
            PinBox.Focus();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        PinBox.Password = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        PinBox.Focus();
    }

    private void Backspace_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(PinBox.Password))
        {
            PinBox.Password = PinBox.Password.Substring(0, PinBox.Password.Length - 1);
        }
        PinBox.Focus();
    }

    private async Task AttemptAuthorizeAsync()
    {
        var pin = PinBox.Password?.Trim();
        if (string.IsNullOrWhiteSpace(pin))
        {
            ShowError("Please enter your PIN.");
            return;
        }

        try
        {
            ErrorText.Visibility = Visibility.Collapsed;
            var authorizer = await _authService.VerifyPinAsync(
                pin: pin,
                minimumRole: _requiredRole,
                tenantId: _tenantId,
                branchId: _branchId,
                counterId: _counterId,
                actionDescription: _actionDescription
            );

            AuthorizedUser = authorizer;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            PinBox.SelectAll();
            PinBox.Focus();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
