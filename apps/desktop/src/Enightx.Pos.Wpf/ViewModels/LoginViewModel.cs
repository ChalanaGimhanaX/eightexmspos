using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.ViewModels;

public class LoginViewModel : INotifyPropertyChanged
{
    private readonly IAuthService _authService;
    private string _username = "";
    private string _errorMessage = "";
    private bool _isLoading;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<User>? LoginSucceeded;

    public LoginViewModel(IAuthService authService)
    {
        _authService = authService;
    }

    public string Username
    {
        get => _username;
        set { _username = value; OnPropertyChanged(); }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public async Task LoginAsync(string password, string tenantId, string branchId, string counterId)
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(password))
        {
            ErrorMessage = "Please enter both username and password.";
            return;
        }

        try
        {
            IsLoading = true;
            ErrorMessage = "";
            var user = await _authService.AuthenticateAsync(Username, password, tenantId, branchId, counterId);
            LoginSucceeded?.Invoke(user);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
