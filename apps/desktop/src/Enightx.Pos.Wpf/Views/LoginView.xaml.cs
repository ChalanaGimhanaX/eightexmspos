using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Wpf.ViewModels;

namespace Enightx.Pos.Wpf.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
        {
            await vm.LoginAsync(PasswordBox.Password, "TENANT_LK_01", "B01", "C01");
        }
    }
}
