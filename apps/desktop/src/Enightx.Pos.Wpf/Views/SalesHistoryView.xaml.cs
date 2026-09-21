using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class SalesHistoryView : UserControl
{
    private readonly ISaleService _saleService;
    private readonly IReceiptService _receiptService;
    private readonly User _currentUser;
    private readonly Guid _currentShiftId;
    private List<Sale> _allSales = new();
    private Sale? _selectedSale;

    public event Action? RequestBackToPos;

    public SalesHistoryView(
        ISaleService saleService,
        IReceiptService receiptService,
        User currentUser,
        Guid currentShiftId)
    {
        InitializeComponent();
        _saleService = saleService;
        _receiptService = receiptService;
        _currentUser = currentUser;
        _currentShiftId = currentShiftId;

        Loaded += async (s, e) => await LoadSalesAsync();
    }

    public async Task LoadSalesAsync()
    {
        try
        {
            _allSales = await _saleService.GetRecentSalesAsync(100);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load sales: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox?.Text?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            SalesGrid.ItemsSource = _allSales;
        }
        else
        {
            SalesGrid.ItemsSource = _allSales.Where(s =>
                s.ReceiptNumber.ToLowerInvariant().Contains(query) ||
                s.CashierId.ToLowerInvariant().Contains(query) ||
                (s.CustomerId != null && s.CustomerId.ToLowerInvariant().Contains(query))
            ).ToList();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        ApplyFilter();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadSalesAsync();
    }

    private void SalesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedSale = SalesGrid.SelectedItem as Sale;
        if (_selectedSale == null)
        {
            SaleDetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        SaleDetailsPanel.Visibility = Visibility.Visible;
        DetailReceiptNo.Text = $"RECEIPT: {_selectedSale.ReceiptNumber}";
        DetailMeta.Text = $"Date: {_selectedSale.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC | Cashier: {_selectedSale.CashierId} | Customer: {_selectedSale.CustomerId ?? "Walk-in"} | Reprints: {_selectedSale.ReprintCount}";
        DetailTotal.Text = $"LKR {_selectedSale.GrandTotal:N2}";
        DetailStatus.Text = $"STATUS: {_selectedSale.Status.ToString().ToUpperInvariant()}";

        LinesGrid.ItemsSource = _selectedSale.Lines;
        TendersGrid.ItemsSource = _selectedSale.Tenders;

        // Disable refund/cancel buttons if already refunded or cancelled
        var canModify = _selectedSale.Status == SaleStatus.Completed;
        RefundBtn.IsEnabled = canModify;
        CancelSaleBtn.IsEnabled = canModify;
    }

    private async void ReprintReceipt_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSale == null) return;

        try
        {
            var printResult = await _receiptService.ReprintReceiptAsync(_selectedSale.SaleId, _currentUser.UserId);
            if (printResult.Success)
            {
                MessageBox.Show($"Receipt {_selectedSale.ReceiptNumber} reprinted successfully.", "Reprint Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Notice: Printer communication failed ({printResult.ErrorMessage}). Receipt reprint queued.", "Printer Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            await LoadSalesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to reprint: {ex.Message}", "Reprint Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ProcessRefund_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSale == null) return;

        var prompt = new ReasonPromptDialog(
            $"Enter mandatory reason for refunding receipt {_selectedSale.ReceiptNumber}:",
            "Customer returned item"
        )
        {
            Owner = Window.GetWindow(this)
        };

        if (prompt.ShowDialog() != true) return;
        var reason = prompt.ReasonText;

        var returnStock = MessageBox.Show(
            "Should the refunded items be returned to saleable inventory?\n\nYes = Return stock to inventory\nNo = Damaged / write-off (do not restore stock)",
            "Inventory Restock Decision",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        ) == MessageBoxResult.Yes;

        try
        {
            var refundLines = _selectedSale.Lines.Select(l => new RefundLineRequest(l.LineId, l.Quantity)).ToList();

            var cmd = new RefundSaleCommand(
                TenantId: _selectedSale.TenantId,
                BranchId: _selectedSale.BranchId,
                CounterId: _selectedSale.CounterId,
                CashierId: _currentUser.UserId,
                ShiftId: _currentShiftId,
                OriginalSaleId: _selectedSale.SaleId,
                Items: refundLines,
                Reason: reason,
                ReturnStockToInventory: returnStock
            );

            var refundSale = await _saleService.RefundSaleAsync(cmd);
            MessageBox.Show($"Refund processed successfully!\nRefund Receipt: {refundSale.ReceiptNumber}\nAmount Refunded: LKR {Math.Abs(refundSale.GrandTotal):N2}", "Refund Completed", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadSalesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Refund failed: {ex.Message}", "Refund Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CancelSale_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSale == null) return;

        var prompt = new ReasonPromptDialog(
            $"Enter mandatory reason for cancelling sale {_selectedSale.ReceiptNumber}:",
            "Mistaken entry / customer left"
        )
        {
            Owner = Window.GetWindow(this)
        };

        if (prompt.ShowDialog() != true) return;
        var reason = prompt.ReasonText;

        try
        {
            var cmd = new CancelSaleCommand(
                TenantId: _selectedSale.TenantId,
                BranchId: _selectedSale.BranchId,
                CounterId: _selectedSale.CounterId,
                CashierId: _currentUser.UserId,
                ShiftId: _currentShiftId,
                SaleId: _selectedSale.SaleId,
                Reason: reason
            );

            await _saleService.CancelSaleAsync(cmd);
            MessageBox.Show($"Sale {_selectedSale.ReceiptNumber} has been cancelled.", "Sale Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadSalesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cancellation failed: {ex.Message}", "Cancellation Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BackToPos_Click(object sender, RoutedEventArgs e)
    {
        RequestBackToPos?.Invoke();
    }
}
