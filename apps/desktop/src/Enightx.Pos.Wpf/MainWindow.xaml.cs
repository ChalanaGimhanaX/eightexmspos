using System.Windows;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Wpf.ViewModels;
using Enightx.Pos.Wpf.Views;

namespace Enightx.Pos.Wpf;

public partial class MainWindow : Window
{
    private User? _currentUser;
    private CashShift? _currentShift;
    private UpdateManifest? _latestManifest;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        ShowLogin();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var check = await App.UpdateService.CheckForUpdatesAsync();
            if (check.UpdateAvailable && check.Manifest != null)
            {
                _latestManifest = check.Manifest;
                UpdateBannerText.Text = $"A new update (v{_latestManifest.Version}) is available! Click to install.";
                UpdateBanner.Visibility = Visibility.Visible;

                var dialog = new UpdateDialog(_latestManifest, App.UpdateService.CurrentVersion, App.UpdateService)
                {
                    Owner = this
                };
                dialog.ShowDialog();
            }
        }
        catch
        {
            // Silently swallow errors during background update check
        }
    }

    private void OpenUpdateDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_latestManifest != null)
        {
            var dialog = new UpdateDialog(_latestManifest, App.UpdateService.CurrentVersion, App.UpdateService)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }
    }

    private void DismissUpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void ShowLogin()
    {
        var loginVm = new LoginViewModel(App.AuthService);
        loginVm.LoginSucceeded += async (user) =>
        {
            _currentUser = user;
            await InitializeShiftAndBillingAsync();
        };

        var loginView = new LoginView { DataContext = loginVm };
        MainContainer.Children.Clear();
        MainContainer.Children.Add(loginView);
    }

    private async Task InitializeShiftAndBillingAsync()
    {
        if (_currentUser == null) return;

        // Check or open shift
        _currentShift = await App.ShiftService.GetActiveShiftAsync("B01", "C01");
        if (_currentShift == null)
        {
            _currentShift = await App.ShiftService.OpenShiftAsync("B01", "C01", _currentUser.UserId, 5000.00m, "TENANT_LK_01");
        }

        var billingVm = new BillingViewModel(_currentUser, _currentShift, App.CatalogService, App.SaleService);
        var billingView = new BillingView { DataContext = billingVm };

        billingView.RequestPayment += () =>
        {
            if (billingVm.CartItems.Count == 0)
            {
                MessageBox.Show("Cannot proceed to payment with an empty cart.", "Empty Cart", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var paymentVm = new PaymentViewModel(billingVm, App.SaleService, App.ReceiptService);
            var dialog = new PaymentDialog
            {
                Owner = this,
                DataContext = paymentVm
            };

            paymentVm.PaymentCompleted += (sale, printResult) =>
            {
                var msg = $"Sale completed successfully!\nReceipt No: {sale.ReceiptNumber}\nTotal: LKR {sale.GrandTotal:F2}";
                if (!printResult.Success)
                {
                    msg += $"\n\nNotice: Printer communication failed ({printResult.ErrorMessage}).\nReceipt is queued for reprint.";
                }
                MessageBox.Show(msg, "Sale Committed", MessageBoxButton.OK, MessageBoxImage.Information);
            };

            dialog.ShowDialog();
        };

        MainContainer.Children.Clear();
        MainContainer.Children.Add(billingView);
    }
}
