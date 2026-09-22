using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;
using Enightx.Pos.ViewModels;
using Enightx.Pos.Wpf.ViewModels;

namespace Enightx.Pos.Tests;

public class Milestone9AdversarialTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _authService;
    private readonly CatalogService _catalogService;
    private readonly ShiftService _shiftService;
    private readonly SaleService _saleService;
    private readonly HeldCartService _heldCartService;

    public Milestone9AdversarialTests()
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

    private static User CreateUser(Role role, string username = "adv_user") => new()
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

    private class ConfigurableMockGate : IAuthorizationGateService
    {
        public bool ShouldApprove { get; set; } = false;
        public User? ApprovingUser { get; set; }
        public int InvocationCount { get; private set; }
        public string? LastOperationName { get; private set; }
        public Role? LastRequiredRole { get; private set; }

        public User? LastAuthorizingUser => ShouldApprove ? ApprovingUser : null;

        public Task<bool> AuthorizeOperationAsync(
            User currentUser,
            Role requiredRole,
            string operationName,
            string? tenantId = null,
            string? branchId = null,
            string? counterId = null)
        {
            InvocationCount++;
            LastOperationName = operationName;
            LastRequiredRole = requiredRole;

            if (currentUser.Role >= requiredRole)
            {
                ApprovingUser = currentUser;
                return Task.FromResult(true);
            }

            return Task.FromResult(ShouldApprove);
        }

        public Task<AuthorizationResult> AuthorizeWithResultAsync(
            User currentUser,
            Role requiredRole,
            string operationName,
            string? tenantId = null,
            string? branchId = null,
            string? counterId = null)
        {
            InvocationCount++;
            LastOperationName = operationName;
            LastRequiredRole = requiredRole;

            if (currentUser.Role >= requiredRole)
            {
                ApprovingUser = currentUser;
                return Task.FromResult(AuthorizationResult.DirectApproval(currentUser));
            }

            if (ShouldApprove && ApprovingUser != null)
            {
                return Task.FromResult(AuthorizationResult.ElevatedApproval(ApprovingUser));
            }

            return Task.FromResult(AuthorizationResult.Denied("Denied by adversarial mock gate"));
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
    // ITEM 1: CART INVARIANCE ON REJECTED / CANCELLED PRICE OVERRIDE
    // =========================================================================

    [Fact]
    public async Task CartInvariance_OnRejectedPriceOverride_PreservesExactPennyValues()
    {
        // Arrange
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new ConfigurableMockGate { ShouldApprove = false }; // Manager rejects
        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var product = new Product
        {
            ProductId = "adv_p1",
            Barcode = "889901",
            Name = "Castrol GTX Engine Oil 4L",
            UnitPrice = 4850.75m,
            TaxRate = 0.00m,
            StockOnHand = 20m
        };
        await _catalogService.AddProductAsync(product);
        vm.AddProductToCart(product);

        var item = vm.CartItems[0];
        item.Quantity = 3.00m;
        item.DiscountRate = 0.05m;
        item.Recalculate();
        vm.RefreshTotals();

        decimal initialUnitPrice = item.UnitPrice;
        decimal initialSubtotal = item.Subtotal;
        decimal initialDiscount = item.DiscountAmount;
        decimal initialLineTotal = item.LineTotal;
        decimal initialGrandTotal = vm.GrandTotal;

        Assert.Equal(4850.75m, initialUnitPrice);
        Assert.Equal(14552.25m, initialSubtotal);
        Assert.Equal(727.61m, initialDiscount);
        Assert.Equal(13824.64m, initialLineTotal);
        Assert.Equal(13824.64m, initialGrandTotal);

        // Act: Attempt price override to 3000.00 LKR
        bool result = await vm.ApplyPriceOverrideAsync(item, 3000.00m, "Customer insists competitor is cheaper");

        // Assert: 100% Invariance - 0.00 LKR drift
        Assert.False(result);
        Assert.Equal(initialUnitPrice, item.UnitPrice);
        Assert.Equal(initialSubtotal, item.Subtotal);
        Assert.Equal(initialDiscount, item.DiscountAmount);
        Assert.Equal(initialLineTotal, item.LineTotal);
        Assert.Equal(initialGrandTotal, vm.GrandTotal);
        Assert.False(item.IsPriceOverridden);
        Assert.Null(item.AuthorizingUserId);
        Assert.Null(item.OverrideReason);
        Assert.Equal(1, mockGate.InvocationCount);
    }

    [Fact]
    public async Task CartInvariance_OnMultipleItemsCart_RejectedOverrideDoesNotAffectOtherLines()
    {
        // Arrange
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new ConfigurableMockGate { ShouldApprove = false };
        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var p1 = new Product { ProductId = "p1", Barcode = "111", Name = "Wiper", UnitPrice = 1250.50m, StockOnHand = 10m };
        var p2 = new Product { ProductId = "p2", Barcode = "222", Name = "Bulb", UnitPrice = 450.25m, StockOnHand = 10m };
        var p3 = new Product { ProductId = "p3", Barcode = "333", Name = "Fuse", UnitPrice = 120.00m, StockOnHand = 10m };
        await _catalogService.AddProductAsync(p1);
        await _catalogService.AddProductAsync(p2);
        await _catalogService.AddProductAsync(p3);

        vm.AddProductToCart(p1);
        vm.AddProductToCart(p2);
        vm.AddProductToCart(p3);

        decimal initialP1Total = vm.CartItems[0].LineTotal;
        decimal initialP2Total = vm.CartItems[1].LineTotal;
        decimal initialP3Total = vm.CartItems[2].LineTotal;
        decimal initialGrandTotal = vm.GrandTotal;

        // Act: Cashier attempts to override p2 (Bulb)
        bool result = await vm.ApplyPriceOverrideAsync(vm.CartItems[1], 100.00m, "Clearance");

        // Assert: All 3 items and grand total completely unmodified
        Assert.False(result);
        Assert.Equal(initialP1Total, vm.CartItems[0].LineTotal);
        Assert.Equal(initialP2Total, vm.CartItems[1].LineTotal);
        Assert.Equal(initialP3Total, vm.CartItems[2].LineTotal);
        Assert.Equal(initialGrandTotal, vm.GrandTotal);
    }

    [Theory]
    [InlineData(-100.00)]
    [InlineData(-0.01)]
    public async Task CartInvariance_OnNegativePriceOverride_FailsSafelyWithoutGateCall(decimal negativePrice)
    {
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new ConfigurableMockGate { ShouldApprove = true }; // Even if gate would approve
        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var p = new Product { ProductId = "p_neg", Barcode = "555", Name = "Spark Plug", UnitPrice = 850.00m, StockOnHand = 10m };
        await _catalogService.AddProductAsync(p);
        vm.AddProductToCart(p);

        bool result = await vm.ApplyPriceOverrideAsync(vm.CartItems[0], negativePrice, "Erroneous credit");

        Assert.False(result);
        Assert.Equal(850.00m, vm.CartItems[0].UnitPrice);
        Assert.Equal(0, mockGate.InvocationCount); // Gate should not even be prompted for negative price
        Assert.Contains("Price cannot be negative", vm.StatusMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CartInvariance_OnEmptyOverrideReason_FailsSafelyWithoutGateCall(string emptyReason)
    {
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new ConfigurableMockGate { ShouldApprove = true };
        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var p = new Product { ProductId = "p_empty_rsn", Barcode = "556", Name = "Relay", UnitPrice = 650.00m, StockOnHand = 10m };
        await _catalogService.AddProductAsync(p);
        vm.AddProductToCart(p);

        bool result = await vm.ApplyPriceOverrideAsync(vm.CartItems[0], 500.00m, emptyReason);

        Assert.False(result);
        Assert.Equal(650.00m, vm.CartItems[0].UnitPrice);
        Assert.Equal(0, mockGate.InvocationCount);
        Assert.Contains("reason is strictly mandatory", vm.StatusMessage);
    }

    // =========================================================================
    // ITEM 2: DISCOUNT INVARIANCE ON REJECTED / CANCELLED DISCOUNT > 10%
    // =========================================================================

    [Fact]
    public async Task DiscountInvariance_OnRejectedLineDiscount_SubtotalAndGrandTotalUnchanged()
    {
        // Arrange
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new ConfigurableMockGate { ShouldApprove = false };
        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var product = new Product
        {
            ProductId = "adv_disc_p1",
            Barcode = "777001",
            Name = "Brembo Brake Rotor",
            UnitPrice = 12750.50m,
            TaxRate = 0.00m,
            StockOnHand = 10m
        };
        await _catalogService.AddProductAsync(product);
        vm.AddProductToCart(product);

        var item = vm.CartItems[0];
        item.Quantity = 2.00m;
        item.DiscountRate = 0.08m; // legitimate 8% cashier discount
        item.Recalculate();
        vm.RefreshTotals();

        decimal originalSubtotal = item.Subtotal;
        decimal originalDiscountAmount = item.DiscountAmount;
        decimal originalLineTotal = item.LineTotal;
        decimal originalGrandTotal = vm.GrandTotal;

        Assert.Equal(25501.00m, originalSubtotal);
        Assert.Equal(2040.08m, originalDiscountAmount);
        Assert.Equal(23460.92m, originalLineTotal);
        Assert.Equal(23460.92m, originalGrandTotal);

        // Act: Cashier attempts to apply 25% discount (> 10%), rejected by gate
        bool success = await vm.ApplyDiscountAsync(item, 0.25m);

        // Assert: 100% Invariance
        Assert.False(success);
        Assert.Equal(0.08m, item.DiscountRate);
        Assert.Equal(originalSubtotal, item.Subtotal);
        Assert.Equal(originalDiscountAmount, item.DiscountAmount);
        Assert.Equal(originalLineTotal, item.LineTotal);
        Assert.Equal(originalGrandTotal, vm.GrandTotal);
        Assert.Equal(1, mockGate.InvocationCount);
    }

    [Fact]
    public async Task DiscountInvariance_OnRejectedCartDiscount_MultiItemCartPreservesExactTotals()
    {
        // Arrange
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new ConfigurableMockGate { ShouldApprove = false };
        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var p1 = new Product { ProductId = "cd_1", Barcode = "901", Name = "Air Filter", UnitPrice = 1850.25m, StockOnHand = 10m };
        var p2 = new Product { ProductId = "cd_2", Barcode = "902", Name = "Cabin Filter", UnitPrice = 2450.75m, StockOnHand = 10m };
        await _catalogService.AddProductAsync(p1);
        await _catalogService.AddProductAsync(p2);

        vm.AddProductToCart(p1);
        vm.AddProductToCart(p2);

        // Item 1 has 5% prior discount, Item 2 has 0%
        vm.CartItems[0].DiscountRate = 0.05m;
        vm.CartItems[0].Recalculate();
        vm.CartItems[1].Recalculate();
        vm.RefreshTotals();

        decimal origSubtotal = vm.Subtotal;
        decimal origDiscount = vm.DiscountTotal;
        decimal origGrandTotal = vm.GrandTotal;

        // Act: Cashier attempts 20% cart discount, rejected
        bool success = await vm.ApplyCartDiscountAsync(0.20m);

        // Assert
        Assert.False(success);
        Assert.Equal(0.05m, vm.CartItems[0].DiscountRate);
        Assert.Equal(0.00m, vm.CartItems[1].DiscountRate);
        Assert.Equal(origSubtotal, vm.Subtotal);
        Assert.Equal(origDiscount, vm.DiscountTotal);
        Assert.Equal(origGrandTotal, vm.GrandTotal);
    }

    [Fact]
    public async Task DiscountInvariance_Cashier10PercentThresholdBoundary()
    {
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new ConfigurableMockGate { ShouldApprove = false };
        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var p = new Product { ProductId = "p_bound", Barcode = "8811", Name = "Coolant 1L", UnitPrice = 1000.00m, StockOnHand = 10m };
        await _catalogService.AddProductAsync(p);
        vm.AddProductToCart(p);

        // Exactly 10% (0.10m): Permitted for cashier without manager PIN
        bool tenPercentAllowed = await vm.ApplyDiscountAsync(vm.CartItems[0], 0.10m);
        Assert.True(tenPercentAllowed);
        Assert.Equal(0.10m, vm.CartItems[0].DiscountRate);
        Assert.Equal(0, mockGate.InvocationCount);

        // 10.01% (0.1001m): Exceeds threshold, requires gate, rejected
        bool tenPointZeroOneRejected = await vm.ApplyDiscountAsync(vm.CartItems[0], 0.1001m);
        Assert.False(tenPointZeroOneRejected);
        Assert.Equal(0.10m, vm.CartItems[0].DiscountRate); // Invariant to prior valid rate
        Assert.Equal(1, mockGate.InvocationCount);
    }

    // =========================================================================
    // ITEM 3: INVENTORY INVARIANCE ON REJECTED STOCK ADJUSTMENT
    // =========================================================================

    [Fact]
    public async Task InventoryInvariance_OnRejectedStockAdjustment_StockOnHandRemainsStrictlyUnchanged()
    {
        // Arrange
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new ConfigurableMockGate { ShouldApprove = false };
        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var product = new Product
        {
            ProductId = "adv_stock_p1",
            Barcode = "661101",
            Name = "Denso Iridium Spark Plug",
            UnitPrice = 3200.00m,
            StockOnHand = 42.50m
        };
        await _catalogService.AddProductAsync(product);

        decimal initialStock = await _catalogService.GetStockOnHandAsync(product.ProductId);
        Assert.Equal(42.50m, initialStock);

        // Act 1: Cashier attempts negative stock adjustment (-10.5m)
        bool negResult = await vm.AdjustStockAsync(product.ProductId, -10.5m, "Damaged in transit");
        Assert.False(negResult);
        Assert.Equal(initialStock, await _catalogService.GetStockOnHandAsync(product.ProductId));

        // Act 2: Cashier attempts positive stock adjustment (+25.0m)
        bool posResult = await vm.AdjustStockAsync(product.ProductId, 25.0m, "Found unrecorded carton");
        Assert.False(posResult);
        Assert.Equal(initialStock, await _catalogService.GetStockOnHandAsync(product.ProductId));

        // Act 3: 5 consecutive rejected attempts
        for (int i = 0; i < 5; i++)
        {
            await vm.AdjustStockAsync(product.ProductId, -1.0m, $"Adversarial loop attempt {i}");
        }

        // Final verification: Absolutely 0 unit drift in database
        decimal finalStock = await _catalogService.GetStockOnHandAsync(product.ProductId);
        Assert.Equal(42.50m, finalStock);
    }

    [Fact]
    public async Task InventoryInvariance_InvalidStockAdjustmentParameters_FailsSafely()
    {
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new ConfigurableMockGate { ShouldApprove = true }; // Even if gate would approve
        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var product = new Product
        {
            ProductId = "adv_stock_inv",
            Barcode = "661102",
            Name = "Valve Cap",
            UnitPrice = 50.00m,
            StockOnHand = 100m
        };
        await _catalogService.AddProductAsync(product);

        // Zero quantity adjustment
        bool zeroQty = await vm.AdjustStockAsync(product.ProductId, 0m, "Valid reason");
        Assert.False(zeroQty);
        Assert.Equal(100m, await _catalogService.GetStockOnHandAsync(product.ProductId));
        Assert.Equal(0, mockGate.InvocationCount);

        // Whitespace reason
        bool emptyReason = await vm.AdjustStockAsync(product.ProductId, -5m, "   ");
        Assert.False(emptyReason);
        Assert.Equal(100m, await _catalogService.GetStockOnHandAsync(product.ProductId));
        Assert.Equal(0, mockGate.InvocationCount);
    }

    // =========================================================================
    // ITEM 4: NAVIGATION ISOLATION (CASHIER CANNOT ACTIVATE MANAGER OR OWNER VIEWS)
    // =========================================================================

    [Fact]
    public void NavigationIsolation_CashierDirectPropertySetter_ManagerAndOwnerBlocked()
    {
        // Arrange
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);
        int viewChangedCount = 0;
        shell.ViewChanged += _ => viewChangedCount++;

        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.False(shell.IsManagerActive);
        Assert.False(shell.IsOwnerActive);

        // Act 1: Cashier attempts direct setter for ManagerOperations
        shell.CurrentViewName = ShellViewNames.ManagerOperations;

        // Assert 1: Invariant to Checkout
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.False(shell.IsManagerActive);
        Assert.Contains("Insufficient permissions", shell.StatusMessage);
        Assert.Equal(0, viewChangedCount);

        // Act 2: Cashier attempts direct setter for OwnerAdmin
        shell.CurrentViewName = ShellViewNames.OwnerAdmin;

        // Assert 2: Invariant to Checkout
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.False(shell.IsOwnerActive);
        Assert.Contains("Insufficient permissions", shell.StatusMessage);
        Assert.Equal(0, viewChangedCount);
    }

    [Fact]
    public void NavigationIsolation_CashierNavigateToMethod_ManagerAndOwnerBlocked()
    {
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);

        // Direct call to NavigateTo
        shell.NavigateTo(ShellViewNames.ManagerOperations);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.False(shell.IsManagerActive);

        shell.NavigateTo(ShellViewNames.OwnerAdmin);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.False(shell.IsOwnerActive);
    }

    [Fact]
    public void NavigationIsolation_CashierNavigateCommand_WithoutElevation_RemainsInCheckout()
    {
        var cashier = CreateUser(Role.Cashier);
        var shell = new ShellViewModel(cashier);

        // Execute NavigateCommand for ManagerOperations
        shell.NavigateCommand.Execute(ShellViewNames.ManagerOperations);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.False(shell.IsManagerActive);

        // Execute NavigateCommand for OwnerAdmin
        shell.NavigateCommand.Execute(ShellViewNames.OwnerAdmin);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.False(shell.IsOwnerActive);
    }

    [Fact]
    public async Task NavigationIsolation_CashierElevationToOwnerAdmin_RequiresOwnerRole_ManagerPinInsufficient()
    {
        // Arrange
        var cashier = CreateUser(Role.Cashier);
        var manager = CreateUser(Role.Manager, "mgr_only");
        var owner = CreateUser(Role.Owner, "owner_only");

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO users (user_id, username, display_name, role, password_hash, password_salt, is_active, created_at_utc, pin_hash, pin_salt)
                VALUES ($mId, 'mgr_only', 'Manager Only', 2, 'h', 's', 1, $dt, 'ph', 'ps'),
                       ($oId, 'owner_only', 'Owner Only', 3, 'h', 's', 1, $dt, 'ph', 'ps');";
            cmd.Parameters.AddWithValue("$mId", manager.UserId);
            cmd.Parameters.AddWithValue("$oId", owner.UserId);
            cmd.Parameters.AddWithValue("$dt", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        var prompt = new AuthorizationGateServiceTestsMockPinPrompt();
        var gate = new AuthorizationGateService(_authService, prompt, _db);
        var shell = new ShellViewModel(cashier, authorizationGateService: gate);

        // Scenario A: Gate receives manager credentials when trying to access OwnerAdmin
        prompt.Handler = (op, requiredRole) =>
        {
            Assert.Equal(Role.Owner, requiredRole); // Must strictly require Role.Owner!
            return Task.FromResult<PinPromptResult>(PinPromptResult.Succeeded(manager)); // Returns Manager user
        };

        bool navResultManager = await shell.RequestNavigateAsync(ShellViewNames.OwnerAdmin);

        // Assert: Access denied because Manager role < Owner role
        Assert.False(navResultManager);
        Assert.Equal(ShellViewNames.Checkout, shell.CurrentView);
        Assert.False(shell.IsOwnerActive);

        // Scenario B: Gate receives actual Owner credentials
        prompt.Handler = (op, requiredRole) =>
        {
            Assert.Equal(Role.Owner, requiredRole);
            return Task.FromResult<PinPromptResult>(PinPromptResult.Succeeded(owner));
        };

        bool navResultOwner = await shell.RequestNavigateAsync(ShellViewNames.OwnerAdmin);

        // Assert: Elevated access granted with proper Owner credential
        Assert.True(navResultOwner);
        Assert.Equal(ShellViewNames.OwnerAdmin, shell.CurrentView);
        Assert.True(shell.IsOwnerActive);
    }

    [Fact]
    public void NavigationIsolation_UnauthenticatedUser_AllProtectedTabsHiddenAndNavigationDenied()
    {
        var anonShell = new ShellViewModel(currentUser: null);

        Assert.False(anonShell.IsAuthenticated);
        Assert.False(anonShell.IsCashierTabVisible);
        Assert.False(anonShell.IsManagerTabVisible);
        Assert.False(anonShell.IsOwnerTabVisible);

        anonShell.NavigateTo(ShellViewNames.Checkout);
        Assert.Equal(ShellViewNames.Checkout, anonShell.CurrentView); // Initial was Checkout, but CanNavigateTo is false:
        Assert.False(anonShell.CanNavigateTo(ShellViewNames.Checkout));
        Assert.False(anonShell.CanNavigateTo(ShellViewNames.ManagerOperations));
        Assert.False(anonShell.CanNavigateTo(ShellViewNames.OwnerAdmin));
    }

    [Fact]
    public async Task DefenseInDepth_CatalogService_AdjustStockAsync_CashierDirectCall_BlockedWithException()
    {
        var cashier = CreateUser(Role.Cashier);
        var product = new Product { ProductId = "p_did1", Barcode = "9988", Name = "Direct Test", UnitPrice = 100m, StockOnHand = 50m };
        await _catalogService.AddProductAsync(product);

        // Attempt direct call without authorizer
        await Assert.ThrowsAsync<UnauthorizedActionException>(async () =>
        {
            await _catalogService.AdjustStockAsync(
                productId: product.ProductId,
                quantityChange: -10m,
                reason: "Direct cashier attempt",
                actor: cashier,
                tenantId: "TENANT_LK_01",
                branchId: "B01",
                counterId: "C01",
                authorizer: null
            );
        });

        // Verify stock on hand strictly unchanged
        Assert.Equal(50m, await _catalogService.GetStockOnHandAsync(product.ProductId));
    }

    [Fact]
    public async Task DefenseInDepth_CatalogService_AdjustStockAsync_InactiveAuthorizer_BlockedWithException()
    {
        var cashier = CreateUser(Role.Cashier);
        var inactiveManager = CreateUser(Role.Manager);
        inactiveManager.IsActive = false;

        var product = new Product { ProductId = "p_did2", Barcode = "9989", Name = "Direct Test 2", UnitPrice = 100m, StockOnHand = 30m };
        await _catalogService.AddProductAsync(product);

        await Assert.ThrowsAsync<UnauthorizedActionException>(async () =>
        {
            await _catalogService.AdjustStockAsync(
                productId: product.ProductId,
                quantityChange: -5m,
                reason: "Inactive authorizer test",
                actor: cashier,
                tenantId: "TENANT_LK_01",
                branchId: "B01",
                counterId: "C01",
                authorizer: inactiveManager
            );
        });

        Assert.Equal(30m, await _catalogService.GetStockOnHandAsync(product.ProductId));
    }

    [Fact]
    public async Task CartInvariance_OnRepeatedOverriddenItem_RejectedSecondOverrideRetainsFirstValidOverride()
    {
        var cashier = CreateUser(Role.Cashier);
        var manager = CreateUser(Role.Manager);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new ConfigurableMockGate();
        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var product = new Product { ProductId = "p_rep", Barcode = "7722", Name = "Brake Disc", UnitPrice = 5000.00m, StockOnHand = 10m };
        await _catalogService.AddProductAsync(product);
        vm.AddProductToCart(product);
        var item = vm.CartItems[0];

        // 1. First override is approved by manager -> 4200.00 LKR
        mockGate.ShouldApprove = true;
        mockGate.ApprovingUser = manager;
        bool firstOk = await vm.ApplyPriceOverrideAsync(item, 4200.00m, "First valid discount");
        Assert.True(firstOk);
        Assert.Equal(4200.00m, item.UnitPrice);
        Assert.Equal(4200.00m, item.LineTotal);

        // 2. Second override is rejected -> attempted 2000.00 LKR
        mockGate.ShouldApprove = false;
        mockGate.ApprovingUser = null;
        bool secondOk = await vm.ApplyPriceOverrideAsync(item, 2000.00m, "Second excessive cut");
        Assert.False(secondOk);

        // Assert: Retains the first authorized state (4200.00 LKR) with 0.00 LKR drift
        Assert.Equal(4200.00m, item.UnitPrice);
        Assert.Equal(4200.00m, item.LineTotal);
        Assert.Equal(manager.UserId, item.AuthorizingUserId);
        Assert.Equal("First valid discount", item.OverrideReason);
    }

    [Fact]
    public async Task SensitiveOp_IssueRefund_CashierWithoutApproval_DoesNotTriggerSaleRefund()
    {
        var cashier = CreateUser(Role.Cashier);
        var shift = CreateShift(cashier.UserId);
        var mockGate = new ConfigurableMockGate { ShouldApprove = false };
        var vm = new BillingViewModel(cashier, shift, _catalogService, _saleService, _heldCartService, mockGate);

        var fakeSaleId = Guid.NewGuid();
        var refundLines = new List<RefundLineRequest>
        {
            new(Guid.NewGuid(), 1.0m)
        };

        bool refundResult = await vm.IssueRefundAsync(fakeSaleId, refundLines, "Customer returned broken part");

        Assert.False(refundResult);
        Assert.Equal(1, mockGate.InvocationCount);
        Assert.Contains("Manager authorization required", vm.StatusMessage);
    }

    private class AuthorizationGateServiceTestsMockPinPrompt : IPinPromptService
    {
        public Func<string, Role, Task<PinPromptResult>>? Handler { get; set; }

        public async Task<PinPromptResult> PromptPinAsync(
            Role requiredRole,
            string operationName,
            string? tenantId = null,
            string? branchId = null,
            string? counterId = null)
        {
            if (Handler != null)
            {
                return await Handler(operationName, requiredRole);
            }
            return PinPromptResult.Cancelled();
        }

        public Task<PinPromptResult> PromptPinAsync(string title, string message, Role minimumRole)
            => PromptPinAsync(minimumRole, string.IsNullOrWhiteSpace(message) ? title : $"{title}: {message}");
    }
}
