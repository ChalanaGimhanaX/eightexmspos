using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class UserEditDialog : Window
{
    private readonly IAuthService _authService;
    private readonly User? _userToEdit;
    private readonly User _actor;
    private readonly string _tenantId;
    private readonly string _branchId;
    private readonly string _counterId;

    public User? SavedUser { get; private set; }

    public UserEditDialog(
        IAuthService authService,
        User actor,
        User? userToEdit = null,
        string tenantId = "TENANT_LK_01",
        string branchId = "B01",
        string counterId = "C01")
    {
        InitializeComponent();
        _authService = authService;
        _actor = actor;
        _userToEdit = userToEdit;
        _tenantId = tenantId;
        _branchId = branchId;
        _counterId = counterId;

        if (_userToEdit != null)
        {
            DialogTitleText.Text = "👤 EDIT USER & ROLE";
            UsernameInput.Text = _userToEdit.Username;
            UsernameInput.IsEnabled = false; // Cannot change username
            DisplayNameInput.Text = _userToEdit.DisplayName;
            PasswordLabel.Text = "New Password (Optional):";
            PasswordHelpText.Text = "Leave blank to keep current password.";
            SaveBtn.Content = "Update User";

            foreach (ComboBoxItem item in RoleCombo.Items)
            {
                if (item.Content.ToString() == _userToEdit.Role.ToString())
                {
                    item.IsSelected = true;
                    break;
                }
            }
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameInput.Text.Trim();
        var displayName = DisplayNameInput.Text.Trim();
        var password = PasswordInput.Password.Trim();

        var selectedRoleStr = ((ComboBoxItem)RoleCombo.SelectedItem)?.Content?.ToString() ?? "Cashier";
        var selectedRole = selectedRoleStr switch
        {
            "Owner" => Role.Owner,
            "Manager" => Role.Manager,
            _ => Role.Cashier
        };

        if (string.IsNullOrWhiteSpace(displayName))
        {
            MessageBox.Show("Please enter a valid display name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (_userToEdit == null)
            {
                // Create New User
                if (string.IsNullOrWhiteSpace(username) || username.Length < 3)
                {
                    MessageBox.Show("Username must be at least 3 characters.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
                {
                    MessageBox.Show("Password must be at least 4 characters.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SavedUser = await _authService.CreateUserAsync(username, displayName, password, selectedRole);
                MessageBox.Show($"User '{SavedUser.Username}' created successfully with role '{SavedUser.Role}'!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // Update Existing User
                if (_userToEdit.Role != selectedRole)
                {
                    await _authService.UpdateUserRoleAsync(_userToEdit.UserId, selectedRole, _actor.UserId, _tenantId, _branchId, _counterId);
                    _userToEdit.Role = selectedRole;
                }

                if (!string.IsNullOrWhiteSpace(password))
                {
                    if (password.Length < 4)
                    {
                        MessageBox.Show("New password must be at least 4 characters.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    await _authService.ResetPasswordAsync(_userToEdit.UserId, password, _actor.UserId, _tenantId, _branchId, _counterId);
                }

                SavedUser = _userToEdit;
                MessageBox.Show($"User '{SavedUser.Username}' updated successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save user: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
