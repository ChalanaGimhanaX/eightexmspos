using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;
using Enightx.Pos.Services;
using Enightx.Pos.Common;

namespace Enightx.Pos.Wpf.Views;

public class GrnLineItem
{
    public required string ProductId { get; set; }
    public required string ProductName { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineTotal => MoneyCalculator.Round(Quantity * UnitCost);
    public string? BatchNumber { get; set; }
}

public partial class ReceivingView : UserControl
{
    private readonly PosDatabase _database;
    private readonly ICatalogService _catalogService;
    private readonly List<GrnLineItem> _items = new();

    public event Action? RequestBackToPos;

    public ReceivingView(PosDatabase database, ICatalogService catalogService)
    {
        InitializeComponent();
        _database = database;
        _catalogService = catalogService;

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

        RefreshGrid();
    }

    private void RefreshGrid()
    {
        GrnItemsGrid.ItemsSource = null;
        GrnItemsGrid.ItemsSource = _items.ToList();

        var totalUnits = _items.Sum(i => i.Quantity);
        var totalCost = _items.Sum(i => i.LineTotal);

        GrnSummaryText.Text = $"GRN Lines: {_items.Count} | Total Units: {totalUnits:F0}";
        TotalCostText.Text = $"LKR {totalCost:F2}";
    }

    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        var selectedProduct = ProductCombo.SelectedItem as Product;
        if (selectedProduct == null)
        {
            MessageBox.Show("Please select a product.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(QtyInput.Text.Trim(), out var qty) || qty <= 0)
        {
            MessageBox.Show("Please enter a valid quantity greater than zero.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(UnitCostInput.Text.Trim(), out var cost) || cost < 0)
        {
            MessageBox.Show("Please enter a valid unit cost.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var batch = string.IsNullOrWhiteSpace(BatchInput.Text) ? null : BatchInput.Text.Trim();

        _items.Add(new GrnLineItem
        {
            ProductId = selectedProduct.ProductId,
            ProductName = selectedProduct.Name,
            Quantity = MoneyCalculator.Round(qty),
            UnitCost = MoneyCalculator.Round(cost),
            BatchNumber = batch
        });

        RefreshGrid();
    }

    private async void CommitGrn_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            MessageBox.Show("Cannot commit an empty Goods Received Note. Please add at least one line item.", "Empty GRN", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var supplier = (SupplierCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unknown";
        var poNumber = PoNumberInput.Text.Trim();
        var invNumber = SupplierInvoiceInput.Text.Trim();

        try
        {
            using var conn = _database.CreateConnection();
            using var tx = conn.BeginTransaction();

            var nowUtc = DateTime.UtcNow.ToString("o");
            var grnId = Guid.NewGuid().ToString();

            foreach (var item in _items)
            {
                // 1. Fetch current stock and cost basis
                decimal currentStock = 0;
                decimal currentCost = 0;

                using (var selectCmd = conn.CreateCommand())
                {
                    selectCmd.Transaction = tx;
                    selectCmd.CommandText = "SELECT stock_on_hand, cost_basis FROM products WHERE product_id = @id;";
                    selectCmd.Parameters.AddWithValue("@id", item.ProductId);
                    using var reader = await selectCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        currentStock = reader.GetDecimal(0);
                        currentCost = reader.GetDecimal(1);
                    }
                }

                // 2. Calculate moving weighted average cost basis
                var totalNewStock = currentStock + item.Quantity;
                decimal newCostBasis;
                if (totalNewStock > 0)
                {
                    newCostBasis = MoneyCalculator.Round(((currentStock * currentCost) + (item.Quantity * item.UnitCost)) / totalNewStock);
                }
                else
                {
                    newCostBasis = item.UnitCost;
                }

                // 3. Update product stock and cost basis
                using (var updateCmd = conn.CreateCommand())
                {
                    updateCmd.Transaction = tx;
                    updateCmd.CommandText = "UPDATE products SET stock_on_hand = @stock, cost_basis = @cost WHERE product_id = @id;";
                    updateCmd.Parameters.AddWithValue("@stock", totalNewStock);
                    updateCmd.Parameters.AddWithValue("@cost", newCostBasis);
                    updateCmd.Parameters.AddWithValue("@id", item.ProductId);
                    await updateCmd.ExecuteNonQueryAsync();
                }

                // 4. Record stock movement
                using (var movCmd = conn.CreateCommand())
                {
                    movCmd.Transaction = tx;
                    movCmd.CommandText = @"INSERT INTO stock_movements (movement_id, product_id, movement_type, quantity_change, reference_id, occurred_at_utc)
                                           VALUES (@movId, @prodId, 'PURCHASE', @qty, @refId, @now);";
                    movCmd.Parameters.AddWithValue("@movId", Guid.NewGuid().ToString());
                    movCmd.Parameters.AddWithValue("@prodId", item.ProductId);
                    movCmd.Parameters.AddWithValue("@qty", item.Quantity);
                    movCmd.Parameters.AddWithValue("@refId", $"GRN:{poNumber}");
                    movCmd.Parameters.AddWithValue("@now", nowUtc);
                    await movCmd.ExecuteNonQueryAsync();
                }
            }

            tx.Commit();

            var totalCost = _items.Sum(i => i.LineTotal);
            var msg = $"GRN committed and posted successfully!\n\nSupplier: {supplier}\nPO: {poNumber}\nInvoice: {invNumber}\nItems Received: {_items.Count}\nTotal Cost: LKR {totalCost:F2}\n\nInventory stock and moving average costs have been updated in the local database.";
            MessageBox.Show(msg, "GRN Posted", MessageBoxButton.OK, MessageBoxImage.Information);

            _items.Clear();
            RefreshGrid();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error committing GRN: {ex.Message}", "GRN Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BackToPos_Click(object sender, RoutedEventArgs e)
    {
        RequestBackToPos?.Invoke();
    }
}

