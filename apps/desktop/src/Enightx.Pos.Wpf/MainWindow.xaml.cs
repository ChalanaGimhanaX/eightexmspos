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
    private BillingViewModel? _billingVm;
    private bool _updateDialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        App.UpdateService.LiveUpdateReceived += OnLiveUpdateReceived;
        App.UpdateService.StartListeningForLiveUpdates();
        Closed += (_, _) => { App.UpdateService.LiveUpdateReceived -= OnLiveUpdateReceived; App.UpdateService.StopListeningForLiveUpdates(); };
        ShowLogin();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync();
    }

    private void OnLiveUpdateReceived(UpdateManifest manifest)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ShowUpdateBanner(manifest);
        });
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var check = await App.UpdateService.CheckForUpdatesAsync();
            if (check.UpdateAvailable && check.Manifest != null)
            {
                ShowUpdateBanner(check.Manifest);
            }
        }
        catch
        {
            // Silently swallow errors during background update check
        }
    }

    private void OpenUpdateDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_updateDialogOpen) return;
        if (_billingVm?.CartItems.Count > 0)
        {
            MessageBox.Show("Finish or clear the current bill before installing an update.", "Update available");
            return;
        }
        if (_latestManifest != null)
        {
            var dialog = new UpdateDialog(_latestManifest, App.UpdateService.CurrentVersion, App.UpdateService)
            {
                Owner = this
            };
            _updateDialogOpen = true;
            try { dialog.ShowDialog(); }
            finally { _updateDialogOpen = false; }
        }
    }

    private void ShowUpdateBanner(UpdateManifest manifest)
    {
        if (_latestManifest != null && !UpdateService.IsVersionNewer(manifest.Version, _latestManifest.Version)) return;
        _latestManifest = manifest;
        UpdateBannerText.Text = $"Version {manifest.Version} is available. Install when billing is finished.";
        UpdateBanner.Visibility = Visibility.Visible;
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

        var billingVm = new BillingViewModel(_currentUser, _currentShift, App.CatalogService, App.SaleService, App.HeldCartService);
        _billingVm = billingVm;
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

        billingView.ShiftClosed += () =>
        {
            _currentShift = null;
            _billingVm = null;
            ShowLogin();
        };

        MainContainer.Children.Clear();
        MainContainer.Children.Add(billingView);
    }
}
