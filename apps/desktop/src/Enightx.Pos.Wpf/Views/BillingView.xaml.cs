using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Enightx.Pos.Wpf.ViewModels;

namespace Enightx.Pos.Wpf.Views;

public partial class BillingView : UserControl
{
    public event Action? RequestPayment;

    public BillingView()
    {
        InitializeComponent();
    }

    private async void AddItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            await vm.AddItemByBarcodeAsync(BarcodeInputBox.Text);
            BarcodeInputBox.Focus();
        }
    }

    private async void BarcodeInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is BillingViewModel vm)
        {
            await vm.AddItemByBarcodeAsync(BarcodeInputBox.Text);
            BarcodeInputBox.Focus();
        }
    }

    private void PayCash_Click(object sender, RoutedEventArgs e)
    {
        RequestPayment?.Invoke();
    }

    private void ClearCart_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            vm.ClearCart();
        }
    }
}
