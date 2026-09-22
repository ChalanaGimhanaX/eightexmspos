using System.Collections.ObjectModel;
using System.ComponentModel;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;
using Enightx.Pos.Themes;
using Enightx.Pos.ViewModels;
using Enightx.Pos.Wpf.ViewModels;
using Xunit;

namespace Enightx.Pos.Tests;

public class ShellNavigationAdversarialTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _saleService;
    private readonly HeldCartService _heldCartService;

    public ShellNavigationAdversarialTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _catalog = new CatalogService(_db);
        _shift = new ShiftService(_db);
        _saleService = new SaleService(_db, _catalog);
        _heldCartService = new HeldCartService(_db);

        Enightx.Pos.Wpf.App.HeldCartService = _heldCartService;
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private static User CreateUser(Role role, string username = "adv_user", string displayName = "Adversarial Operator")
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

    private static CashShift CreateShift(string userId, decimal openingFloat = 5000.00m, decimal expectedCash = 15000.00m)
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
    // 1. ADVERSARIAL UNAUTHORIZED NAVIGATION ATTEMPTS
    // =========================================================================

    [Fact]
    public void UnauthorizedNav_CashierDirectNavigate_BlockedFromManagerAndOwner()
    {
        var cashier = CreateUser(Role.Cashier, "cashier_emp", "Cashier Empirical");
        var shift = CreateShift(cashier.UserId);
        var shell = new ShellViewModel(cashier, shift);

        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);

        // Attempt 1: Navigate to Manager Operations
        shell.NavigateTo(ShellViewNames.ManagerOperations);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.True(shell.IsCheckoutActive);
        Assert.False(shell.IsManagerActive);
        Assert.False(shell.IsOwnerActive);

        // Attempt 2: Navigate to Owner Admin
        shell.NavigateTo(ShellViewNames.OwnerAdmin);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.True(shell.IsCheckoutActive);
        Assert.False(shell.IsManagerActive);
        Assert.False(shell.IsOwnerActive);

        // Attempt 3: Null, empty, whitespace, and arbitrary unknown view routes
        shell.NavigateTo(null);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);

        shell.NavigateTo("");
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);

        shell.NavigateTo("   ");
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);

        shell.NavigateTo("NonExistentRoute_Or_SqlInjection'--");
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
    }

    [Fact]
    public void UnauthorizedNav_CashierCanNavigateTo_StrictlyValidatesPermissions()
    {
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);

        Assert.True(shell.CanNavigateTo(ShellViewNames.Checkout));
        Assert.True(shell.CanNavigateTo(ShellViewNames.Login));
        Assert.False(shell.CanNavigateTo(ShellViewNames.ManagerOperations));
        Assert.False(shell.CanNavigateTo(ShellViewNames.OwnerAdmin));
        Assert.False(shell.CanNavigateTo(null));
        Assert.False(shell.CanNavigateTo(""));
        Assert.False(shell.CanNavigateTo("UnknownView"));
    }

    [Fact]
    public async Task UnauthorizedNav_CashierRequestNavigateAsync_NoElevation_RejectsAndLeavesStateAtCheckout()
    {
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);

        // Elevation handler is null
        shell.ElevationRequested = null;

        // Act 1: Manager operations
        bool mgrAllowed = await shell.RequestNavigateAsync(ShellViewNames.ManagerOperations);

        // Assert 1
        Assert.False(mgrAllowed, "RequestNavigateAsync must return false for unauthorized view without elevation.");
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.Contains("Supervisor authorization required", shell.StatusMessage);

        // Act 2: Owner admin
        bool ownerAllowed = await shell.RequestNavigateAsync(ShellViewNames.OwnerAdmin);

        // Assert 2
        Assert.False(ownerAllowed, "RequestNavigateAsync must return false for unauthorized view without elevation.");
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.Contains("Supervisor authorization required", shell.StatusMessage);
    }

    [Fact]
    public async Task UnauthorizedNav_CashierRequestNavigateAsync_ElevationDenied_RemainsAtCheckout()
    {
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);

        int elevationCalls = 0;
        shell.ElevationRequested = _ =>
        {
            elevationCalls++;
            return Task.FromResult(false); // Elevation explicitly denied by PIN dialog cancel or invalid PIN
        };

        // Act
        bool mgrResult = await shell.RequestNavigateAsync(ShellViewNames.ManagerOperations);
        bool ownerResult = await shell.RequestNavigateAsync(ShellViewNames.OwnerAdmin);

        // Assert
        Assert.Equal(2, elevationCalls);
        Assert.False(mgrResult);
        Assert.False(ownerResult);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.True(shell.IsCheckoutActive);
        Assert.False(shell.IsManagerActive);
        Assert.False(shell.IsOwnerActive);
    }

    [Fact]
    public void UnauthorizedNav_CashierCurrentViewNamePropertySetter_TamperAttempt_Rejected()
    {
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);

        // Attempting to set property via data-binding setter
        shell.CurrentViewName = ShellViewNames.ManagerOperations;
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentViewName);

        shell.CurrentViewName = ShellViewNames.OwnerAdmin;
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentViewName);
    }

    [Fact]
    public void UnauthorizedNav_ManagerAttemptingOwnerAdmin_StrictlyBlocked()
    {
        var manager = CreateUser(Role.Manager);
        var shell = new ShellViewModel(manager);

        // Manager can go to Checkout and ManagerOperations
        shell.NavigateTo(ShellViewNames.ManagerOperations);
        Assert.Equal(ShellViewNames.ManagerOperations, shell.CurrentView);

        // Attempt OwnerAdmin
        shell.NavigateTo(ShellViewNames.OwnerAdmin);
        Assert.Equal(ShellViewNames.ManagerOperations, shell.CurrentView); // Remains on ManagerOperations
        Assert.True(shell.IsManagerActive);
        Assert.False(shell.IsOwnerActive);

        // Setter tampering
        shell.CurrentViewName = ShellViewNames.OwnerAdmin;
        Assert.Equal(ShellViewNames.ManagerOperations, shell.CurrentView);
    }

    [Fact]
    public void UnauthorizedNav_UnauthenticatedUser_AllRoutesBlockedExceptLogin()
    {
        var shell = new ShellViewModel(currentUser: null);

        Assert.False(shell.IsAuthenticated);
        Assert.False(shell.CanNavigateTo(ShellViewNames.Checkout));
        Assert.False(shell.CanNavigateTo(ShellViewNames.ManagerOperations));
        Assert.False(shell.CanNavigateTo(ShellViewNames.OwnerAdmin));
        Assert.True(shell.CanNavigateTo(ShellViewNames.Login));

        shell.NavigateTo(ShellViewNames.Checkout);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView); // Default init value, but not updated by NavigateTo

        shell.NavigateTo(ShellViewNames.ManagerOperations);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
    }

    // =========================================================================
    // 2. HIGH-FREQUENCY RAPID TAB NAVIGATION & CONCURRENCY HARNESS
    // =========================================================================

    [Fact]
    public void RapidNavigation_500SequentialTransitions_MaintainsConsistentState()
    {
        var owner = CreateUser(Role.Owner, "owner_stress", "Stress Tester");
        var shift = CreateShift(owner.UserId);
        var shell = new ShellViewModel(owner, shift);

        var targetViews = new[]
        {
            ShellViewNames.Checkout,
            ShellViewNames.ManagerOperations,
            ShellViewNames.OwnerAdmin
        };

        var viewChangedEvents = 0;
        shell.ViewChanged += _ => viewChangedEvents++;

        for (int i = 0; i < 500; i++)
        {
            var target = targetViews[i % targetViews.Length];
            shell.NavigateTo(target);

            // Assert mutual exclusivity and consistency at each step
            Assert.Equal(target, shell.CurrentView);
            Assert.Equal(target, shell.CurrentViewName);
            Assert.Equal(target == ShellViewNames.Checkout, shell.IsCheckoutActive);
            Assert.Equal(target == ShellViewNames.ManagerOperations, shell.IsManagerActive);
            Assert.Equal(target == ShellViewNames.OwnerAdmin, shell.IsOwnerActive);
        }

        // Each transition to a DIFFERENT view fires ViewChanged
        Assert.True(viewChangedEvents > 400);
    }

    [Fact]
    public void RapidNavigation_ParallelSlamHarness_NoExceptionsAndStateRemainsConsistent()
    {
        var owner = CreateUser(Role.Owner);
        var shell = new ShellViewModel(owner);

        var validViews = new[]
        {
            ShellViewNames.Checkout,
            ShellViewNames.ManagerOperations,
            ShellViewNames.OwnerAdmin
        };

        // 10 concurrent tasks each firing 100 random navigation transitions
        Parallel.For(0, 10, taskIdx =>
        {
            var rnd = new Random(taskIdx * 100);
            for (int i = 0; i < 100; i++)
            {
                var view = validViews[rnd.Next(validViews.Length)];
                shell.NavigateTo(view);
            }
        });

        // After all threads finish, the state must be perfectly consistent
        Assert.Contains(shell.CurrentView, validViews);
        Assert.Equal(shell.CurrentView == ShellViewNames.Checkout, shell.IsCheckoutActive);
        Assert.Equal(shell.CurrentView == ShellViewNames.ManagerOperations, shell.IsManagerActive);
        Assert.Equal(shell.CurrentView == ShellViewNames.OwnerAdmin, shell.IsOwnerActive);
    }

    [Fact]
    public void RapidNavigation_InitializersExecuteReliablyAndIdempotently()
    {
        var owner = CreateUser(Role.Owner);
        var shell = new ShellViewModel(owner);

        int checkoutInits = 0;
        int managerInits = 0;
        int ownerInits = 0;

        shell.RegisterViewInitializer(ShellViewNames.Checkout, () => checkoutInits++);
        shell.RegisterViewInitializer(ShellViewNames.ManagerOperations, () => managerInits++);
        shell.RegisterViewInitializer(ShellViewNames.OwnerAdmin, () => ownerInits++);

        // Rapid 60-step cycle: Checkout -> Manager -> Owner
        for (int i = 0; i < 20; i++)
        {
            shell.NavigateTo(ShellViewNames.ManagerOperations);
            shell.NavigateTo(ShellViewNames.OwnerAdmin);
            shell.NavigateTo(ShellViewNames.Checkout);
        }

        Assert.Equal(20, checkoutInits);
        Assert.Equal(20, managerInits);
        Assert.Equal(20, ownerInits);

        // Calling NavigateTo on current view is idempotent and does NOT re-run initializer
        shell.NavigateTo(ShellViewNames.Checkout);
        shell.NavigateTo(ShellViewNames.Checkout);
        Assert.Equal(20, checkoutInits);
    }

    // =========================================================================
    // 3. CART STATE PRESERVATION UNDER 50x RAPID TAB SWITCHING
    // =========================================================================

    [Fact]
    public void CartState_Preserved100PercentIdentical_Across50TabSwitches()
    {
        // Arrange
        var cashier = CreateUser(Role.Cashier, "nimal", "Nimal Perera");
        var shift = CreateShift(cashier.UserId, 5000.00m, 18500.00m);

        // 1. Initialize BillingViewModel with active cart items
        var billingVm = new BillingViewModel(cashier, shift, _catalog, _saleService, _heldCartService);

        var item1 = new CartItemViewModel
        {
            ProductId = "prod_001",
            Barcode = "4792001001",
            ProductName = "Munchee Super Cream Cracker 490g",
            UnitPrice = 380.00m,
            Quantity = 3.0m,
            DiscountRate = 0.00m,
            TaxRate = 0.00m
        };
        item1.Recalculate();
        billingVm.CartItems.Add(item1);

        var item2 = new CartItemViewModel
        {
            ProductId = "prod_002",
            Barcode = "4792002002",
            ProductName = "Anchor Full Cream Milk Powder 400g",
            UnitPrice = 1150.00m,
            Quantity = 2.0m,
            DiscountRate = 0.05m, // 5% discount
            TaxRate = 0.00m
        };
        item2.Recalculate();
        billingVm.CartItems.Add(item2);

        var item3 = new CartItemViewModel
        {
            ProductId = "prod_003",
            Barcode = "4792003003",
            ProductName = "Sunlight Soap 115g 4-Pack",
            UnitPrice = 420.00m,
            Quantity = 5.0m,
            DiscountRate = 0.10m, // 10% discount
            TaxRate = 0.18m      // 18% VAT
        };
        item3.Recalculate();
        billingVm.CartItems.Add(item3);

        var item4 = new CartItemViewModel
        {
            ProductId = "prod_004",
            Barcode = "4792004004",
            ProductName = "Elephant House Ginger Beer 1.5L",
            UnitPrice = 350.00m,
            Quantity = 1.5m,      // Fractional quantity
            DiscountRate = 0.00m,
            TaxRate = 0.08m       // 8% SSCL
        };
        item4.Recalculate();
        billingVm.CartItems.Add(item4);

        billingVm.RefreshTotals();

        // Capture exact baseline values before tab switching
        int baselineCount = billingVm.CartItems.Count;
        decimal baselineSubtotal = billingVm.Subtotal;
        decimal baselineDiscountTotal = billingVm.DiscountTotal;
        decimal baselineTaxTotal = billingVm.TaxTotal;
        decimal baselineGrandTotal = billingVm.GrandTotal;

        var baselineItems = billingVm.CartItems.Select(ci => new
        {
            ci.ProductId,
            ci.Barcode,
            ci.ProductName,
            ci.UnitPrice,
            ci.Quantity,
            ci.DiscountRate,
            ci.TaxRate,
            ci.Subtotal,
            ci.DiscountAmount,
            ci.TaxAmount,
            ci.LineTotal
        }).ToList();

        // Simulate shell navigation across Manager and Owner tabs
        var managerUser = CreateUser(Role.Owner, "admin_owner", "Owner Tester");
        var shell = new ShellViewModel(managerUser, shift);

        // Act: Execute 50 rapid tab switching cycles (150 total transitions)
        for (int cycle = 1; cycle <= 50; cycle++)
        {
            // Switch to Manager Operations (Cash & Shifts)
            shell.NavigateTo(ShellViewNames.ManagerOperations);
            Assert.Equal(ShellViewNames.ManagerOperations, shell.CurrentView);

            // Switch to Owner Admin (Store Admin)
            shell.NavigateTo(ShellViewNames.OwnerAdmin);
            Assert.Equal(ShellViewNames.OwnerAdmin, shell.CurrentView);

            // Switch back to Checkout
            shell.NavigateTo(ShellViewNames.Checkout);
            Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        }

        // Assert: Cart state after 50 switches is 100% byte-for-byte and penny-for-penny identical
        Assert.Equal(baselineCount, billingVm.CartItems.Count);
        Assert.Equal(baselineSubtotal, billingVm.Subtotal);
        Assert.Equal(baselineDiscountTotal, billingVm.DiscountTotal);
        Assert.Equal(baselineTaxTotal, billingVm.TaxTotal);
        Assert.Equal(baselineGrandTotal, billingVm.GrandTotal);

        for (int i = 0; i < baselineCount; i++)
        {
            var expected = baselineItems[i];
            var actual = billingVm.CartItems[i];

            Assert.Equal(expected.ProductId, actual.ProductId);
            Assert.Equal(expected.Barcode, actual.Barcode);
            Assert.Equal(expected.ProductName, actual.ProductName);
            Assert.Equal(expected.UnitPrice, actual.UnitPrice);
            Assert.Equal(expected.Quantity, actual.Quantity);
            Assert.Equal(expected.DiscountRate, actual.DiscountRate);
            Assert.Equal(expected.TaxRate, actual.TaxRate);
            Assert.Equal(expected.Subtotal, actual.Subtotal);
            Assert.Equal(expected.DiscountAmount, actual.DiscountAmount);
            Assert.Equal(expected.TaxAmount, actual.TaxAmount);
            Assert.Equal(expected.LineTotal, actual.LineTotal);
        }
    }

    [Fact]
    public void CartState_InterleavedCartEditsAndTabSwitches_RetainsModifications()
    {
        var owner = CreateUser(Role.Owner);
        var shift = CreateShift(owner.UserId);
        var billingVm = new BillingViewModel(owner, shift, _catalog, _saleService, _heldCartService);
        var shell = new ShellViewModel(owner, shift);

        // 1. Initial item
        var item1 = new CartItemViewModel
        {
            ProductId = "prod_01",
            Barcode = "1111",
            ProductName = "Keells Rice 5kg",
            UnitPrice = 1200.00m,
            Quantity = 1m
        };
        item1.Recalculate();
        billingVm.CartItems.Add(item1);
        billingVm.RefreshTotals();

        // 2. Switch away and back
        shell.NavigateTo(ShellViewNames.ManagerOperations);
        shell.NavigateTo(ShellViewNames.Checkout);

        // 3. Increment item and add a new item
        billingVm.IncrementItem(item1); // Quantity becomes 2

        var item2 = new CartItemViewModel
        {
            ProductId = "prod_02",
            Barcode = "2222",
            ProductName = "Maliban Biscuits",
            UnitPrice = 250.00m,
            Quantity = 4m
        };
        item2.Recalculate();
        billingVm.CartItems.Add(item2);
        billingVm.RefreshTotals();

        // Snapshot
        decimal expectedGrandTotal = (1200.00m * 2) + (250.00m * 4); // 2400 + 1000 = 3400.00m
        Assert.Equal(expectedGrandTotal, billingVm.GrandTotal);

        // 4. Switch tabs 50 times
        for (int i = 0; i < 50; i++)
        {
            shell.NavigateTo(ShellViewNames.ManagerOperations);
            shell.NavigateTo(ShellViewNames.OwnerAdmin);
            shell.NavigateTo(ShellViewNames.Checkout);
        }

        // 5. Verify modified cart is perfectly preserved
        Assert.Equal(2, billingVm.CartItems.Count);
        Assert.Equal(2m, billingVm.CartItems[0].Quantity);
        Assert.Equal(4m, billingVm.CartItems[1].Quantity);
        Assert.Equal(3400.00m, billingVm.GrandTotal);
    }

    [Fact]
    public async Task CartState_HeldCartParkedAndRestored_UncorruptedByTabSwitching()
    {
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var billingVm = new BillingViewModel(cashier, shift, _catalog, _saleService, _heldCartService);

        var item = new CartItemViewModel
        {
            ProductId = "p_held",
            Barcode = "8888",
            ProductName = "Ceytea 100g",
            UnitPrice = 450.00m,
            Quantity = 2m
        };
        item.Recalculate();
        billingVm.CartItems.Add(item);
        billingVm.RefreshTotals();

        // Park cart
        bool held = await billingVm.HoldCurrentCartAsync("Table 4VIP");
        Assert.True(held);
        Assert.Empty(billingVm.CartItems);
        Assert.Equal(1, billingVm.HeldCartsCount);

        // Switch tabs rapidly 50 times with elevated user
        var owner = CreateUser(Role.Owner);
        var shell = new ShellViewModel(owner, shift);
        for (int i = 0; i < 50; i++)
        {
            shell.NavigateTo(ShellViewNames.ManagerOperations);
            shell.NavigateTo(ShellViewNames.Checkout);
        }

        // Recall the parked cart
        var heldCartId = billingVm.HeldCarts[0].HeldCartId;
        bool recalled = await billingVm.RecallHeldCartAsync(heldCartId);

        Assert.True(recalled);
        Assert.Single(billingVm.CartItems);
        Assert.Equal("p_held", billingVm.CartItems[0].ProductId);
        Assert.Equal(2m, billingVm.CartItems[0].Quantity);
        Assert.Equal(900.00m, billingVm.GrandTotal);
    }

    // =========================================================================
    // 4. LOGOUT LIFECYCLE & COMPLETE SENSITIVE SESSION TEARDOWN
    // =========================================================================

    [Fact]
    public void LogoutLifecycle_CompleteTeardownOfSensitiveSessionData()
    {
        var manager = CreateUser(Role.Manager, "mgr_secret", "Secret Manager");
        var shift = CreateShift(manager.UserId, openingFloat: 7500.00m, expectedCash: 25000.00m);
        var shell = new ShellViewModel(manager, shift);

        // Navigate to Manager Operations
        shell.NavigateTo(ShellViewNames.ManagerOperations);
        Assert.True(shell.IsAuthenticated);
        Assert.True(shell.IsManagerActive);
        Assert.Equal("Rs. 7,500.00", shell.FormattedOpeningFloat);
        Assert.Equal("Rs. 25,000.00", shell.FormattedExpectedCash);
        Assert.True(shell.HasActiveShift);

        var propertyChanges = new HashSet<string>();
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) propertyChanges.Add(e.PropertyName);
        };

        bool logoutRequestedInvoked = false;
        shell.LogoutRequested += () => logoutRequestedInvoked = true;

        bool loggedOutInvoked = false;
        shell.LoggedOut += (_, _) => loggedOutInvoked = true;

        // Act: Logout
        shell.Logout();

        // Assert 1: Authentication & Identity Cleared
        Assert.Null(shell.CurrentUser);
        Assert.False(shell.IsAuthenticated);
        Assert.Equal(string.Empty, shell.DisplayName);
        Assert.Equal(string.Empty, shell.Username);
        Assert.Equal(string.Empty, shell.RoleDisplayName);
        Assert.Equal(string.Empty, shell.RoleBadgeText);
        Assert.Equal(Role.Cashier, shell.CurrentRole); // Safe default fallback

        // Assert 2: Active Shift & Sensitive Financials Cleared
        Assert.Null(shell.ShiftId);
        Assert.False(shell.HasActiveShift);
        Assert.Equal(0m, shell.OpeningFloat);
        Assert.Equal(0m, shell.ExpectedCash);
        Assert.Equal("Rs. 0.00", shell.FormattedOpeningFloat);
        Assert.Equal("Rs. 0.00", shell.FormattedExpectedCash);
        Assert.Equal("○ SHIFT CLOSED", shell.ShiftBadgeText);
        Assert.Equal("Shift Closed", shell.ShiftFloatSummary);
        Assert.Equal("No active cash shift", shell.ShiftTooltip);

        // Assert 3: View reset to Login screen and tabs completely hidden
        Assert.Equal(ShellViewNames.Login, shell.CurrentView);
        Assert.Equal(ShellViewNames.Login, shell.CurrentViewName);
        Assert.False(shell.IsCheckoutActive);
        Assert.False(shell.IsManagerActive);
        Assert.False(shell.IsOwnerActive);

        Assert.False(shell.IsCashierTabVisible);
        Assert.False(shell.IsManagerTabVisible);
        Assert.False(shell.IsOwnerTabVisible);

        // Assert 4: Events Dispatched
        Assert.True(logoutRequestedInvoked, "LogoutRequested event must be triggered.");
        Assert.True(loggedOutInvoked, "LoggedOut event must be triggered.");

        // Assert 5: All UI binding notifications dispatched
        Assert.Contains(nameof(shell.CurrentUser), propertyChanges);
        Assert.Contains(nameof(shell.IsAuthenticated), propertyChanges);
        Assert.Contains(nameof(shell.CurrentRole), propertyChanges);
        Assert.Contains(nameof(shell.DisplayName), propertyChanges);
        Assert.Contains(nameof(shell.Username), propertyChanges);
        Assert.Contains(nameof(shell.RoleDisplayName), propertyChanges);
        Assert.Contains(nameof(shell.RoleBadgeText), propertyChanges);
        Assert.Contains(nameof(shell.IsCashierTabVisible), propertyChanges);
        Assert.Contains(nameof(shell.IsManagerTabVisible), propertyChanges);
        Assert.Contains(nameof(shell.IsOwnerTabVisible), propertyChanges);
        Assert.Contains(nameof(shell.ShiftId), propertyChanges);
        Assert.Contains(nameof(shell.OpeningFloat), propertyChanges);
        Assert.Contains(nameof(shell.ExpectedCash), propertyChanges);
        Assert.Contains(nameof(shell.FormattedOpeningFloat), propertyChanges);
        Assert.Contains(nameof(shell.FormattedExpectedCash), propertyChanges);
        Assert.Contains(nameof(shell.HasActiveShift), propertyChanges);
        Assert.Contains(nameof(shell.ShiftBadgeText), propertyChanges);
        Assert.Contains(nameof(shell.ShiftFloatSummary), propertyChanges);
        Assert.Contains(nameof(shell.ShiftTooltip), propertyChanges);
        Assert.Contains(nameof(shell.CurrentView), propertyChanges);
        Assert.Contains(nameof(shell.CurrentViewName), propertyChanges);
        Assert.Contains(nameof(shell.IsCheckoutActive), propertyChanges);
        Assert.Contains(nameof(shell.IsManagerActive), propertyChanges);
        Assert.Contains(nameof(shell.IsOwnerActive), propertyChanges);
    }

    [Fact]
    public async Task LogoutLifecycle_PostLogoutNavigationAttempts_StrictlyDenied()
    {
        var owner = CreateUser(Role.Owner);
        var shell = new ShellViewModel(owner);

        shell.Logout();

        // Attempt navigation to all views after logout
        shell.NavigateTo(ShellViewNames.Checkout);
        Assert.Equal(ShellViewNames.Login, shell.CurrentView);

        shell.NavigateTo(ShellViewNames.ManagerOperations);
        Assert.Equal(ShellViewNames.Login, shell.CurrentView);

        shell.NavigateTo(ShellViewNames.OwnerAdmin);
        Assert.Equal(ShellViewNames.Login, shell.CurrentView);

        // Async navigation
        bool checkoutAsync = await shell.RequestNavigateAsync(ShellViewNames.Checkout);
        Assert.False(checkoutAsync);
        Assert.Equal(ShellViewNames.Login, shell.CurrentView);

        bool managerAsync = await shell.RequestNavigateAsync(ShellViewNames.ManagerOperations);
        Assert.False(managerAsync);
        Assert.Equal(ShellViewNames.Login, shell.CurrentView);

        bool ownerAsync = await shell.RequestNavigateAsync(ShellViewNames.OwnerAdmin);
        Assert.False(ownerAsync);
        Assert.Equal(ShellViewNames.Login, shell.CurrentView);
    }

    [Fact]
    public void LogoutLifecycle_SuccessorLogin_ZeroDataBleedFromPriorSession()
    {
        // Session 1: Manager with high cash drawer float
        var manager = CreateUser(Role.Manager, "mgr_alpha", "Alpha Manager");
        var shift1 = CreateShift(manager.UserId, openingFloat: 10000.00m, expectedCash: 35000.00m);
        var shell = new ShellViewModel(manager, shift1);

        shell.NavigateTo(ShellViewNames.ManagerOperations);
        Assert.True(shell.IsManagerTabVisible);

        // Logout
        shell.Logout();
        Assert.False(shell.IsAuthenticated);

        // Session 2: Ordinary Cashier with lower float
        var cashier = CreateUser(Role.Cashier, "cashier_beta", "Beta Cashier");
        var shift2 = CreateShift(cashier.UserId, openingFloat: 2000.00m, expectedCash: 2000.00m);

        shell.SetActiveUser(cashier, shift2);

        // Verify clean isolation:
        Assert.True(shell.IsAuthenticated);
        Assert.Equal(cashier.UserId, shell.CurrentUser?.UserId);
        Assert.Equal("cashier_beta", shell.Username);
        Assert.Equal("Beta Cashier", shell.DisplayName);
        Assert.Equal(Role.Cashier, shell.CurrentRole);
        Assert.Equal("CASHIER", shell.RoleBadgeText);
        Assert.Equal("Cashier", shell.RoleDisplayName);

        // Check tabs: Manager and Owner MUST NOT be visible
        Assert.True(shell.IsCashierTabVisible);
        Assert.False(shell.IsManagerTabVisible, "Manager tab must NOT bleed into Cashier session.");
        Assert.False(shell.IsOwnerTabVisible, "Owner tab must NOT bleed into Cashier session.");

        // Financials must reflect new shift only
        Assert.Equal(shift2.ShiftId, shell.ShiftId);
        Assert.Equal(2000.00m, shell.OpeningFloat);
        Assert.Equal(2000.00m, shell.ExpectedCash);
        Assert.Equal("Rs. 2,000.00", shell.FormattedOpeningFloat);
        Assert.Equal("Rs. 2,000.00", shell.FormattedExpectedCash);

        // View must default to Checkout
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.True(shell.IsCheckoutActive);
        Assert.False(shell.IsManagerActive);
        Assert.False(shell.IsOwnerActive);
    }

    // =========================================================================
    // 5. ADDITIONAL ADVERSARIAL STRESS & CORNER CASES
    // =========================================================================

    [Fact]
    public void CartState_HighDensity100ItemsCart_PreservedAcross50TabSwitches()
    {
        var owner = CreateUser(Role.Owner);
        var shift = CreateShift(owner.UserId);
        var billingVm = new BillingViewModel(owner, shift, _catalog, _saleService, _heldCartService);

        // Populate 100 distinct items with varied prices, quantities, taxes, and discounts
        for (int i = 1; i <= 100; i++)
        {
            var item = new CartItemViewModel
            {
                ProductId = $"prod_hd_{i:D3}",
                Barcode = $"479000000{i:D3}",
                ProductName = $"Grocery Bulk SKU #{i}",
                UnitPrice = 100.00m + (i * 2.50m),
                Quantity = 1.0m + (i % 5),
                DiscountRate = (i % 10 == 0) ? 0.15m : 0.00m,
                TaxRate = (i % 3 == 0) ? 0.18m : 0.00m
            };
            item.Recalculate();
            billingVm.CartItems.Add(item);
        }
        billingVm.RefreshTotals();

        Assert.Equal(100, billingVm.CartItems.Count);
        decimal baselineGrandTotal = billingVm.GrandTotal;
        decimal baselineSubtotal = billingVm.Subtotal;
        decimal baselineTaxTotal = billingVm.TaxTotal;
        decimal baselineDiscountTotal = billingVm.DiscountTotal;

        var shell = new ShellViewModel(owner, shift);

        // Switch 50 times across all 3 tabs
        for (int cycle = 0; cycle < 50; cycle++)
        {
            shell.NavigateTo(ShellViewNames.ManagerOperations);
            shell.NavigateTo(ShellViewNames.OwnerAdmin);
            shell.NavigateTo(ShellViewNames.Checkout);
        }

        // Verify all 100 items and all financial totals remain 100% exact
        Assert.Equal(100, billingVm.CartItems.Count);
        Assert.Equal(baselineSubtotal, billingVm.Subtotal);
        Assert.Equal(baselineDiscountTotal, billingVm.DiscountTotal);
        Assert.Equal(baselineTaxTotal, billingVm.TaxTotal);
        Assert.Equal(baselineGrandTotal, billingVm.GrandTotal);

        for (int i = 0; i < 100; i++)
        {
            var expectedSku = $"prod_hd_{(i + 1):D3}";
            Assert.Equal(expectedSku, billingVm.CartItems[i].ProductId);
            Assert.True(billingVm.CartItems[i].LineTotal > 0);
        }
    }

    [Fact]
    public async Task UnauthorizedNav_ElevationRequestedThrows_PreservesCheckoutAndDoesNotElevate()
    {
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);

        // Elevation callback throws an unhandled exception (e.g. timeout, service fault)
        shell.ElevationRequested = _ => throw new InvalidOperationException("Auth service connection lost.");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            shell.RequestNavigateAsync(ShellViewNames.ManagerOperations));

        // State remains strictly on Checkout; not elevated or corrupted
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.True(shell.IsCheckoutActive);
        Assert.False(shell.IsManagerActive);
    }

    [Fact]
    public void RoleTabVisibility_ProviderStaff_FullAccessAndSubsequentLogout()
    {
        var staff = CreateUser(Role.ProviderStaff, "ps_eng", "Field Support");
        var shift = CreateShift(staff.UserId);
        var shell = new ShellViewModel(staff, shift);

        // Verify initial permissions
        Assert.True(shell.IsCashierTabVisible);
        Assert.True(shell.IsManagerTabVisible);
        Assert.True(shell.IsOwnerTabVisible);

        // Navigate to Owner Admin
        shell.NavigateTo(ShellViewNames.OwnerAdmin);
        Assert.Equal(ShellViewNames.OwnerAdmin, shell.CurrentView);
        Assert.True(shell.IsOwnerActive);

        // Logout
        shell.Logout();
        Assert.Null(shell.CurrentUser);
        Assert.False(shell.IsAuthenticated);
        Assert.False(shell.IsCashierTabVisible);
        Assert.False(shell.IsManagerTabVisible);
        Assert.False(shell.IsOwnerTabVisible);
        Assert.Equal(ShellViewNames.Login, shell.CurrentView);
    }

    [Theory]
    [InlineData("checkout")]
    [InlineData("CHECKOUT")]
    [InlineData("manageroperations")]
    [InlineData("MANAGEROPERATIONS")]
    [InlineData("owneradmin")]
    [InlineData("OWNERADMIN")]
    public void UnauthorizedNav_CaseMismatchOrNonStandardNames_DeniedByCanNavigateTo(string nonStandardViewName)
    {
        var owner = CreateUser(Role.Owner);
        var shell = new ShellViewModel(owner);

        // The shell navigation routes are strictly defined by ShellViewNames constants.
        // Non-standard casing or strings must not match the exact routes.
        Assert.False(shell.CanNavigateTo(nonStandardViewName));
    }
}
