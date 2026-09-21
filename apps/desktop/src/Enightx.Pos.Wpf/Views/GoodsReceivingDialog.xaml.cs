using System.Collections.ObjectModel;
using System.Windows;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public class GoodsReceivingItemRow
{
    public required string ProductId { get; set; }
    public required string ProductName { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineTotalCost => MoneyCalculator.Round(Quantity * UnitCost);
}

public partial class GoodsReceivingDialog : Window
{
    private readonly IGoodsReceivingService _receivingService;
    private readonly ICatalogService _catalogService;
    private readonly User _currentUser;
    private readonly string _branchId;
    private readonly ObservableCollection<GoodsReceivingItemRow> _lines = new();

    public GoodsReceivingDialog(IGoodsReceivingService receivingService, ICatalogService catalogService, User currentUser, string branchId)
    {
        InitializeComponent();
        _receivingService = receivingService;
        _catalogService = catalogService;
        _currentUser = currentUser;
        _branchId = branchId;

        ItemsGrid.ItemsSource = _lines;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var prods = await _catalogService.GetAllActiveProductsAsync();
            ProductComboBox.ItemsSource = prods;
            if (prods.Count > 0)
            {
                ProductComboBox.SelectedIndex = 0;
                UnitCostBox.Text = prods[0].CostBasis.ToString("F2");
            }

            ProductComboBox.SelectionChanged += (_, _) =>
            {
                if (ProductComboBox.SelectedItem is Product p)
                {
                    UnitCostBox.Text = p.CostBasis.ToString("F2");
                }
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load products: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        if (ProductComboBox.SelectedItem is not Product selected)
        {
            MessageBox.Show("Please select a product.", "Product Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(QtyBox.Text, out var qty) || qty <= 0)
        {
            MessageBox.Show("Please enter a valid quantity greater than zero.", "Invalid Quantity", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(UnitCostBox.Text, out var cost) || cost < 0)
        {
            MessageBox.Show("Please enter a valid non-negative unit cost.", "Invalid Cost", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existing = _lines.FirstOrDefault(l => l.ProductId == selected.ProductId);
        if (existing != null)
        {
            existing.Quantity += qty;
            existing.UnitCost = cost;
        }
        else
        {
            _lines.Add(new GoodsReceivingItemRow
            {
                ProductId = selected.ProductId,
                ProductName = selected.Name,
                Quantity = qty,
                UnitCost = cost
            });
        }

        RefreshTotal();
    }

    private void RemoveLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GoodsReceivingItemRow row)
        {
            _lines.Remove(row);
            RefreshTotal();
        }
    }

    private void RefreshTotal()
    {
        var total = MoneyCalculator.Round(_lines.Sum(l => l.LineTotalCost));
        TotalCostText.Text = $"LKR {total:N2}";
    }

    private async void CommitReceiving_Click(object sender, RoutedEventArgs e)
    {
        var supplier = SupplierBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(supplier))
        {
            MessageBox.Show("Supplier name is required.", "Supplier Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var invoice = InvoiceBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(invoice))
        {
            MessageBox.Show("Invoice or delivery reference is required.", "Invoice Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_lines.Count == 0)
        {
            MessageBox.Show("Please add at least one line item to receive.", "Items Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var cmd = new GoodsReceivingCommand(
                TenantId: "TENANT_LK_01",
                BranchId: _branchId,
                SupplierName: supplier,
                InvoiceReference: invoice,
                Actor: _currentUser,
                Items: _lines.Select(l => new GoodsReceivingItemRequest(l.ProductId, l.Quantity, l.UnitCost)).ToList()
            );

            var receipt = await _receivingService.ReceiveGoodsAsync(cmd);

            MessageBox.Show(
                $"Goods receiving committed successfully!\n\nSupplier: {receipt.SupplierName}\nInvoice: {receipt.InvoiceReference}\nItems Received: {receipt.Lines.Count}\nTotal Cost: LKR {receipt.TotalCost:N2}\n\nInventory levels and moving average cost bases updated.",
                "Receiving Committed",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Receiving failed: {ex.Message}", "Receiving Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

