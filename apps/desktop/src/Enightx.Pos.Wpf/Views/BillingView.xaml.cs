using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Enightx.Pos.Domain;
using Enightx.Pos.Wpf.ViewModels;

namespace Enightx.Pos.Wpf.Views;

public partial class BillingView : UserControl
{
    public event Action? RequestPayment;
    public event Action? ShiftClosed;

    public BillingView()
    {
        InitializeComponent();
    }

    private async void BillingView_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            await vm.LoadCatalogAsync();
            await vm.LoadHeldCartsAsync();
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

    private void Refund_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            var dialog = new RefundDialog(App.SaleService, vm.CurrentUser, vm.CurrentShift)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
        }
    }

    private void CashMovement_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            var dialog = new CashMovementDialog(App.ShiftService, vm.CurrentUser, vm.CurrentShift)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
        }
    }

    private void ShiftReport_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            var dialog = new ShiftReportDialog(App.ReportService, vm.CurrentShift.ShiftId)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
        }
    }

    private void GoodsReceiving_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            if (vm.CurrentUser.Role != Role.Manager && vm.CurrentUser.Role != Role.Owner)
            {
                MessageBox.Show("Access Denied: Goods Receiving is restricted to Store Managers and Owners.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new GoodsReceivingDialog(App.GoodsReceivingService, App.CatalogService, vm.CurrentUser, vm.CurrentShift.BranchId)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() == true)
            {
                _ = vm.LoadCatalogAsync();
            }
        }
    }

    private void CloseShift_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            var dialog = new CloseShiftDialog(App.ShiftService, vm.CurrentUser, vm.CurrentShift)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() == true)
            {
                ShiftClosed?.Invoke();
            }
        }
    }

    private async void HoldCart_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            if (vm.CartItems.Count == 0)
            {
                MessageBox.Show("Cannot hold an empty cart.", "Empty Cart", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var success = await vm.HoldCurrentCartAsync();
            if (success)
            {
                MessageBox.Show("Current bill has been parked successfully.", "Bill Parked", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private async void RecallHeldCarts_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            await vm.LoadHeldCartsAsync();
            var dialog = new HeldCartsDialog(vm)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
        }
    }
}
