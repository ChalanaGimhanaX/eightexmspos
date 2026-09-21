using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;
using Enightx.Pos.Common;

namespace Enightx.Pos.Wpf.Views;

public partial class CustomerLedgerView : UserControl
{
    private readonly PosDatabase _database;
    private readonly string _tenantId;
    private readonly string _branchId;
    private readonly string _counterId;
    private readonly string _actorId;
    private List<Customer> _allCustomers = new();
    private Customer? _selectedCustomer;

    public event Action? RequestBackToPos;

    public CustomerLedgerView(PosDatabase database, string tenantId, string branchId, string counterId, string actorId)
    {
        InitializeComponent();
        _database = database;
        _tenantId = tenantId;
        _branchId = branchId;
        _counterId = counterId;
        _actorId = actorId;

        Loaded += async (s, e) => await LoadCustomersAsync();
    }

    private async Task LoadCustomersAsync()
    {
        _allCustomers.Clear();
        using var conn = _database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT customer_id, tenant_id, name, phone, email, nic_or_brn, credit_limit, current_balance, is_active, created_at_utc FROM customers ORDER BY name ASC;";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            _allCustomers.Add(new Customer
            {
                CustomerId = reader.GetString(0),
                TenantId = reader.GetString(1),
                Name = reader.GetString(2),
                Phone = reader.GetString(3),
                Email = reader.IsDBNull(4) ? null : reader.GetString(4),
                NicOrBrn = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreditLimit = reader.GetDecimal(6),
                CurrentBalance = reader.GetDecimal(7),
                IsActive = reader.GetInt32(8) == 1,
                CreatedAtUtc = DateTime.Parse(reader.GetString(9))
            });
        }

        FilterCustomers();
    }

    private void FilterCustomers()
    {
        var query = SearchBox.Text.Trim().ToLowerInvariant();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allCustomers
            : _allCustomers.Where(c => c.Name.ToLowerInvariant().Contains(query) || c.Phone.Contains(query) || (c.NicOrBrn != null && c.NicOrBrn.ToLowerInvariant().Contains(query))).ToList();

        CustomersGrid.ItemsSource = null;
        CustomersGrid.ItemsSource = filtered;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterCustomers();
    }

    private async void CustomersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedCustomer = CustomersGrid.SelectedItem as Customer;
        if (_selectedCustomer == null)
        {
            SelectedCustomerName.Text = "Select a customer from the list";
            SelectedCustomerDetails.Text = "Phone: -- | NIC/BRN: --";
            CreditLimitText.Text = "LKR 0.00";
            CurrentBalanceText.Text = "LKR 0.00";
            AvailableCreditText.Text = "LKR 0.00";
            LedgerGrid.ItemsSource = null;
            return;
        }

        SelectedCustomerName.Text = _selectedCustomer.Name;
        SelectedCustomerDetails.Text = $"Phone: {_selectedCustomer.Phone} | NIC/BRN: {_selectedCustomer.NicOrBrn ?? "--"}";
        CreditLimitText.Text = $"LKR {_selectedCustomer.CreditLimit:F2}";
        CurrentBalanceText.Text = $"LKR {_selectedCustomer.CurrentBalance:F2}";

        var availableCredit = Math.Max(0m, _selectedCustomer.CreditLimit - _selectedCustomer.CurrentBalance);
        AvailableCreditText.Text = $"LKR {availableCredit:F2}";

        await LoadLedgerEntriesAsync(_selectedCustomer.CustomerId);
    }

    private async Task LoadLedgerEntriesAsync(string customerId)
    {
        var entries = new List<CustomerLedgerEntry>();
        using var conn = _database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT entry_id, tenant_id, customer_id, branch_id, counter_id, entry_type, amount, balance_after, reference_id, payment_method, actor_id, notes, occurred_at_utc 
                            FROM customer_ledger WHERE customer_id = @customerId ORDER BY occurred_at_utc DESC;";
        cmd.Parameters.AddWithValue("@customerId", customerId);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new CustomerLedgerEntry
            {
                EntryId = Guid.Parse(reader.GetString(0)),
                TenantId = reader.GetString(1),
                CustomerId = reader.GetString(2),
                BranchId = reader.GetString(3),
                CounterId = reader.GetString(4),
                EntryType = reader.GetString(5),
                Amount = reader.GetDecimal(6),
                BalanceAfter = reader.GetDecimal(7),
                ReferenceId = reader.IsDBNull(8) ? null : reader.GetString(8),
                PaymentMethod = reader.IsDBNull(9) ? null : reader.GetString(9),
                ActorId = reader.GetString(10),
                Notes = reader.IsDBNull(11) ? null : reader.GetString(11),
                OccurredAtUtc = DateTime.Parse(reader.GetString(12))
            });
        }

        LedgerGrid.ItemsSource = entries;
    }

    private async void SettleDebt_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCustomer == null)
        {
            MessageBox.Show("Please select a customer to record a debt settlement.", "No Customer Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(SettlementAmountInput.Text.Trim(), out var amount) || amount <= 0)
        {
            MessageBox.Show("Please enter a valid settlement amount greater than 0.", "Invalid Amount", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var method = (PaymentMethodCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CASH";
        var reference = string.IsNullOrWhiteSpace(ReferenceInput.Text) ? null : ReferenceInput.Text.Trim();

        try
        {
            var roundedAmount = MoneyCalculator.Round(amount);
            var newBalance = MoneyCalculator.Round(Math.Max(0m, _selectedCustomer.CurrentBalance - roundedAmount));

            using var conn = _database.CreateConnection();
            using var tx = conn.BeginTransaction();

            var entryId = Guid.NewGuid();
            var nowUtc = DateTime.UtcNow;

            // 1. Update customer balance
            using var updateCmd = conn.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = "UPDATE customers SET current_balance = @balance WHERE customer_id = @id;";
            updateCmd.Parameters.AddWithValue("@balance", newBalance);
            updateCmd.Parameters.AddWithValue("@id", _selectedCustomer.CustomerId);
            await updateCmd.ExecuteNonQueryAsync();

            // 2. Insert ledger entry
            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = @"INSERT INTO customer_ledger (entry_id, tenant_id, customer_id, branch_id, counter_id, entry_type, amount, balance_after, reference_id, payment_method, actor_id, notes, occurred_at_utc)
                                      VALUES (@entryId, @tenantId, @customerId, @branchId, @counterId, 'SETTLEMENT', @amount, @balanceAfter, @refId, @method, @actorId, 'Counter Debt Settlement', @occurredAt);";
            insertCmd.Parameters.AddWithValue("@entryId", entryId.ToString());
            insertCmd.Parameters.AddWithValue("@tenantId", _tenantId);
            insertCmd.Parameters.AddWithValue("@customerId", _selectedCustomer.CustomerId);
            insertCmd.Parameters.AddWithValue("@branchId", _branchId);
            insertCmd.Parameters.AddWithValue("@counterId", _counterId);
            insertCmd.Parameters.AddWithValue("@amount", roundedAmount);
            insertCmd.Parameters.AddWithValue("@balanceAfter", newBalance);
            insertCmd.Parameters.AddWithValue("@refId", (object?)reference ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@method", method);
            insertCmd.Parameters.AddWithValue("@actorId", _actorId);
            insertCmd.Parameters.AddWithValue("@occurredAt", nowUtc.ToString("o"));
            await insertCmd.ExecuteNonQueryAsync();

            tx.Commit();

            _selectedCustomer.CurrentBalance = newBalance;
            CurrentBalanceText.Text = $"LKR {newBalance:F2}";
            var availableCredit = Math.Max(0m, _selectedCustomer.CreditLimit - newBalance);
            AvailableCreditText.Text = $"LKR {availableCredit:F2}";
            SettlementAmountInput.Text = "";
            ReferenceInput.Text = "";

            await LoadLedgerEntriesAsync(_selectedCustomer.CustomerId);
            FilterCustomers();

            MessageBox.Show($"Settlement of LKR {roundedAmount:F2} recorded successfully!\nRemaining balance: LKR {newBalance:F2}", "Settlement Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error recording settlement: {ex.Message}", "Settlement Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void NewCustomer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CustomerCreateDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var customerId = $"cust_{Guid.NewGuid():N}"[..12];
            using var conn = _database.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO customers (customer_id, tenant_id, name, phone, email, nic_or_brn, credit_limit, current_balance, is_active, created_at_utc)
                                VALUES (@id, @tenantId, @name, @phone, NULL, @nic, @limit, 0.00, 1, @now);";
            cmd.Parameters.AddWithValue("@id", customerId);
            cmd.Parameters.AddWithValue("@tenantId", _tenantId);
            cmd.Parameters.AddWithValue("@name", dialog.CustomerName);
            cmd.Parameters.AddWithValue("@phone", dialog.CustomerPhone);
            cmd.Parameters.AddWithValue("@nic", (object?)dialog.CustomerNic ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@limit", dialog.CreditLimit);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();

            await LoadCustomersAsync();
            MessageBox.Show($"Customer '{dialog.CustomerName}' registered successfully!", "Customer Registered", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error creating customer: {ex.Message}", "Registration Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BackToPos_Click(object sender, RoutedEventArgs e)
    {
        RequestBackToPos?.Invoke();
    }
}
