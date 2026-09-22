using System.Windows;
using Enightx.Pos.Themes;

namespace Enightx.Pos.Wpf.Themes;

public class WpfThemeResourceApplier : IThemeResourceApplier
{
    private const string LightThemeUri = "/Themes/Light.xaml";
    private const string DarkThemeUri = "/Themes/Dark.xaml";

    public void ApplyTheme(Theme theme)
    {
        var app = Application.Current;
        if (app == null) return;

        // Ensure execution on WPF UI Thread
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => ApplyTheme(theme));
            return;
        }

        var targetSource = theme == Theme.Dark ? DarkThemeUri : LightThemeUri;
        var newThemeDict = new ResourceDictionary
        {
            Source = new Uri(targetSource, UriKind.Relative)
        };

        var mergedDicts = app.Resources.MergedDictionaries;

        // Find the index of the existing theme dictionary
        int existingIndex = -1;
        for (int i = 0; i < mergedDicts.Count; i++)
        {
            var sourceStr = mergedDicts[i].Source?.OriginalString;
            if (sourceStr != null &&
                (sourceStr.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                 sourceStr.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                existingIndex = i;
                break;
            }
        }

        if (existingIndex >= 0)
        {
            // In-place replacement triggers immediate dynamic resource re-evaluation
            mergedDicts[existingIndex] = newThemeDict;
        }
        else
        {
            // Fallback insertion at index 1 (between Typography and Styles)
            if (mergedDicts.Count > 1)
            {
                mergedDicts.Insert(1, newThemeDict);
            }
            else
            {
                mergedDicts.Add(newThemeDict);
            }
        }
    }
}
