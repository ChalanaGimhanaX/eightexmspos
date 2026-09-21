using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Wpf.ViewModels;

namespace Enightx.Pos.Wpf.Views;

public partial class PaymentDialog : Window
{
    public PaymentDialog()
    {
        InitializeComponent();
        Loaded += PaymentDialog_Loaded;
    }

    private void PaymentDialog_Loaded(object sender, RoutedEventArgs e)
    {
        UpdatePanels();
        TenderBox.Focus();
        TenderBox.SelectAll();
    }

    private void TenderRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && DataContext is PaymentViewModel vm)
        {
            vm.SelectedTenderType = tag;
            UpdatePanels();
        }
    }

    private void UpdatePanels()
    {
        if (DataContext is not PaymentViewModel vm) return;

        CashTenderPanel.Visibility = vm.IsCashSelected ? Visibility.Visible : Visibility.Collapsed;
        CardTenderPanel.Visibility = vm.IsCardSelected ? Visibility.Visible : Visibility.Collapsed;
        CreditTenderPanel.Visibility = vm.IsCreditSelected ? Visibility.Visible : Visibility.Collapsed;
        SplitTenderPanel.Visibility = vm.IsSplitSelected ? Visibility.Visible : Visibility.Collapsed;
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
            await vm.CompleteSaleAsync();
            if (string.IsNullOrEmpty(vm.ErrorMessage))
            {
                DialogResult = true;
                Close();
            }
        }
    }
}
