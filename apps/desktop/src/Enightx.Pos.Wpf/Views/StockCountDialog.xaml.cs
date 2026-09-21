using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Wpf.Views;

public class StockCountItemViewModel : INotifyPropertyChanged
{
    private decimal _physicalCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string ProductId { get; set; }
    public required string Barcode { get; set; }
    public required string Name { get; set; }
    public decimal CutoffStock { get; set; }

    public decimal PhysicalCount
    {
        get => _physicalCount;
        set
        {
            if (_physicalCount != value)
            {
                _physicalCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Variance));
            }
        }
    }

    public decimal Variance => PhysicalCount - CutoffStock;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public partial class StockCountDialog : Window
{
    private readonly ICatalogService _catalogService;
    private readonly PosDatabase _db;
    private readonly User _user;
    private readonly DateTime _cutoffUtc;
    private readonly ObservableCollection<StockCountItemViewModel> _items = new();

    public StockCountDialog(ICatalogService catalogService, PosDatabase db, User user)
    {
        InitializeComponent();
        _catalogService = catalogService;
        _db = db;
        _user = user;
        _cutoffUtc = DateTime.UtcNow;

        CutoffInfoText.Text = $"Count Cutoff Time: {_cutoffUtc:yyyy-MM-dd HH:mm:ss} UTC | Logged by: {user.DisplayName}";
        CountGrid.ItemsSource = _items;

        Loaded += async (s, e) => await LoadProductsAsync();
    }

    private async Task LoadProductsAsync()
    {
        try
        {
            var prods = await _catalogService.GetAllActiveProductsAsync();
            _items.Clear();

            foreach (var p in prods)
            {
                _items.Add(new StockCountItemViewModel
                {
                    ProductId = p.ProductId,
                    Barcode = p.Barcode,
                    Name = p.Name,
                    CutoffStock = p.StockOnHand,
                    PhysicalCount = p.StockOnHand
                });
            }

            UpdateSummary();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load products: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CountInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var discrepancies = _items.Count(i => i.Variance != 0);
        SummaryText.Text = $"{discrepancies} item(s) with count discrepancies";
    }

    private async void CommitReconciliation_Click(object sender, RoutedEventArgs e)
    {
        var discrepancies = _items.Where(i => i.Variance != 0).ToList();
        if (discrepancies.Count == 0)
        {
            MessageBox.Show("No stock discrepancies found. All physical counts match system records.", "Balanced Count", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
            return;
        }

        var confirmMsg = $"Reconciliation will adjust {discrepancies.Count} product(s) in the database based on the cutoff snapshot.\n\nProceed?";
        if (MessageBox.Show(confirmMsg, "Confirm Reconciliation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            using var conn = _db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var sessionId = Guid.NewGuid().ToString();

            foreach (var item in discrepancies)
            {
                // 1. Additive adjustment to stock on hand (preserves concurrent sales)
                using var stockCmd = conn.CreateCommand();
                stockCmd.Transaction = tx;
                stockCmd.CommandText = @"
                    UPDATE products
                    SET stock_on_hand = stock_on_hand + $variance
                    WHERE product_id = $pid;
                ";
                stockCmd.Parameters.AddWithValue("$variance", item.Variance);
                stockCmd.Parameters.AddWithValue("$pid", item.ProductId);
                await stockCmd.ExecuteNonQueryAsync();

                // 2. Log stock movement
                using var mCmd = conn.CreateCommand();
                mCmd.Transaction = tx;
                mCmd.CommandText = @"
                    INSERT INTO stock_movements (movement_id, product_id, movement_type, quantity_change, reference_id, created_at_utc)
                    VALUES ($mid, $pid, 'COUNT_ADJUSTMENT', $qty, $ref, $created);
                ";
                mCmd.Parameters.AddWithValue("$mid", Guid.NewGuid().ToString());
                mCmd.Parameters.AddWithValue("$pid", item.ProductId);
                mCmd.Parameters.AddWithValue("$qty", item.Variance);
                mCmd.Parameters.AddWithValue("$ref", sessionId);
                mCmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o"));
                await mCmd.ExecuteNonQueryAsync();

                // 3. Log audit event
                using var aCmd = conn.CreateCommand();
                aCmd.Transaction = tx;
                aCmd.CommandText = @"
                    INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                    VALUES ($eid, 'TENANT_LK_01', 'B01', 'C01', $actor, 'COUNT_ADJUSTMENT', $details, $created);
                ";
                aCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
                aCmd.Parameters.AddWithValue("$actor", _user.UserId);
                aCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
                {
                    SessionId = sessionId,
                    ProductId = item.ProductId,
                    CutoffStock = item.CutoffStock,
                    PhysicalCount = item.PhysicalCount,
                    Variance = item.Variance,
                    CutoffUtc = _cutoffUtc
                }));
                aCmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o"));
                await aCmd.ExecuteNonQueryAsync();
            }

            tx.Commit();

            MessageBox.Show($"Inventory count reconciliation committed successfully!\n{discrepancies.Count} item(s) adjusted.", "Reconciliation Success", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to commit count reconciliation: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

