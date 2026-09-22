using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Enightx.Pos.Domain;
using Enightx.Pos.ViewModels;
using Xunit;

namespace Enightx.Pos.Tests;

public class M7XamlAdversarialTests
{
    private static readonly XNamespace PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private readonly string _wpfDir;
    private readonly string _mainWindowXaml;
    private readonly string _billingViewXaml;
    private readonly string _managerOpsXaml;
    private readonly string _ownerAdminXaml;
    private readonly string _stylesXaml;

    public M7XamlAdversarialTests()
    {
        _wpfDir = LocateWpfDirectory();
        _mainWindowXaml = Path.Combine(_wpfDir, "MainWindow.xaml");
        _billingViewXaml = Path.Combine(_wpfDir, "Views", "BillingView.xaml");
        _managerOpsXaml = Path.Combine(_wpfDir, "Views", "ManagerOperationsView.xaml");
        _ownerAdminXaml = Path.Combine(_wpfDir, "Views", "OwnerAdminView.xaml");
        _stylesXaml = Path.Combine(_wpfDir, "Themes", "Styles.xaml");
    }

    private static string LocateWpfDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "apps", "desktop", "src", "Enightx.Pos.Wpf");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        var fallback = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "apps", "desktop", "src", "Enightx.Pos.Wpf"));
        if (Directory.Exists(fallback))
        {
            return fallback;
        }

        throw new DirectoryNotFoundException("Could not locate apps/desktop/src/Enightx.Pos.Wpf directory.");
    }

    private static string? GetKey(XElement elem)
    {
        var keyAttr = elem.Attribute(XNs + "Key");
        if (keyAttr != null) return keyAttr.Value;
        return elem.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
    }

    private Dictionary<string, (double? minHeight, double? height, double? minWidth, double? width)> LoadStyleDimensions()
    {
        var styles = new Dictionary<string, (double? minHeight, double? height, double? minWidth, double? width)>();
        var doc = XDocument.Load(_stylesXaml);
        foreach (var style in doc.Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            var key = GetKey(style);
            if (!string.IsNullOrEmpty(key))
            {
                var minH = GetSetterDouble(style, "MinHeight");
                var h = GetSetterDouble(style, "Height");
                var minW = GetSetterDouble(style, "MinWidth");
                var w = GetSetterDouble(style, "Width");
                styles[key] = (minH, h, minW, w);
            }
        }

        // Also load inline styles in MainWindow.xaml
        var mainDoc = XDocument.Load(_mainWindowXaml);
        foreach (var style in mainDoc.Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            var key = GetKey(style);
            if (!string.IsNullOrEmpty(key))
            {
                var minH = GetSetterDouble(style, "MinHeight");
                var h = GetSetterDouble(style, "Height");
                var minW = GetSetterDouble(style, "MinWidth");
                var w = GetSetterDouble(style, "Width");

                // Check BasedOn
                var basedOn = style.Attribute("BasedOn")?.Value;
                if (!string.IsNullOrEmpty(basedOn))
                {
                    var match = Regex.Match(basedOn, @"\{(?:StaticResource|DynamicResource)\s+([^\}]+)\}");
                    if (match.Success && styles.TryGetValue(match.Groups[1].Value.Trim(), out var baseDims))
                    {
                        minH ??= baseDims.minHeight;
                        h ??= baseDims.height;
                        minW ??= baseDims.minWidth;
                        w ??= baseDims.width;
                    }
                }
                styles[key] = (minH, h, minW, w);
            }
        }

        return styles;
    }

    private static double? GetSetterDouble(XElement styleElem, string propName)
    {
        var setter = styleElem.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == propName);
        if (setter == null) return null;
        var val = setter.Attribute("Value")?.Value;
        if (double.TryParse(val, out var d)) return d;
        return null;
    }

    private (double effectiveHeight, double effectiveWidth) ResolveButtonDimensions(
        XElement button,
        Dictionary<string, (double? minHeight, double? height, double? minWidth, double? width)> styles)
    {
        double? elemMinH = double.TryParse(button.Attribute("MinHeight")?.Value, out var mh) ? mh : null;
        double? elemH = double.TryParse(button.Attribute("Height")?.Value, out var h) ? h : null;
        double? elemMinW = double.TryParse(button.Attribute("MinWidth")?.Value, out var mw) ? mw : null;
        double? elemW = double.TryParse(button.Attribute("Width")?.Value, out var w) ? w : null;

        var styleAttr = button.Attribute("Style")?.Value;
        if (!string.IsNullOrEmpty(styleAttr))
        {
            var match = Regex.Match(styleAttr, @"\{(?:StaticResource|DynamicResource)\s+([^\}]+)\}");
            if (match.Success && styles.TryGetValue(match.Groups[1].Value.Trim(), out var sDims))
            {
                elemMinH ??= sDims.minHeight;
                elemH ??= sDims.height;
                elemMinW ??= sDims.minWidth;
                elemW ??= sDims.width;
            }
        }

        double effectiveHeight = Math.Max(elemMinH ?? 0, elemH ?? 0);
        double effectiveWidth = Math.Max(elemMinW ?? 0, elemW ?? 0);

        return (effectiveHeight, effectiveWidth);
    }

    // =========================================================================
    // 1. TOUCH TARGET SIZING ENFORCEMENT (>= 48px)
    // =========================================================================

    [Fact]
    public void M7Views_AllButtons_AuditTouchTargetEnforcement()
    {
        var styles = LoadStyleDimensions();
        var filesToAudit = new[]
        {
            ("MainWindow.xaml", _mainWindowXaml),
            ("BillingView.xaml", _billingViewXaml),
            ("ManagerOperationsView.xaml", _managerOpsXaml),
            ("OwnerAdminView.xaml", _ownerAdminXaml)
        };

        var violations = new List<string>();

        foreach (var (fileName, filePath) in filesToAudit)
        {
            var doc = XDocument.Load(filePath);
            var buttons = doc.Descendants().Where(e => e.Name.LocalName == "Button").ToList();

            foreach (var btn in buttons)
            {
                var name = btn.Attribute(XNs + "Name")?.Value ?? btn.Attribute("Name")?.Value ?? btn.Attribute("Content")?.Value ?? btn.Attribute("CommandParameter")?.Value ?? "UnnamedButton";
                var (h, w) = ResolveButtonDimensions(btn, styles);

                if (h < 48.0 || w < 48.0)
                {
                    violations.Add($"{fileName} -> Button '{name}': Height={h}px, Width={w}px (Requires MinHeight >= 48px and MinWidth >= 48px)");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} touch target sizing violations in M7 views:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void HeaderActionButtons_EnforceMin48x48()
    {
        var styles = LoadStyleDimensions();
        var doc = XDocument.Load(_mainWindowXaml);

        var topHeader = doc.Descendants().FirstOrDefault(e =>
            (e.Attribute(XNs + "Name")?.Value == "TopHeaderBar" || e.Attribute("Name")?.Value == "TopHeaderBar"));

        Assert.NotNull(topHeader);

        var buttons = topHeader.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.NotEmpty(buttons);

        foreach (var btn in buttons)
        {
            var name = btn.Attribute(XNs + "Name")?.Value ?? btn.Attribute("Name")?.Value ?? "Unnamed";
            var (h, w) = ResolveButtonDimensions(btn, styles);

            Assert.True(h >= 48.0, $"Header button '{name}' height is {h}px (expected >= 48px)");
            Assert.True(w >= 48.0, $"Header button '{name}' width is {w}px (expected >= 48px)");
        }
    }

    [Fact]
    public void NavigationRailTabs_EnforceMin48x48()
    {
        var styles = LoadStyleDimensions();
        var doc = XDocument.Load(_mainWindowXaml);

        var navRail = doc.Descendants().FirstOrDefault(e =>
            (e.Attribute(XNs + "Name")?.Value == "LeftNavRail" || e.Attribute("Name")?.Value == "LeftNavRail"));

        Assert.NotNull(navRail);

        var buttons = navRail.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.Equal(3, buttons.Count); // TabCheckout, TabManager, TabOwner

        var violations = new List<string>();
        foreach (var btn in buttons)
        {
            var name = btn.Attribute(XNs + "Name")?.Value ?? btn.Attribute("Name")?.Value ?? "UnnamedTab";
            var (h, w) = ResolveButtonDimensions(btn, styles);

            if (h < 48.0 || w < 48.0)
            {
                violations.Add($"Nav rail tab '{name}': Height={h}px, Width={w}px (Expected MinHeight >= 48px and MinWidth >= 48px)");
            }
        }

        Assert.True(violations.Count == 0,
            $"Navigation rail tabs violate touch target requirements:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void BillingView_QuantitySteppersAndDeleteButtons_EnforceMin48x48()
    {
        var styles = LoadStyleDimensions();
        var doc = XDocument.Load(_billingViewXaml);

        // Stepper decrement, increment, and delete buttons
        var buttons = doc.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        var cartButtons = buttons.Where(b =>
        {
            var content = b.Attribute("Content")?.Value ?? "";
            var style = b.Attribute("Style")?.Value ?? "";
            return content == "+" || content == "-" || content == "✕" && style.Contains("Delete");
        }).ToList();

        Assert.NotEmpty(cartButtons);

        foreach (var btn in cartButtons)
        {
            var content = btn.Attribute("Content")?.Value;
            var (h, w) = ResolveButtonDimensions(btn, styles);

            Assert.True(h >= 48.0, $"Cart button '{content}' height {h}px < 48px");
            Assert.True(w >= 48.0, $"Cart button '{content}' width {w}px < 48px");
        }
    }

    [Fact]
    public void BillingView_CategoryChips_EnforceMin48x48()
    {
        var styles = LoadStyleDimensions();
        var doc = XDocument.Load(_billingViewXaml);

        var chipButton = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Button" && (e.Attribute("Style")?.Value?.Contains("CategoryChip") ?? false));

        Assert.NotNull(chipButton);
        var (h, w) = ResolveButtonDimensions(chipButton, styles);

        Assert.True(h >= 48.0, $"Category chip height {h}px < 48px");
        Assert.True(w >= 48.0, $"Category chip width {w}px < 48px");
    }

    // =========================================================================
    // 2. CURRENCY FORMATTING STANDARDIZATION (Rs. #,##0.00)
    // =========================================================================

    [Fact]
    public void BillingView_AllCurrencyBindings_EnforceStandardFormat()
    {
        var doc = XDocument.Load(_billingViewXaml);
        var textBlocks = doc.Descendants().Where(e => e.Name.LocalName == "TextBlock").ToList();

        // Find TextBlocks with StringFormat bindings
        var stringFormattedTextBlocks = textBlocks.Where(tb =>
        {
            var textAttr = tb.Attribute("Text")?.Value ?? "";
            return textAttr.Contains("StringFormat=") &&
                   (textAttr.Contains("Price") ||
                    textAttr.Contains("Total") ||
                    textAttr.Contains("Float") ||
                    textAttr.Contains("Subtotal") ||
                    textAttr.Contains("Discount") ||
                    textAttr.Contains("Tax"));
        }).ToList();

        Assert.NotEmpty(stringFormattedTextBlocks);

        foreach (var tb in stringFormattedTextBlocks)
        {
            var textAttr = tb.Attribute("Text")!.Value;

            // Must NOT contain legacy LKR
            Assert.DoesNotContain("LKR", textAttr, StringComparison.OrdinalIgnoreCase);

            // Must enforce standard Rs. format (allowing optional negative sign e.g. -Rs. for discounts)
            Assert.Matches(@"-?Rs\.\s*\{0:(?:#,)##0\.00\}", textAttr);
        }
    }

    [Fact]
    public void ShellViewModel_CurrencyFormatting_EnforcesStandardRsFormat()
    {
        var shift = new CashShift
        {
            ShiftId = Guid.NewGuid(),
            BranchId = "B01",
            CounterId = "C01",
            CashierId = "u1",
            OpeningFloat = 5000.00m,
            ExpectedCash = 12550.75m,
            Status = ShiftStatus.Open
        };

        var user = new User
        {
            UserId = "u1",
            Username = "cashier1",
            DisplayName = "Cashier 1",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Role = Role.Cashier
        };
        var vm = new ShellViewModel(user, shift);

        Assert.Equal("Rs. 5,000.00", vm.FormattedOpeningFloat);
        Assert.Equal("Rs. 12,550.75", vm.FormattedExpectedCash);

        // Adversarial zero and fractional edge cases
        shift.OpeningFloat = 0m;
        shift.ExpectedCash = 0.50m;
        vm.UpdateShift(shift);

        Assert.Equal("Rs. 0.00", vm.FormattedOpeningFloat);
        Assert.Equal("Rs. 0.50", vm.FormattedExpectedCash);

        // Half-up rounding verification
        shift.ExpectedCash = 100.005m;
        vm.UpdateShift(shift);
        Assert.Equal("Rs. 100.01", vm.FormattedExpectedCash);
    }

    [Fact]
    public void Adversarial_ZeroLegacyLkrTokens_InM7XamlAndCodeBehind()
    {
        var targetFiles = new[]
        {
            _mainWindowXaml,
            Path.Combine(_wpfDir, "MainWindow.xaml.cs"),
            _billingViewXaml,
            Path.Combine(_wpfDir, "Views", "BillingView.xaml.cs"),
            _managerOpsXaml,
            Path.Combine(_wpfDir, "Views", "ManagerOperationsView.xaml.cs"),
            _ownerAdminXaml,
            Path.Combine(_wpfDir, "Views", "OwnerAdminView.xaml.cs"),
            Path.Combine(_wpfDir, "ViewModels", "BillingViewModel.cs")
        };

        var lkrMatches = new List<string>();

        foreach (var file in targetFiles)
        {
            if (!File.Exists(file)) continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Contains("LKR", StringComparison.OrdinalIgnoreCase))
                {
                    lkrMatches.Add($"{Path.GetFileName(file)}:{i + 1} -> {line.Trim()}");
                }
            }
        }

        Assert.True(lkrMatches.Count == 0,
            $"Found legacy 'LKR' tokens in M7 files:\n" + string.Join("\n", lkrMatches));
    }

    // =========================================================================
    // 3. BRUSH BINDINGS (100% {DynamicResource}, Zero StaticResource/Hex/Hardcoded)
    // =========================================================================

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("Views/BillingView.xaml")]
    [InlineData("Views/ManagerOperationsView.xaml")]
    [InlineData("Views/OwnerAdminView.xaml")]
    public void M7Views_BrushProperties_StrictlyUseDynamicResource(string relativePath)
    {
        var filePath = Path.Combine(_wpfDir, relativePath);
        var doc = XDocument.Load(filePath);

        var brushProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Background", "Foreground", "BorderBrush", "CaretBrush",
            "HorizontalGridLinesBrush", "VerticalGridLinesBrush", "Fill", "Stroke"
        };

        var violations = new List<string>();

        // 1. Check direct attributes
        foreach (var elem in doc.Descendants())
        {
            foreach (var attr in elem.Attributes())
            {
                if (brushProperties.Contains(attr.Name.LocalName))
                {
                    var val = attr.Value.Trim();
                    CheckBrushValue(val, elem, attr.Name.LocalName, relativePath, violations);
                }
            }
        }

        // 2. Check Setters in local Styles/Templates
        foreach (var setter in doc.Descendants().Where(e => e.Name.LocalName == "Setter"))
        {
            var prop = setter.Attribute("Property")?.Value;
            if (prop != null && brushProperties.Contains(prop))
            {
                var val = setter.Attribute("Value")?.Value?.Trim();
                if (val != null)
                {
                    CheckBrushValue(val, setter, prop, relativePath, violations);
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} non-dynamic brush binding violations in '{relativePath}':\n" +
            string.Join("\n", violations));
    }

    private static void CheckBrushValue(string val, XElement elem, string propName, string file, List<string> violations)
    {
        // Allow Transparent
        if (string.Equals(val, "Transparent", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Allow TemplateBinding in ControlTemplates
        if (val.StartsWith("{TemplateBinding"))
        {
            return;
        }

        // Hardcoded hex check
        if (val.StartsWith('#'))
        {
            violations.Add($"{file} -> <{elem.Name.LocalName}> {propName}='{val}' (Hardcoded hex color)");
            return;
        }

        // StaticResource check
        if (val.StartsWith("{StaticResource") && val.Contains("Brush"))
        {
            violations.Add($"{file} -> <{elem.Name.LocalName}> {propName}='{val}' (StaticResource brush binding)");
            return;
        }

        // DynamicResource check
        if (val.StartsWith("{DynamicResource") && val.Contains("Brush"))
        {
            return; // Valid!
        }

        // Hardcoded color name check (e.g., White, Black, Red, Gray)
        violations.Add($"{file} -> <{elem.Name.LocalName}> {propName}='{val}' (Hardcoded named brush/color instead of DynamicResource)");
    }
}
