using System.ComponentModel;
using System.Windows.Input;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Themes;
using Enightx.Pos.ViewModels;
using Xunit;

namespace Enightx.Pos.Tests;

public class ShellViewModelTests
{
    // =========================================================================
    // TEST DOUBLES & FIXTURES
    // =========================================================================

    private class SpyThemeManager : IThemeManager
    {
        public Theme CurrentTheme { get; private set; } = Theme.Light;
        public bool IsDarkTheme => CurrentTheme == Theme.Dark;
        public bool IsLightTheme => CurrentTheme == Theme.Light;
        public string ToggleButtonText => IsDarkTheme ? "☀️ Light Mode" : "🌙 Dark Mode";
        public string ToggleButtonIcon => IsDarkTheme ? "☀️" : "🌙";
        private int _toggleCallCount;
        public int ToggleCallCount => _toggleCallCount;
        public ICommand ToggleThemeCommand => new RelayCommand(_ => ToggleTheme());

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

        public void SetTheme(Theme theme)
        {
            var old = CurrentTheme;
            CurrentTheme = theme;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTheme)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDarkTheme)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLightTheme)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleButtonText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleButtonIcon)));
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(old, theme));
        }

        public void ToggleTheme()
        {
            Interlocked.Increment(ref _toggleCallCount);
            SetTheme(CurrentTheme == Theme.Light ? Theme.Dark : Theme.Light);
        }

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object?> _execute;
            public RelayCommand(Action<object?> execute) => _execute = execute;
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _execute(parameter);
            public event EventHandler? CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static User CreateUser(Role role, string username = "testuser", string displayName = "Test Operator")
    {
        return new User
        {
            UserId = $"usr_{Guid.NewGuid():N}",
            Username = username,
            DisplayName = displayName,
            Role = role,
            PasswordHash = "dummy_hash",
            PasswordSalt = "dummy_salt",
            IsActive = true
        };
    }

    private static CashShift CreateShift(string userId, decimal openingFloat = 5000.00m, decimal expectedCash = 12500.50m)
    {
        return new CashShift
        {
            ShiftId = Guid.NewGuid(),
            TenantId = "TENANT_LK_01",
            BranchId = "B01",
            CounterId = "C01",
            CashierId = userId,
            OpeningFloat = openingFloat,
            ExpectedCash = expectedCash,
            OpenedAtUtc = DateTime.UtcNow,
            Status = ShiftStatus.Open
        };
    }

    // =========================================================================
    // FIXTURE 1: ROLE-BASED TAB VISIBILITY SPECIFICATION
    // =========================================================================

    [Fact]
    public void RoleTabVisibility_CashierRole_DisplaysOnlyCashierTab()
    {
        // Arrange
        var cashier = CreateUser(Role.Cashier, "cashier_1", "Nimal Perera");

        // Act
        var shell = new ShellViewModel(cashier);

        // Assert
        Assert.True(shell.IsCashierTabVisible, "Cashier tab must be visible to Cashier.");
        Assert.False(shell.IsManagerTabVisible, "Manager tab must NOT be visible to Cashier.");
        Assert.False(shell.IsOwnerTabVisible, "Owner tab must NOT be visible to Cashier.");
        Assert.Equal(Role.Cashier, shell.CurrentRole);
        Assert.Equal("CASHIER", shell.RoleBadgeText);
        Assert.Equal("Cashier", shell.RoleDisplayName);
    }

    [Fact]
    public void RoleTabVisibility_ManagerRole_DisplaysCashierAndManagerTabs()
    {
        // Arrange
        var manager = CreateUser(Role.Manager, "manager_1", "Sunil Silva");

        // Act
        var shell = new ShellViewModel(manager);

        // Assert
        Assert.True(shell.IsCashierTabVisible, "Cashier tab must be visible to Manager.");
        Assert.True(shell.IsManagerTabVisible, "Manager tab must be visible to Manager.");
        Assert.False(shell.IsOwnerTabVisible, "Owner tab must NOT be visible to Manager.");
        Assert.Equal(Role.Manager, shell.CurrentRole);
        Assert.Equal("MANAGER", shell.RoleBadgeText);
        Assert.Equal("Store Manager", shell.RoleDisplayName);
    }

    [Fact]
    public void RoleTabVisibility_OwnerRole_DisplaysAllTabs()
    {
        // Arrange
        var owner = CreateUser(Role.Owner, "owner_1", "Kasun Fernando");

        // Act
        var shell = new ShellViewModel(owner);

        // Assert
        Assert.True(shell.IsCashierTabVisible, "Cashier tab must be visible to Owner.");
        Assert.True(shell.IsManagerTabVisible, "Manager tab must be visible to Owner.");
        Assert.True(shell.IsOwnerTabVisible, "Owner tab must be visible to Owner.");
        Assert.Equal(Role.Owner, shell.CurrentRole);
        Assert.Equal("OWNER", shell.RoleBadgeText);
        Assert.Equal("Business Owner", shell.RoleDisplayName);
    }

    [Fact]
    public void RoleTabVisibility_ProviderStaffRole_DisplaysAllTabs()
    {
        // Arrange
        var staff = CreateUser(Role.ProviderStaff, "staff_1", "Support Engineer");

        // Act
        var shell = new ShellViewModel(staff);

        // Assert
        Assert.True(shell.IsCashierTabVisible, "Cashier tab must be visible to Provider Staff.");
        Assert.True(shell.IsManagerTabVisible, "Manager tab must be visible to Provider Staff.");
        Assert.True(shell.IsOwnerTabVisible, "Owner tab must be visible to Provider Staff.");
        Assert.Equal(Role.ProviderStaff, shell.CurrentRole);
    }

    [Fact]
    public void RoleTabVisibility_UnauthenticatedOrNullUser_HidesAllTabs()
    {
        // Arrange & Act
        var shell = new ShellViewModel(currentUser: null);

        // Assert
        Assert.False(shell.IsAuthenticated);
        Assert.False(shell.IsCashierTabVisible, "Cashier tab must be hidden when unauthenticated.");
        Assert.False(shell.IsManagerTabVisible, "Manager tab must be hidden when unauthenticated.");
        Assert.False(shell.IsOwnerTabVisible, "Owner tab must be hidden when unauthenticated.");
    }

    // =========================================================================
    // FIXTURE 2: NAVIGATION TRANSITIONS & VIEW LIFECYCLE
    // =========================================================================

    [Fact]
    public void Navigation_DefaultView_IsCheckout()
    {
        var manager = CreateUser(Role.Manager);
        var shell = new ShellViewModel(manager);

        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.True(shell.IsCheckoutActive);
        Assert.False(shell.IsManagerActive);
        Assert.False(shell.IsOwnerActive);
    }

    [Fact]
    public void NavigateTo_ValidTransition_UpdatesCurrentViewAndDispatchesPropertyChanged()
    {
        var manager = CreateUser(Role.Manager);
        var shell = new ShellViewModel(manager);

        var propertyChanges = new List<string>();
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) propertyChanges.Add(e.PropertyName);
        };

        string? notifiedView = null;
        shell.ViewChanged += v => notifiedView = v;

        // Act: Navigate to Manager Operations
        shell.NavigateTo(ShellViewNames.ManagerOperations);

        // Assert
        Assert.Equal(ShellViewNames.ManagerOperations, shell.CurrentView);
        Assert.Equal(ShellViewNames.ManagerOperations, shell.CurrentViewName);
        Assert.False(shell.IsCheckoutActive);
        Assert.True(shell.IsManagerActive);
        Assert.False(shell.IsOwnerActive);
        Assert.Equal(ShellViewNames.ManagerOperations, notifiedView);

        Assert.Contains(nameof(shell.CurrentView), propertyChanges);
        Assert.Contains(nameof(shell.IsCheckoutActive), propertyChanges);
        Assert.Contains(nameof(shell.IsManagerActive), propertyChanges);
    }

    [Fact]
    public void NavigateTo_TriggersRegisteredViewInitializer()
    {
        var owner = CreateUser(Role.Owner);
        var shell = new ShellViewModel(owner);

        bool managerInitialized = false;
        bool ownerInitialized = false;

        shell.RegisterViewInitializer(ShellViewNames.ManagerOperations, () => managerInitialized = true);
        shell.RegisterViewInitializer(ShellViewNames.OwnerAdmin, () => ownerInitialized = true);

        // Act 1: Navigate to Manager
        shell.NavigateTo(ShellViewNames.ManagerOperations);
        Assert.True(managerInitialized);
        Assert.False(ownerInitialized);

        // Act 2: Navigate to Owner
        shell.NavigateTo(ShellViewNames.OwnerAdmin);
        Assert.True(ownerInitialized);
    }

    [Fact]
    public void NavigateTo_IdempotentWhenAlreadyOnCurrentView()
    {
        var manager = CreateUser(Role.Manager);
        var shell = new ShellViewModel(manager);

        int viewChangedCount = 0;
        shell.ViewChanged += _ => viewChangedCount++;

        // Already on Checkout by default
        shell.NavigateTo(ShellViewNames.Checkout);

        Assert.Equal(0, viewChangedCount);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
    }

    [Fact]
    public void NavigateCommand_ExecutesTransitionViaICommand()
    {
        var owner = CreateUser(Role.Owner);
        var shell = new ShellViewModel(owner);

        Assert.True(shell.NavigateCommand.CanExecute(ShellViewNames.OwnerAdmin));

        shell.NavigateCommand.Execute(ShellViewNames.OwnerAdmin);

        Assert.Equal(ShellViewNames.OwnerAdmin, shell.CurrentView);
        Assert.True(shell.IsOwnerActive);
    }

    // =========================================================================
    // FIXTURE 3: ROLE AUTHORIZATION GUARDS & ELEVATION GATE
    // =========================================================================

    [Fact]
    public void CanNavigateTo_Cashier_RestrictedFromAdministrativeViews()
    {
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);

        Assert.True(shell.CanNavigateTo(ShellViewNames.Checkout));
        Assert.False(shell.CanNavigateTo(ShellViewNames.ManagerOperations));
        Assert.False(shell.CanNavigateTo(ShellViewNames.OwnerAdmin));
    }

    [Fact]
    public void CanNavigateTo_Manager_RestrictedFromOwnerAdmin()
    {
        var manager = CreateUser(Role.Manager);
        var shell = new ShellViewModel(manager);

        Assert.True(shell.CanNavigateTo(ShellViewNames.Checkout));
        Assert.True(shell.CanNavigateTo(ShellViewNames.ManagerOperations));
        Assert.False(shell.CanNavigateTo(ShellViewNames.OwnerAdmin));
    }

    [Fact]
    public async Task RequestNavigateAsync_CashierAttemptsManagerView_TriggersElevationRequested()
    {
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);

        bool elevationCalled = false;
        shell.ElevationRequested = targetView =>
        {
            elevationCalled = true;
            Assert.Equal(ShellViewNames.ManagerOperations, targetView);
            return Task.FromResult(true); // Grant elevation
        };

        // Act
        bool result = await shell.RequestNavigateAsync(ShellViewNames.ManagerOperations);

        // Assert
        Assert.True(elevationCalled);
        Assert.True(result);
        Assert.Equal(ShellViewNames.ManagerOperations, shell.CurrentView);
    }

    [Fact]
    public async Task RequestNavigateAsync_ElevationRejected_KeepsPreviousViewAndSetsStatusMessage()
    {
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);

        shell.ElevationRequested = _ => Task.FromResult(false); // Deny elevation

        // Act
        bool result = await shell.RequestNavigateAsync(ShellViewNames.ManagerOperations);

        // Assert
        Assert.False(result);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.Contains("Supervisor authorization required", shell.StatusMessage);
    }

    // =========================================================================
    // FIXTURE 4: LOGOUT & SESSION RESET SPECIFICATION
    // =========================================================================

    [Fact]
    public void Logout_ResetsActiveUserActiveViewAndTabVisibility()
    {
        // Arrange
        var manager = CreateUser(Role.Manager, "mgr_active", "Active Manager");
        var shift = CreateShift(manager.UserId);
        var shell = new ShellViewModel(manager, shift);
        shell.NavigateTo(ShellViewNames.ManagerOperations);

        var propertyChanges = new List<string>();
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) propertyChanges.Add(e.PropertyName);
        };

        bool logoutRequestedFired = false;
        shell.LogoutRequested += () => logoutRequestedFired = true;

        bool loggedOutFired = false;
        shell.LoggedOut += (_, _) => loggedOutFired = true;

        // Act: Invoke Logout
        shell.Logout();

        // Assert: Active User is reset to null
        Assert.Null(shell.CurrentUser);
        Assert.False(shell.IsAuthenticated);
        Assert.Equal(string.Empty, shell.DisplayName);
        Assert.Equal(string.Empty, shell.Username);

        // Assert: Active view is returned to Login
        Assert.Equal(ShellViewNames.Login, shell.CurrentView);
        Assert.False(shell.IsCheckoutActive);
        Assert.False(shell.IsManagerActive);
        Assert.False(shell.IsOwnerActive);

        // Assert: Role tabs are all hidden
        Assert.False(shell.IsCashierTabVisible);
        Assert.False(shell.IsManagerTabVisible);
        Assert.False(shell.IsOwnerTabVisible);

        // Assert: Shift session data is cleared
        Assert.Null(shell.ShiftId);
        Assert.False(shell.HasActiveShift);

        // Assert: Events and PropertyChanged were dispatched
        Assert.True(logoutRequestedFired, "LogoutRequested event must fire on logout.");
        Assert.True(loggedOutFired, "LoggedOut event must fire on logout.");
        Assert.Contains(nameof(shell.CurrentUser), propertyChanges);
        Assert.Contains(nameof(shell.CurrentView), propertyChanges);
        Assert.Contains(nameof(shell.IsCashierTabVisible), propertyChanges);
        Assert.Contains(nameof(shell.IsManagerTabVisible), propertyChanges);
        Assert.Contains(nameof(shell.IsOwnerTabVisible), propertyChanges);
    }

    [Fact]
    public void LogoutCommand_ExecutesLogoutViaICommand()
    {
        var owner = CreateUser(Role.Owner);
        var shell = new ShellViewModel(owner);

        Assert.True(shell.LogoutCommand.CanExecute(null));

        shell.LogoutCommand.Execute(null);

        Assert.Null(shell.CurrentUser);
        Assert.Equal(ShellViewNames.Login, shell.CurrentView);
    }

    [Fact]
    public void SetActiveUser_AfterLogout_RestoresSessionAndVisibility()
    {
        var shell = new ShellViewModel(currentUser: null);
        Assert.False(shell.IsAuthenticated);

        var newCashier = CreateUser(Role.Cashier, "new_cashier", "Siripala");
        var newShift = CreateShift(newCashier.UserId, 3000m, 3000m);

        // Act
        shell.SetActiveUser(newCashier, newShift);

        // Assert
        Assert.True(shell.IsAuthenticated);
        Assert.Equal(newCashier.UserId, shell.CurrentUser?.UserId);
        Assert.Equal("Siripala", shell.DisplayName);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.True(shell.IsCashierTabVisible);
        Assert.False(shell.IsManagerTabVisible);
        Assert.False(shell.IsOwnerTabVisible);
        Assert.Equal(newShift.ShiftId, shell.ShiftId);
    }

    // =========================================================================
    // FIXTURE 5: THEME TOGGLE COMMAND DELEGATION
    // =========================================================================

    [Fact]
    public void ToggleThemeCommand_DelegatesDirectlyToThemeManager()
    {
        var spyTheme = new SpyThemeManager();
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier, themeManager: spyTheme);

        Assert.Equal(Theme.Light, spyTheme.CurrentTheme);
        Assert.Equal(0, spyTheme.ToggleCallCount);
        Assert.False(shell.IsDarkTheme);
        Assert.Equal("🌙", shell.ThemeIcon);

        // Act 1: Execute Command
        shell.ToggleThemeCommand.Execute(null);

        // Assert 1: Delegated to ThemeManager
        Assert.Equal(1, spyTheme.ToggleCallCount);
        Assert.Equal(Theme.Dark, spyTheme.CurrentTheme);
        Assert.True(shell.IsDarkTheme);
        Assert.Equal("☀️", shell.ThemeIcon);

        // Act 2: Execute Command Again
        shell.ToggleThemeCommand.Execute(null);

        // Assert 2: Swapped back to Light
        Assert.Equal(2, spyTheme.ToggleCallCount);
        Assert.Equal(Theme.Light, spyTheme.CurrentTheme);
        Assert.False(shell.IsDarkTheme);
        Assert.Equal("🌙", shell.ThemeIcon);
    }

    [Fact]
    public void ShellViewModel_SubscribesToThemeManagerPropertyChanged()
    {
        var spyTheme = new SpyThemeManager();
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier, themeManager: spyTheme);

        var propertyChanges = new List<string>();
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) propertyChanges.Add(e.PropertyName);
        };

        // Act: External theme change on manager
        spyTheme.SetTheme(Theme.Dark);

        // Assert: Shell dispatches corresponding PropertyChanged
        Assert.Contains(nameof(shell.ThemeIcon), propertyChanges);
        Assert.Contains(nameof(shell.ThemeText), propertyChanges);
        Assert.Contains(nameof(shell.IsDarkTheme), propertyChanges);
    }

    // =========================================================================
    // FIXTURE 6: SHIFT INFORMATION & SRI LANKAN CURRENCY FORMATTING
    // =========================================================================

    [Fact]
    public void ShiftFormatting_FormattedOpeningFloatAndExpectedCash_EnforceLkrCurrency()
    {
        var user = CreateUser(Role.Cashier);
        var shift = CreateShift(user.UserId, openingFloat: 5000.00m, expectedCash: 12500.50m);
        var shell = new ShellViewModel(user, shift);

        Assert.Equal("Rs. 5,000.00", shell.FormattedOpeningFloat);
        Assert.Equal("Rs. 12,500.50", shell.FormattedExpectedCash);
        Assert.True(shell.HasActiveShift);
        Assert.Equal("● SHIFT OPEN", shell.ShiftBadgeText);
        Assert.Equal("Float: Rs. 5,000.00", shell.ShiftFloatSummary);
    }

    [Theory]
    [InlineData(0.0, "Rs. 0.00")]
    [InlineData(2500.0, "Rs. 2,500.00")]
    [InlineData(1000000.755, "Rs. 1,000,000.76")] // AwayFromZero half-up rounding
    [InlineData(999.994, "Rs. 999.99")]
    public void ShiftFormatting_HalfUpRounding_PreservedAccurately(decimal amount, string expectedFormatted)
    {
        var user = CreateUser(Role.Manager);
        var shift = CreateShift(user.UserId, openingFloat: amount, expectedCash: amount);
        var shell = new ShellViewModel(user, shift);

        Assert.Equal(expectedFormatted, shell.FormattedOpeningFloat);
        Assert.Equal(expectedFormatted, shell.FormattedExpectedCash);
    }

    [Fact]
    public void UpdateShift_UpdatesPropertiesAndFiresNotifications()
    {
        var user = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(user, currentShift: null);

        Assert.False(shell.HasActiveShift);
        Assert.Equal("Rs. 0.00", shell.FormattedOpeningFloat);
        Assert.Equal("○ SHIFT CLOSED", shell.ShiftBadgeText);

        var propertyChanges = new List<string>();
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) propertyChanges.Add(e.PropertyName);
        };

        var newShift = CreateShift(user.UserId, openingFloat: 10000m, expectedCash: 15400m);

        // Act
        shell.UpdateShift(newShift);

        // Assert
        Assert.True(shell.HasActiveShift);
        Assert.Equal(newShift.ShiftId, shell.ShiftId);
        Assert.Equal("Rs. 10,000.00", shell.FormattedOpeningFloat);
        Assert.Equal("Rs. 15,400.00", shell.FormattedExpectedCash);
        Assert.Equal("● SHIFT OPEN", shell.ShiftBadgeText);

        Assert.Contains(nameof(shell.ShiftId), propertyChanges);
        Assert.Contains(nameof(shell.FormattedOpeningFloat), propertyChanges);
        Assert.Contains(nameof(shell.FormattedExpectedCash), propertyChanges);
        Assert.Contains(nameof(shell.HasActiveShift), propertyChanges);
    }

    // =========================================================================
    // FIXTURE 7: ADVERSARIAL & EDGE CASE RESILIENCE
    // =========================================================================

    [Fact]
    public void Adversarial_NullTargetViewInNavigate_DoesNotThrow()
    {
        var user = CreateUser(Role.Manager);
        var shell = new ShellViewModel(user);

        // Must handle null or invalid parameters safely without unhandled exceptions
        shell.NavigateTo(null!);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);

        shell.NavigateCommand.Execute(null);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
    }

    [Fact]
    public void Adversarial_RapidConsecutiveNavigationClicks_MaintainsConsistentState()
    {
        var owner = CreateUser(Role.Owner);
        var shell = new ShellViewModel(owner);

        var views = new[]
        {
            ShellViewNames.Checkout,
            ShellViewNames.ManagerOperations,
            ShellViewNames.OwnerAdmin
        };

        for (int i = 0; i < 100; i++)
        {
            var target = views[i % views.Length];
            shell.NavigateTo(target);
            Assert.Equal(target, shell.CurrentView);
        }
    }

    [Fact]
    public void Adversarial_ConcurrentThemeToggles_NoRaceCondition()
    {
        var spyTheme = new SpyThemeManager();
        var user = CreateUser(Role.Owner);
        var shell = new ShellViewModel(user, themeManager: spyTheme);

        Parallel.For(0, 50, _ =>
        {
            shell.ToggleThemeCommand.Execute(null);
        });

        Assert.Equal(50, spyTheme.ToggleCallCount);
    }
}
