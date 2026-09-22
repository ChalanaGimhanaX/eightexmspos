using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.ViewModels;
using Enightx.Pos.Wpf.ViewModels;
using Enightx.Pos.Wpf.Views;

namespace Enightx.Pos.Wpf;

public partial class MainWindow : Window
{
    private User? _currentUser;
    private CashShift? _currentShift;
    private UpdateManifest? _latestManifest;
    private bool _updateDialogOpen;

    // Shell state machine
    private ShellViewModel? _shellVm;

    // In-memory view caching for zero state loss during navigation
    private BillingView? _cachedBillingView;
    private BillingViewModel? _cachedBillingVm;
    private UserControl? _cachedManagerOperationsView;
    private UserControl? _cachedOwnerAdminView;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        App.UpdateService.LiveUpdateReceived += OnLiveUpdateReceived;
        App.UpdateService.StartListeningForLiveUpdates();
        Closed += (_, _) =>
        {
            App.UpdateService.LiveUpdateReceived -= OnLiveUpdateReceived;
            App.UpdateService.StopListeningForLiveUpdates();
        };

        ShowLogin();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync();
    }

    // =========================================================================
    // 1. LOGIN & SHELL LIFECYCLE
    // =========================================================================

    public void ShowLogin()
    {
        _currentUser = null;
        _currentShift = null;
        _shellVm = null;
        _cachedBillingView = null;
        _cachedBillingVm = null;
        _cachedManagerOperationsView = null;
        _cachedOwnerAdminView = null;

        // Hide persistent shell Chrome on Login screen
        TopHeaderBar.Visibility = Visibility.Collapsed;
        LeftNavRail.Visibility = Visibility.Collapsed;

        var loginVm = new LoginViewModel(App.AuthService);
        loginVm.LoginSucceeded += async (user) =>
        {
            _currentUser = user;
            await InitializeShiftAndShellAsync();
        };

        var loginView = new LoginView { DataContext = loginVm };
        MainContentContainer.Children.Clear();
        MainContentContainer.Children.Add(loginView);
    }

    private async Task InitializeShiftAndShellAsync()
    {
        if (_currentUser == null) return;

        // Query active cashier shift
        _currentShift = await App.ShiftService.GetActiveShiftAsync("B01", "C01");
        if (_currentShift == null)
        {
            var openShiftDialog = new OpenShiftDialog(_currentUser, "B01", "C01")
            {
                Owner = this
            };

            if (openShiftDialog.ShowDialog() != true)
            {
                // Shift cancelled; abort session and return to login
                ShowLogin();
                return;
            }

            try
            {
                _currentShift = await App.ShiftService.OpenShiftAsync(
                    "B01",
                    "C01",
                    _currentUser.UserId,
                    openShiftDialog.OpeningFloat,
                    "TENANT_LK_01"
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to open cashier shift: {ex.Message}",
                    "Shift Open Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                ShowLogin();
                return;
            }
        }

        // Instantiate ShellViewModel
        _shellVm = new ShellViewModel(_currentUser, _currentShift, App.ThemeManager);
        DataContext = _shellVm;

        // Wire Shell events
        _shellVm.ViewChanged += OnShellViewChanged;
        _shellVm.LogoutRequested += OnShellLogoutRequested;
        _shellVm.ElevationRequested = OnShellElevationRequestedAsync;

        // Show Top Header Bar and Left Navigation Rail
        TopHeaderBar.Visibility = Visibility.Visible;
        LeftNavRail.Visibility = Visibility.Visible;

        // Default navigation to Checkout view
        SwitchToView(ShellViewNames.Checkout);
    }

    // =========================================================================
    // 2. VIEW ROUTING & IN-MEMORY CACHING (ZERO CART LOSS)
    // =========================================================================

    private void OnShellViewChanged(string viewName)
    {
        SwitchToView(viewName);
    }

    private void SwitchToView(string viewName)
    {
        MainContentContainer.Children.Clear();
        UpdateNavRailHighlight(viewName);

        switch (viewName)
        {
            case ShellViewNames.Checkout:
                if (_cachedBillingView == null)
                {
                    _cachedBillingVm = new BillingViewModel(_currentUser!, _currentShift!, App.CatalogService, App.SaleService, App.HeldCartService);
                    _cachedBillingView = new BillingView { DataContext = _cachedBillingVm };
                    
                    _cachedBillingView.RequestPayment += HandleRequestPayment;
                }
                MainContentContainer.Children.Add(_cachedBillingView);
                break;

            case ShellViewNames.ManagerOperations:
                if (_cachedManagerOperationsView == null)
                {
                    _cachedManagerOperationsView = CreateManagerOperationsView();
                }
                MainContentContainer.Children.Add(_cachedManagerOperationsView);
                break;

            case ShellViewNames.OwnerAdmin:
                if (_cachedOwnerAdminView == null)
                {
                    _cachedOwnerAdminView = CreateOwnerAdminView();
                }
                MainContentContainer.Children.Add(_cachedOwnerAdminView);
                break;
        }
    }

    private void UpdateNavRailHighlight(string activeView)
    {
        var activeStyle = (Style)FindResource("NavRailTabButtonActiveStyle");
        var baseStyle = (Style)FindResource("NavRailTabButtonStyle");

        TabCheckout.Style = activeView == ShellViewNames.Checkout ? activeStyle : baseStyle;
        TabManager.Style = activeView == ShellViewNames.ManagerOperations ? activeStyle : baseStyle;
        TabOwner.Style = activeView == ShellViewNames.OwnerAdmin ? activeStyle : baseStyle;
    }

    // =========================================================================
    // 3. ELEVATION & LOGOUT HANDLERS
    // =========================================================================

    private Task<bool> OnShellElevationRequestedAsync(string targetView)
    {
        var pinDialog = new ManagerPinDialog(App.AuthService, $"Access {targetView}")
        {
            Owner = this
        };

        if (pinDialog.ShowDialog() == true && pinDialog.AuthorizedUser != null)
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private void OnShellLogoutRequested()
    {
        if (_cachedBillingVm?.CartItems.Count > 0)
        {
            var result = MessageBox.Show(
                "You have active items in the billing cart.\nDo you want to clear the cart and logout?",
                "Active Cart Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        ShowLogin();
    }

    private void HandleRequestPayment()
    {
        if (_cachedBillingVm == null || _cachedBillingVm.CartItems.Count == 0)
        {
            MessageBox.Show("Cannot proceed to payment with an empty cart.", "Empty Cart", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var paymentVm = new PaymentViewModel(_cachedBillingVm, App.SaleService, App.ReceiptService, App.CustomerService);
        var dialog = new PaymentDialog
        {
            Owner = this,
            DataContext = paymentVm
        };

        paymentVm.PaymentCompleted += (sale, printResult) =>
        {
            // Refresh shell expected cash
            if (_currentShift != null)
            {
                _shellVm?.UpdateShift(_currentShift);
            }

            var msg = $"Sale completed successfully!\nReceipt No: {sale.ReceiptNumber}\nTotal: Rs. {sale.GrandTotal:N2}";
            if (!printResult.Success)
            {
                msg += $"\n\nNotice: Printer communication failed ({printResult.ErrorMessage}).\nReceipt is queued for reprint.";
            }
            MessageBox.Show(msg, "Sale Committed", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        dialog.ShowDialog();
    }

    private void HandleShiftClosed()
    {
        _currentShift = null;
        ShowLogin();
    }

    // =========================================================================
    // 4. MANAGER & OWNER VIEW FACTORY CREATORS
    // =========================================================================

    private UserControl CreateManagerOperationsView()
    {
        var mgr = new ManagerOperationsView(_currentUser!, _currentShift!);
        mgr.ShiftClosed += HandleShiftClosed;
        return mgr;
    }

    private UserControl CreateOwnerAdminView()
    {
        return new OwnerAdminView(_currentUser!, _currentShift!);
    }

    // =========================================================================
    // 5. SOFTWARE UPDATE NOTIFICATIONS
    // =========================================================================

    private void OnLiveUpdateReceived(UpdateManifest manifest)
    {
        Dispatcher.InvokeAsync(() => ShowUpdateBanner(manifest));
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

    private void OpenUpdateDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_updateDialogOpen) return;
        if (_cachedBillingVm?.CartItems.Count > 0)
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
}
