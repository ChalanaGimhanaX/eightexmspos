using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Wpf.Views;

public partial class OwnerAdminView : UserControl
{
    private readonly User _currentUser;
    private readonly CashShift _currentShift;
    private ITransferService? _transferService;
    private ITransferService TransferService => _transferService ??= new TransferService(App.Database, App.CatalogService);
    private IStockCountService? _stockCountService;
    private IStockCountService StockCountService => _stockCountService ??= new StockCountService(App.Database, App.CatalogService);

    public OwnerAdminView()
    {
        InitializeComponent();
        _currentUser = new User
        {
            UserId = "owner_default",
            Username = "owner",
            DisplayName = "Business Owner",
            Role = Role.Owner,
            PasswordHash = string.Empty,
            PasswordSalt = string.Empty
        };
        _currentShift = new CashShift
        {
            BranchId = "B01",
            CounterId = "C01",
            CashierId = "owner_default"
        };
    }

    public OwnerAdminView(User currentUser, CashShift currentShift)
    {
        InitializeComponent();
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _currentShift = currentShift ?? throw new ArgumentNullException(nameof(currentShift));
    }

    private void ProductMaster_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProductManagementDialog(App.CatalogService, _currentUser)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private async void StaffPin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new StaffManagementDialog(App.Database, App.AuthService, _currentShift.BranchId)
            {
                Owner = Window.GetWindow(this)
            };
            await dialog.LoadDataAsync();
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load staff accounts: {ex.Message}", "Staff Access Control", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Transfers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new TransferManagementDialog(TransferService, _currentShift.BranchId, _currentUser.Username)
            {
                Owner = Window.GetWindow(this)
            };
            await dialog.LoadDataAsync();
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load stock transfers: {ex.Message}", "Stock Transfers", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StockCount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new StockCountManagementDialog(StockCountService, _currentShift.BranchId, _currentUser.Username)
            {
                Owner = Window.GetWindow(this)
            };
            await dialog.LoadDataAsync();
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load stock count sessions: {ex.Message}", "Stock Audits", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Valuation_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InventoryValuationDialog(App.ReportService)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncButton.IsEnabled = false;
            SyncStatusText.Text = "Syncing with cloud...";

            var pullResult = await App.SyncService.PullCatalogUpdatesAsync();
            var pushResult = await App.SyncService.PushPendingBatchesAsync();

            if (pullResult.Success && pushResult.Success)
            {
                SyncStatusText.Text = $"Synced: {pullResult.ProductsUpdated} items updated, {pushResult.PushedCount} pushed";
                MessageBox.Show(
                    $"Cloud sync completed successfully!\nProducts updated: {pullResult.ProductsUpdated}\nTransactions uploaded: {pushResult.PushedCount}",
                    "Sync Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            else
            {
                var errorMsg = pullResult.ErrorMessage ?? pushResult.ErrorMessage ?? "Replication server offline";
                SyncStatusText.Text = "Sync: Offline (cached)";
                MessageBox.Show(
                    $"Cloud synchronization notice:\n{errorMsg}\nLocal SQLite database remains fully operational in offline WAL mode.",
                    "Sync Notice",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = "Sync: Offline (cached)";
            MessageBox.Show(
                $"Cloud synchronization notice:\n{ex.Message}\nLocal SQLite database remains fully operational in offline WAL mode.",
                "Sync Notice",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }
}

public sealed class TransferManagementDialog : Window
{
    private readonly ITransferService _transferService;
    private readonly string _branchId;
    private readonly string _username;
    private readonly DataGrid _grid;
    private readonly TextBlock _summaryText;

    public TransferManagementDialog(ITransferService transferService, string branchId, string username)
    {
        _transferService = transferService ?? throw new ArgumentNullException(nameof(transferService));
        _branchId = branchId;
        _username = username;

        Title = "Multi-Branch Stock Transfers & Manifests";
        Width = 860;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");

        var mainGrid = new Grid { Margin = new Thickness(20) };
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var headerPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var titleText = new TextBlock
        {
            Text = "🚚 Multi-Branch Stock Transfers & Dispatches",
            FontSize = 20,
            FontWeight = FontWeights.Bold
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var subtitleText = new TextBlock
        {
            Text = $"Active Branch: {_branchId} | Inter-branch dispatch manifests and in-transit receipts",
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0)
        };
        subtitleText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        headerPanel.Children.Add(titleText);
        headerPanel.Children.Add(subtitleText);
        Grid.SetRow(headerPanel, 0);
        mainGrid.Children.Add(headerPanel);

        // Summary Bar
        var summaryBorder = new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 14),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1)
        };
        summaryBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        summaryBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        _summaryText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        _summaryText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        summaryBorder.Child = _summaryText;
        Grid.SetRow(summaryBorder, 1);
        mainGrid.Children.Add(summaryBorder);

        // DataGrid
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            IsReadOnly = true,
            RowHeight = 44,
            ColumnHeaderHeight = 40,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column
        };
        _grid.SetResourceReference(DataGrid.BackgroundProperty, "SurfaceBrush");
        _grid.SetResourceReference(DataGrid.BorderBrushProperty, "BorderBrush");

        _grid.Columns.Add(new DataGridTextColumn { Header = "Transfer #", Binding = new Binding("TransferNumber"), Width = 170 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Source", Binding = new Binding("SourceBranchId"), Width = 80 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Destination", Binding = new Binding("DestBranchId"), Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Items / Lines", Binding = new Binding("LineCount"), Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Total Qty", Binding = new Binding("TotalDispatchedQuantity"), Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Dispatched By", Binding = new Binding("DispatchedBy"), Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Date (UTC)", Binding = new Binding("DispatchedAt"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        Grid.SetRow(_grid, 2);
        mainGrid.Children.Add(_grid);

        // Footer buttons
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };

        var refreshBtn = new Button
        {
            Content = "🔄 Refresh",
            MinHeight = 48,
            Height = 48,
            MinWidth = 110,
            Margin = new Thickness(0, 0, 10, 0),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        refreshBtn.SetResourceReference(Button.StyleProperty, "TouchPrimaryButtonStyle");
        refreshBtn.Click += async (_, _) => await LoadDataAsync();
        DockPanel.SetDock(refreshBtn, Dock.Left);
        footer.Children.Add(refreshBtn);

        var closeBtn = new Button
        {
            Content = "Close",
            MinHeight = 48,
            Height = 48,
            MinWidth = 100,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeBtn.SetResourceReference(Button.StyleProperty, "TouchPrimaryButtonStyle");
        closeBtn.Click += (_, _) => Close();
        DockPanel.SetDock(closeBtn, Dock.Right);
        footer.Children.Add(closeBtn);

        Grid.SetRow(footer, 3);
        mainGrid.Children.Add(footer);

        Content = mainGrid;
    }

    public async Task LoadDataAsync()
    {
        var transfers = await _transferService.GetTransfersAsync();
        int inTransit = transfers.Count(t => t.Status == TransferStatus.InTransit);
        int received = transfers.Count(t => t.Status == TransferStatus.Received);
        int other = transfers.Count - inTransit - received;

        _summaryText.Text = $"Total Transfers: {transfers.Count}  |  In-Transit: {inTransit}  |  Received: {received}  |  Other: {other}  |  Total Lines: {transfers.Sum(t => t.Lines?.Count ?? 0)}";

        var viewItems = transfers.Select(t => new
        {
            t.TransferNumber,
            t.SourceBranchId,
            t.DestBranchId,
            Status = t.Status.ToString(),
            LineCount = t.Lines?.Count ?? 0,
            TotalDispatchedQuantity = t.TotalDispatchedQuantity.ToString("N0"),
            t.DispatchedBy,
            DispatchedAt = t.DispatchedAtUtc.ToString("yyyy-MM-dd HH:mm")
        }).ToList();

        _grid.ItemsSource = viewItems;
    }
}

public sealed class StockCountManagementDialog : Window
{
    private readonly IStockCountService _stockCountService;
    private readonly string _branchId;
    private readonly string _username;
    private readonly DataGrid _grid;
    private readonly TextBlock _statusText;

    public StockCountManagementDialog(IStockCountService stockCountService, string branchId, string username)
    {
        _stockCountService = stockCountService ?? throw new ArgumentNullException(nameof(stockCountService));
        _branchId = branchId;
        _username = username;

        Title = "Physical Stock Count Audits";
        Width = 860;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");

        var mainGrid = new Grid { Margin = new Thickness(20) };
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var headerPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var titleText = new TextBlock
        {
            Text = "📋 Physical Stock Count Audits & Inventory Sessions",
            FontSize = 20,
            FontWeight = FontWeights.Bold
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var subtitleText = new TextBlock
        {
            Text = $"Active Branch: {_branchId} | Periodic physical counts, freeze stock cutoffs, and reconcile variance",
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0)
        };
        subtitleText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        headerPanel.Children.Add(titleText);
        headerPanel.Children.Add(subtitleText);
        Grid.SetRow(headerPanel, 0);
        mainGrid.Children.Add(headerPanel);

        // Status banner
        var statusBorder = new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 14),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1)
        };
        statusBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        statusBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        _statusText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        statusBorder.Child = _statusText;
        Grid.SetRow(statusBorder, 1);
        mainGrid.Children.Add(statusBorder);

        // DataGrid
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            IsReadOnly = true,
            RowHeight = 44,
            ColumnHeaderHeight = 40,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column
        };
        _grid.SetResourceReference(DataGrid.BackgroundProperty, "SurfaceBrush");
        _grid.SetResourceReference(DataGrid.BorderBrushProperty, "BorderBrush");

        _grid.Columns.Add(new DataGridTextColumn { Header = "Session ID", Binding = new Binding("SessionId"), Width = 220 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Started By", Binding = new Binding("StartedBy"), Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Started Date", Binding = new Binding("StartedAt"), Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Items Audited", Binding = new Binding("ItemsAudited"), Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Total Variance", Binding = new Binding("Variance"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        Grid.SetRow(_grid, 2);
        mainGrid.Children.Add(_grid);

        // Footer buttons
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };

        var startSessionBtn = new Button
        {
            Content = "▶️ Start New Count Session",
            MinHeight = 48,
            Height = 48,
            MinWidth = 180,
            Margin = new Thickness(0, 0, 10, 0),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        startSessionBtn.SetResourceReference(Button.StyleProperty, "TouchPrimaryButtonStyle");
        startSessionBtn.Click += async (_, _) =>
        {
            try
            {
                startSessionBtn.IsEnabled = false;
                await _stockCountService.StartSessionAsync(_branchId, _username, "TENANT_LK_01", notes: "Owner initiated physical count audit");
                await LoadDataAsync();
                MessageBox.Show("Stock count audit session initiated successfully.\nStock movement cutoff active.", "Audit Started", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not start count session: {ex.Message}", "Stock Audit Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                startSessionBtn.IsEnabled = true;
            }
        };
        DockPanel.SetDock(startSessionBtn, Dock.Left);
        footer.Children.Add(startSessionBtn);

        var closeBtn = new Button
        {
            Content = "Close",
            MinHeight = 48,
            Height = 48,
            MinWidth = 100,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeBtn.SetResourceReference(Button.StyleProperty, "TouchPrimaryButtonStyle");
        closeBtn.Click += (_, _) => Close();
        DockPanel.SetDock(closeBtn, Dock.Right);
        footer.Children.Add(closeBtn);

        Grid.SetRow(footer, 3);
        mainGrid.Children.Add(footer);

        Content = mainGrid;
    }

    public async Task LoadDataAsync()
    {
        var sessions = await _stockCountService.GetSessionsAsync(_branchId);
        var active = sessions.FirstOrDefault(s => s.Status == StockCountStatus.InProgress);

        if (active != null)
        {
            _statusText.Text = $"ACTIVE AUDIT SESSION IN PROGRESS: Session {active.SessionId[..Math.Min(8, active.SessionId.Length)]}... started by {active.StartedBy} on {active.StartedAtUtc:yyyy-MM-dd HH:mm}. Movement cutoff active.";
        }
        else
        {
            _statusText.Text = $"No active stock audit session in progress. {sessions.Count} completed/historical sessions on record.";
        }

        var viewItems = sessions.Select(s => new
        {
            s.SessionId,
            Status = s.Status.ToString(),
            s.StartedBy,
            StartedAt = s.StartedAtUtc.ToString("yyyy-MM-dd HH:mm"),
            ItemsAudited = s.TotalItemsCounted.ToString(),
            Variance = s.TotalVarianceQuantity.ToString("N0")
        }).ToList();

        _grid.ItemsSource = viewItems;
    }
}

public sealed class StaffManagementDialog : Window
{
    private readonly PosDatabase _db;
    private readonly IAuthService _authService;
    private readonly string _branchId;
    private readonly DataGrid _grid;
    private readonly TextBox _pinInput;
    private readonly TextBlock _statusText;

    private class StaffItem
    {
        public string UserId { get; set; } = "";
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Role { get; set; } = "";
        public string Status { get; set; } = "";
        public string PinConfigured { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }

    public StaffManagementDialog(PosDatabase db, IAuthService authService, string branchId)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _branchId = branchId;

        Title = "Staff PIN & Access Control";
        Width = 860;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");

        var mainGrid = new Grid { Margin = new Thickness(20) };
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var headerPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var titleText = new TextBlock
        {
            Text = "🔑 Staff Credentials & PIN Access Control",
            FontSize = 20,
            FontWeight = FontWeights.Bold
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var subtitleText = new TextBlock
        {
            Text = $"Active Branch: {_branchId} | Configure 4-digit authorization PINs and manage operator credentials",
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0)
        };
        subtitleText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        headerPanel.Children.Add(titleText);
        headerPanel.Children.Add(subtitleText);
        Grid.SetRow(headerPanel, 0);
        mainGrid.Children.Add(headerPanel);

        // Status / Instructions
        var statusBorder = new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 14),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1)
        };
        statusBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        statusBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        _statusText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Text = "Select a staff member from the grid below and enter a 4-digit PIN to configure quick supervisor/cashier access."
        };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        statusBorder.Child = _statusText;
        Grid.SetRow(statusBorder, 1);
        mainGrid.Children.Add(statusBorder);

        // DataGrid
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            IsReadOnly = true,
            RowHeight = 44,
            ColumnHeaderHeight = 40,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column
        };
        _grid.SetResourceReference(DataGrid.BackgroundProperty, "SurfaceBrush");
        _grid.SetResourceReference(DataGrid.BorderBrushProperty, "BorderBrush");

        _grid.Columns.Add(new DataGridTextColumn { Header = "Username", Binding = new Binding("Username"), Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Full Name", Binding = new Binding("DisplayName"), Width = 180 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Role", Binding = new Binding("Role"), Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "PIN Configured", Binding = new Binding("PinConfigured"), Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Created Date", Binding = new Binding("CreatedAt"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        Grid.SetRow(_grid, 2);
        mainGrid.Children.Add(_grid);

        // Footer PIN update panel
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };

        var pinPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var pinLabel = new TextBlock
        {
            Text = "New 4-Digit PIN:",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        pinLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        pinPanel.Children.Add(pinLabel);

        _pinInput = new TextBox
        {
            Width = 100,
            Height = 48,
            MinHeight = 48,
            MinWidth = 48,
            MaxLength = 6,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        _pinInput.SetResourceReference(TextBox.BackgroundProperty, "SurfaceBrush");
        _pinInput.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");
        _pinInput.SetResourceReference(TextBox.BorderBrushProperty, "InputBorderBrush");
        pinPanel.Children.Add(_pinInput);

        var updatePinBtn = new Button
        {
            Content = "Update Staff PIN",
            MinHeight = 48,
            Height = 48,
            MinWidth = 130,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        updatePinBtn.SetResourceReference(Button.StyleProperty, "TouchPrimaryButtonStyle");
        updatePinBtn.Click += async (_, _) =>
        {
            if (_grid.SelectedItem is not StaffItem selectedStaff)
            {
                MessageBox.Show("Please select a staff member from the list to set their PIN.", "Staff Selection Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var pin = _pinInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(pin) || pin.Length < 4)
            {
                MessageBox.Show("PIN must be at least 4 digits.", "Invalid PIN", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                updatePinBtn.IsEnabled = false;
                await _authService.SetUserPinAsync(selectedStaff.UserId, pin);
                await LoadDataAsync();
                _pinInput.Clear();
                MessageBox.Show($"PIN successfully updated for {selectedStaff.DisplayName}.\nSalted PBKDF2 hash stored in database.", "PIN Updated", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting PIN: {ex.Message}", "Security Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                updatePinBtn.IsEnabled = true;
            }
        };
        pinPanel.Children.Add(updatePinBtn);

        DockPanel.SetDock(pinPanel, Dock.Left);
        footer.Children.Add(pinPanel);

        var closeBtn = new Button
        {
            Content = "Close",
            MinHeight = 48,
            Height = 48,
            MinWidth = 100,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeBtn.SetResourceReference(Button.StyleProperty, "TouchPrimaryButtonStyle");
        closeBtn.Click += (_, _) => Close();
        DockPanel.SetDock(closeBtn, Dock.Right);
        footer.Children.Add(closeBtn);

        Grid.SetRow(footer, 3);
        mainGrid.Children.Add(footer);

        Content = mainGrid;
    }

    public async Task LoadDataAsync()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT user_id, username, display_name, role, is_active, created_at_utc, pin_hash
            FROM users
            ORDER BY role DESC, username ASC;
        ";

        var items = new List<StaffItem>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var roleNum = reader.GetInt32(3);
            var roleName = ((Role)roleNum).ToString();
            var isActive = reader.GetInt32(4) == 1;
            var created = reader.GetString(5);
            var hasPin = !reader.IsDBNull(6) && !string.IsNullOrEmpty(reader.GetString(6));

            items.Add(new StaffItem
            {
                UserId = reader.GetString(0),
                Username = reader.GetString(1),
                DisplayName = reader.GetString(2),
                Role = roleName,
                Status = isActive ? "Active" : "Inactive",
                PinConfigured = hasPin ? "Configured" : "Not Set",
                CreatedAt = DateTime.TryParse(created, out var dt) ? dt.ToString("yyyy-MM-dd") : created
            });
        }

        _grid.ItemsSource = items;
    }
}
