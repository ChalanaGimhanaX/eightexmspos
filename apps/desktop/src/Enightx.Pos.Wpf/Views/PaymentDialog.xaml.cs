using System.Windows;
using Enightx.Pos.Wpf.ViewModels;

namespace Enightx.Pos.Wpf.Views;

public partial class PaymentDialog : Window
{
    public PaymentDialog()
    {
        InitializeComponent();
        TenderBox.Focus();
        TenderBox.SelectAll();
    }

    private void Exact_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PaymentViewModel vm) vm.SetExactAmount();
    }

    private void Add500_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PaymentViewModel vm) vm.AddTenderAmount(500m);
    }

    private void Add1000_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PaymentViewModel vm) vm.AddTenderAmount(1000m);
    }

    private void Add5000_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PaymentViewModel vm) vm.AddTenderAmount(5000m);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PaymentViewModel vm)
        {
            await vm.CompleteCashSaleAsync();
            if (string.IsNullOrEmpty(vm.ErrorMessage))
            {
                DialogResult = true;
                Close();
            }
        }
    }
}
