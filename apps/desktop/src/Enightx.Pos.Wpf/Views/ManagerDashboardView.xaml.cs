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
    private readonly ISaleService _saleService;
    private readonly IAuthService _authService;
    private readonly IReceiptService _receiptService;

    private List<Sale> _allSales = new();
    private List<User> _allUsers = new();
    private CashShift? _activeShift;

    public event Action? RequestBackToPos;
    public event Action? RequestOpenBilling;
    public event Action? RequestOpenCustomers;
    public event Action? RequestOpenTransfers;
    public event Action? RequestOpenReceiving;
    public event Action? RequestOpenSalesHistory;
    public event Action? RequestOpenCloseShift;

    public ManagerDashboardView(
        User currentUser,
        PosDatabase database,
        ICatalogService catalogService,
        IShiftService shiftService,
        ISaleService saleService,
        IAuthService authService,
        IReceiptService receiptService)
    {
        InitializeComponent();
        _currentUser = currentUser;
        _database = database;
        _catalogService = catalogService;
        _shiftService = shiftService;
        _saleService = saleService;
        _authService = authService;
        _receiptService = receiptService;

        RoleBadgeText.Text = $"ROLE: {_currentUser.Role.ToString().ToUpperInvariant()}";

        Loaded += async (s, e) => await LoadDashboardDataAsync();
    }

    private async Task LoadDashboardDataAsync()
    {
        await LoadSalesKpisAsync();
        await LoadCustomerDebtAsync();
        await LoadActiveShiftAsync();
        await LoadLowStockAlertsAsync();
        await LoadRecentSalesAsync();
        await LoadUsersAsync();
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
            _activeShift = await _shiftService.GetActiveShiftAsync("B01", "C01");
            if (_activeShift != null)
            {
                DrawerCashText.Text = $"LKR {_activeShift.ExpectedCash:F2}";
                DrawerShiftText.Text = $"Shift ID: {_activeShift.ShiftId:N}[..8]";

                ShiftStatusBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 125, 50));
                ShiftStatusText.Text = "SHIFT ACTIVE";
                ActiveCashierInfoText.Text = $"Cashier: {_activeShift.CashierId} | Opened: {_activeShift.OpenedAtUtc:HH:mm:ss} UTC";

                DayOpeningFloatText.Text = $"LKR {_activeShift.OpeningFloat:F2}";
                DayCashReceivedText.Text = $"LKR {_activeShift.CashReceived:F2}";
                DayChangeGivenText.Text = $"LKR {_activeShift.ChangeGiven:F2}";
                DayCashRefundsText.Text = $"LKR {_activeShift.CashRefunds:F2}";
                DayCashInText.Text = $"LKR {_activeShift.CashIn:F2}";
                DayCashOutText.Text = $"LKR {_activeShift.CashOut:F2}";
                DayExpectedCashText.Text = $"LKR {_activeShift.ExpectedCash:F2}";
                CloseShiftDirectBtn.IsEnabled = true;
            }
            else
            {
                DrawerCashText.Text = "LKR 0.00";
                DrawerShiftText.Text = "No Active Shift";

                ShiftStatusBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(198, 40, 40));
                ShiftStatusText.Text = "NO ACTIVE SHIFT";
                ActiveCashierInfoText.Text = "No cashier shift is currently active on terminal C01.";

                DayOpeningFloatText.Text = "LKR 0.00";
                DayCashReceivedText.Text = "LKR 0.00";
                DayChangeGivenText.Text = "LKR 0.00";
                DayCashRefundsText.Text = "LKR 0.00";
                DayCashInText.Text = "LKR 0.00";
                DayCashOutText.Text = "LKR 0.00";
                DayExpectedCashText.Text = "LKR 0.00";
                CloseShiftDirectBtn.IsEnabled = false;
            }
        }
        catch
        {
            DrawerCashText.Text = "LKR 0.00";
            CloseShiftDirectBtn.IsEnabled = false;
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

    private async Task LoadRecentSalesAsync()
    {
        try
        {
            _allSales = await _saleService.GetRecentSalesAsync(50);
            SalesBillsGrid.ItemsSource = _allSales;
            if (_allSales.Count > 0)
            {
                SalesBillsGrid.SelectedIndex = 0;
            }
        }
        catch { }
    }

    private async Task LoadUsersAsync()
    {
        try
        {
            _allUsers = await _authService.GetAllUsersAsync();
            UsersGrid.ItemsSource = _allUsers;
        }
        catch { }
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

    // --- Tab 1: Cashier & Navigation Handlers ---
    private void CountMoneyCloseShift_Click(object sender, RoutedEventArgs e)
    {
        if (_activeShift == null)
        {
            MessageBox.Show("No active shift to close.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ShiftCloseDialog(_activeShift, _shiftService, _currentUser.UserId)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true)
        {
            MessageBox.Show("Shift closed and reconciled successfully!", "Shift Closed", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = LoadDashboardDataAsync();
        }
    }

    private void OpenCashMovement_Click(object sender, RoutedEventArgs e)
    {
        if (_activeShift == null)
        {
            MessageBox.Show("An active shift is required to record drawer cash movements.", "Shift Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new CashMovementDialog(_shiftService, _activeShift, _currentUser)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true)
        {
            _ = LoadDashboardDataAsync();
        }
    }

    private void PrintZReport_Click(object sender, RoutedEventArgs e)
    {
        var zReport = $"========================================\n" +
                      $"         ENIGHTX POS Z-REPORT          \n" +
                      $"========================================\n" +
                      $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                      $"Terminal: C01 | Branch: B01\n" +
                      $"Generated By: {_currentUser.DisplayName} ({_currentUser.Role})\n" +
                      $"----------------------------------------\n" +
                      $"Completed Sales: {OrdersCountText.Text}\n" +
                      $"Gross Revenue:   {TodayRevenueText.Text}\n" +
                      $"Average Ticket:  {AvgTicketText.Text}\n" +
                      $"----------------------------------------\n" +
                      $"TENDERS COLLECTED:\n" +
                      $"Cash:     {TenderCashText.Text}\n" +
                      $"Card:     {TenderCardText.Text}\n" +
                      $"QR:       {TenderQrText.Text}\n" +
                      $"Credit:   {TenderCreditText.Text}\n" +
                      $"----------------------------------------\n" +
                      $"DRAWER EXPECTED: {DrawerCashText.Text}\n" +
                      $"========================================\n" +
                      $"       END OF REPORT (AUDITED)          \n" +
                      $"========================================";

        MessageBox.Show(zReport, "End-of-Day Z-Report", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void QuickNavBilling_Click(object sender, RoutedEventArgs e) => RequestOpenBilling?.Invoke();
    private void QuickNavCustomers_Click(object sender, RoutedEventArgs e) => RequestOpenCustomers?.Invoke();
    private void QuickNavTransfers_Click(object sender, RoutedEventArgs e) => RequestOpenTransfers?.Invoke();
    private void QuickNavReceiving_Click(object sender, RoutedEventArgs e) => RequestOpenReceiving?.Invoke();
    private void QuickNavStockCount_Click(object sender, RoutedEventArgs e) => StartStockCountSession_Click(sender, e);

    // --- Tab 2: Sales Bills Handlers ---
    private void SalesSearch_GotFocus(object sender, RoutedEventArgs e)
    {
        if (SalesSearchInput.Text == "Search receipt / cashier...")
        {
            SalesSearchInput.Text = "";
        }
    }

    private void SalesSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SalesSearchInput.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(query) || query == "search receipt / cashier...")
        {
            SalesBillsGrid.ItemsSource = _allSales;
            return;
        }

        var filtered = _allSales.Where(s =>
            s.ReceiptNumber.ToLowerInvariant().Contains(query) ||
            s.CashierId.ToLowerInvariant().Contains(query)).ToList();
        SalesBillsGrid.ItemsSource = filtered;
    }

    private async void RefreshSales_Click(object sender, RoutedEventArgs e)
    {
        await LoadRecentSalesAsync();
    }

    private void SalesBillsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedSale = SalesBillsGrid.SelectedItem as Sale;
        if (selectedSale != null)
        {
            PreviewReceiptNum.Text = $"Receipt #{selectedSale.ReceiptNumber}";
            PreviewBillMeta.Text = $"Cashier: {selectedSale.CashierId} | {selectedSale.OccurredAtUtc:yyyy-MM-dd HH:mm} UTC | Status: {selectedSale.Status}";
            PreviewItemsList.ItemsSource = selectedSale.Items;
            PreviewTendersList.ItemsSource = selectedSale.Tenders;
            ReprintBillBtn.IsEnabled = true;
        }
        else
        {
            PreviewReceiptNum.Text = "Select a bill from the left to preview";
            PreviewBillMeta.Text = "";
            PreviewItemsList.ItemsSource = null;
            PreviewTendersList.ItemsSource = null;
            ReprintBillBtn.IsEnabled = false;
        }
    }

    private async void ReprintBill_Click(object sender, RoutedEventArgs e)
    {
        var selectedSale = SalesBillsGrid.SelectedItem as Sale;
        if (selectedSale == null) return;

        try
        {
            await _receiptService.ReprintReceiptAsync(selectedSale.SaleId, _currentUser.UserId, "TENANT_LK_01", "B01", "C01");
            MessageBox.Show($"Receipt #{selectedSale.ReceiptNumber} successfully sent to printer!", "Reprint Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to reprint receipt: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void JumpToRefunds_Click(object sender, RoutedEventArgs e)
    {
        RequestOpenSalesHistory?.Invoke();
    }

    // --- Tab 3: Inventory Handlers ---
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
                old_stock = oldStock,
                new_stock = newStock,
                difference = diff,
                reason = reason,
                notes = notes,
                actor = _currentUser.UserId
            });

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                                    VALUES (@evId, 'TENANT_LK_01', 'B01', 'C01', @actor, 'STOCK_ADJUSTMENT', @details, @now);";
                cmd.Parameters.AddWithValue("@evId", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@actor", _currentUser.UserId);
                cmd.Parameters.AddWithValue("@details", details);
                cmd.Parameters.AddWithValue("@now", nowUtc);
                await cmd.ExecuteNonQueryAsync();
            }

            tx.Commit();

            MessageBox.Show($"Stock successfully adjusted for {product.Name}!\nOld: {oldStock:F0} -> New: {newStock:F0} (Change: {diff:+0;-0;0})\nLogged to immutable audit events.", "Stock Adjustment Committed", MessageBoxButton.OK, MessageBoxImage.Information);

            AdjustQtyInput.Text = "";
            AdjustNotesInput.Text = "";

            await LoadLowStockAlertsAsync();
            await LoadAuditTrailAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to commit stock adjustment: {ex.Message}", "Adjustment Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartStockCountSession_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser.Role != Role.Manager && _currentUser.Role != Role.Owner)
        {
            MessageBox.Show("ACCESS DENIED (A09 Security Rule):\nPhysical inventory count sessions are restricted to Store Managers and Owners.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        var dialog = new StockCountDialog(_catalogService, _database, _currentUser)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true)
        {
            _ = LoadDashboardDataAsync();
        }
    }

    // --- Tab 4: User & Role Management Handlers ---
    private async void AddUser_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser.Role != Role.Manager && _currentUser.Role != Role.Owner)
        {
            MessageBox.Show("ACCESS DENIED:\nUser management is restricted to Store Managers and Owners.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        var dialog = new UserEditDialog(_authService, _currentUser)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true)
        {
            await LoadUsersAsync();
            await LoadAuditTrailAsync();
        }
    }

    private async void EditUser_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser.Role != Role.Manager && _currentUser.Role != Role.Owner)
        {
            MessageBox.Show("ACCESS DENIED:\nUser management is restricted to Store Managers and Owners.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        var selectedUser = UsersGrid.SelectedItem as User;
        if (selectedUser == null)
        {
            MessageBox.Show("Please select a user to edit.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new UserEditDialog(_authService, _currentUser, userToEdit: selectedUser)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true)
        {
            await LoadUsersAsync();
            await LoadAuditTrailAsync();
        }
    }

    private async void ToggleUserActive_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser.Role != Role.Manager && _currentUser.Role != Role.Owner)
        {
            MessageBox.Show("ACCESS DENIED:\nUser management is restricted to Store Managers and Owners.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        var selectedUser = UsersGrid.SelectedItem as User;
        if (selectedUser == null)
        {
            MessageBox.Show("Please select a user to activate or deactivate.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selectedUser.UserId == _currentUser.UserId)
        {
            MessageBox.Show("You cannot deactivate your own active account.", "Action Blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newStatus = !selectedUser.IsActive;
        var actionVerb = newStatus ? "activate" : "deactivate";
        var confirm = MessageBox.Show($"Are you sure you want to {actionVerb} user '{selectedUser.Username}'?", "Confirm Action", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _authService.SetUserActiveAsync(selectedUser.UserId, newStatus, _currentUser.UserId, "TENANT_LK_01", "B01", "C01");
            MessageBox.Show($"User '{selectedUser.Username}' has been {(newStatus ? "activated" : "deactivated")}.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadUsersAsync();
            await LoadAuditTrailAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to update user status: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Tab 5: Security Audit Handlers ---
    private async void RefreshAudit_Click(object sender, RoutedEventArgs e)
    {
        await LoadAuditTrailAsync();
    }

    private void AuditLogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var ev = AuditLogGrid.SelectedItem as AuditEvent;
        if (ev != null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(ev.DetailsJson);
                AuditDetailsText.Text = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                AuditDetailsText.Text = ev.DetailsJson;
            }
        }
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
