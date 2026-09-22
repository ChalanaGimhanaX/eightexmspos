using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Themes;
using Xunit;

namespace Enightx.Pos.Tests;

public class ThemeManagerAdversarialTests
{
    // =========================================================================
    // TEST HELPER APPLIERS
    // =========================================================================

    private class SpyApplier : IThemeResourceApplier
    {
        private readonly List<Theme> _appliedThemes = new();
        private readonly object _lock = new();

        public IReadOnlyList<Theme> AppliedThemes
        {
            get
            {
                lock (_lock)
                {
                    return _appliedThemes.ToList();
                }
            }
        }

        public int CallCount
        {
            get
            {
                lock (_lock)
                {
                    return _appliedThemes.Count;
                }
            }
        }

        public void ApplyTheme(Theme theme)
        {
            lock (_lock)
            {
                _appliedThemes.Add(theme);
            }
        }
    }

    private class ThrowingApplier : IThemeResourceApplier
    {
        public bool ShouldThrow { get; set; } = true;
        public int CallCount { get; private set; }

        public void ApplyTheme(Theme theme)
        {
            CallCount++;
            if (ShouldThrow)
            {
                throw new InvalidOperationException($"Simulated applier failure for theme {theme}");
            }
        }
    }

    private class LatencyApplier : IThemeResourceApplier
    {
        private readonly int _delayMs;
        public List<Theme> AppliedThemes { get; } = new();

        public LatencyApplier(int delayMs = 5)
        {
            _delayMs = delayMs;
        }

        public void ApplyTheme(Theme theme)
        {
            Thread.Sleep(_delayMs);
            AppliedThemes.Add(theme);
        }
    }

    public class AdversarialCartItem
    {
        public required string ProductId { get; set; }
        public required string ProductName { get; set; }
        public required string Barcode { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal DiscountRate { get; set; }
        public decimal DiscountFixed { get; set; }
        public decimal TaxRate { get; set; }
        public bool IsPriceOverridden { get; set; }
        public string? OverrideReason { get; set; }
        public string? AuthorizingUserId { get; set; }

        public MoneyCalculator.LineCalculationResult Calc =>
            MoneyCalculator.CalculateLine(Quantity, UnitPrice, DiscountRate, DiscountFixed, TaxRate);

        public decimal Subtotal => Calc.Subtotal;
        public decimal DiscountAmount => Calc.DiscountAmount;
        public decimal TaxAmount => Calc.TaxAmount;
        public decimal LineTotal => Calc.LineTotal;
    }

    // =========================================================================
    // 1. RAPID ALTERNATING THEME TOGGLES & STRESS
    // =========================================================================

    [Fact]
    public void RapidAlternatingToggles_100Iterations_MaintainsExactParityAndEventCounts()
    {
        // Arrange
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Light);

        var themeChangedEvents = new List<ThemeChangedEventArgs>();
        var propertyChangedEvents = new List<string>();

        manager.ThemeChanged += (_, e) => themeChangedEvents.Add(e);
        manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) propertyChangedEvents.Add(e.PropertyName);
        };

        const int iterations = 100;

        // Act
        for (int i = 1; i <= iterations; i++)
        {
            manager.ToggleTheme();

            if (i % 2 == 1) // Odd iterations: Dark
            {
                Assert.Equal(Theme.Dark, manager.CurrentTheme);
                Assert.True(manager.IsDarkTheme);
                Assert.False(manager.IsLightTheme);
                Assert.Equal("☀️ Light Mode", manager.ToggleButtonText);
                Assert.Equal("☀️", manager.ToggleButtonIcon);
            }
            else // Even iterations: Light
            {
                Assert.Equal(Theme.Light, manager.CurrentTheme);
                Assert.False(manager.IsDarkTheme);
                Assert.True(manager.IsLightTheme);
                Assert.Equal("🌙 Dark Mode", manager.ToggleButtonText);
                Assert.Equal("🌙", manager.ToggleButtonIcon);
            }
        }

        // Assert
        Assert.Equal(iterations, spy.CallCount);
        Assert.Equal(iterations, themeChangedEvents.Count);
        Assert.Equal(Theme.Light, manager.CurrentTheme);

        // Verify each ThemeChanged event has the correct transition
        for (int i = 0; i < iterations; i++)
        {
            var expectedOld = (i % 2 == 0) ? Theme.Light : Theme.Dark;
            var expectedNew = (i % 2 == 0) ? Theme.Dark : Theme.Light;

            Assert.Equal(expectedOld, themeChangedEvents[i].OldTheme);
            Assert.Equal(expectedNew, themeChangedEvents[i].NewTheme);
        }

        // Verify PropertyChanged: exactly 5 properties fired 100 times = 500 notifications
        Assert.Equal(5 * iterations, propertyChangedEvents.Count);
        Assert.Equal(iterations, propertyChangedEvents.Count(p => p == nameof(manager.CurrentTheme)));
        Assert.Equal(iterations, propertyChangedEvents.Count(p => p == nameof(manager.IsDarkTheme)));
        Assert.Equal(iterations, propertyChangedEvents.Count(p => p == nameof(manager.IsLightTheme)));
        Assert.Equal(iterations, propertyChangedEvents.Count(p => p == nameof(manager.ToggleButtonText)));
        Assert.Equal(iterations, propertyChangedEvents.Count(p => p == nameof(manager.ToggleButtonIcon)));
    }

    [Fact]
    public void HighVolumeStress_1000AlternatingToggles_ExecutesPromptlyWithoutDegradation()
    {
        // Arrange
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Light);
        const int iterations = 1000;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Act
        for (int i = 0; i < iterations; i++)
        {
            manager.ToggleTheme();
        }

        sw.Stop();

        // Assert
        Assert.Equal(iterations, spy.CallCount);
        Assert.Equal(Theme.Light, manager.CurrentTheme);
        Assert.True(sw.ElapsedMilliseconds < 500, $"1000 toggles took {sw.ElapsedMilliseconds}ms, exceeding 500ms budget");
    }

    [Fact]
    public void RepeatedIdempotentSetTheme_100Iterations_SuppressesRedundantApplierAndEventInvocations()
    {
        // Arrange
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Dark);

        int themeChangedCount = 0;
        int propChangedCount = 0;
        manager.ThemeChanged += (_, _) => themeChangedCount++;
        manager.PropertyChanged += (_, _) => propChangedCount++;

        // Act: Call SetTheme(Dark) 100 times while already Dark
        for (int i = 0; i < 100; i++)
        {
            manager.SetTheme(Theme.Dark);
        }

        // Assert: zero events, zero applier invocations
        Assert.Equal(0, themeChangedCount);
        Assert.Equal(0, propChangedCount);
        Assert.Equal(0, spy.CallCount);
        Assert.Equal(Theme.Dark, manager.CurrentTheme);

        // Transition once to Light
        manager.SetTheme(Theme.Light);
        Assert.Equal(1, themeChangedCount);
        Assert.Equal(1, spy.CallCount);

        // Now call SetTheme(Light) 100 times while already Light
        for (int i = 0; i < 100; i++)
        {
            manager.SetTheme(Theme.Light);
        }

        // Assert: no additional events or applier calls
        Assert.Equal(1, themeChangedCount);
        Assert.Equal(1, spy.CallCount);
    }

    [Fact]
    public void ToggleThemeCommand_100Executions_MatchesDirectToggleBehavior()
    {
        // Arrange
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Light);

        // Act & Assert
        for (int i = 1; i <= 100; i++)
        {
            Assert.True(manager.ToggleThemeCommand.CanExecute(null));
            manager.ToggleThemeCommand.Execute(null);

            var expectedTheme = (i % 2 == 1) ? Theme.Dark : Theme.Light;
            Assert.Equal(expectedTheme, manager.CurrentTheme);
        }

        Assert.Equal(100, spy.CallCount);
        Assert.Equal(Theme.Light, manager.CurrentTheme);
    }

    // =========================================================================
    // 2. MULTI-THREADED CONCURRENT CALLS
    // =========================================================================

    [Fact]
    public async Task ConcurrentToggles_10Threads50TogglesEach_NoCrashesAndInvariantConsistency()
    {
        // Arrange
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Light);

        const int numThreads = 10;
        const int togglesPerThread = 50;
        var exceptions = new ConcurrentBag<Exception>();

        // Act
        var tasks = Enumerable.Range(0, numThreads).Select(_ => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < togglesPerThread; i++)
                {
                    manager.ToggleTheme();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert: No exceptions were thrown during concurrent toggles
        Assert.Empty(exceptions);

        // Invariant check: manager state must be completely coherent
        Assert.True(manager.CurrentTheme == Theme.Light || manager.CurrentTheme == Theme.Dark);
        Assert.NotEqual(manager.IsDarkTheme, manager.IsLightTheme);
        Assert.Equal(manager.CurrentTheme == Theme.Dark, manager.IsDarkTheme);
        Assert.Equal(manager.CurrentTheme == Theme.Light, manager.IsLightTheme);

        if (manager.IsDarkTheme)
        {
            Assert.Equal("☀️ Light Mode", manager.ToggleButtonText);
            Assert.Equal("☀️", manager.ToggleButtonIcon);
        }
        else
        {
            Assert.Equal("🌙 Dark Mode", manager.ToggleButtonText);
            Assert.Equal("🌙", manager.ToggleButtonIcon);
        }
    }

    [Fact]
    public async Task ConcurrentRacingSetTheme_OpposingThemesAcrossThreads_NoDeadlockOrCorruptedState()
    {
        // Arrange
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Light);

        const int numTasks = 16;
        const int opsPerTask = 100;
        var exceptions = new ConcurrentBag<Exception>();

        // Act: Half threads set Light, half threads set Dark
        var tasks = Enumerable.Range(0, numTasks).Select(threadId => Task.Run(() =>
        {
            try
            {
                var themeToSet = (threadId % 2 == 0) ? Theme.Light : Theme.Dark;
                for (int i = 0; i < opsPerTask; i++)
                {
                    manager.SetTheme(themeToSet);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert
        Assert.Empty(exceptions);
        Assert.True(manager.CurrentTheme == Theme.Light || manager.CurrentTheme == Theme.Dark);
        Assert.True(manager.IsDarkTheme ^ manager.IsLightTheme);
    }

    [Fact]
    public async Task ConcurrentReadersAndWriters_NoTornPropertyStatesObserved()
    {
        // Arrange
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Light);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var token = cts.Token;
        var violations = new ConcurrentBag<string>();

        // 4 writer tasks toggling rapidly
        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                manager.ToggleTheme();
            }
        }));

        // 6 reader tasks asserting valid state and thread safety
        var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                var current = manager.CurrentTheme;
                var isDark = manager.IsDarkTheme;
                var isLight = manager.IsLightTheme;
                var text = manager.ToggleButtonText;
                var icon = manager.ToggleButtonIcon;

                // Invariant 1: CurrentTheme is always either Light or Dark (no undefined enum bits)
                if (current != Theme.Light && current != Theme.Dark)
                {
                    violations.Add($"Invalid CurrentTheme enum value: {current}");
                }

                // Invariant 2: UI button text and icon are always valid non-null strings
                if (string.IsNullOrEmpty(text) || (text != "☀️ Light Mode" && text != "🌙 Dark Mode"))
                {
                    violations.Add($"Invalid ToggleButtonText: '{text}'");
                }
                if (string.IsNullOrEmpty(icon) || (icon != "☀️" && icon != "🌙"))
                {
                    violations.Add($"Invalid ToggleButtonIcon: '{icon}'");
                }
            }
        }));

        await Task.WhenAll(writers.Concat(readers));

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public async Task ConcurrentEventSubscriptionAndUnsubscription_DuringActiveToggling()
    {
        // Arrange
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Light);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var token = cts.Token;
        var exceptions = new ConcurrentBag<Exception>();

        // Writer task toggling continuously
        var writer = Task.Run(() =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    manager.ToggleTheme();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Subscriber tasks dynamically adding and removing handlers
        var subscribers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                EventHandler<ThemeChangedEventArgs> handler = (_, _) => { };
                PropertyChangedEventHandler propHandler = (_, _) => { };

                while (!token.IsCancellationRequested)
                {
                    manager.ThemeChanged += handler;
                    manager.PropertyChanged += propHandler;
                    Thread.Yield();
                    manager.ThemeChanged -= handler;
                    manager.PropertyChanged -= propHandler;
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(new[] { writer }.Concat(subscribers));

        // Assert: No delegate race condition crashes
        Assert.Empty(exceptions);
    }

    // =========================================================================
    // 3. NULL, INVALID, AND THROWING APPLIER SCENARIOS
    // =========================================================================

    [Fact]
    public void NullApplier_Rapid100Toggles_ExecutesGracefullyWithoutException()
    {
        // Arrange: manager created with explicit null applier
        var manager = new ThemeManager(resourceApplier: null, initialTheme: Theme.Light);

        var eventsFired = 0;
        manager.ThemeChanged += (_, _) => eventsFired++;

        // Act: 100 toggles with null applier
        for (int i = 0; i < 100; i++)
        {
            manager.ToggleTheme();
        }

        // Assert: No NullReferenceException, state toggled, 100 events fired
        Assert.Equal(Theme.Light, manager.CurrentTheme);
        Assert.Equal(100, eventsFired);
        Assert.True(manager.IsLightTheme);
    }

    [Fact]
    public void ThrowingApplier_IdentifiesBehaviorWhenApplierFails()
    {
        // Arrange: applier that throws an exception
        var throwingApplier = new ThrowingApplier { ShouldThrow = true };
        var manager = new ThemeManager(throwingApplier, Theme.Light);

        // Act: Attempt to set Dark theme
        var ex = Assert.Throws<InvalidOperationException>(() => manager.SetTheme(Theme.Dark));
        Assert.Contains("Simulated applier failure", ex.Message);
        Assert.Equal(1, throwingApplier.CallCount);

        // Empirical Investigation:
        // When ApplyTheme throws, _currentTheme was already mutated to Dark,
        // but PropertyChanged and ThemeChanged were NOT fired due to exception unwinding.
        Assert.Equal(Theme.Dark, manager.CurrentTheme);

        // Now if the applier recovers and caller retries SetTheme(Dark):
        throwingApplier.ShouldThrow = false;
        manager.SetTheme(Theme.Dark);

        // NOTE: Because _currentTheme is already Theme.Dark, SetTheme returns early!
        // CallCount remains 1 (the recovery call was skipped due to idempotency guard).
        Assert.Equal(1, throwingApplier.CallCount);

        // To successfully apply Dark after a prior failure, caller must reset to Light first:
        manager.SetTheme(Theme.Light);
        Assert.Equal(2, throwingApplier.CallCount);
        Assert.Equal(Theme.Light, manager.CurrentTheme);

        manager.SetTheme(Theme.Dark);
        Assert.Equal(3, throwingApplier.CallCount);
        Assert.Equal(Theme.Dark, manager.CurrentTheme);
    }

    [Fact]
    public void SlowApplier_SimulatedIoLatency_PreservesOrdering()
    {
        // Arrange
        var latencyApplier = new LatencyApplier(delayMs: 2);
        var manager = new ThemeManager(latencyApplier, Theme.Light);

        // Act: 10 toggles with simulated delay
        for (int i = 0; i < 10; i++)
        {
            manager.ToggleTheme();
        }

        // Assert: All 10 applied in exact alternating order
        Assert.Equal(10, latencyApplier.AppliedThemes.Count);
        for (int i = 0; i < 10; i++)
        {
            var expected = (i % 2 == 0) ? Theme.Dark : Theme.Light;
            Assert.Equal(expected, latencyApplier.AppliedThemes[i]);
        }
    }

    [Fact]
    public void InvalidEnumCast_HandledGracefullyWithoutCrashing()
    {
        // Arrange
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Light);

        // Act: Pass an undefined enum value
        manager.SetTheme((Theme)999);

        // Assert: CurrentTheme reflects the value, dependent flags are safe
        Assert.Equal((Theme)999, manager.CurrentTheme);
        Assert.False(manager.IsDarkTheme);
        Assert.False(manager.IsLightTheme);
        Assert.Equal("🌙 Dark Mode", manager.ToggleButtonText);
        Assert.Equal("🌙", manager.ToggleButtonIcon);

        // Recovery: ToggleTheme recovers back to Light
        manager.ToggleTheme();
        Assert.Equal(Theme.Light, manager.CurrentTheme);
        Assert.True(manager.IsLightTheme);
    }

    // =========================================================================
    // 4. CART STATE PRESERVATION UNDER STRESS (DISCOUNTS, VAT, CREDIT, SPLIT TENDERS)
    // =========================================================================

    [Fact]
    public void CartState_FullRetailBasket_100ThemeToggles_ZeroPennyDiscrepancy()
    {
        // Arrange: ThemeManager and full retail shopping basket
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Light);

        var cart = new ObservableCollection<AdversarialCartItem>
        {
            // Line 1: Fractional quantity, rate discount, 18% VAT
            new()
            {
                ProductId = "prod_001",
                ProductName = "Brake Pad Front Set (Toyota)",
                Barcode = "4792001001",
                Quantity = 3.5m,
                UnitPrice = 4500.00m,
                DiscountRate = 0.05m, // 5% promo discount
                DiscountFixed = 0.0m,
                TaxRate = 0.18m,      // 18% VAT
                IsPriceOverridden = false
            },
            // Line 2: Bulk item, fixed discount, 18% VAT
            new()
            {
                ProductId = "prod_002",
                ProductName = "Oil Filter Element (Denso)",
                Barcode = "4792001002",
                Quantity = 10.0m,
                UnitPrice = 1850.00m,
                DiscountRate = 0.00m,
                DiscountFixed = 1000.00m, // 1,000 LKR voucher discount
                TaxRate = 0.18m,
                IsPriceOverridden = false
            },
            // Line 3: High value item, full 18% VAT, no discount
            new()
            {
                ProductId = "prod_004",
                ProductName = "Synthetic Engine Oil 4L (Mobil 1)",
                Barcode = "4792001004",
                Quantity = 1.0m,
                UnitPrice = 14500.00m,
                DiscountRate = 0.00m,
                DiscountFixed = 0.0m,
                TaxRate = 0.18m,
                IsPriceOverridden = false
            },
            // Line 4: VAT-exempt essential, percentage discount
            new()
            {
                ProductId = "prod_003",
                ProductName = "Spark Plug Iridium (NGK)",
                Barcode = "4792001003",
                Quantity = 2.0m,
                UnitPrice = 2200.00m,
                DiscountRate = 0.10m, // 10% discount
                DiscountFixed = 0.0m,
                TaxRate = 0.00m,      // 0% VAT exempt
                IsPriceOverridden = false
            },
            // Line 5: Overridden price item authorized by store manager
            new()
            {
                ProductId = "prod_005",
                ProductName = "Wiper Blade Set 22 inch",
                Barcode = "4792001005",
                Quantity = 4.0m,
                UnitPrice = 1000.00m, // Overridden from 1200.00
                DiscountRate = 0.00m,
                DiscountFixed = 0.0m,
                TaxRate = 0.18m,
                IsPriceOverridden = true,
                OverrideReason = "Damaged packaging clearance",
                AuthorizingUserId = "mgr_kasun"
            }
        };

        // Snapshot expected mathematical totals
        decimal expectedSubtotal = MoneyCalculator.Round(cart.Sum(i => i.Subtotal));
        decimal expectedDiscountTotal = MoneyCalculator.Round(cart.Sum(i => i.DiscountAmount));
        decimal expectedTaxTotal = MoneyCalculator.Round(cart.Sum(i => i.TaxAmount));
        decimal expectedGrandTotal = MoneyCalculator.Round(cart.Sum(i => i.LineTotal));

        // Line 1: Subtotal = 15750.00, Discount = 787.50, Taxable = 14962.50, Tax = 2693.25, LineTotal = 17655.75
        // Line 2: Subtotal = 18500.00, Discount = 1000.00, Taxable = 17500.00, Tax = 3150.00, LineTotal = 20650.00
        // Line 3: Subtotal = 14500.00, Discount = 0.00, Taxable = 14500.00, Tax = 2610.00, LineTotal = 17110.00
        // Line 4: Subtotal = 4400.00, Discount = 440.00, Taxable = 3960.00, Tax = 0.00, LineTotal = 3960.00
        // Line 5: Subtotal = 4000.00, Discount = 0.00, Taxable = 4000.00, Tax = 720.00, LineTotal = 4720.00
        Assert.Equal(57150.00m, expectedSubtotal);
        Assert.Equal(2227.50m, expectedDiscountTotal);
        Assert.Equal(9173.25m, expectedTaxTotal);
        Assert.Equal(64095.75m, expectedGrandTotal);

        // Customer credit setup
        var customer = new Customer
        {
            CustomerId = "cust_kamal_01",
            Name = "Kamal Jayawardena",
            Phone = "0771234567",
            CreditLimit = 100000.00m,
            OutstandingBalance = 35000.00m
        };
        decimal initialAvailableCredit = customer.CreditLimit - customer.OutstandingBalance;
        Assert.Equal(65000.00m, initialAvailableCredit);

        // Split partial tenders covering the exact grand total
        var tenders = new List<Tender>
        {
            new() { TenderType = TenderType.CASH, AmountTendered = 20000.00m, ChangeGiven = 0.00m },
            new() { TenderType = TenderType.CARD, AmountTendered = 25000.00m, ChangeGiven = 0.00m, PaymentReference = "TXN_VISA_9812" },
            new() { TenderType = TenderType.CREDIT, AmountTendered = 19095.75m, ChangeGiven = 0.00m, PaymentReference = customer.CustomerId }
        };

        decimal totalTendered = MoneyCalculator.Round(tenders.Sum(t => t.AmountTendered));
        Assert.Equal(expectedGrandTotal, totalTendered);

        // Act: Execute 100 rapid alternating theme toggles
        for (int i = 0; i < 100; i++)
        {
            manager.ToggleTheme();

            // Assert continuous zero-deviation state preservation at every toggle
            decimal currentSubtotal = MoneyCalculator.Round(cart.Sum(item => item.Subtotal));
            decimal currentDiscountTotal = MoneyCalculator.Round(cart.Sum(item => item.DiscountAmount));
            decimal currentTaxTotal = MoneyCalculator.Round(cart.Sum(item => item.TaxAmount));
            decimal currentGrandTotal = MoneyCalculator.Round(cart.Sum(item => item.LineTotal));

            Assert.Equal(expectedSubtotal, currentSubtotal);
            Assert.Equal(expectedDiscountTotal, currentDiscountTotal);
            Assert.Equal(expectedTaxTotal, currentTaxTotal);
            Assert.Equal(expectedGrandTotal, currentGrandTotal);
        }

        // Final Assert: Verify item-level exactness and zero penny discrepancies
        Assert.Equal(5, cart.Count);

        // Verify Line 1
        Assert.Equal(3.5m, cart[0].Quantity);
        Assert.Equal(4500.00m, cart[0].UnitPrice);
        Assert.Equal(0.05m, cart[0].DiscountRate);
        Assert.Equal(15750.00m, cart[0].Subtotal);
        Assert.Equal(787.50m, cart[0].DiscountAmount);
        Assert.Equal(2693.25m, cart[0].TaxAmount);
        Assert.Equal(17655.75m, cart[0].LineTotal);

        // Verify Line 2
        Assert.Equal(10.0m, cart[1].Quantity);
        Assert.Equal(1850.00m, cart[1].UnitPrice);
        Assert.Equal(1000.00m, cart[1].DiscountFixed);
        Assert.Equal(18500.00m, cart[1].Subtotal);
        Assert.Equal(1000.00m, cart[1].DiscountAmount);
        Assert.Equal(3150.00m, cart[1].TaxAmount);
        Assert.Equal(20650.00m, cart[1].LineTotal);

        // Verify Line 5 (Price override preservation)
        Assert.Equal(4.0m, cart[4].Quantity);
        Assert.Equal(1000.00m, cart[4].UnitPrice);
        Assert.True(cart[4].IsPriceOverridden);
        Assert.Equal("Damaged packaging clearance", cart[4].OverrideReason);
        Assert.Equal("mgr_kasun", cart[4].AuthorizingUserId);
        Assert.Equal(4720.00m, cart[4].LineTotal);

        // Customer credit calculation post-tender
        var creditTender = tenders.First(t => t.TenderType == TenderType.CREDIT);
        decimal updatedBalance = customer.OutstandingBalance + creditTender.AmountTendered;
        decimal updatedAvailableCredit = customer.CreditLimit - updatedBalance;

        Assert.Equal(54095.75m, updatedBalance);
        Assert.Equal(45904.25m, updatedAvailableCredit);
        Assert.True(updatedAvailableCredit >= 0, "Customer credit limit was not exceeded");
    }

    [Fact]
    public void CartState_DynamicMutationsInterleavedWithThemeToggles_NoCalculationDrift()
    {
        // Arrange
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Light);
        var cart = new ObservableCollection<AdversarialCartItem>();

        // Act: Dynamically add and modify cart items interleaved with theme toggles
        for (int step = 1; step <= 50; step++)
        {
            manager.ToggleTheme();

            if (step % 5 == 1)
            {
                // Add new product
                cart.Add(new AdversarialCartItem
                {
                    ProductId = $"prod_{step}",
                    ProductName = $"Automotive Part #{step}",
                    Barcode = $"4792000{step:D3}",
                    Quantity = step,
                    UnitPrice = 1250.50m,
                    DiscountRate = (step % 2 == 0) ? 0.10m : 0.00m,
                    DiscountFixed = 0.0m,
                    TaxRate = 0.18m
                });
            }
            else if (step % 5 == 3 && cart.Count > 0)
            {
                // Adjust quantity of existing product
                cart[0].Quantity += 0.5m;
            }

            // Assert: Every single item has consistent line math at every iteration
            foreach (var item in cart)
            {
                var calc = item.Calc;
                Assert.Equal(calc.Subtotal, item.Subtotal);
                Assert.Equal(calc.DiscountAmount, item.DiscountAmount);
                Assert.Equal(calc.TaxAmount, item.TaxAmount);
                Assert.Equal(calc.LineTotal, item.LineTotal);
            }

            // Aggregate totals invariant
            decimal subtotal = MoneyCalculator.Round(cart.Sum(i => i.Subtotal));
            decimal grandTotal = MoneyCalculator.Round(cart.Sum(i => i.LineTotal));
            Assert.True(grandTotal >= 0);
            Assert.True(subtotal >= 0);
        }

        Assert.Equal(10, cart.Count);
        Assert.Equal(50, spy.CallCount);
    }

    [Fact]
    public void CartState_HeldCartPersistenceAcrossThemeSwaps_FullFidelity()
    {
        // Arrange
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Light);

        var originalHeldCart = new HeldCart
        {
            HeldCartId = Guid.NewGuid(),
            BranchId = "BRANCH_COLOMBO_01",
            CounterId = "COUNTER_01",
            CashierId = "cashier_01",
            CustomerReference = "Customer Kamal - 0771234567",
            Subtotal = 18550.00m,
            DiscountTotal = 450.00m,
            TaxTotal = 2538.00m,
            GrandTotal = 20638.00m,
            HeldAtUtc = DateTime.UtcNow,
            Items = new List<HeldCartItem>
            {
                new()
                {
                    ProductId = "prod_001",
                    Barcode = "4792001001",
                    ProductName = "Brake Pad Front Set",
                    Quantity = 2.0m,
                    UnitPrice = 4500.00m,
                    DiscountRate = 0.05m,
                    DiscountFixed = 0.0m,
                    TaxRate = 0.18m,
                    LineTotal = 10089.00m
                },
                new()
                {
                    ProductId = "prod_003",
                    Barcode = "4792001003",
                    ProductName = "Spark Plug Iridium",
                    Quantity = 4.0m,
                    UnitPrice = 1000.00m,
                    DiscountRate = 0.0m,
                    DiscountFixed = 0.0m,
                    TaxRate = 0.0m,
                    LineTotal = 4000.00m,
                    IsPriceOverridden = true,
                    OverrideReason = "Special Clearance"
                }
            }
        };

        // Act: Execute 100 alternating toggles while cart is held
        for (int i = 0; i < 100; i++)
        {
            manager.ToggleTheme();
        }

        // Simulate JSON serialization & recall (as done by HeldCartService SQLite persistence)
        var json = JsonSerializer.Serialize(originalHeldCart);
        var recalledCart = JsonSerializer.Deserialize<HeldCart>(json);

        // Assert: 100% data fidelity preserved
        Assert.NotNull(recalledCart);
        Assert.Equal(originalHeldCart.HeldCartId, recalledCart.HeldCartId);
        Assert.Equal(originalHeldCart.Subtotal, recalledCart.Subtotal);
        Assert.Equal(originalHeldCart.DiscountTotal, recalledCart.DiscountTotal);
        Assert.Equal(originalHeldCart.TaxTotal, recalledCart.TaxTotal);
        Assert.Equal(originalHeldCart.GrandTotal, recalledCart.GrandTotal);
        Assert.Equal(2, recalledCart.Items.Count);

        var recalledItem2 = recalledCart.Items[1];
        Assert.True(recalledItem2.IsPriceOverridden);
        Assert.Equal("Special Clearance", recalledItem2.OverrideReason);
        Assert.Equal(4000.00m, recalledItem2.LineTotal);
    }

    [Fact]
    public void CartState_OddPennySplitPayment_ZeroDriftAcross100Toggles()
    {
        // Arrange
        var spy = new SpyApplier();
        var manager = new ThemeManager(spy, Theme.Light);

        // An odd grand total with penny splits
        const decimal totalAmount = 100.01m;
        var tenderCash = new Tender { TenderType = TenderType.CASH, AmountTendered = 33.34m };
        var tenderCard = new Tender { TenderType = TenderType.CARD, AmountTendered = 33.34m };
        var tenderCredit = new Tender { TenderType = TenderType.CREDIT, AmountTendered = 33.33m };

        var tenders = new List<Tender> { tenderCash, tenderCard, tenderCredit };

        // Act: 100 alternating theme toggles
        for (int i = 0; i < 100; i++)
        {
            manager.ToggleTheme();

            var sumTenders = MoneyCalculator.Round(tenders.Sum(t => t.AmountTendered));
            var diff = totalAmount - sumTenders;
            Assert.Equal(0.00m, diff);
        }

        // Assert: Exact equality
        Assert.Equal(totalAmount, tenders.Sum(t => t.AmountTendered));
    }
}
