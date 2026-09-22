using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Enightx.Pos.Tests;

/// <summary>
/// Milestone M8 Adversarial Verification Test Suite.
/// Validates touch target sizing (>= 48px), card container CornerRadius (10-12px),
/// 100% DynamicResource brush bindings, and strict Rs. #,##0.00 currency formatting
/// across all 9 overhauled dialogs.
/// </summary>
public class DialogTouchAndFormattingAdversarialTests
{
    private static readonly XNamespace PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] NineDialogRelativePaths =
    [
        "Views/OpenShiftDialog.xaml",
        "Views/PaymentDialog.xaml",
        "Views/HeldCartsDialog.xaml",
        "Views/ProductManagementDialog.xaml",
        "Views/DailyReportDialog.xaml",
        "Views/InventoryValuationDialog.xaml",
        "Views/CustomerManagementDialog.xaml",
        "Views/ManagerPinDialog.xaml",
        "Views/UpdateDialog.xaml"
    ];

    private static readonly HashSet<string> BrushProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Background", "Foreground", "BorderBrush", "CaretBrush",
        "HorizontalGridLinesBrush", "VerticalGridLinesBrush", "Fill", "Stroke",
        "RowBackground", "AlternatingRowBackground"
    };

    private static readonly Regex CurrencyStringFormatRegex = new(
        @"^.*-?Rs\.\s*(?:\{0:(?:#,##0\.00|N2)\}|#,##0\.00).*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _wpfDir;
    private readonly string _stylesXamlPath;

    public DialogTouchAndFormattingAdversarialTests()
    {
        _wpfDir = LocateWpfDirectory();
        _stylesXamlPath = Path.Combine(_wpfDir, "Themes", "Styles.xaml");
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

    private static double? ParseDouble(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        return double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static double? GetSetterDouble(XElement styleElem, string propName)
    {
        var setter = styleElem.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == propName);
        if (setter == null) return null;
        return ParseDouble(setter.Attribute("Value")?.Value);
    }

    private static string? GetSetterString(XElement styleElem, string propName)
    {
        var setter = styleElem.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == propName);
        return setter?.Attribute("Value")?.Value;
    }

    private Dictionary<string, StyleDimensions> LoadAllStyles(XDocument dialogDoc)
    {
        var styles = new Dictionary<string, StyleDimensions>();

        // 1. Load styles from Themes/Styles.xaml
        if (File.Exists(_stylesXamlPath))
        {
            var stylesDoc = XDocument.Load(_stylesXamlPath);
            foreach (var style in stylesDoc.Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                var key = GetKey(style);
                if (!string.IsNullOrEmpty(key))
                {
                    styles[key] = ExtractStyleDimensions(style);
                }
            }
        }

        // 2. Load inline styles from the dialog XAML (including BasedOn inheritance)
        foreach (var style in dialogDoc.Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            var key = GetKey(style);
            if (!string.IsNullOrEmpty(key))
            {
                var dims = ExtractStyleDimensions(style);

                var basedOn = style.Attribute("BasedOn")?.Value;
                if (!string.IsNullOrEmpty(basedOn))
                {
                    var match = Regex.Match(basedOn, @"\{(?:StaticResource|DynamicResource)\s+([^\}]+)\}");
                    if (match.Success && styles.TryGetValue(match.Groups[1].Value.Trim(), out var baseDims))
                    {
                        dims.MinHeight ??= baseDims.MinHeight;
                        dims.Height ??= baseDims.Height;
                        dims.MinWidth ??= baseDims.MinWidth;
                        dims.Width ??= baseDims.Width;
                        dims.CornerRadius ??= baseDims.CornerRadius;
                    }
                }
                styles[key] = dims;
            }
        }

        return styles;
    }

    private static StyleDimensions ExtractStyleDimensions(XElement style)
    {
        var minH = GetSetterDouble(style, "MinHeight");
        var h = GetSetterDouble(style, "Height");
        var minW = GetSetterDouble(style, "MinWidth");
        var w = GetSetterDouble(style, "Width");
        var crStr = GetSetterString(style, "CornerRadius");
        double? cr = null;
        if (!string.IsNullOrEmpty(crStr))
        {
            var parts = crStr.Split(',');
            if (double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var crVal))
            {
                cr = crVal;
            }
        }

        return new StyleDimensions
        {
            MinHeight = minH,
            Height = h,
            MinWidth = minW,
            Width = w,
            CornerRadius = cr
        };
    }

    private static (double effectiveHeight, double effectiveWidth) ResolveButtonDimensions(
        XElement button,
        Dictionary<string, StyleDimensions> styles)
    {
        double? elemMinH = ParseDouble(button.Attribute("MinHeight")?.Value);
        double? elemH = ParseDouble(button.Attribute("Height")?.Value);
        double? elemMinW = ParseDouble(button.Attribute("MinWidth")?.Value);
        double? elemW = ParseDouble(button.Attribute("Width")?.Value);

        var styleAttr = button.Attribute("Style")?.Value;
        if (!string.IsNullOrEmpty(styleAttr))
        {
            var match = Regex.Match(styleAttr, @"\{(?:StaticResource|DynamicResource)\s+([^\}]+)\}");
            if (match.Success && styles.TryGetValue(match.Groups[1].Value.Trim(), out var sDims))
            {
                elemMinH ??= sDims.MinHeight;
                elemH ??= sDims.Height;
                elemMinW ??= sDims.MinWidth;
                elemW ??= sDims.Width;
            }
        }

        double effectiveHeight = Math.Max(elemMinH ?? 0, elemH ?? 0);
        double effectiveWidth = Math.Max(elemMinW ?? 0, elemW ?? 0);

        return (effectiveHeight, effectiveWidth);
    }

    // =========================================================================
    // 1. FILE EXISTENCE VERIFICATION
    // =========================================================================

    [Fact]
    public void AllNineDialogs_FilesExist_InExpectedDirectory()
    {
        foreach (var relPath in NineDialogRelativePaths)
        {
            var xamlPath = Path.Combine(_wpfDir, relPath);
            var csPath = xamlPath + ".cs";

            Assert.True(File.Exists(xamlPath), $"Dialog XAML file not found: {xamlPath}");
            Assert.True(File.Exists(csPath), $"Dialog code-behind file not found: {csPath}");
        }
    }

    // =========================================================================
    // 2. ROOT CONTAINER & CARD BORDER CORNER RADIUS (10-12px)
    // =========================================================================

    [Theory]
    [InlineData("Views/OpenShiftDialog.xaml")]
    [InlineData("Views/PaymentDialog.xaml")]
    [InlineData("Views/HeldCartsDialog.xaml")]
    [InlineData("Views/ProductManagementDialog.xaml")]
    [InlineData("Views/DailyReportDialog.xaml")]
    [InlineData("Views/InventoryValuationDialog.xaml")]
    [InlineData("Views/CustomerManagementDialog.xaml")]
    [InlineData("Views/ManagerPinDialog.xaml")]
    [InlineData("Views/UpdateDialog.xaml")]
    public void Dialog_RootContainer_EnforcesCardBorderWithCornerRadius10To12(string relativePath)
    {
        var filePath = Path.Combine(_wpfDir, relativePath);
        var doc = XDocument.Load(filePath);
        var styles = LoadAllStyles(doc);

        var root = doc.Root;
        Assert.NotNull(root);

        // Find candidate card borders:
        // Either direct child of Window, or primary container within Window
        var allBorders = doc.Descendants().Where(e => e.Name.LocalName == "Border").ToList();
        Assert.NotEmpty(allBorders);

        var cardBorders = new List<(XElement element, double cornerRadius)>();

        foreach (var border in allBorders)
        {
            double? cr = null;
            var crAttr = border.Attribute("CornerRadius")?.Value;
            if (!string.IsNullOrEmpty(crAttr))
            {
                var parts = crAttr.Split(',');
                if (double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedCr))
                {
                    cr = parsedCr;
                }
            }

            var styleAttr = border.Attribute("Style")?.Value;
            if (cr == null && !string.IsNullOrEmpty(styleAttr))
            {
                var match = Regex.Match(styleAttr, @"\{(?:StaticResource|DynamicResource)\s+([^\}]+)\}");
                if (match.Success && styles.TryGetValue(match.Groups[1].Value.Trim(), out var sDims) && sDims.CornerRadius != null)
                {
                    cr = sDims.CornerRadius;
                }
            }

            if (cr != null)
            {
                cardBorders.Add((border, cr.Value));
            }
        }

        // Must contain at least one card border with CornerRadius in [10, 12]
        var validCardBorders = cardBorders.Where(cb => cb.cornerRadius >= 10.0 && cb.cornerRadius <= 12.0).ToList();

        Assert.True(validCardBorders.Count > 0,
            $"{relativePath} must contain at least one card container Border with CornerRadius between 10px and 12px (or styled via TouchCardBorderStyle). " +
            $"Found CornerRadii: [{string.Join(", ", cardBorders.Select(c => c.cornerRadius + "px"))}]");

        // Verify that no primary card border retains sub-standard legacy radius (< 10px, e.g. 6px or 8px)
        var invalidBorders = cardBorders
            .Where(cb => cb.cornerRadius < 10.0 && (
                cb.element.Parent == root || 
                cb.element.Parent?.Name.LocalName == "Grid" || 
                cb.element.Attribute("Style")?.Value?.Contains("Card") == true))
            .ToList();

        Assert.True(invalidBorders.Count == 0,
            $"{relativePath} contains card borders with sub-standard CornerRadius < 10px: " +
            string.Join(", ", invalidBorders.Select(b => $"{b.element.Name.LocalName} (Radius={b.cornerRadius}px)")));
    }

    // =========================================================================
    // 3. BUTTON TOUCH TARGET SIZING (MinHeight >= 48px, MinWidth >= 48px)
    // =========================================================================

    [Theory]
    [InlineData("Views/OpenShiftDialog.xaml")]
    [InlineData("Views/PaymentDialog.xaml")]
    [InlineData("Views/HeldCartsDialog.xaml")]
    [InlineData("Views/ProductManagementDialog.xaml")]
    [InlineData("Views/DailyReportDialog.xaml")]
    [InlineData("Views/InventoryValuationDialog.xaml")]
    [InlineData("Views/CustomerManagementDialog.xaml")]
    [InlineData("Views/ManagerPinDialog.xaml")]
    [InlineData("Views/UpdateDialog.xaml")]
    public void Dialog_AllButtons_EnforceMinimum48x48TouchTargets(string relativePath)
    {
        var filePath = Path.Combine(_wpfDir, relativePath);
        var doc = XDocument.Load(filePath);
        var styles = LoadAllStyles(doc);

        var buttons = doc.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        var violations = new List<string>();

        foreach (var btn in buttons)
        {
            var identifier = btn.Attribute(XNs + "Name")?.Value ??
                             btn.Attribute("Name")?.Value ??
                             btn.Attribute("Content")?.Value ??
                             btn.Attribute("Tag")?.Value ??
                             "UnnamedButton";

            var (effH, effW) = ResolveButtonDimensions(btn, styles);

            if (effH < 48.0 || effW < 48.0)
            {
                violations.Add($"<{btn.Name.LocalName} Content='{identifier}'> -> Effective Height={effH}px, Width={effW}px (Required >= 48x48px)");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} touch target sizing violations in '{relativePath}':\n" +
            string.Join("\n", violations));
    }

    // =========================================================================
    // 4. BRUSH BINDINGS (100% {DynamicResource}, Zero StaticResource/Hex/Named)
    // =========================================================================

    [Theory]
    [InlineData("Views/OpenShiftDialog.xaml")]
    [InlineData("Views/PaymentDialog.xaml")]
    [InlineData("Views/HeldCartsDialog.xaml")]
    [InlineData("Views/ProductManagementDialog.xaml")]
    [InlineData("Views/DailyReportDialog.xaml")]
    [InlineData("Views/InventoryValuationDialog.xaml")]
    [InlineData("Views/CustomerManagementDialog.xaml")]
    [InlineData("Views/ManagerPinDialog.xaml")]
    [InlineData("Views/UpdateDialog.xaml")]
    public void Dialog_BrushProperties_StrictlyUseDynamicResource(string relativePath)
    {
        var filePath = Path.Combine(_wpfDir, relativePath);
        var doc = XDocument.Load(filePath);

        var violations = new List<string>();

        // 1. Direct XML attributes
        foreach (var elem in doc.Descendants())
        {
            foreach (var attr in elem.Attributes())
            {
                if (BrushProperties.Contains(attr.Name.LocalName))
                {
                    CheckBrushValue(attr.Value.Trim(), elem, attr.Name.LocalName, relativePath, violations);
                }
            }
        }

        // 2. Style & Template Setters
        foreach (var setter in doc.Descendants().Where(e => e.Name.LocalName == "Setter"))
        {
            var prop = setter.Attribute("Property")?.Value;
            if (prop != null && BrushProperties.Contains(prop))
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
        // Allowed: Transparent
        if (string.Equals(val, "Transparent", StringComparison.OrdinalIgnoreCase)) return;

        // Allowed: TemplateBinding inside ControlTemplates
        if (val.StartsWith("{TemplateBinding", StringComparison.OrdinalIgnoreCase)) return;

        // Allowed: Dynamic data binding to ViewModel properties (e.g. {Binding AlertColor})
        if (val.StartsWith("{Binding", StringComparison.OrdinalIgnoreCase)) return;

        // Disallowed: Hardcoded hex color
        if (val.StartsWith('#'))
        {
            violations.Add($"{file} -> <{elem.Name.LocalName}> {propName}='{val}' (Hardcoded hex color)");
            return;
        }

        // Disallowed: StaticResource brush binding
        if (val.StartsWith("{StaticResource", StringComparison.OrdinalIgnoreCase) && val.Contains("Brush"))
        {
            violations.Add($"{file} -> <{elem.Name.LocalName}> {propName}='{val}' (StaticResource brush binding, must be DynamicResource)");
            return;
        }

        // Allowed: DynamicResource brush binding
        if (val.StartsWith("{DynamicResource", StringComparison.OrdinalIgnoreCase) && val.Contains("Brush"))
        {
            return;
        }

        // Disallowed: Hardcoded named colors (e.g. White, Black, Red, Gray)
        violations.Add($"{file} -> <{elem.Name.LocalName}> {propName}='{val}' (Hardcoded color/brush name instead of DynamicResource)");
    }

    // =========================================================================
    // 5. CURRENCY STRINGFORMAT ENFORCEMENT (Rs. #,##0.00 or Rs. {0:N2})
    // =========================================================================

    [Theory]
    [InlineData("Views/OpenShiftDialog.xaml")]
    [InlineData("Views/PaymentDialog.xaml")]
    [InlineData("Views/HeldCartsDialog.xaml")]
    [InlineData("Views/ProductManagementDialog.xaml")]
    [InlineData("Views/DailyReportDialog.xaml")]
    [InlineData("Views/InventoryValuationDialog.xaml")]
    [InlineData("Views/CustomerManagementDialog.xaml")]
    [InlineData("Views/ManagerPinDialog.xaml")]
    [InlineData("Views/UpdateDialog.xaml")]
    public void Dialog_CurrencyStringFormats_StrictlyUseRsPrefixAndTwoDecimals(string relativePath)
    {
        var filePath = Path.Combine(_wpfDir, relativePath);
        var doc = XDocument.Load(filePath);

        var monetaryKeywords = new[]
        {
            "Price", "Cost", "Total", "Float", "Subtotal", "Discount", "Tax",
            "Valuation", "Balance", "Due", "Tender", "Tendered", "Change", "Credit", "Cash",
            "Amount", "Revenue", "Profit", "Refund", "Sales"
        };

        var violations = new List<string>();

        // Check attributes on all elements (Text, Binding, Header, Content, etc.)
        foreach (var elem in doc.Descendants())
        {
            foreach (var attr in elem.Attributes())
            {
                var val = attr.Value;
                if (!val.Contains("StringFormat", StringComparison.OrdinalIgnoreCase)) continue;

                // Check if binding targets monetary data
                bool isMonetary = monetaryKeywords.Any(k => val.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (!isMonetary) continue;

                // 1. Must NOT contain legacy LKR
                if (val.Contains("LKR", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{relativePath} -> <{elem.Name.LocalName}> {attr.Name.LocalName}='{val}' contains legacy 'LKR' token");
                    continue;
                }

                // 2. Must match Rs. format pattern
                if (!CurrencyStringFormatRegex.IsMatch(val))
                {
                    violations.Add($"{relativePath} -> <{elem.Name.LocalName}> {attr.Name.LocalName}='{val}' does not conform to 'Rs. #,##0.00' or 'Rs. {{0:N2}}'");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} currency StringFormat violations in '{relativePath}':\n" +
            string.Join("\n", violations));
    }

    // =========================================================================
    // 6. ADVERSARIAL: ZERO LEGACY 'LKR' TOKENS IN ALL 9 DIALOGS & CODE-BEHIND
    // =========================================================================

    [Fact]
    public void AllNineDialogs_Adversarial_ZeroLegacyLkrTokens()
    {
        var targetFiles = new List<string>();
        foreach (var relPath in NineDialogRelativePaths)
        {
            var xaml = Path.Combine(_wpfDir, relPath);
            var cs = xaml + ".cs";
            if (File.Exists(xaml)) targetFiles.Add(xaml);
            if (File.Exists(cs)) targetFiles.Add(cs);
        }

        var lkrMatches = new List<string>();

        foreach (var file in targetFiles)
        {
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
            $"Found {lkrMatches.Count} legacy 'LKR' tokens in M8 dialog files:\n" +
            string.Join("\n", lkrMatches));
    }

    // =========================================================================
    // 7. COMPONENT-SPECIFIC AUDITS
    // =========================================================================

    [Fact]
    public void ManagerPinDialog_NumericKeypad_EnforcesMinimum56x56TouchTargets()
    {
        var filePath = Path.Combine(_wpfDir, "Views", "ManagerPinDialog.xaml");
        var doc = XDocument.Load(filePath);
        var styles = LoadAllStyles(doc);

        var buttons = doc.Descendants().Where(e => e.Name.LocalName == "Button").ToList();

        // Must have at least 12 buttons (digits 0-9, Clear, Backspace/Enter, plus Authorize/Cancel)
        Assert.True(buttons.Count >= 12,
            $"ManagerPinDialog must include an embedded on-screen 3x4 numeric keypad (found {buttons.Count} buttons, expected >= 12)");

        var keypadButtons = buttons.Where(b =>
        {
            var tag = b.Attribute("Tag")?.Value ?? "";
            var content = b.Attribute("Content")?.Value ?? "";
            var style = b.Attribute("Style")?.Value ?? "";
            return style.Contains("Keypad") ||
                   int.TryParse(content, out _) ||
                   content == "C" || content == "⌫" || content == "Clear" ||
                   tag == "CLEAR" || tag == "BACK";
        }).ToList();

        Assert.True(keypadButtons.Count >= 10,
            $"ManagerPinDialog keypad must contain numeric and control keys (found {keypadButtons.Count})");

        var violations = new List<string>();
        foreach (var btn in keypadButtons)
        {
            var content = btn.Attribute("Content")?.Value ?? btn.Attribute("Tag")?.Value ?? "Key";
            var (h, w) = ResolveButtonDimensions(btn, styles);

            if (h < 56.0 || w < 56.0)
            {
                violations.Add($"Keypad button '{content}': Height={h}px, Width={w}px (Requires >= 56x56px for touchscreen operation)");
            }
        }

        Assert.True(violations.Count == 0,
            $"Keypad buttons fail minimum 56x56px touch target requirement:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void OpenShiftDialog_QuickPresets_EnforceMinimum48x48AndDynamicBrushes()
    {
        var filePath = Path.Combine(_wpfDir, "Views", "OpenShiftDialog.xaml");
        var doc = XDocument.Load(filePath);
        var styles = LoadAllStyles(doc);

        var buttons = doc.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        var presetButtons = buttons.Where(b => b.Attribute("Click")?.Value == "Preset_Click" || b.Attribute("Tag") != null).ToList();

        Assert.True(presetButtons.Count >= 4, $"OpenShiftDialog must contain opening float quick presets (found {presetButtons.Count})");

        foreach (var btn in presetButtons)
        {
            var (h, w) = ResolveButtonDimensions(btn, styles);
            Assert.True(h >= 48.0, $"Preset button '{btn.Attribute("Content")?.Value}' height {h}px < 48px");
            Assert.True(w >= 48.0, $"Preset button '{btn.Attribute("Content")?.Value}' width {w}px < 48px");
        }
    }

    [Fact]
    public void PaymentDialog_QuickCashAndTenderOptions_EnforceTouchTargets()
    {
        var filePath = Path.Combine(_wpfDir, "Views", "PaymentDialog.xaml");
        var doc = XDocument.Load(filePath);
        var styles = LoadAllStyles(doc);

        var buttons = doc.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        var quickCashButtons = buttons.Where(b =>
        {
            var click = b.Attribute("Click")?.Value ?? "";
            return click.Contains("Exact") || click.Contains("Add");
        }).ToList();

        Assert.True(quickCashButtons.Count >= 4, $"PaymentDialog must contain quick cash preset buttons (found {quickCashButtons.Count})");

        foreach (var btn in quickCashButtons)
        {
            var (h, w) = ResolveButtonDimensions(btn, styles);
            Assert.True(h >= 48.0, $"Quick cash button '{btn.Attribute("Content")?.Value}' height {h}px < 48px");
            Assert.True(w >= 48.0, $"Quick cash button '{btn.Attribute("Content")?.Value}' width {w}px < 48px");
        }
    }

    [Theory]
    [InlineData("Views/HeldCartsDialog.xaml")]
    [InlineData("Views/DailyReportDialog.xaml")]
    [InlineData("Views/InventoryValuationDialog.xaml")]
    [InlineData("Views/CustomerManagementDialog.xaml")]
    public void Dialog_DataGrids_EnforceTouchRowHeight(string relativePath)
    {
        var filePath = Path.Combine(_wpfDir, relativePath);
        var doc = XDocument.Load(filePath);
        var styles = LoadAllStyles(doc);

        var dataGrids = doc.Descendants().Where(e => e.Name.LocalName == "DataGrid").ToList();
        if (dataGrids.Count == 0) return;

        foreach (var dg in dataGrids)
        {
            double? rowHeight = ParseDouble(dg.Attribute("RowHeight")?.Value);
            var styleAttr = dg.Attribute("Style")?.Value;
            if (rowHeight == null && !string.IsNullOrEmpty(styleAttr))
            {
                var match = Regex.Match(styleAttr, @"\{(?:StaticResource|DynamicResource)\s+([^\}]+)\}");
                if (match.Success && styles.TryGetValue(match.Groups[1].Value.Trim(), out var sDims) && sDims.Height != null)
                {
                    rowHeight = sDims.Height;
                }
            }

            Assert.True(rowHeight != null && rowHeight >= 48.0,
                $"{relativePath} DataGrid must enforce touch-friendly RowHeight >= 48px (found {rowHeight ?? 0}px)");
        }
    }

    private class StyleDimensions
    {
        public double? MinHeight { get; set; }
        public double? Height { get; set; }
        public double? MinWidth { get; set; }
        public double? Width { get; set; }
        public double? CornerRadius { get; set; }
    }
}

