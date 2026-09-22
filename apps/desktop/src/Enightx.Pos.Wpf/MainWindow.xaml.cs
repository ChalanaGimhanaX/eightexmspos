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
        App.UpdateService.LiveUpdateReceived += OnLiveUpdateReceived;
        App.UpdateService.StartListeningForLiveUpdates();
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
            _latestManifest = manifest;
            UpdateBannerText.Text = $"🚀 LIVE UPDATE: Version {manifest.Version} is available! Click to install.";
            UpdateBanner.Visibility = Visibility.Visible;

            var dialog = new UpdateDialog(_latestManifest, App.UpdateService.CurrentVersion, App.UpdateService)
            {
                Owner = this
            };
            dialog.ShowDialog();
        });
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

    private BillingView? _billingView;

    private void ShowLogin()
    {
        NavBar.Visibility = Visibility.Collapsed;
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

        // Check or prompt shift opening
        _currentShift = await App.ShiftService.GetActiveShiftAsync("B01", "C01");
        if (_currentShift == null)
        {
            var openDialog = new ShiftOpenDialog(App.ShiftService, _currentUser, "B01", "C01", "TENANT_LK_01")
            {
                Owner = this
            };

            if (openDialog.ShowDialog() == true && openDialog.OpenedShift != null)
            {
                _currentShift = openDialog.OpenedShift;
            }
            else
            {
                // User cancelled shift opening -> return to login
                ShowLogin();
                return;
            }
        }

        var billingVm = new BillingViewModel(_currentUser, _currentShift, App.CatalogService, App.SaleService);
        _billingView = new BillingView { DataContext = billingVm };

        _billingView.RequestPayment += () =>
        {
            if (billingVm.CartItems.Count == 0)
            {
                MessageBox.Show("Cannot proceed to payment with an empty cart.", "Empty Cart", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Pre-checkout license lockout check (A13-A16, A21)
            try
            {
                App.LicenseService.VerifyEntitlement();
            }
            catch (Exception)
            {
                var lockDialog = new ActivationLockoutDialog(App.LicenseService)
                {
                    Owner = this
                };
                lockDialog.ShowDialog();
                if (!lockDialog.IsEntitled)
                {
                    return;
                }
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

        // Wire up sync worker status badge
        if (App.SyncWorker != null)
        {
            App.SyncWorker.OnlineStatusChanged += isOnline =>
            {
                Dispatcher.InvokeAsync(() => UpdateSyncBadge(isOnline));
            };
            UpdateSyncBadge(App.SyncWorker.IsOnline);
        }

        NavBar.Visibility = Visibility.Visible;
        MainContainer.Children.Clear();
        MainContainer.Children.Add(_billingView);
    }

    private void UpdateSyncBadge(bool isOnline)
    {
        if (SyncStatusBadge == null || SyncStatusText == null) return;
        if (isOnline)
        {
            SyncStatusBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));
            SyncStatusText.Text = "🟢 ONLINE";
        }
        else
        {
            SyncStatusBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD8, 0x43, 0x15));
            SyncStatusText.Text = "🟠 OFFLINE";
        }
    }

    private async void ManualSync_Click(object sender, RoutedEventArgs e)
    {
        if (App.SyncWorker == null) return;
        SyncStatusText.Text = "⏳ SYNCING...";
        try
        {
            await App.SyncWorker.SyncCycleAsync();
            UpdateSyncBadge(App.SyncWorker.IsOnline);
            await CheckForUpdatesAsync();
        }
        catch
        {
            UpdateSyncBadge(false);
        }
    }

    private void LangEn_Click(object sender, RoutedEventArgs e) => SwitchLanguage("en");
    private void LangSi_Click(object sender, RoutedEventArgs e) => SwitchLanguage("si");
    private void LangTa_Click(object sender, RoutedEventArgs e) => SwitchLanguage("ta");

    private void SwitchLanguage(string cultureCode)
    {
        try
        {
            var culture = new System.Globalization.CultureInfo(cultureCode);
            System.Threading.Thread.CurrentThread.CurrentCulture = culture;
            System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
            MessageBox.Show($"Interface language switched to {culture.NativeName}.", "Language Switch", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Language switch error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void NavBilling_Click(object sender, RoutedEventArgs e)
    {
        if (_billingView != null)
        {
            MainContainer.Children.Clear();
            MainContainer.Children.Add(_billingView);
        }
    }

    private void NavSalesHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser == null || _currentShift == null) return;
        var view = new SalesHistoryView(App.SaleService, App.ReceiptService, _currentUser, _currentShift.ShiftId);
        view.RequestBackToPos += () => NavBilling_Click(this, new RoutedEventArgs());
        MainContainer.Children.Clear();
        MainContainer.Children.Add(view);
    }

    private void NavCustomers_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser == null) return;
        var view = new CustomerLedgerView(App.Database, "TENANT_LK_01", "B01", "C01", _currentUser.UserId);
        view.RequestBackToPos += () => NavBilling_Click(this, new RoutedEventArgs());
        MainContainer.Children.Clear();
        MainContainer.Children.Add(view);
    }

    private void NavTransfers_Click(object sender, RoutedEventArgs e)
    {
        var view = new StockTransferView(App.CatalogService, App.TransferService);
        view.RequestBackToPos += () => NavBilling_Click(this, new RoutedEventArgs());
        MainContainer.Children.Clear();
        MainContainer.Children.Add(view);
    }

    private void NavReceiving_Click(object sender, RoutedEventArgs e)
    {
        var view = new ReceivingView(App.Database, App.CatalogService);
        view.RequestBackToPos += () => NavBilling_Click(this, new RoutedEventArgs());
        MainContainer.Children.Clear();
        MainContainer.Children.Add(view);
    }

    private void NavDashboard_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser == null) return;
        var view = new ManagerDashboardView(
            _currentUser,
            App.Database,
            App.CatalogService,
            App.ShiftService,
            App.SaleService,
            App.AuthService,
            App.ReceiptService);

        view.RequestBackToPos += () => NavBilling_Click(this, new RoutedEventArgs());
        view.RequestOpenBilling += () => NavBilling_Click(this, new RoutedEventArgs());
        view.RequestOpenCustomers += () => NavCustomers_Click(this, new RoutedEventArgs());
        view.RequestOpenTransfers += () => NavTransfers_Click(this, new RoutedEventArgs());
        view.RequestOpenReceiving += () => NavReceiving_Click(this, new RoutedEventArgs());
        view.RequestOpenSalesHistory += () => NavSalesHistory_Click(this, new RoutedEventArgs());
        view.RequestOpenCloseShift += () => NavCloseShift_Click(this, new RoutedEventArgs());

        MainContainer.Children.Clear();
        MainContainer.Children.Add(view);
    }

    private void NavLicense_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ActivationLockoutDialog(App.LicenseService)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void NavCashMovement_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser == null || _currentShift == null)
        {
            MessageBox.Show("An active shift is required to record drawer cash movements.", "Shift Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new CashMovementDialog(App.ShiftService, _currentShift, _currentUser)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void NavBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BackupRestoreDialog
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void NavCashierBalance_Click(object sender, RoutedEventArgs e)
    {
        if (_currentShift == null || _currentUser == null)
        {
            MessageBox.Show("No active cashier shift found on this counter.\nPlease log in or open a shift first.", "Cashier Balance", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ShiftCloseDialog(_currentShift, App.ShiftService, _currentUser.UserId)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _currentShift = null;
            _billingView = null;
            ShowLogin();
        }
    }

    private void NavCloseShift_Click(object sender, RoutedEventArgs e)
    {
        if (_currentShift == null || _currentUser == null)
        {
            MessageBox.Show("No active shift found to close.", "Shift Reconciliation", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ShiftCloseDialog(_currentShift, App.ShiftService, _currentUser.UserId)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _currentShift = null;
            _billingView = null;
            ShowLogin();
        }
    }

    private void NavLogout_Click(object sender, RoutedEventArgs e)
    {
        _currentUser = null;
        _currentShift = null;
        _billingView = null;
        ShowLogin();
    }
}

