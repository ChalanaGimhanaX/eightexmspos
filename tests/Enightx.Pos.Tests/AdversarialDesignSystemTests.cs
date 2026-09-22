using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Enightx.Pos.Common;
using Enightx.Pos.Themes;
using Xunit;

namespace Enightx.Pos.Tests;

public class AdversarialDesignSystemTests
{
    private static readonly XNamespace PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Regex StrictHexRegex = new(
        @"^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _themesDir;
    private readonly string _lightXamlPath;
    private readonly string _darkXamlPath;
    private readonly string _typographyXamlPath;
    private readonly string _stylesXamlPath;

    public AdversarialDesignSystemTests()
    {
        _themesDir = LocateThemesDirectory();
        _lightXamlPath = Path.Combine(_themesDir, "Light.xaml");
        _darkXamlPath = Path.Combine(_themesDir, "Dark.xaml");
        _typographyXamlPath = Path.Combine(_themesDir, "Typography.xaml");
        _stylesXamlPath = Path.Combine(_themesDir, "Styles.xaml");
    }

    private static string LocateThemesDirectory()
    {
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

        var fallback = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "apps", "desktop", "src", "Enightx.Pos.Wpf", "Themes"));
        if (Directory.Exists(fallback))
        {
            return fallback;
        }

        throw new DirectoryNotFoundException($"Could not locate Themes directory.");
    }

    private static string? GetKey(XElement elem)
    {
        var keyAttr = elem.Attribute(XNs + "Key");
        if (keyAttr != null) return keyAttr.Value;
        return elem.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
    }

    // =========================================================================
    // 1. TOKEN PARITY & SYMMETRY
    // =========================================================================

    [Fact]
    public void Adversarial_TokenParity_CompleteBiDirectionalMatch()
    {
        var lightDoc = XDocument.Load(_lightXamlPath);
        var darkDoc = XDocument.Load(_darkXamlPath);

        var lightColors = lightDoc.Descendants().Where(e => e.Name.LocalName == "Color").Select(GetKey).Where(k => k != null).ToHashSet();
        var darkColors = darkDoc.Descendants().Where(e => e.Name.LocalName == "Color").Select(GetKey).Where(k => k != null).ToHashSet();
        var lightBrushes = lightDoc.Descendants().Where(e => e.Name.LocalName == "SolidColorBrush").Select(GetKey).Where(k => k != null).ToHashSet();
        var darkBrushes = darkDoc.Descendants().Where(e => e.Name.LocalName == "SolidColorBrush").Select(GetKey).Where(k => k != null).ToHashSet();

        Assert.Equal(27, lightColors.Count);
        Assert.Equal(27, darkColors.Count);
        Assert.Equal(27, lightBrushes.Count);
        Assert.Equal(27, darkBrushes.Count);

        var colorDiffLightDark = lightColors.Except(darkColors).ToList();
        var colorDiffDarkLight = darkColors.Except(lightColors).ToList();
        var brushDiffLightDark = lightBrushes.Except(darkBrushes).ToList();
        var brushDiffDarkLight = darkBrushes.Except(lightBrushes).ToList();

        Assert.Empty(colorDiffLightDark);
        Assert.Empty(colorDiffDarkLight);
        Assert.Empty(brushDiffLightDark);
        Assert.Empty(brushDiffDarkLight);
    }

    [Fact]
    public void Adversarial_BrushBindings_StrictlyReferenceCorrespondingColorTokens()
    {
        var lightDoc = XDocument.Load(_lightXamlPath);
        var darkDoc = XDocument.Load(_darkXamlPath);

        VerifyBrushReferences(lightDoc, "Light.xaml");
        VerifyBrushReferences(darkDoc, "Dark.xaml");
    }

    private static void VerifyBrushReferences(XDocument doc, string fileName)
    {
        var brushes = doc.Descendants().Where(e => e.Name.LocalName == "SolidColorBrush").ToList();
        foreach (var brush in brushes)
        {
            var brushKey = GetKey(brush);
            Assert.NotNull(brushKey);
            Assert.EndsWith("Brush", brushKey);

            var expectedColorKey = brushKey.Substring(0, brushKey.Length - 5) + "Color";
            var colorAttr = brush.Attribute("Color")?.Value;
            Assert.NotNull(colorAttr);

            var expectedRef = $"{{StaticResource {expectedColorKey}}}";
            Assert.True(
                colorAttr == expectedRef,
                $"File '{fileName}': Brush '{brushKey}' Color binding '{colorAttr}' does not match expected '{expectedRef}'");
        }
    }

    // =========================================================================
    // 2. STRICT HEX VALIDATION
    // =========================================================================

    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    public void Adversarial_HexFormatting_StrictRgbOrArgb_NoShortHand_NoControlChars(string fileName)
    {
        var path = Path.Combine(_themesDir, fileName);
        var doc = XDocument.Load(path);

        var colors = doc.Descendants().Where(e => e.Name.LocalName == "Color").ToList();
        foreach (var colorElem in colors)
        {
            var key = GetKey(colorElem);
            var val = colorElem.Value;

            // No leading or trailing whitespace
            Assert.Equal(val, val.Trim());

            // Strict regex match
            Assert.Matches(StrictHexRegex, val);

            // Cannot be 3, 4, or 5 hex digits (must be exactly 7 (#RRGGBB) or 9 (#AARRGGBB) chars)
            Assert.True(val.Length == 7 || val.Length == 9, $"Color '{key}' in '{fileName}' has invalid length {val.Length}: '{val}'");

            // Channel byte parse verification
            var hexOnly = val.Substring(1);
            for (int i = 0; i < hexOnly.Length; i += 2)
            {
                var channel = hexOnly.Substring(i, 2);
                Assert.True(byte.TryParse(channel, System.Globalization.NumberStyles.HexNumber, null, out _),
                    $"Color '{key}' channel '{channel}' failed byte parse in '{val}'");
            }
        }
    }

    // =========================================================================
    // 3. ERGONOMIC TOUCH SIZING (>= 48x48)
    // =========================================================================

    [Fact]
    public void Adversarial_EveryInteractiveButtonStyle_Enforces48pxHitTargetInBothDimensions()
    {
        var doc = XDocument.Load(_stylesXamlPath);
        var styles = doc.Descendants()
            .Where(e => e.Name.LocalName == "Style")
            .Where(e =>
            {
                var target = e.Attribute("TargetType")?.Value ?? "";
                var key = GetKey(e) ?? "";
                return target.Contains("Button") || key.Contains("Button") || key.Contains("Chip") || key.Contains("Stepper");
            })
            .ToList();

        Assert.NotEmpty(styles);

        foreach (var style in styles)
        {
            var styleKey = GetKey(style) ?? "UnnamedStyle";

            var height = GetSetterDouble(style, "Height");
            var minHeight = GetSetterDouble(style, "MinHeight");
            var width = GetSetterDouble(style, "Width");
            var minWidth = GetSetterDouble(style, "MinWidth");

            var effectiveHeight = Math.Max(height ?? 0, minHeight ?? 0);
            var effectiveWidth = Math.Max(width ?? 0, minWidth ?? 0);

            Assert.True(
                effectiveHeight >= 48.0,
                $"Style '{styleKey}' violates vertical touch target minimum (effective height: {effectiveHeight}px, expected >= 48px)");

            // For buttons, minWidth or fixed width must be at least 48px
            Assert.True(
                effectiveWidth >= 48.0,
                $"Style '{styleKey}' violates horizontal touch target minimum (effective width: {effectiveWidth}px, expected >= 48px)");
        }
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

    // =========================================================================
    // 4. TYPOGRAPHY FONT METRICS & TRILINGUAL LINE HEIGHTS
    // =========================================================================

    [Fact]
    public void Adversarial_Typography_AllTextStylesHaveLineHeightAbove135Percent()
    {
        var doc = XDocument.Load(_typographyXamlPath);
        var styles = doc.Descendants().Where(e => e.Name.LocalName == "Style").ToList();

        foreach (var style in styles)
        {
            var key = GetKey(style) ?? "Unnamed";
            if (key == "ProductMultilingualSubtitleStyle")
            {
                // Inherits from CaptionTextStyle via BasedOn
                var basedOn = style.Attribute("BasedOn")?.Value;
                Assert.Equal("{StaticResource CaptionTextStyle}", basedOn);
                continue;
            }

            var fontSize = GetSetterDouble(style, "FontSize");
            var lineHeight = GetSetterDouble(style, "LineHeight");
            var stacking = style.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == "LineStackingStrategy")
                ?.Attribute("Value")?.Value;

            Assert.True(fontSize.HasValue && fontSize.Value > 0, $"Style '{key}' missing valid FontSize");
            Assert.True(lineHeight.HasValue && lineHeight.Value > 0, $"Style '{key}' missing valid LineHeight");
            Assert.Equal("BlockLineHeight", stacking);

            var ratio = lineHeight.Value / fontSize.Value;
            Assert.True(
                ratio >= 1.35,
                $"Style '{key}' has LineHeight/FontSize ratio {ratio:F2}x, which is below 1.35x required for Sinhala/Tamil diacritics.");
        }
    }

    [Fact]
    public void Adversarial_Typography_SinhalaAndTamilSampleStrings_ValidUtf8Representation()
    {
        // Real-world sample strings with complex Indic combining characters
        var sinhalaSamples = new[]
        {
            "ඉදිරිපස බ්‍රේක් පෑඩ් කට්ටලය", // Front brake pad set
            "ඔයිල් ෆිල්ටරය",                 // Oil filter
            "ස්පාර්ක් ප්ලග්",               // Spark plug
            "එන්ජින් ඔයිල් 4L",             // Engine oil
            "ශ්‍රී ලංකා රුපියල්",           // Sri Lankan Rupee
            "මිල අඩු කිරීම"                // Discount
        };

        var tamilSamples = new[]
        {
            "முன் பிரேக் பேட் தொகுப்பு", // Front brake pad set
            "எண்ணெய் வடிகட்டி",             // Oil filter
            "ஸ்பார்க் பிளக்",               // Spark plug
            "என்ஜின் எண்ணெய் 4L",         // Engine oil
            "இலங்கை ரூபாய்",               // Sri Lankan Rupee
            "தள்ளுபடி"                     // Discount
        };

        foreach (var sample in sinhalaSamples)
        {
            // Verify UTF-8 round-trip without corruption
            var bytes = System.Text.Encoding.UTF8.GetBytes(sample);
            var decoded = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.Equal(sample, decoded);

            // Assert contains characters in Sinhala Unicode block (U+0D80 to U+0DFF)
            Assert.Contains(sample, s => s >= '\u0D80' && s <= '\u0DFF');
        }

        foreach (var sample in tamilSamples)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(sample);
            var decoded = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.Equal(sample, decoded);

            // Assert contains characters in Tamil Unicode block (U+0B80 to U+0BFF)
            Assert.Contains(sample, s => s >= '\u0B80' && s <= '\u0BFF');
        }
    }

    // =========================================================================
    // 5. THEMEMANAGER STRESS & CONCURRENCY
    // =========================================================================

    private class ThreadSafeSpyApplier : IThemeResourceApplier
    {
        public ConcurrentBag<Theme> AppliedThemes { get; } = new();

        public void ApplyTheme(Theme theme)
        {
            AppliedThemes.Add(theme);
        }
    }

    [Fact]
    public async Task Adversarial_ThemeManager_MultiThreadedStressToggle_MaintainsConsistentState()
    {
        var applier = new ThreadSafeSpyApplier();
        var manager = new ThemeManager(applier, initialTheme: Theme.Light);

        int threadCount = 8;
        int togglesPerThread = 100;
        var tasks = new Task[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < togglesPerThread; j++)
                {
                    manager.ToggleTheme();
                }
            });
        }

        await Task.WhenAll(tasks);

        // Final state must be self-consistent
        if (manager.CurrentTheme == Theme.Light)
        {
            Assert.True(manager.IsLightTheme);
            Assert.False(manager.IsDarkTheme);
            Assert.Equal("🌙 Dark Mode", manager.ToggleButtonText);
        }
        else
        {
            Assert.True(manager.IsDarkTheme);
            Assert.False(manager.IsLightTheme);
            Assert.Equal("☀️ Light Mode", manager.ToggleButtonText);
        }

        Assert.NotEmpty(applier.AppliedThemes);
    }
}
