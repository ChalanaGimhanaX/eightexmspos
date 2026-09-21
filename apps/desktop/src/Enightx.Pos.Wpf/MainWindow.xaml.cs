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

        // Check or open shift
        _currentShift = await App.ShiftService.GetActiveShiftAsync("B01", "C01");
        if (_currentShift == null)
        {
            _currentShift = await App.ShiftService.OpenShiftAsync("B01", "C01", _currentUser.UserId, 5000.00m, "TENANT_LK_01");
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

        NavBar.Visibility = Visibility.Visible;
        MainContainer.Children.Clear();
        MainContainer.Children.Add(_billingView);
    }

    private void NavBilling_Click(object sender, RoutedEventArgs e)
    {
        if (_billingView != null)
        {
            MainContainer.Children.Clear();
            MainContainer.Children.Add(_billingView);
        }
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

