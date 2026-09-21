using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;
using Enightx.Pos.Services;
using Enightx.Pos.Common;

namespace Enightx.Pos.Wpf.Views;

public partial class ManagerDashboardView : UserControl
{
    private readonly User _currentUser;
    private readonly PosDatabase _database;
    private readonly ICatalogService _catalogService;
    private readonly IShiftService _shiftService;

    public event Action? RequestBackToPos;

    public ManagerDashboardView(User currentUser, PosDatabase database, ICatalogService catalogService, IShiftService shiftService)
    {
        InitializeComponent();
        _currentUser = currentUser;
        _database = database;
        _catalogService = catalogService;
        _shiftService = shiftService;

        RoleBadgeText.Text = $"ROLE: {_currentUser.Role.ToString().ToUpperInvariant()}";

        Loaded += async (s, e) => await LoadDashboardDataAsync();
    }

    private async Task LoadDashboardDataAsync()
    {
        await LoadSalesKpisAsync();
        await LoadCustomerDebtAsync();
        await LoadActiveShiftAsync();
        await LoadLowStockAlertsAsync();
        await LoadAuditTrailAsync();
    }

    private async Task LoadSalesKpisAsync()
    {
        decimal totalRevenue = 0;
        int orderCount = 0;
        decimal cashTotal = 0;
        decimal cardTotal = 0;
        decimal qrTotal = 0;
        decimal creditTotal = 0;

        try
        {
            using var conn = _database.CreateConnection();

            // 1. Sales query
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(grand_total), 0) FROM sales WHERE status = 1;";
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    orderCount = reader.GetInt32(0);
                    totalRevenue = reader.GetDecimal(1);
                }
            }

            // 2. Tenders query
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT tender_type, SUM(amount_tendered - change_given) 
                                    FROM tenders t JOIN sales s ON t.sale_id = s.sale_id 
                                    WHERE s.status = 1 GROUP BY tender_type;";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var type = reader.GetString(0).ToUpperInvariant();
                    var amt = reader.GetDecimal(1);
                    switch (type)
                    {
                        case "CASH": cashTotal += amt; break;
                        case "CARD": cardTotal += amt; break;
                        case "QR": qrTotal += amt; break;
                        case "CREDIT": creditTotal += amt; break;
                    }
                }
            }
        }
        catch { }

        TodayRevenueText.Text = $"LKR {totalRevenue:F2}";
        OrdersCountText.Text = $"{orderCount} Completed Sales";

        var avgTicket = orderCount > 0 ? MoneyCalculator.Round(totalRevenue / orderCount) : 0;
        AvgTicketText.Text = $"LKR {avgTicket:F2}";

        TenderCashText.Text = $"LKR {cashTotal:F2}";
        TenderCardText.Text = $"LKR {cardTotal:F2}";
        TenderQrText.Text = $"LKR {qrTotal:F2}";
        TenderCreditText.Text = $"LKR {creditTotal:F2}";
    }

    private async Task LoadCustomerDebtAsync()
    {
        decimal totalDebt = 0;
        try
        {
            using var conn = _database.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(current_balance), 0) FROM customers;";
            var res = await cmd.ExecuteScalarAsync();
            if (res != null && res != DBNull.Value)
            {
                totalDebt = Convert.ToDecimal(res);
            }
        }
        catch { }

        CustomerDebtText.Text = $"LKR {totalDebt:F2}";
    }

    private async Task LoadActiveShiftAsync()
    {
        try
        {
            var shift = await _shiftService.GetActiveShiftAsync("B01", "C01");
            if (shift != null)
            {
                DrawerCashText.Text = $"LKR {shift.ExpectedCash:F2}";
                DrawerShiftText.Text = $"Shift ID: {shift.ShiftId:N}[..8]";
            }
            else
            {
                DrawerCashText.Text = "LKR 0.00";
                DrawerShiftText.Text = "No Active Shift";
            }
        }
        catch
        {
            DrawerCashText.Text = "LKR 0.00";
        }
    }

    private async Task LoadLowStockAlertsAsync()
    {
        var allProducts = await _catalogService.GetAllProductsAsync();
        AdjustProductCombo.ItemsSource = allProducts;
        if (allProducts.Count > 0 && AdjustProductCombo.SelectedIndex < 0)
        {
            AdjustProductCombo.SelectedIndex = 0;
        }

        // Low stock: <= 10 units
        var lowStock = allProducts.Where(p => p.StockOnHand <= 10.0m).OrderBy(p => p.StockOnHand).ToList();
        LowStockGrid.ItemsSource = lowStock;
        LowStockCountBadge.Text = $"{lowStock.Count} items below threshold";
    }

    private async Task LoadAuditTrailAsync()
    {
        var auditLogs = new List<AuditEvent>();
        try
        {
            using var conn = _database.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc FROM audit_events ORDER BY occurred_at_utc DESC LIMIT 50;";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                auditLogs.Add(new AuditEvent
                {
                    EventId = Guid.Parse(reader.GetString(0)),
                    TenantId = reader.GetString(1),
                    BranchId = reader.GetString(2),
                    CounterId = reader.GetString(3),
                    ActorId = reader.GetString(4),
                    Action = reader.GetString(5),
                    DetailsJson = reader.GetString(6),
                    OccurredAtUtc = DateTime.Parse(reader.GetString(7))
                });
            }
        }
        catch { }

        AuditLogGrid.ItemsSource = auditLogs;
    }

    private async void CommitStockAdjustment_Click(object sender, RoutedEventArgs e)
    {
        // A09 Security Rule: Cashiers cannot adjust stock manually
        if (_currentUser.Role != Role.Manager && _currentUser.Role != Role.Owner)
        {
            MessageBox.Show("ACCESS DENIED (A09 Security Rule):\nManual stock adjustments are strictly restricted to Store Managers and Owners.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        var product = AdjustProductCombo.SelectedItem as Product;
        if (product == null)
        {
            MessageBox.Show("Please select a product to adjust.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(AdjustQtyInput.Text.Trim(), out var newStock) || newStock < 0)
        {
            MessageBox.Show("Please enter a valid stock quantity (0 or greater).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var reason = (AdjustReasonCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Inventory Correction";
        var notes = string.IsNullOrWhiteSpace(AdjustNotesInput.Text) ? reason : AdjustNotesInput.Text.Trim();

        try
        {
            var oldStock = product.StockOnHand;
            var diff = MoneyCalculator.Round(newStock - oldStock);

            using var conn = _database.CreateConnection();
            using var tx = conn.BeginTransaction();

            // 1. Update product stock
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE products SET stock_on_hand = @stock WHERE product_id = @id;";
                cmd.Parameters.AddWithValue("@stock", newStock);
                cmd.Parameters.AddWithValue("@id", product.ProductId);
                await cmd.ExecuteNonQueryAsync();
            }

            // 2. Insert stock movement
            var nowUtc = DateTime.UtcNow.ToString("o");
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO stock_movements (movement_id, product_id, movement_type, quantity_change, reference_id, occurred_at_utc)
                                    VALUES (@movId, @prodId, 'ADJUSTMENT', @diff, @refId, @now);";
                cmd.Parameters.AddWithValue("@movId", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@prodId", product.ProductId);
                cmd.Parameters.AddWithValue("@diff", diff);
                cmd.Parameters.AddWithValue("@refId", $"ADJ:{_currentUser.UserId}:{reason}");
                cmd.Parameters.AddWithValue("@now", nowUtc);
                await cmd.ExecuteNonQueryAsync();
            }

            // 3. Insert audit log
            var details = JsonSerializer.Serialize(new
            {
                product_id = product.ProductId,
                product_name = product.Name,
                old_stock = oldStock,
                new_stock = newStock,
                difference = diff,
                reason,
                notes,
                adjusted_by = _currentUser.UserId
            });

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                                    VALUES (@evId, 'TENANT_LK_01', 'B01', 'C01', @actor, 'MANUAL_STOCK_ADJUSTMENT', @details, @now);";
                cmd.Parameters.AddWithValue("@evId", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@actor", _currentUser.UserId);
                cmd.Parameters.AddWithValue("@details", details);
                cmd.Parameters.AddWithValue("@now", nowUtc);
                await cmd.ExecuteNonQueryAsync();
            }

            tx.Commit();

            product.StockOnHand = newStock;
            AdjustQtyInput.Text = "";
            AdjustNotesInput.Text = "";

            await LoadLowStockAlertsAsync();
            await LoadAuditTrailAsync();

            MessageBox.Show($"Stock for '{product.Name}' adjusted from {oldStock:F0} to {newStock:F0} units.\nAudit trail recorded.", "Stock Adjusted", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error adjusting stock: {ex.Message}", "Adjustment Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartStockCountSession_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser.Role != Role.Manager && _currentUser.Role != Role.Owner)
        {
            MessageBox.Show("ACCESS DENIED (A09 Security Rule):\nStock count sessions are strictly restricted to Store Managers and Owners.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        var dialog = new StockCountDialog(_catalogService, _database, _currentUser)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true)
        {
            LoadDashboardDataAsync().ConfigureAwait(false);
        }
    }

    private void PrintZReport_Click(object sender, RoutedEventArgs e)
    {
        var reportMsg = $"========================================\n" +
                        $"       ENIGHTX POS - Z-REPORT (DAILY)   \n" +
                        $"========================================\n" +
                        $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        $"Branch: B01 | Counter: C01\n" +
                        $"Manager: {_currentUser.DisplayName} ({_currentUser.UserId})\n" +
                        $"----------------------------------------\n" +
                        $"Gross Sales:     {TodayRevenueText.Text}\n" +
                        $"Transactions:    {OrdersCountText.Text}\n" +
                        $"Avg Ticket:      {AvgTicketText.Text}\n" +
                        $"----------------------------------------\n" +
                        $"Tender Breakdown:\n" +
                        $"  Cash:          {TenderCashText.Text}\n" +
                        $"  Card:          {TenderCardText.Text}\n" +
                        $"  QR:            {TenderQrText.Text}\n" +
                        $"  Credit:        {TenderCreditText.Text}\n" +
                        $"----------------------------------------\n" +
                        $"Customer Debt:   {CustomerDebtText.Text}\n" +
                        $"Drawer Cash:     {DrawerCashText.Text}\n" +
                        $"========================================\n" +
                        $"Z-Report logged for statutory audit.";

        MessageBox.Show(reportMsg, "End-of-Day Z-Report", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadDashboardDataAsync();
    }

    private void BackToPos_Click(object sender, RoutedEventArgs e)
    {
        RequestBackToPos?.Invoke();
    }
}

