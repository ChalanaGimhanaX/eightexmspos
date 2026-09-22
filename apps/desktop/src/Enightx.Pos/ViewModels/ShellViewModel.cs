using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Themes;

namespace Enightx.Pos.ViewModels;

public static class ShellViewNames
{
    public const string Login = "Login";
    public const string Checkout = "Checkout";
    public const string ManagerOperations = "ManagerOperations";
    public const string OwnerAdmin = "OwnerAdmin";
}

public class ShellViewModel : INotifyPropertyChanged
{
    private User? _currentUser;
    private CashShift? _currentShift;
    private readonly IThemeManager _themeManager;
    private readonly IAuthorizationGateService? _authorizationGateService;
    private string _currentView = ShellViewNames.Checkout;
    private string _statusMessage = "Ready for billing";
    private readonly Dictionary<string, Action> _viewInitializers = new(StringComparer.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? ViewChanged;
    public event Action? LogoutRequested;
    public event EventHandler? LoggedOut;
    public Func<string, Task<bool>>? ElevationRequested { get; set; }

    public ShellViewModel(
        User? currentUser = null,
        CashShift? currentShift = null,
        IThemeManager? themeManager = null,
        IAuthorizationGateService? authorizationGateService = null)
    {
        _currentUser = currentUser;
        _currentShift = currentShift;
        _themeManager = themeManager ?? Enightx.Pos.Themes.ThemeManager.Current;
        _authorizationGateService = authorizationGateService;

        // Subscribe to theme manager property changes
        _themeManager.PropertyChanged += OnThemeManagerPropertyChanged;

        NavigateCommand = new RelayCommand(async param =>
        {
            if (param is string targetView)
            {
                await RequestNavigateAsync(targetView);
            }
        });

        LogoutCommand = new RelayCommand(_ => Logout());
        ToggleThemeCommand = new RelayCommand(_ => _themeManager.ToggleTheme());
        RefreshShiftCommand = new RelayCommand(_ => OnPropertyChanged(nameof(FormattedExpectedCash)));
    }

    private void OnThemeManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == nameof(IThemeManager.CurrentTheme) ||
            e.PropertyName == nameof(IThemeManager.ToggleButtonIcon) ||
            e.PropertyName == nameof(IThemeManager.ToggleButtonText) ||
            e.PropertyName == nameof(IThemeManager.IsDarkTheme) ||
            e.PropertyName == nameof(IThemeManager.IsLightTheme))
        {
            OnPropertyChanged(nameof(ThemeIcon));
            OnPropertyChanged(nameof(ThemeText));
            OnPropertyChanged(nameof(IsDarkTheme));
        }
    }

    // =========================================================================
    // 1. CURRENT USER & ROLE TRACKING
    // =========================================================================

    public User? CurrentUser => _currentUser;
    public bool IsAuthenticated => _currentUser != null;
    public Role CurrentRole => _currentUser?.Role ?? Role.Cashier;
    public string DisplayName => _currentUser?.DisplayName ?? string.Empty;
    public string Username => _currentUser?.Username ?? string.Empty;

    public string RoleDisplayName => _currentUser == null ? string.Empty : CurrentRole switch
    {
        Role.Cashier => "Cashier",
        Role.Manager => "Store Manager",
        Role.Owner => "Business Owner",
        Role.ProviderStaff => "Provider Staff",
        _ => CurrentRole.ToString()
    };

    public string RoleBadgeText => _currentUser == null ? string.Empty : CurrentRole switch
    {
        Role.Cashier => "CASHIER",
        Role.Manager => "MANAGER",
        Role.Owner => "OWNER",
        Role.ProviderStaff => "STAFF",
        _ => "USER"
    };

    public string BranchId => _currentShift?.BranchId ?? "B01";
    public string CounterId => _currentShift?.CounterId ?? "C01";
    public string CounterBranchText => $"{BranchId} / {CounterId}";

    // =========================================================================
    // 2. ROLE TAB VISIBILITY FLAGS
    // =========================================================================

    public bool IsCashierTabVisible => _currentUser != null && _currentUser.Role >= Role.Cashier;
    public bool IsManagerTabVisible => _currentUser != null && _currentUser.Role >= Role.Manager;
    public bool IsOwnerTabVisible => _currentUser != null && _currentUser.Role >= Role.Owner;

    // =========================================================================
    // 3. SHIFT STATUS & FLOAT TRACKING
    // =========================================================================

    public Guid? ShiftId => _currentShift?.ShiftId;
    public decimal OpeningFloat => _currentShift?.OpeningFloat ?? 0m;
    public decimal ExpectedCash => _currentShift?.ExpectedCash ?? 0m;

    public string FormattedOpeningFloat => FormatCurrency(OpeningFloat);
    public string FormattedExpectedCash => FormatCurrency(ExpectedCash);

    public bool HasActiveShift => _currentShift != null && _currentShift.Status == ShiftStatus.Open;
    public string ShiftBadgeText => HasActiveShift ? "● SHIFT OPEN" : "○ SHIFT CLOSED";
    public string ShiftFloatSummary => HasActiveShift ? $"Float: {FormattedOpeningFloat}" : "Shift Closed";

    public string ShiftTooltip => HasActiveShift
        ? $"Shift ID: {ShiftId}\nOpening Float: {FormattedOpeningFloat}\nExpected Cash: {FormattedExpectedCash}"
        : "No active cash shift";

    private static string FormatCurrency(decimal amount)
    {
        return $"Rs. {MoneyCalculator.Round(amount).ToString("N2", CultureInfo.InvariantCulture)}";
    }

    public void UpdateShift(CashShift? shift)
    {
        _currentShift = shift;
        OnPropertyChanged(nameof(ShiftId));
        OnPropertyChanged(nameof(OpeningFloat));
        OnPropertyChanged(nameof(ExpectedCash));
        OnPropertyChanged(nameof(FormattedOpeningFloat));
        OnPropertyChanged(nameof(FormattedExpectedCash));
        OnPropertyChanged(nameof(HasActiveShift));
        OnPropertyChanged(nameof(ShiftBadgeText));
        OnPropertyChanged(nameof(ShiftFloatSummary));
        OnPropertyChanged(nameof(ShiftTooltip));
        OnPropertyChanged(nameof(BranchId));
        OnPropertyChanged(nameof(CounterId));
        OnPropertyChanged(nameof(CounterBranchText));
    }

    public void SetActiveUser(User user, CashShift? shift = null)
    {
        _currentUser = user ?? throw new ArgumentNullException(nameof(user));
        _currentShift = shift;
        _currentView = ShellViewNames.Checkout;

        NotifyAllSessionProperties();
    }

    public void Logout()
    {
        _currentUser = null;
        _currentShift = null;
        _currentView = ShellViewNames.Login;

        NotifyAllSessionProperties();

        LogoutRequested?.Invoke();
        LoggedOut?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyAllSessionProperties()
    {
        OnPropertyChanged(nameof(CurrentUser));
        OnPropertyChanged(nameof(IsAuthenticated));
        OnPropertyChanged(nameof(CurrentRole));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(RoleDisplayName));
        OnPropertyChanged(nameof(RoleBadgeText));
        OnPropertyChanged(nameof(IsCashierTabVisible));
        OnPropertyChanged(nameof(IsManagerTabVisible));
        OnPropertyChanged(nameof(IsOwnerTabVisible));
        OnPropertyChanged(nameof(ShiftId));
        OnPropertyChanged(nameof(OpeningFloat));
        OnPropertyChanged(nameof(ExpectedCash));
        OnPropertyChanged(nameof(FormattedOpeningFloat));
        OnPropertyChanged(nameof(FormattedExpectedCash));
        OnPropertyChanged(nameof(HasActiveShift));
        OnPropertyChanged(nameof(ShiftBadgeText));
        OnPropertyChanged(nameof(ShiftFloatSummary));
        OnPropertyChanged(nameof(ShiftTooltip));
        OnPropertyChanged(nameof(BranchId));
        OnPropertyChanged(nameof(CounterId));
        OnPropertyChanged(nameof(CounterBranchText));
        OnPropertyChanged(nameof(CurrentView));
        OnPropertyChanged(nameof(CurrentViewName));
        OnPropertyChanged(nameof(IsCheckoutActive));
        OnPropertyChanged(nameof(IsManagerActive));
        OnPropertyChanged(nameof(IsOwnerActive));
    }

    // =========================================================================
    // 4. ACTIVE VIEW TRACKING & NAVIGATION
    // =========================================================================

    public string CurrentView
    {
        get => _currentView;
        private set
        {
            if (_currentView != value)
            {
                _currentView = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentViewName));
                OnPropertyChanged(nameof(IsCheckoutActive));
                OnPropertyChanged(nameof(IsManagerActive));
                OnPropertyChanged(nameof(IsOwnerActive));
                ViewChanged?.Invoke(_currentView);
            }
        }
    }

    public string CurrentViewName
    {
        get => CurrentView;
        set => NavigateTo(value);
    }

    public bool IsCheckoutActive => CurrentView == ShellViewNames.Checkout;
    public bool IsManagerActive => CurrentView == ShellViewNames.ManagerOperations;
    public bool IsOwnerActive => CurrentView == ShellViewNames.OwnerAdmin;

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public void RegisterViewInitializer(string viewName, Action initializer)
    {
        if (string.IsNullOrEmpty(viewName)) return;
        _viewInitializers[viewName] = initializer ?? throw new ArgumentNullException(nameof(initializer));
    }

    public bool CanNavigateTo(string? targetView)
    {
        if (string.IsNullOrEmpty(targetView)) return false;

        return targetView switch
        {
            ShellViewNames.Checkout => _currentUser != null && _currentUser.Role >= Role.Cashier,
            ShellViewNames.ManagerOperations => _currentUser != null && _currentUser.Role >= Role.Manager,
            ShellViewNames.OwnerAdmin => _currentUser != null && _currentUser.Role >= Role.Owner,
            ShellViewNames.Login => true,
            _ => false
        };
    }

    public void NavigateTo(string? targetView)
    {
        if (string.IsNullOrEmpty(targetView)) return;
        if (string.Equals(_currentView, targetView, StringComparison.OrdinalIgnoreCase)) return;

        if (CanNavigateTo(targetView))
        {
            CurrentView = targetView;
            if (_viewInitializers.TryGetValue(targetView, out var init))
            {
                init.Invoke();
            }
        }
        else
        {
            StatusMessage = "Access restricted: Insufficient permissions.";
        }
    }

    public async Task<bool> RequestNavigateAsync(string? targetView)
    {
        if (string.IsNullOrEmpty(targetView)) return false;

        if (string.Equals(_currentView, targetView, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Direct authorization check based on current user role
        if (CanNavigateTo(targetView))
        {
            CurrentView = targetView;
            if (_viewInitializers.TryGetValue(targetView, out var init))
            {
                init.Invoke();
            }
            return true;
        }

        // If supervisor elevation handler is registered
        if (ElevationRequested != null)
        {
            bool authorized = await ElevationRequested.Invoke(targetView);
            if (authorized)
            {
                CurrentView = targetView;
                if (_viewInitializers.TryGetValue(targetView, out var init))
                {
                    init.Invoke();
                }
                return true;
            }
        }
        else if (_authorizationGateService != null && _currentUser != null)
        {
            var requiredRole = targetView switch
            {
                ShellViewNames.ManagerOperations => Role.Manager,
                ShellViewNames.OwnerAdmin => Role.Owner,
                _ => Role.Manager
            };

            bool authorized = await _authorizationGateService.AuthorizeOperationAsync(
                _currentUser,
                requiredRole,
                $"Navigate to {targetView}",
                _currentShift?.TenantId,
                _currentShift?.BranchId,
                _currentShift?.CounterId
            );

            if (authorized)
            {
                CurrentView = targetView;
                if (_viewInitializers.TryGetValue(targetView, out var init))
                {
                    init.Invoke();
                }
                return true;
            }
        }

        StatusMessage = "Access restricted: Supervisor authorization required.";
        return false;
    }

    // =========================================================================
    // 5. THEME INTEGRATION
    // =========================================================================

    public IThemeManager ThemeManager => _themeManager;
    public string ThemeIcon => _themeManager.ToggleButtonIcon;
    public string ThemeText => _themeManager.ToggleButtonText;
    public bool IsDarkTheme => _themeManager.IsDarkTheme;

    // =========================================================================
    // 6. COMMANDS
    // =========================================================================

    public ICommand NavigateCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand RefreshShiftCommand { get; }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
