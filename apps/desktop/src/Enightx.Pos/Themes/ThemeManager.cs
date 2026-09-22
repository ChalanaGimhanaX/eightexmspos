using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Enightx.Pos.Themes;

public enum Theme
{
    Light,
    Dark
}

public class ThemeChangedEventArgs : EventArgs
{
    public Theme OldTheme { get; }
    public Theme NewTheme { get; }

    public ThemeChangedEventArgs(Theme oldTheme, Theme newTheme)
    {
        OldTheme = oldTheme;
        NewTheme = newTheme;
    }
}

public interface IThemeResourceApplier
{
    void ApplyTheme(Theme theme);
}

public interface IThemeManager : INotifyPropertyChanged
{
    Theme CurrentTheme { get; }
    bool IsDarkTheme { get; }
    bool IsLightTheme { get; }
    string ToggleButtonText { get; }
    string ToggleButtonIcon { get; }
    ICommand ToggleThemeCommand { get; }

    void SetTheme(Theme theme);
    void ToggleTheme();
    event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
}

public class ThemeManager : IThemeManager
{
    private static ThemeManager? _instance;
    public static ThemeManager Current => _instance ??= new ThemeManager();

    private readonly IThemeResourceApplier? _resourceApplier;
    private Theme _currentTheme;
    private bool _isInitialized = true;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    public Theme CurrentTheme => _currentTheme;

    public bool IsDarkTheme => _currentTheme == Theme.Dark;
    public bool IsLightTheme => _currentTheme == Theme.Light;
    public string ToggleButtonText => IsDarkTheme ? "☀️ Light Mode" : "🌙 Dark Mode";
    public string ToggleButtonIcon => IsDarkTheme ? "☀️" : "🌙";

    public ICommand ToggleThemeCommand { get; }

    public ThemeManager(IThemeResourceApplier? resourceApplier = null, Theme initialTheme = Theme.Light)
    {
        _resourceApplier = resourceApplier;
        _currentTheme = initialTheme;
        _isInitialized = true;
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
    }

    public static void Initialize(IThemeResourceApplier resourceApplier, Theme initialTheme = Theme.Light)
    {
        _instance = new ThemeManager(resourceApplier, initialTheme);
    }

    public void ToggleTheme()
    {
        SetTheme(_currentTheme == Theme.Light ? Theme.Dark : Theme.Light);
    }

    public void SetTheme(Theme theme)
    {
        if (_currentTheme == theme && _isInitialized)
        {
            return;
        }

        var oldTheme = _currentTheme;
        _currentTheme = theme;
        _isInitialized = true;

        _resourceApplier?.ApplyTheme(theme);

        OnPropertyChanged(nameof(CurrentTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(ToggleButtonText));
        OnPropertyChanged(nameof(ToggleButtonIcon));
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(oldTheme, theme));
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
