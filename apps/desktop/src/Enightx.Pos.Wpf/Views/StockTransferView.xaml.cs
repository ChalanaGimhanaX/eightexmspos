using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Common;

namespace Enightx.Pos.Wpf.Views;

public partial class StockTransferView : UserControl
{
    private readonly ICatalogService _catalogService;
    private readonly ITransferService _transferService;
    private readonly List<DesktopStockTransfer> _transfers = new();
    private readonly Dictionary<string, Dictionary<string, BranchStockItem>> _branchInventory = new();

    public event Action? RequestBackToPos;

    public StockTransferView(ICatalogService catalogService, ITransferService transferService)
    {
        InitializeComponent();
        _catalogService = catalogService;
        _transferService = transferService;

        Loaded += async (s, e) => await InitializeViewAsync();
    }

    private async Task InitializeViewAsync()
    {
        var products = await _catalogService.GetAllProductsAsync();
        ProductCombo.ItemsSource = products;
        if (products.Count > 0)
        {
            ProductCombo.SelectedIndex = 0;
        }

        // Initialize mock branch inventories for branches B01, B02, B03
        foreach (var branch in new[] { "B01", "B02", "B03" })
        {
            if (!_branchInventory.ContainsKey(branch))
            {
                _branchInventory[branch] = new Dictionary<string, BranchStockItem>();
                foreach (var p in products)
                {
                    _branchInventory[branch][p.ProductId] = new BranchStockItem
                    {
                        BranchId = branch,
                        ProductId = p.ProductId,
                        StockOnHand = branch == "B01" ? p.StockOnHand : 10.0m,
                        StockInTransit = 0.0m
                    };
                }
            }
        }

        RefreshGrid();
    }

    private void RefreshGrid()
    {
        TransfersGrid.ItemsSource = null;
        TransfersGrid.ItemsSource = _transfers.ToList();
    }

    private void RequestTransfer_Click(object sender, RoutedEventArgs e)
    {
        var sourceBranch = GetSelectedBranchCode(SourceBranchCombo);
        var destBranch = GetSelectedBranchCode(DestBranchCombo);

        if (sourceBranch == destBranch)
        {
            MessageBox.Show("Source and destination branch cannot be the same.", "Invalid Transfer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedProduct = ProductCombo.SelectedItem as Product;
        if (selectedProduct == null)
        {
            MessageBox.Show("Please select a product to transfer.", "Invalid Product", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(QuantityInput.Text.Trim(), out var qty) || qty <= 0)
        {
            MessageBox.Show("Please enter a valid quantity greater than zero.", "Invalid Quantity", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var transfer = _transferService.RequestTransfer(sourceBranch, destBranch, selectedProduct.ProductId, qty);
            _transfers.Insert(0, transfer);
            RefreshGrid();
            QuantityInput.Text = "";

            MessageBox.Show($"Transfer request created successfully!\nID: {transfer.TransferId:N}\nFrom: {sourceBranch} To: {destBranch}\nQty: {qty:F0} x {selectedProduct.Name}", "Transfer Requested", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error creating transfer request: {ex.Message}", "Transfer Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Dispatch_Click(object sender, RoutedEventArgs e)
    {
        var selected = TransfersGrid.SelectedItem as DesktopStockTransfer;
        if (selected == null)
        {
            MessageBox.Show("Please select a transfer from the list to dispatch.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var sourceStock = _branchInventory[selected.SourceBranchId][selected.ProductId];
            var destStock = _branchInventory[selected.DestBranchId][selected.ProductId];

            _transferService.DispatchTransfer(selected, sourceStock, destStock);
            RefreshGrid();

            MessageBox.Show($"Transfer dispatched successfully!\nStock moved to IN-TRANSIT at {selected.DestBranchId}.\nSource ({selected.SourceBranchId}) Stock Remaining: {sourceStock.StockOnHand:F0}", "Transfer Dispatched", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error dispatching transfer: {ex.Message}", "Dispatch Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Receive_Click(object sender, RoutedEventArgs e)
    {
        var selected = TransfersGrid.SelectedItem as DesktopStockTransfer;
        if (selected == null)
        {
            MessageBox.Show("Please select a transfer from the list to receive.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var destStock = _branchInventory[selected.DestBranchId][selected.ProductId];
            _transferService.ReceiveTransfer(selected, destStock);
            RefreshGrid();

            MessageBox.Show($"Transfer received at destination successfully!\nDestination ({selected.DestBranchId}) Stock on Hand: {destStock.StockOnHand:F0}\nIn-Transit: {destStock.StockInTransit:F0}", "Transfer Received", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error receiving transfer: {ex.Message}", "Receive Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TransfersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = TransfersGrid.SelectedItem as DesktopStockTransfer;
        if (selected == null)
        {
            DispatchBtn.IsEnabled = false;
            ReceiveBtn.IsEnabled = false;
            return;
        }

        DispatchBtn.IsEnabled = selected.Status == TransferStatus.Requested;
        ReceiveBtn.IsEnabled = selected.Status == TransferStatus.Dispatched;
    }

    private static string GetSelectedBranchCode(ComboBox combo)
    {
        var text = (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "B01";
        return text[..3];
    }

    private void BackToPos_Click(object sender, RoutedEventArgs e)
    {
        RequestBackToPos?.Invoke();
    }
}

