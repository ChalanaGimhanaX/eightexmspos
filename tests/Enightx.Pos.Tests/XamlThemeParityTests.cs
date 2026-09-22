using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Enightx.Pos.Tests;

public class XamlThemeParityTests
{
    private static readonly XNamespace PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    // Strict regex for 6-character (#RRGGBB) or 8-character (#AARRGGBB) hex colors
    private static readonly Regex HexColorRegex = new(
        @"^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _themesDir;
    private readonly string _lightXamlPath;
    private readonly string _darkXamlPath;
    private readonly string _typographyXamlPath;
    private readonly string _stylesXamlPath;

    public XamlThemeParityTests()
    {
        _themesDir = LocateThemesDirectory();
        _lightXamlPath = Path.Combine(_themesDir, "Light.xaml");
        _darkXamlPath = Path.Combine(_themesDir, "Dark.xaml");
        _typographyXamlPath = Path.Combine(_themesDir, "Typography.xaml");
        _stylesXamlPath = Path.Combine(_themesDir, "Styles.xaml");
    }

    private static string LocateThemesDirectory()
    {
        // Traverse upwards from test execution base directory to workspace root
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "apps", "desktop", "src", "Enightx.Pos.Wpf", "Themes");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        // Fallback relative to current working directory
        var fallback = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "apps", "desktop", "src", "Enightx.Pos.Wpf", "Themes"));
        if (Directory.Exists(fallback))
        {
            return fallback;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate Themes directory. Searched upwards from '{AppContext.BaseDirectory}' and current directory '{Directory.GetCurrentDirectory()}'.");
    }

    // =========================================================================
    // 1. FILE EXISTENCE & XML WELL-FORMEDNESS
    // =========================================================================

    [Fact]
    public void AllRequiredThemeFiles_ExistInThemesDirectory()
    {
        Assert.True(File.Exists(_lightXamlPath), $"Missing Light.xaml at {_lightXamlPath}");
        Assert.True(File.Exists(_darkXamlPath), $"Missing Dark.xaml at {_darkXamlPath}");
        Assert.True(File.Exists(_typographyXamlPath), $"Missing Typography.xaml at {_typographyXamlPath}");
        Assert.True(File.Exists(_stylesXamlPath), $"Missing Styles.xaml at {_stylesXamlPath}");
    }

    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    [InlineData("Typography.xaml")]
    [InlineData("Styles.xaml")]
    public void AllThemeFiles_AreWellFormedXml_WithResourceDictionaryRoot(string fileName)
    {
        var path = Path.Combine(_themesDir, fileName);
        var doc = XDocument.Load(path);
        Assert.NotNull(doc.Root);
        Assert.Equal("ResourceDictionary", doc.Root.Name.LocalName);
    }

    // =========================================================================
    // 2. BIDIRECTIONAL BRUSH KEY PARITY (LIGHT <-> DARK)
    // =========================================================================

    [Fact]
    public void LightAndDarkThemes_HaveExactMatchingBrushKeys_Bidirectionally()
    {
        var lightDoc = XDocument.Load(_lightXamlPath);
        var darkDoc = XDocument.Load(_darkXamlPath);

        var lightKeys = ExtractBrushKeys(lightDoc);
        var darkKeys = ExtractBrushKeys(darkDoc);

        Assert.NotEmpty(lightKeys);
        Assert.NotEmpty(darkKeys);

        // Assert all keys in Light exist in Dark
        var missingInDark = lightKeys.Except(darkKeys).OrderBy(k => k).ToList();
        Assert.True(
            missingInDark.Count == 0,
            $"The following brush keys exist in Light.xaml but are missing in Dark.xaml: {string.Join(", ", missingInDark)}");

        // Assert all keys in Dark exist in Light
        var missingInLight = darkKeys.Except(lightKeys).OrderBy(k => k).ToList();
        Assert.True(
            missingInLight.Count == 0,
            $"The following brush keys exist in Dark.xaml but are missing in Light.xaml: {string.Join(", ", missingInLight)}");

        // Assert identical counts
        Assert.Equal(lightKeys.Count, darkKeys.Count);
    }

    [Fact]
    public void LightAndDarkThemes_ContainAllMandatorySemanticBrushes()
    {
        var requiredTokens = new[]
        {
            "PrimaryBrush",
            "PrimaryDarkBrush",
            "PrimaryLightBrush",
            "SecondaryBrush",
            "AccentBrush",
            "BackgroundBrush",
            "SurfaceBrush",
            "CardBrush",
            "CardHoverBrush",
            "HeaderBackgroundBrush",
            "RowAlternateBrush",
            "BorderBrush",
            "BorderFocusBrush",
            "SuccessBrush",
            "SuccessSurfaceBrush",
            "WarningBrush",
            "WarningSurfaceBrush",
            "DangerBrush",
            "DangerSurfaceBrush",
            "InfoBrush",
            "InfoSurfaceBrush",
            "TextPrimaryBrush",
            "TextSecondaryBrush",
            "TextMutedBrush",
            "TextOnPrimaryBrush",
            "InputBackgroundBrush",
            "InputBorderBrush"
        };

        var lightDoc = XDocument.Load(_lightXamlPath);
        var darkDoc = XDocument.Load(_darkXamlPath);

        var lightKeys = ExtractBrushKeys(lightDoc);
        var darkKeys = ExtractBrushKeys(darkDoc);

        foreach (var token in requiredTokens)
        {
            Assert.Contains(token, lightKeys);
            Assert.Contains(token, darkKeys);
        }
    }

    // =========================================================================
    // 3. COLOR HEX FORMAT VALIDATION (#RRGGBB or #AARRGGBB)
    // =========================================================================

    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    public void AllColorDefinitions_UseStrictHexFormat_6Or8Characters(string fileName)
    {
        var path = Path.Combine(_themesDir, fileName);
        var doc = XDocument.Load(path);

        // 1. Check all <Color> element text nodes
        var colorElements = doc.Descendants()
            .Where(e => e.Name.LocalName == "Color")
            .ToList();

        Assert.NotEmpty(colorElements);

        foreach (var elem in colorElements)
        {
            var keyAttr = GetKeyAttribute(elem);
            var colorValue = elem.Value?.Trim() ?? "";

            Assert.False(
                string.IsNullOrWhiteSpace(colorValue),
                $"File '{fileName}' has empty <Color> element for key '{keyAttr}'");

            Assert.Matches(
                HexColorRegex,
                colorValue);

            // Verify valid hex byte parsing
            VerifyHexChannelsAreValidBytes(colorValue, keyAttr, fileName);
        }

        // 2. Check all SolidColorBrush Color attributes that specify literal hex
        var brushElements = doc.Descendants()
            .Where(e => e.Name.LocalName == "SolidColorBrush" && e.Attribute("Color") != null)
            .ToList();

        foreach (var brush in brushElements)
        {
            var colorAttr = brush.Attribute("Color")!.Value.Trim();
            var keyAttr = GetKeyAttribute(brush);

            // If it's a literal hex color (not a {StaticResource} binding)
            if (colorAttr.StartsWith('#'))
            {
                Assert.Matches(
                    HexColorRegex,
                    colorAttr);

                VerifyHexChannelsAreValidBytes(colorAttr, keyAttr, fileName);
            }
        }
    }

    // =========================================================================
    // 4. TOUCH TARGET SIZING ENFORCEMENT (>= 48px)
    // =========================================================================

    [Fact]
    public void StylesXaml_AllButtonStyles_EnforceMinDimensions48px()
    {
        var doc = XDocument.Load(_stylesXamlPath);

        // Find all Style elements targeting Button or named with Button / Touch / Chip
        var buttonStyles = doc.Descendants()
            .Where(e => e.Name.LocalName == "Style")
            .Where(e =>
            {
                var targetType = e.Attribute("TargetType")?.Value ?? "";
                var key = GetKeyAttribute(e) ?? "";
                return targetType.Contains("Button") || key.Contains("Button") || key.Contains("Stepper") || key.Contains("Chip");
            })
            .ToList();

        Assert.NotEmpty(buttonStyles);

        foreach (var style in buttonStyles)
        {
            var styleKey = GetKeyAttribute(style) ?? style.Attribute("TargetType")?.Value ?? "UnnamedStyle";

            // Extract MinHeight and Height setters
            var minHeight = ExtractNumericSetterValue(style, "MinHeight");
            var height = ExtractNumericSetterValue(style, "Height");
            var effectiveHeight = Math.Max(minHeight ?? 0, height ?? 0);

            Assert.True(
                effectiveHeight >= 48.0,
                $"Style '{styleKey}' in Styles.xaml violates minimum touch height: effective height is {effectiveHeight}px (expected >= 48px).");

            // Extract MinWidth and Width setters (or verify MinWidth >= 48)
            var minWidth = ExtractNumericSetterValue(style, "MinWidth");
            var width = ExtractNumericSetterValue(style, "Width");
            var effectiveWidth = Math.Max(minWidth ?? 0, width ?? 0);

            // If fixed or minimum width is explicitly constrained, it must be >= 48px
            if (minWidth.HasValue || width.HasValue)
            {
                Assert.True(
                    effectiveWidth >= 48.0,
                    $"Style '{styleKey}' in Styles.xaml violates minimum touch width: effective width is {effectiveWidth}px (expected >= 48px).");
            }
        }
    }

    [Theory]
    [InlineData("TouchStepperButtonStyle", 48.0, 48.0)]
    [InlineData("TouchDeleteButtonStyle", 48.0, 48.0)]
    [InlineData("CategoryChipButtonStyle", 48.0, 48.0)]
    [InlineData("BigTenderButtonStyle", 56.0, 48.0)]
    [InlineData("TouchPrimaryButtonStyle", 48.0, 48.0)]
    [InlineData("TouchSecondaryButtonStyle", 48.0, 48.0)]
    [InlineData("TouchKeypadButtonStyle", 56.0, 48.0)]
    public void SpecificTouchStyles_MeetOrExceedTargetDimensions(string styleKey, double expectedMinHeight, double expectedMinWidth)
    {
        var doc = XDocument.Load(_stylesXamlPath);
        var style = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Style" && GetKeyAttribute(e) == styleKey);

        Assert.NotNull(style);

        var minHeight = ExtractNumericSetterValue(style, "MinHeight");
        var height = ExtractNumericSetterValue(style, "Height");
        var effectiveHeight = Math.Max(minHeight ?? 0, height ?? 0);

        Assert.True(
            effectiveHeight >= expectedMinHeight,
            $"Style '{styleKey}' effective height {effectiveHeight} is less than required {expectedMinHeight}px.");

        var minWidth = ExtractNumericSetterValue(style, "MinWidth");
        var width = ExtractNumericSetterValue(style, "Width");
        var effectiveWidth = Math.Max(minWidth ?? 0, width ?? 0);

        Assert.True(
            effectiveWidth >= expectedMinWidth,
            $"Style '{styleKey}' effective width {effectiveWidth} is less than required {expectedMinWidth}px.");
    }

    // =========================================================================
    // 5. TRILINGUAL TYPOGRAPHY FALLBACK STACK & SCALE
    // =========================================================================

    [Fact]
    public void TypographyXaml_DefinesTrilingualFontFamilyFallbackChain()
    {
        var doc = XDocument.Load(_typographyXamlPath);

        // Find FontFamily definition (either as element or style setter)
        var fontFamilyElement = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "FontFamily" && GetKeyAttribute(e) == "PosFontFamily");

        string fontStack;
        if (fontFamilyElement != null)
        {
            fontStack = fontFamilyElement.Value?.Trim() ?? "";
        }
        else
        {
            // Fallback: look for a Setter with Property="FontFamily"
            var setter = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == "FontFamily");
            Assert.NotNull(setter);
            fontStack = setter.Attribute("Value")?.Value ?? "";
        }

        Assert.False(string.IsNullOrWhiteSpace(fontStack), "Font family fallback chain in Typography.xaml is empty.");

        // Assert all 5 required fallback fonts exist in the chain
        Assert.Contains("Segoe UI", fontStack);
        Assert.Contains("Nirmala UI", fontStack);
        Assert.Contains("Iskoola Pota", fontStack);
        Assert.Contains("Latha", fontStack);
        Assert.Contains("Arial", fontStack);

        // Assert correct fallback order: Segoe UI (Latin) -> Nirmala UI (Indic unified) -> Iskoola Pota (Sinhala) -> Latha (Tamil) -> Arial (Universal)
        int idxSegoe = fontStack.IndexOf("Segoe UI", StringComparison.Ordinal);
        int idxNirmala = fontStack.IndexOf("Nirmala UI", StringComparison.Ordinal);
        int idxIskoola = fontStack.IndexOf("Iskoola Pota", StringComparison.Ordinal);
        int idxLatha = fontStack.IndexOf("Latha", StringComparison.Ordinal);
        int idxArial = fontStack.IndexOf("Arial", StringComparison.Ordinal);

        Assert.True(idxSegoe < idxNirmala, "Segoe UI must precede Nirmala UI for Latin tabular numerals.");
        Assert.True(idxNirmala < idxIskoola, "Nirmala UI must precede Iskoola Pota.");
        Assert.True(idxIskoola < idxLatha, "Iskoola Pota must precede Latha.");
        Assert.True(idxLatha < idxArial, "Latha must precede Arial fallback.");
    }

    [Theory]
    [InlineData("DisplayTextStyle", 28.0, 40.0)]
    [InlineData("TitleTextStyle", 20.0, 30.0)]
    [InlineData("SubtitleTextStyle", 15.0, 24.0)]
    [InlineData("BodyTextStyle", 13.0, 20.0)]
    [InlineData("CaptionTextStyle", 11.0, 16.0)]
    public void TypographyXaml_TextStyles_EnforceMinimumFontSizeAndClearance(
        string styleKey, double minFontSize, double minHeight)
    {
        var doc = XDocument.Load(_typographyXamlPath);
        var style = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Style" && GetKeyAttribute(e) == styleKey);

        Assert.NotNull(style);

        var fontSize = ExtractNumericSetterValue(style, "FontSize");
        Assert.True(
            fontSize.HasValue && fontSize.Value >= minFontSize,
            $"Style '{styleKey}' FontSize {fontSize} must be >= {minFontSize}");

        var effectiveHeight = ExtractNumericSetterValue(style, "MinHeight") ?? ExtractNumericSetterValue(style, "LineHeight");
        Assert.True(
            effectiveHeight.HasValue && effectiveHeight.Value >= minHeight,
            $"Style '{styleKey}' vertical clearance {effectiveHeight} must be >= {minHeight} to prevent Sinhala/Tamil glyph clipping.");
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    private static HashSet<string> ExtractBrushKeys(XDocument doc)
    {
        var keys = new HashSet<string>();
        var brushes = doc.Descendants()
            .Where(e => e.Name.LocalName == "SolidColorBrush")
            .ToList();

        foreach (var brush in brushes)
        {
            var key = GetKeyAttribute(brush);
            if (!string.IsNullOrWhiteSpace(key))
            {
                keys.Add(key);
            }
        }
        return keys;
    }

    private static string? GetKeyAttribute(XElement elem)
    {
        // Try x:Key first
        var keyAttr = elem.Attribute(XNs + "Key");
        if (keyAttr != null) return keyAttr.Value;

        // Fallback to local name "Key"
        var localKey = elem.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key");
        return localKey?.Value;
    }

    private static double? ExtractNumericSetterValue(XElement styleElement, string propertyName)
    {
        var setter = styleElement.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == propertyName);

        if (setter == null) return null;

        var valStr = setter.Attribute("Value")?.Value;
        if (string.IsNullOrWhiteSpace(valStr)) return null;

        // Handle static resource double references or raw numbers
        if (double.TryParse(valStr, out double val))
        {
            return val;
        }

        var match = Regex.Match(valStr, @"\{(?:StaticResource|DynamicResource)\s+([^\}]+)\}");
        if (match.Success)
        {
            var key = match.Groups[1].Value.Trim();
            var resElem = styleElement.Document?.Descendants()
                .FirstOrDefault(e => GetKeyAttribute(e) == key);
            if (resElem != null && double.TryParse(resElem.Value?.Trim(), out double resVal))
            {
                return resVal;
            }
        }

        return null;
    }

    private static void VerifyHexChannelsAreValidBytes(string hex, string? key, string file)
    {
        var cleanHex = hex.TrimStart('#');
        int channelCount = cleanHex.Length / 2;

        for (int i = 0; i < channelCount; i++)
        {
            var byteSub = cleanHex.Substring(i * 2, 2);
            bool parsed = byte.TryParse(byteSub, System.Globalization.NumberStyles.HexNumber, null, out _);
            Assert.True(
                parsed,
                $"File '{file}', key '{key}' has invalid hex channel '{byteSub}' in '{hex}'");
        }
    }
}
