using System.Text.Json;
using System.Windows;
using Enightx.Pos.Common;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class ActivationLockoutDialog : Window
{
    private readonly ILicenseService _licenseService;
    private const string DefaultSigningKey = "enightx_licence_secret_key_prod_2026";

    public bool IsEntitled { get; private set; }

    public ActivationLockoutDialog(ILicenseService licenseService)
    {
        InitializeComponent();
        _licenseService = licenseService;
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var ent = _licenseService.CurrentEntitlement;
        if (ent == null)
        {
            PlanTypeText.Text = "Not Activated";
            StatusText.Text = "LOCKED OUT (No License)";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
            TenantIdText.Text = "-";
            DeviceIdText.Text = "-";
            ExpiresAtText.Text = "-";
            FreezeReasonRow.Visibility = Visibility.Collapsed;
            LockoutAlertBanner.Visibility = Visibility.Visible;
            LockoutAlertMessage.Text = "No active license is loaded on this terminal. Committing new transactions is blocked. Please activate a trial, subscription, or permanent license.";
            IsEntitled = false;
            return;
        }

        PlanTypeText.Text = ent.PlanType switch
        {
            LicensePlanType.Trial => "2-Day Trial (48 Hours)",
            LicensePlanType.Subscription => "Monthly Subscription (LKR 10,000/mo)",
            LicensePlanType.Permanent => "Permanent Purchase",
            _ => ent.PlanType.ToString()
        };

        TenantIdText.Text = ent.TenantId;
        DeviceIdText.Text = ent.DeviceId;
        ExpiresAtText.Text = ent.ExpiresAtUtc.HasValue ? $"{ent.ExpiresAtUtc.Value:yyyy-MM-dd HH:mm:ss} UTC" : "NEVER (Permanent)";

        try
        {
            _licenseService.VerifyEntitlement();
            StatusText.Text = "ACTIVE & LICENSED";
            StatusText.Foreground = System.Windows.Media.Brushes.Green;
            FreezeReasonRow.Visibility = Visibility.Collapsed;
            LockoutAlertBanner.Visibility = Visibility.Collapsed;
            IsEntitled = true;
        }
        catch (LicenseExpiredException ex)
        {
            StatusText.Text = "EXPIRED (LOCKED OUT)";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
            FreezeReasonRow.Visibility = Visibility.Collapsed;
            LockoutAlertBanner.Visibility = Visibility.Visible;
            LockoutAlertMessage.Text = ex.Message;
            IsEntitled = false;
        }
        catch (DeviceFrozenException ex)
        {
            StatusText.Text = "REMOTELY FROZEN (LOCKED OUT)";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
            FreezeReasonRow.Visibility = Visibility.Visible;
            FreezeReasonText.Text = ent.FreezeReason ?? "Administrative lockout";
            LockoutAlertBanner.Visibility = Visibility.Visible;
            LockoutAlertMessage.Text = ex.Message;
            IsEntitled = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = "INVALID";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
            LockoutAlertBanner.Visibility = Visibility.Visible;
            LockoutAlertMessage.Text = ex.Message;
            IsEntitled = false;
        }
    }

    private void QuickTrial_Click(object sender, RoutedEventArgs e)
    {
        var trial = new LicenseEntitlement
        {
            TenantId = "TENANT_LK_01",
            DeviceId = "C01",
            PlanType = LicensePlanType.Trial,
            IssuedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(48),
            IsFrozen = false
        };
        trial.Signature = LicenseService.ComputeSignature(trial, DefaultSigningKey);

        EntitlementJsonInput.Text = JsonSerializer.Serialize(trial, new JsonSerializerOptions { WriteIndented = true });
    }

    private void ApplyEntitlement_Click(object sender, RoutedEventArgs e)
    {
        var json = EntitlementJsonInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            MessageBox.Show("Please paste or generate a license entitlement JSON.", "Empty Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var ent = JsonSerializer.Deserialize<LicenseEntitlement>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (ent == null)
            {
                MessageBox.Show("Invalid license format.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _licenseService.RenewLicense(ent, DefaultSigningKey);
            MessageBox.Show("License entitlement applied successfully!", "Activation Success", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to apply license: {ex.Message}", "Activation Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

