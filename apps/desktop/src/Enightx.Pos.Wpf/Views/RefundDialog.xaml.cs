using System.Windows;
using System.Windows.Input;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class RefundDialog : Window
{
    private readonly ISaleService _saleService;
    private readonly User _currentUser;
    private readonly CashShift _currentShift;
    private Sale? _loadedSale;

    public RefundDialog(ISaleService saleService, User currentUser, CashShift currentShift)
    {
        InitializeComponent();
        _saleService = saleService;
        _currentUser = currentUser;
        _currentShift = currentShift;
    }

    private async void FindReceipt_Click(object sender, RoutedEventArgs e)
    {
        await LookupReceiptAsync();
    }

    private async void ReceiptNumberBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await LookupReceiptAsync();
        }
    }

    private async Task LookupReceiptAsync()
    {
        var rcpt = ReceiptNumberBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(rcpt))
        {
            MessageBox.Show("Please enter a valid receipt number.", "Search Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var sale = await _saleService.GetSaleByReceiptNumberAsync(rcpt);
        if (sale == null)
        {
            MessageBox.Show($"Receipt '{rcpt}' was not found in the local database.", "Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (sale.Status == SaleStatus.Cancelled)
        {
            MessageBox.Show($"Sale '{rcpt}' has been cancelled.", "Sale Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _loadedSale = sale;
        LinesGrid.ItemsSource = sale.Lines;
        if (sale.Lines.Count > 0)
        {
            LinesGrid.SelectedIndex = 0;
            RefundQtyBox.Text = sale.Lines[0].Quantity.ToString("F0");
        }
    }

    private void LinesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LinesGrid.SelectedItem is SaleLine selectedLine)
        {
            RefundQtyBox.Text = selectedLine.Quantity.ToString("F0");
        }
    }

    private async void ProcessRefund_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedSale == null)
        {
            MessageBox.Show("Please search and load a receipt first.", "Receipt Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (LinesGrid.SelectedItem is not SaleLine selectedLine)
        {
            MessageBox.Show("Please select an item line to refund.", "Line Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(RefundQtyBox.Text, out var qtyToRefund) || qtyToRefund <= 0)
        {
            MessageBox.Show("Please enter a valid quantity greater than zero.", "Invalid Quantity", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var reason = ReasonBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            MessageBox.Show("A refund reason is strictly mandatory (A08). Please provide a reason.", "Reason Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var refundCmd = new RefundSaleCommand(
                TenantId: _loadedSale.TenantId,
                BranchId: _currentShift.BranchId,
                CounterId: _currentShift.CounterId,
                CashierId: _currentUser.UserId,
                ShiftId: _currentShift.ShiftId,
                OriginalSaleId: _loadedSale.SaleId,
                Items: new List<RefundLineRequest>
                {
                    new(LineId: selectedLine.LineId, QuantityToRefund: qtyToRefund)
                },
                Reason: reason,
                ReturnStockToInventory: RestockCheckBox.IsChecked == true
            );

            var refundSale = await _saleService.RefundSaleAsync(refundCmd);

            MessageBox.Show(
                $"Refund processed successfully!\n\nRefund Receipt: {refundSale.ReceiptNumber}\nAmount Refunded (Cash): LKR {refundSale.GrandTotal:N2}\nRestocked to Inventory: {(RestockCheckBox.IsChecked == true ? "Yes" : "No")}",
                "Refund Completed",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Refund failed: {ex.Message}", "Refund Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

