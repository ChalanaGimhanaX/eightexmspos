using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;
using Enightx.Pos.ViewModels;
using Enightx.Pos.Wpf.ViewModels;

namespace Enightx.Pos.Tests;

public class RolePermissionGuardrailTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _authService;
    private readonly CatalogService _catalogService;
    private readonly ShiftService _shiftService;
    private readonly SaleService _saleService;
    private readonly HeldCartService _heldCartService;

    public RolePermissionGuardrailTests()
    {
        _db = PosDatabase.CreateInMemory();
        _authService = new AuthService(_db);
        _catalogService = new CatalogService(_db);
        _shiftService = new ShiftService(_db);
        _saleService = new SaleService(_db, _catalogService);
        _heldCartService = new HeldCartService(_db);

        Enightx.Pos.Wpf.App.HeldCartService = _heldCartService;
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private static User CreateUser(Role role, string username = "guard_user") => new()
    {
        UserId = $"usr_{Guid.NewGuid():N}",
        Username = username,
        DisplayName = $"Display {username}",
        Role = role,
        PasswordHash = "dummy_hash",
        PasswordSalt = "dummy_salt",
        IsActive = true
    };

    private static CashShift CreateShift(string userId) => new()
    {
        ShiftId = Guid.NewGuid(),
        TenantId = "TENANT_LK_01",
        BranchId = "B01",
        CounterId = "C01",
        CashierId = userId,
        OpeningFloat = 5000.00m,
        ExpectedCash = 5000.00m,
        OpenedAtUtc = DateTime.UtcNow,
        Status = ShiftStatus.Open
    };

    private class MockAuthorizationGate : IAuthorizationGateService
    {
        public bool NextResult { get; set; } = true;
        public User? AuthorizingUserToReturn { get; set; }
        public int CallCount { get; private set; }
        public string? LastOperation { get; private set; }
        public Role? LastRole { get; private set; }

        public User? LastAuthorizingUser => NextResult ? AuthorizingUserToReturn : null;

        public Task<bool> AuthorizeOperationAsync(
            User currentUser,
            Role requiredRole,
            string operationName,
            string? tenantId = null,
            string? branchId = null,
            string? counterId = null)
        {
            CallCount++;
            LastOperation = operationName;
            LastRole = requiredRole;

            if (currentUser.Role >= requiredRole)
            {
                AuthorizingUserToReturn = currentUser;
                return Task.FromResult(true);
            }

            return Task.FromResult(NextResult);
        }

        public Task<AuthorizationResult> AuthorizeWithResultAsync(
            User currentUser,
            Role requiredRole,
            string operationName,
            string? tenantId = null,
            string? branchId = null,
            string? counterId = null)
        {
            CallCount++;
            LastOperation = operationName;
            LastRole = requiredRole;

            if (currentUser.Role >= requiredRole)
            {
                AuthorizingUserToReturn = currentUser;
                return Task.FromResult(AuthorizationResult.DirectApproval(currentUser));
            }

            if (NextResult && AuthorizingUserToReturn != null)
            {
                return Task.FromResult(AuthorizationResult.ElevatedApproval(AuthorizingUserToReturn));
            }

            return Task.FromResult(AuthorizationResult.Denied("Denied by test"));
        }

        public Task<AuthorizationResult> AuthorizeAsync(
            User currentUser,
            Role requiredRole,
            string operationName,
            string? tenantId = null,
            string? branchId = null,
            string? counterId = null) => AuthorizeWithResultAsync(currentUser, requiredRole, operationName, tenantId, branchId, counterId);
    }

    // =========================================================================
    // SECTION 1: ShellViewModel ROLE TAB VISIBILITY & NAVIGATION GUARDRAILS
    // =========================================================================

    [Fact]
    public void ShellViewModel_RoleTabVisibility_MatrixVerification()
    {
        // 1. Cashier Role
        var cashier = CreateUser(Role.Cashier);
        var shellCashier = new ShellViewModel(cashier);
        Assert.True(shellCashier.IsCashierTabVisible);
        Assert.False(shellCashier.IsManagerTabVisible);
        Assert.False(shellCashier.IsOwnerTabVisible);

        // 2. Manager Role
        var manager = CreateUser(Role.Manager);
        var shellManager = new ShellViewModel(manager);
        Assert.True(shellManager.IsCashierTabVisible);
        Assert.True(shellManager.IsManagerTabVisible);
        Assert.False(shellManager.IsOwnerTabVisible);

        // 3. Owner Role
        var owner = CreateUser(Role.Owner);
        var shellOwner = new ShellViewModel(owner);
        Assert.True(shellOwner.IsCashierTabVisible);
        Assert.True(shellOwner.IsManagerTabVisible);
        Assert.True(shellOwner.IsOwnerTabVisible);

        // 4. Unauthenticated
        var shellAnon = new ShellViewModel(currentUser: null);
        Assert.False(shellAnon.IsCashierTabVisible);
        Assert.False(shellAnon.IsManagerTabVisible);
        Assert.False(shellAnon.IsOwnerTabVisible);
    }

    [Fact]
    public void ShellViewModel_UnauthorizedNavigationAttempts_DirectlyBlocked()
    {
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);

        // Direct navigation to Manager Operations blocked
        shell.NavigateTo(ShellViewNames.ManagerOperations);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.False(shell.IsManagerActive);
        Assert.Contains("Insufficient permissions", shell.StatusMessage);

        // Direct navigation to Owner Admin blocked
        shell.NavigateTo(ShellViewNames.OwnerAdmin);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.False(shell.IsOwnerActive);
        Assert.Contains("Insufficient permissions", shell.StatusMessage);

        // CurrentViewName property setter tampering blocked
        shell.CurrentViewName = ShellViewNames.ManagerOperations;
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
    }

    [Fact]
    public async Task ShellViewModel_RequestNavigateAsync_ElevationRequiredAndEnforced()
    {
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);

        // Case A: No elevation delegate configured -> Fails safely
        shell.ElevationRequested = null;
        bool deniedWithoutHandler = await shell.RequestNavigateAsync(ShellViewNames.ManagerOperations);
        Assert.False(deniedWithoutHandler);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);

        // Case B: Elevation delegate denies authorization (e.g. invalid PIN)
        shell.ElevationRequested = _ => Task.FromResult(false);
        bool deniedByElevation = await shell.RequestNavigateAsync(ShellViewNames.ManagerOperations);
        Assert.False(deniedByElevation);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);

        // Case C: Elevation delegate approves authorization (valid PIN)
        shell.ElevationRequested = _ => Task.FromResult(true);
        bool approvedByElevation = await shell.RequestNavigateAsync(ShellViewNames.ManagerOperations);
        Assert.True(approvedByElevation);
        Assert.Equal(ShellViewNames.ManagerOperations, shell.CurrentView);
    }

    [Fact]
    public async Task ShellViewModel_RequestNavigateAsync_WithGateService_EnforcesRoleElevation()
    {
        var cashier = CreateUser(Role.Cashier);
        var mockGate = new MockAuthorizationGate { NextResult = false };
        var shell = new ShellViewModel(cashier, authorizationGateService: mockGate);

        // Cashier navigates to OwnerAdmin -> gate called with Role.Owner
        bool denied = await shell.RequestNavigateAsync(ShellViewNames.OwnerAdmin);
        Assert.False(denied);
        Assert.Equal(1, mockGate.CallCount);
        Assert.Equal(Role.Owner, mockGate.LastRole);

        // Enable gate approval
        mockGate.NextResult = true;
        mockGate.AuthorizingUserToReturn = CreateUser(Role.Owner);
        bool approved = await shell.RequestNavigateAsync(ShellViewNames.OwnerAdmin);
        Assert.True(approved);
        Assert.Equal(ShellViewNames.OwnerAdmin, shell.CurrentView);
    }

    // =========================================================================
    // SECTION 2: BillingViewModel SENSITIVE CASHIER OPERATIONS GUARDRAILS
    // =========================================================================

    [Fact]
    public async Task SensitiveOp_PriceOverride_BlockedWhenAuthorizationFails()
    {
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new MockAuthorizationGate { NextResult = false }; // Rejects

        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var product = new Product
        {
            ProductId = "p_test_price",
            Barcode = "990001",
            Name = "Test Oil Filter",
            UnitPrice = 2500.00m,
            StockOnHand = 10m
        };
        await _catalogService.AddProductAsync(product);
        vm.AddProductToCart(product);
        var cartItem = vm.CartItems[0];

        // Act: Cashier attempts to override price to 1800.00m
        bool overrideSuccess = await vm.ApplyPriceOverrideAsync(cartItem, 1800.00m, "Competitor Price Match");

        // Assert: Override strictly blocked
        Assert.False(overrideSuccess);
        Assert.Equal(2500.00m, cartItem.UnitPrice); // Unchanged!
        Assert.Equal(2500.00m, cartItem.LineTotal);
        Assert.False(cartItem.IsPriceOverridden);
        Assert.Null(cartItem.AuthorizingUserId);
        Assert.Equal(1, mockGate.CallCount);
        Assert.Contains("Price Override", mockGate.LastOperation);
        Assert.Contains("Manager authorization required", vm.StatusMessage);
    }

    [Fact]
    public async Task SensitiveOp_PriceOverride_SucceedsWhenAuthorizedByManager()
    {
        var cashier = CreateUser(Role.Cashier);
        var manager = CreateUser(Role.Manager, "approver_mgr");
        var shift = CreateShift(cashier.UserId);
        var mockGate = new MockAuthorizationGate
        {
            NextResult = true,
            AuthorizingUserToReturn = manager
        };

        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var product = new Product
        {
            ProductId = "p_test_price2",
            Barcode = "990002",
            Name = "Brake Fluid 500ml",
            UnitPrice = 1200.00m,
            StockOnHand = 10m
        };
        await _catalogService.AddProductAsync(product);
        vm.AddProductToCart(product);
        var cartItem = vm.CartItems[0];

        // Act: Cashier overrides price with manager approval
        bool overrideSuccess = await vm.ApplyPriceOverrideAsync(cartItem, 1000.00m, "Manager Goodwill");

        // Assert: Override applied and authorizer recorded
        Assert.True(overrideSuccess);
        Assert.Equal(1000.00m, cartItem.UnitPrice);
        Assert.Equal(1000.00m, cartItem.LineTotal);
        Assert.True(cartItem.IsPriceOverridden);
        Assert.Equal(manager.UserId, cartItem.AuthorizingUserId);
        Assert.Equal("Manager Goodwill", cartItem.OverrideReason);
    }

    [Fact]
    public async Task SensitiveOp_ExcessiveDiscount_BlockedWhenAuthorizationFails()
    {
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new MockAuthorizationGate { NextResult = false }; // Rejects

        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var product = new Product
        {
            ProductId = "p_disc_test",
            Barcode = "990003",
            Name = "Synthetic Engine Oil 4L",
            UnitPrice = 8000.00m,
            StockOnHand = 5m
        };
        await _catalogService.AddProductAsync(product);
        vm.AddProductToCart(product);
        var cartItem = vm.CartItems[0];

        // Normal small discount (e.g. 5% <= 10% threshold) does not require manager PIN
        bool normalDiscSuccess = await vm.ApplyDiscountAsync(cartItem, 0.05m);
        Assert.True(normalDiscSuccess);
        Assert.Equal(0.05m, cartItem.DiscountRate);
        Assert.Equal(0, mockGate.CallCount); // No PIN gate triggered for <= threshold

        // Excessive discount (30% > 10% threshold) MUST trigger PIN gate and fail
        bool excessiveDiscSuccess = await vm.ApplyDiscountAsync(cartItem, 0.30m);
        Assert.False(excessiveDiscSuccess);
        Assert.Equal(0.05m, cartItem.DiscountRate); // Retains prior valid discount
        Assert.Equal(1, mockGate.CallCount);
        Assert.Contains("Excessive Discount", mockGate.LastOperation);
        Assert.Contains("exceeding 10% require Manager authorization", vm.StatusMessage);
    }

    [Fact]
    public async Task SensitiveOp_CartDiscount_ExcessiveThresholdChecked()
    {
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new MockAuthorizationGate { NextResult = false };

        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var product = new Product
        {
            ProductId = "p_cart_disc",
            Barcode = "990009",
            Name = "Wiper Blade 20in",
            UnitPrice = 1500.00m,
            StockOnHand = 10m
        };
        await _catalogService.AddProductAsync(product);
        vm.AddProductToCart(product);

        // 15% cart discount exceeds 10% threshold
        bool denied = await vm.ApplyCartDiscountAsync(0.15m);
        Assert.False(denied);
        Assert.Equal(0m, vm.CartItems[0].DiscountRate);
        Assert.Equal(1, mockGate.CallCount);

        // Approve elevation
        var manager = CreateUser(Role.Manager);
        mockGate.NextResult = true;
        mockGate.AuthorizingUserToReturn = manager;

        bool approved = await vm.ApplyCartDiscountAsync(0.15m);
        Assert.True(approved);
        Assert.Equal(0.15m, vm.CartItems[0].DiscountRate);
        Assert.Equal(manager.UserId, vm.CartItems[0].AuthorizingUserId);
    }

    [Fact]
    public async Task SensitiveOp_RefundIssue_BlockedWhenAuthorizationFails()
    {
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new MockAuthorizationGate { NextResult = false }; // Rejects

        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var fakeSaleId = Guid.NewGuid();
        var refundLines = new List<RefundLineRequest>
        {
            new(Guid.NewGuid(), 1.0m)
        };

        // Act: Cashier attempts to issue refund without manager approval
        bool refundSuccess = await vm.IssueRefundAsync(fakeSaleId, refundLines, "Customer dissatisfied");

        // Assert: Blocked
        Assert.False(refundSuccess);
        Assert.Equal(1, mockGate.CallCount);
        Assert.Contains("Refund", mockGate.LastOperation);
        Assert.Contains("Manager authorization required", vm.StatusMessage);
    }

    [Fact]
    public async Task SensitiveOp_ManualStockAdjustment_BlockedWhenAuthorizationFails()
    {
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new MockAuthorizationGate { NextResult = false }; // Rejects

        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var product = new Product
        {
            ProductId = "p_stock_adj",
            Barcode = "990004",
            Name = "Spark Plug BKR6E",
            UnitPrice = 650.00m,
            StockOnHand = 25m
        };
        await _catalogService.AddProductAsync(product);

        // Act: Cashier attempts manual stock adjustment (-5 items damaged)
        bool adjSuccess = await vm.AdjustStockAsync(product.ProductId, -5m, "Damaged in store handling");

        // Assert: Blocked and stock remains unchanged
        Assert.False(adjSuccess);
        Assert.Equal(25m, await _catalogService.GetStockOnHandAsync(product.ProductId));
        Assert.Equal(1, mockGate.CallCount);
        Assert.Contains("Stock Adjustment", mockGate.LastOperation);
        Assert.Contains("Manager authorization required", vm.StatusMessage);
    }

    [Fact]
    public async Task SensitiveOp_ManualStockAdjustment_SucceedsWhenAuthorizedByManager()
    {
        var cashier = CreateUser(Role.Cashier);
        var manager = CreateUser(Role.Manager, "mgr_adj");
        var shift = CreateShift(cashier.UserId);
        var mockGate = new MockAuthorizationGate
        {
            NextResult = true,
            AuthorizingUserToReturn = manager
        };

        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var product = new Product
        {
            ProductId = "p_stock_adj_ok",
            Barcode = "990006",
            Name = "Air Filter AF102",
            UnitPrice = 1800.00m,
            StockOnHand = 20m
        };
        await _catalogService.AddProductAsync(product);

        // Act: Cashier adjusts stock with manager approval
        bool adjSuccess = await vm.AdjustStockAsync(product.ProductId, -3m, "Damaged filter carton");

        // Assert: Approved and stock updated
        Assert.True(adjSuccess);
        Assert.Equal(17m, await _catalogService.GetStockOnHandAsync(product.ProductId));
    }

    [Fact]
    public async Task SensitiveOp_ManagerRole_AutoPassesAllOperationsWithoutGatePrompt()
    {
        var manager = CreateUser(Role.Manager);
        var shift = CreateShift(manager.UserId);
        var mockGate = new MockAuthorizationGate();

        var vm = new BillingViewModel(manager, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var product = new Product
        {
            ProductId = "p_mgr_auto",
            Barcode = "990005",
            Name = "Brake Pads Set",
            UnitPrice = 4500.00m,
            StockOnHand = 10m
        };
        await _catalogService.AddProductAsync(product);
        vm.AddProductToCart(product);
        var cartItem = vm.CartItems[0];

        // Manager executes price override directly
        bool overrideSuccess = await vm.ApplyPriceOverrideAsync(cartItem, 4000.00m, "Manager Discretion");
        Assert.True(overrideSuccess);
        Assert.Equal(4000.00m, cartItem.UnitPrice);

        // Manager executes excessive discount directly
        bool discountSuccess = await vm.ApplyDiscountAsync(cartItem, 0.25m);
        Assert.True(discountSuccess);
        Assert.Equal(0.25m, cartItem.DiscountRate);

        // Authorizing user set to manager
        Assert.Equal(manager.UserId, cartItem.AuthorizingUserId);
    }
}
