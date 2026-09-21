using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Enightx.Pos.Domain;
using Enightx.Pos.Wpf.ViewModels;

namespace Enightx.Pos.Wpf.Views;

public partial class BillingView : UserControl
{
    public event Action? RequestPayment;

    public BillingView()
    {
        InitializeComponent();
    }

    private async void BillingView_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            await vm.LoadCatalogAsync();
        }
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

    private void ProductCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is Product product && DataContext is BillingViewModel vm)
        {
            vm.AddProductToCart(product);
        }
    }

    private void CategoryFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is Category cat && DataContext is BillingViewModel vm)
        {
            vm.SelectedCategory = cat;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            vm.SearchQuery = SearchBox.Text;
        }
    }

    private void IncrementQty_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is CartItemViewModel item && DataContext is BillingViewModel vm)
        {
            vm.IncrementItem(item);
        }
    }

    private void DecrementQty_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is CartItemViewModel item && DataContext is BillingViewModel vm)
        {
            vm.DecrementItem(item);
        }
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is CartItemViewModel item && DataContext is BillingViewModel vm)
        {
            vm.RemoveItem(item);
        }
    }

    private async void SyncCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            await vm.SyncCatalogWithCloudAsync();
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
