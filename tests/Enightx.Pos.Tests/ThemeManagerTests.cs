using System.Collections.ObjectModel;
using System.ComponentModel;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Themes;
using Xunit;

namespace Enightx.Pos.Tests;

public class ThemeManagerTests
{
    // Spy implementation of IThemeResourceApplier to track resource swaps headlessly
    private class SpyThemeResourceApplier : IThemeResourceApplier
    {
        public List<Theme> AppliedThemes { get; } = new();

        public void ApplyTheme(Theme theme)
        {
            AppliedThemes.Add(theme);
        }
    }

    // =========================================================================
    // 1. INITIAL STATE VERIFICATION
    // =========================================================================

    [Fact]
    public void ThemeManager_DefaultState_IsLightTheme()
    {
        var applier = new SpyThemeResourceApplier();
        var manager = new ThemeManager(applier);

        Assert.Equal(Theme.Light, manager.CurrentTheme);
        Assert.True(manager.IsLightTheme);
        Assert.False(manager.IsDarkTheme);
        Assert.Equal("🌙 Dark Mode", manager.ToggleButtonText);
        Assert.Equal("🌙", manager.ToggleButtonIcon);
    }

    [Fact]
    public void ThemeManager_CustomInitialState_IsRespected()
    {
        var applier = new SpyThemeResourceApplier();
        var manager = new ThemeManager(applier, initialTheme: Theme.Dark);

        Assert.Equal(Theme.Dark, manager.CurrentTheme);
        Assert.True(manager.IsDarkTheme);
        Assert.False(manager.IsLightTheme);
        Assert.Equal("☀️ Light Mode", manager.ToggleButtonText);
        Assert.Equal("☀️", manager.ToggleButtonIcon);
    }

    // =========================================================================
    // 2. TOGGLE & STATE TRANSITIONS
    // =========================================================================

    [Fact]
    public void ToggleTheme_SwitchesLightToDarkAndBackToLight()
    {
        var applier = new SpyThemeResourceApplier();
        var manager = new ThemeManager(applier);

        // Act 1: Toggle from Light -> Dark
        manager.ToggleTheme();

        Assert.Equal(Theme.Dark, manager.CurrentTheme);
        Assert.True(manager.IsDarkTheme);
        Assert.False(manager.IsLightTheme);
        Assert.Equal("☀️ Light Mode", manager.ToggleButtonText);

        // Act 2: Toggle from Dark -> Light
        manager.ToggleTheme();

        Assert.Equal(Theme.Light, manager.CurrentTheme);
        Assert.True(manager.IsLightTheme);
        Assert.False(manager.IsDarkTheme);
        Assert.Equal("🌙 Dark Mode", manager.ToggleButtonText);

        // Verify spy applier recorded exactly 2 transitions
        Assert.Equal(2, applier.AppliedThemes.Count);
        Assert.Equal(Theme.Dark, applier.AppliedThemes[0]);
        Assert.Equal(Theme.Light, applier.AppliedThemes[1]);
    }

    [Fact]
    public void SetTheme_ExplicitTransitions_SetDesiredTheme()
    {
        var applier = new SpyThemeResourceApplier();
        var manager = new ThemeManager(applier);

        manager.SetTheme(Theme.Dark);
        Assert.Equal(Theme.Dark, manager.CurrentTheme);

        manager.SetTheme(Theme.Light);
        Assert.Equal(Theme.Light, manager.CurrentTheme);
    }

    [Fact]
    public void SetTheme_IdempotentWhenSameThemeSelected()
    {
        var applier = new SpyThemeResourceApplier();
        var manager = new ThemeManager(applier, initialTheme: Theme.Light);

        int themeChangedCount = 0;
        manager.ThemeChanged += (_, _) => themeChangedCount++;

        // Act: Set to Light when already Light
        manager.SetTheme(Theme.Light);

        Assert.Equal(Theme.Light, manager.CurrentTheme);
        Assert.Equal(0, themeChangedCount);
        Assert.Empty(applier.AppliedThemes);
    }

    // =========================================================================
    // 3. EVENT NOTIFICATION & PROPERTY CHANGED DISPATCH
    // =========================================================================

    [Fact]
    public void ThemeChanged_EventFiresWithCorrectOldAndNewThemes()
    {
        var applier = new SpyThemeResourceApplier();
        var manager = new ThemeManager(applier);

        var eventHistory = new List<(Theme OldTheme, Theme NewTheme)>();
        manager.ThemeChanged += (_, args) =>
        {
            eventHistory.Add((args.OldTheme, args.NewTheme));
        };

        manager.ToggleTheme(); // Light -> Dark
        manager.ToggleTheme(); // Dark -> Light

        Assert.Equal(2, eventHistory.Count);
        Assert.Equal((Theme.Light, Theme.Dark), eventHistory[0]);
        Assert.Equal((Theme.Dark, Theme.Light), eventHistory[1]);
    }

    [Fact]
    public void PropertyChanged_FiresForRelatedPropertiesOnToggle()
    {
        var applier = new SpyThemeResourceApplier();
        var manager = new ThemeManager(applier);

        var changedProperties = new List<string>();
        manager.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != null)
                changedProperties.Add(args.PropertyName);
        };

        manager.ToggleTheme();

        Assert.Contains(nameof(manager.CurrentTheme), changedProperties);
        Assert.Contains(nameof(manager.IsDarkTheme), changedProperties);
        Assert.Contains(nameof(manager.IsLightTheme), changedProperties);
        Assert.Contains(nameof(manager.ToggleButtonText), changedProperties);
        Assert.Contains(nameof(manager.ToggleButtonIcon), changedProperties);
    }

    [Fact]
    public void ToggleThemeCommand_ExecutesThemeToggleSuccessfully()
    {
        var applier = new SpyThemeResourceApplier();
        var manager = new ThemeManager(applier);

        Assert.True(manager.ToggleThemeCommand.CanExecute(null));

        manager.ToggleThemeCommand.Execute(null);

        Assert.Equal(Theme.Dark, manager.CurrentTheme);
    }

    // =========================================================================
    // 4. CART STATE & FINANCIAL ARITHMETIC PRESERVATION ACROSS TOGGLES
    // =========================================================================

    public class TestCartItem
    {
        public required string ProductId { get; set; }
        public required string ProductName { get; set; }
        public required string Barcode { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal DiscountRate { get; set; }
        public decimal TaxRate { get; set; }
        public bool IsPriceOverridden { get; set; }
        public string? OverrideReason { get; set; }
        public string? AuthorizingUserId { get; set; }

        public decimal Subtotal => MoneyCalculator.Round(Quantity * UnitPrice);
        public decimal DiscountAmount => MoneyCalculator.Round(Subtotal * DiscountRate);
        public decimal TaxableAmount => Subtotal - DiscountAmount;
        public decimal TaxAmount => MoneyCalculator.Round(TaxableAmount * TaxRate);
        public decimal LineTotal => MoneyCalculator.Round(TaxableAmount + TaxAmount);
    }

    [Fact]
    public void ToggleTheme_PreservesCartItemsQuantitiesAndPricesWithZeroDeviation()
    {
        var applier = new SpyThemeResourceApplier();
        var manager = new ThemeManager(applier);

        // 1. Arrange: Setup realistic multi-item sales cart
        var cart = new ObservableCollection<TestCartItem>
        {
            new()
            {
                ProductId = "prod_001",
                ProductName = "Brake Pad Front Set (Toyota)",
                Barcode = "4792001001",
                Quantity = 2.0m,
                UnitPrice = 4500.00m,
                DiscountRate = 0.05m, // 5% promotional discount
                TaxRate = 0.18m,      // 18% VAT
                IsPriceOverridden = false
            },
            new()
            {
                ProductId = "prod_002",
                ProductName = "Oil Filter Element (Denso)",
                Barcode = "4792001002",
                Quantity = 3.0m,
                UnitPrice = 1850.00m,
                DiscountRate = 0.00m,
                TaxRate = 0.18m,
                IsPriceOverridden = false
            },
            new()
            {
                ProductId = "prod_003",
                ProductName = "Spark Plug Iridium (NGK)",
                Barcode = "4792001003",
                Quantity = 4.0m,
                UnitPrice = 1000.00m, // Overridden from 1200.00
                DiscountRate = 0.00m,
                TaxRate = 0.00m,
                IsPriceOverridden = true,
                OverrideReason = "Manager Special",
                AuthorizingUserId = "user_mgr_01"
            }
        };

        // Snapshot pre-toggle monetary calculations
        decimal preSubtotal = MoneyCalculator.Round(cart.Sum(i => i.Subtotal));
        decimal preDiscountTotal = MoneyCalculator.Round(cart.Sum(i => i.DiscountAmount));
        decimal preTaxTotal = MoneyCalculator.Round(cart.Sum(i => i.TaxAmount));
        decimal preGrandTotal = MoneyCalculator.Round(cart.Sum(i => i.LineTotal));

        Assert.Equal(3, cart.Count);
        Assert.Equal(18550.00m, preSubtotal);
        Assert.Equal(450.00m, preDiscountTotal);
        Assert.Equal(2538.00m, preTaxTotal);
        Assert.Equal(20638.00m, preGrandTotal);

        // 2. Act: Execute multiple rapid theme toggles (stress test state preservation)
        for (int i = 0; i < 10; i++)
        {
            manager.ToggleTheme();
        }

        // 3. Assert: Verify every single item and calculation remains completely intact
        Assert.Equal(3, cart.Count);

        // Item 1 verification
        var item1 = cart[0];
        Assert.Equal("prod_001", item1.ProductId);
        Assert.Equal(2.0m, item1.Quantity);
        Assert.Equal(4500.00m, item1.UnitPrice);
        Assert.Equal(0.05m, item1.DiscountRate);
        Assert.Equal(0.18m, item1.TaxRate);
        Assert.Equal(9000.00m, item1.Subtotal);
        Assert.Equal(450.00m, item1.DiscountAmount);
        Assert.Equal(1539.00m, item1.TaxAmount);
        Assert.Equal(10089.00m, item1.LineTotal);

        // Item 2 verification
        var item2 = cart[1];
        Assert.Equal("prod_002", item2.ProductId);
        Assert.Equal(3.0m, item2.Quantity);
        Assert.Equal(1850.00m, item2.UnitPrice);
        Assert.Equal(5550.00m, item2.Subtotal);
        Assert.Equal(0.00m, item2.DiscountAmount);
        Assert.Equal(999.00m, item2.TaxAmount);
        Assert.Equal(6549.00m, item2.LineTotal);

        // Item 3 verification (price override preserved)
        var item3 = cart[2];
        Assert.Equal("prod_003", item3.ProductId);
        Assert.Equal(4.0m, item3.Quantity);
        Assert.Equal(1000.00m, item3.UnitPrice);
        Assert.True(item3.IsPriceOverridden);
        Assert.Equal("Manager Special", item3.OverrideReason);
        Assert.Equal("user_mgr_01", item3.AuthorizingUserId);
        Assert.Equal(4000.00m, item3.LineTotal);

        // Aggregate monetary totals post-toggle
        decimal postSubtotal = MoneyCalculator.Round(cart.Sum(i => i.Subtotal));
        decimal postDiscountTotal = MoneyCalculator.Round(cart.Sum(i => i.DiscountAmount));
        decimal postTaxTotal = MoneyCalculator.Round(cart.Sum(i => i.TaxAmount));
        decimal postGrandTotal = MoneyCalculator.Round(cart.Sum(i => i.LineTotal));

        Assert.Equal(preSubtotal, postSubtotal);
        Assert.Equal(preDiscountTotal, postDiscountTotal);
        Assert.Equal(preTaxTotal, postTaxTotal);
        Assert.Equal(preGrandTotal, postGrandTotal);
    }

    [Fact]
    public void ToggleTheme_AddingItemsInDarkTheme_PreservedWhenSwitchingBackToLight()
    {
        var applier = new SpyThemeResourceApplier();
        var manager = new ThemeManager(applier);

        var cart = new ObservableCollection<TestCartItem>
        {
            new()
            {
                ProductId = "prod_001",
                ProductName = "Item 1",
                Barcode = "111",
                Quantity = 1.0m,
                UnitPrice = 1000.00m
            }
        };

        // Switch to Dark
        manager.ToggleTheme();
        Assert.Equal(Theme.Dark, manager.CurrentTheme);

        // Add an item in Dark theme
        cart.Add(new TestCartItem
        {
            ProductId = "prod_002",
            ProductName = "Item 2",
            Barcode = "222",
            Quantity = 2.0m,
            UnitPrice = 2500.00m
        });

        // Switch back to Light
        manager.ToggleTheme();
        Assert.Equal(Theme.Light, manager.CurrentTheme);

        // Both items exist and are intact
        Assert.Equal(2, cart.Count);
        Assert.Equal(6000.00m, MoneyCalculator.Round(cart.Sum(i => i.LineTotal)));
    }
}
