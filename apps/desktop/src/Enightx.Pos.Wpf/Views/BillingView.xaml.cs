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

    private void HoldBill_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            if (vm.CartItems.Count == 0)
            {
                MessageBox.Show("Cannot hold an empty cart.", "Empty Cart", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var prompt = new ReasonPromptDialog("Enter note / customer name for this held bill (Optional):", $"Customer #{vm.HeldBillsCount + 1}")
            {
                Owner = Window.GetWindow(this)
            };

            if (prompt.ShowDialog() == true)
            {
                vm.HoldCurrentBill(prompt.ReasonText);
            }
        }
    }

    private void RecallBill_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            if (vm.HeldBills.Count == 0)
            {
                MessageBox.Show("No held bills currently parked.", "No Held Bills", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new HeldBillsDialog(vm)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
        }
    }
}
