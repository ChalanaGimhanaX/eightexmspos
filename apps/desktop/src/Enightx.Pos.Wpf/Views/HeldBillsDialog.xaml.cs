using System.Windows;
using Enightx.Pos.Wpf.ViewModels;

namespace Enightx.Pos.Wpf.Views;

public partial class HeldBillsDialog : Window
{
    private readonly BillingViewModel _vm;

    public HeldBillsDialog(BillingViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        HeldBillsGrid.ItemsSource = _vm.HeldBills;
        HeldCountText.Text = $"{_vm.HeldBills.Count} Bill(s) Held";
    }

    private void Recall_Click(object sender, RoutedEventArgs e)
    {
        var selected = HeldBillsGrid.SelectedItem as HeldBill;
        if (selected == null)
        {
            MessageBox.Show("Please select a held bill to recall.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_vm.CartItems.Count > 0)
        {
            MessageBox.Show("The active cart currently contains items. Please hold or clear the active cart before recalling a held bill.", "Cart Not Empty", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_vm.RecallBill(selected))
        {
            DialogResult = true;
            Close();
        }
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        var selected = HeldBillsGrid.SelectedItem as HeldBill;
        if (selected == null)
        {
            MessageBox.Show("Please select a held bill to discard.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Are you sure you want to discard '{selected.Note}'?", "Confirm Discard", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _vm.DiscardHeldBill(selected);
            HeldCountText.Text = $"{_vm.HeldBills.Count} Bill(s) Held";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

