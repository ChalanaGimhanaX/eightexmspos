using System.Windows;
using Enightx.Pos.Domain;
using Enightx.Pos.Wpf.ViewModels;

namespace Enightx.Pos.Wpf.Views;

public partial class HeldCartsDialog : Window
{
    private readonly BillingViewModel _billingVm;

    public HeldCartsDialog(BillingViewModel billingVm)
    {
        InitializeComponent();
        _billingVm = billingVm;
        DataContext = _billingVm;
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (HeldCartsGrid.SelectedItem is HeldCart selected)
        {
            if (_billingVm.CartItems.Count > 0)
            {
                var confirm = MessageBox.Show(
                    "The active cart currently has items. Clear active cart and resume this held bill?",
                    "Active Cart Not Empty",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );
                if (confirm != MessageBoxResult.Yes) return;
                _billingVm.CartItems.Clear();
            }

            var success = await _billingVm.RecallHeldCartAsync(selected.HeldCartId);
            if (success)
            {
                DialogResult = true;
                Close();
            }
        }
        else
        {
            MessageBox.Show("Please select a held bill to resume.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (HeldCartsGrid.SelectedItem is HeldCart selected)
        {
            var confirm = MessageBox.Show(
                $"Discard held bill '{selected.CustomerReference ?? selected.HeldCartId.ToString()}'?",
                "Confirm Discard",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );
            if (confirm == MessageBoxResult.Yes)
            {
                await _billingVm.DiscardHeldCartAsync(selected.HeldCartId, "Cashier discarded from held bills dialog");
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

