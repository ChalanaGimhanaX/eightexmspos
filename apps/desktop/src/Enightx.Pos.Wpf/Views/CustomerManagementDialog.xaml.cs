using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class CustomerManagementDialog : Window
{
    private readonly ICustomerService _customerService;
    private readonly User _currentUser;
    private readonly CashShift? _currentShift;
    private List<Customer> _allCustomers = new();
    private Customer? _selectedCustomer;

    public CustomerManagementDialog(ICustomerService customerService, User currentUser, CashShift? currentShift)
    {
        InitializeComponent();
        _customerService = customerService;
        _currentUser = currentUser;
        _currentShift = currentShift;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ReloadCustomersAsync();
    }

    public async Task ReloadCustomersAsync()
    {
        _allCustomers = await _customerService.GetAllCustomersAsync(activeOnly: false);
        ApplyFilter();

        if (_selectedCustomer != null)
        {
            var refreshed = _allCustomers.FirstOrDefault(c => c.CustomerId == _selectedCustomer.CustomerId);
            if (refreshed != null)
            {
                CustomersListBox.SelectedItem = refreshed;
            }
        }
        else if (CustomersListBox.Items.Count > 0)
        {
            CustomersListBox.SelectedIndex = 0;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allCustomers
            : _allCustomers.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                       c.Phone.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        CustomersListBox.ItemsSource = filtered;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private async void CustomersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomersListBox.SelectedItem is Customer customer)
        {
            _selectedCustomer = customer;
            DetailNameText.Text = customer.Name;
            DetailPhoneText.Text = $"📞 {customer.Phone}";
            DetailAddressText.Text = string.IsNullOrWhiteSpace(customer.Address) ? "No address specified" : $"📍 {customer.Address}";
            DetailCreditLimitText.Text = $"LKR {customer.CreditLimit:N2}";
            DetailOutstandingText.Text = $"LKR {customer.OutstandingBalance:N2}";
            var avail = Math.Max(0, customer.CreditLimit - customer.OutstandingBalance);
            DetailAvailableText.Text = $"LKR {avail:N2}";
            DetailActiveBadge.Visibility = customer.IsActive ? Visibility.Visible : Visibility.Collapsed;

            PaymentAmountBox.Text = customer.OutstandingBalance > 0 ? customer.OutstandingBalance.ToString("F2") : "0.00";

            await LoadLedgerAsync(customer.CustomerId);
        }
    }

    private async Task LoadLedgerAsync(string customerId)
    {
        try
        {
            var entries = await _customerService.GetCustomerLedgerAsync(customerId, limit: 100);
            LedgerGrid.ItemsSource = entries;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load customer ledger: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PayFull_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCustomer != null)
        {
            PaymentAmountBox.Text = _selectedCustomer.OutstandingBalance.ToString("F2");
        }
    }

    private async void RecordPayment_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCustomer == null)
        {
            MessageBox.Show("Please select a customer first.", "Customer Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(PaymentAmountBox.Text, out var amount) || amount <= 0)
        {
            MessageBox.Show("Please enter a valid payment amount greater than zero.", "Invalid Amount", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var method = (PaymentMethodCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CASH";
        var notes = PaymentNotesBox.Text?.Trim();

        try
        {
            var entry = await _customerService.RecordPaymentAsync(
                customerId: _selectedCustomer.CustomerId,
                amount: amount,
                paymentMethod: method,
                actorId: _currentUser.UserId,
                shiftId: _currentShift?.ShiftId,
                tenantId: _currentShift?.TenantId ?? "TENANT_LK_01",
                branchId: _currentShift?.BranchId ?? "B01",
                counterId: _currentShift?.CounterId ?? "C01",
                notes: string.IsNullOrWhiteSpace(notes) ? null : notes
            );

            var msg = $"Payment of LKR {amount:N2} ({method}) recorded successfully!\nNew Outstanding Balance: LKR {entry.BalanceAfter:N2}";
            if (method.Equals("CASH", StringComparison.OrdinalIgnoreCase) && _currentShift != null)
            {
                msg += "\n\nDrawer Cash has been updated for the active shift.";
            }

            MessageBox.Show(msg, "Payment Recorded", MessageBoxButton.OK, MessageBoxImage.Information);

            PaymentNotesBox.Text = "";
            await ReloadCustomersAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to record payment: {ex.Message}", "Payment Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void NewCustomer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CustomerFormDialog();
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            try
            {
                var created = await _customerService.CreateCustomerAsync(
                    name: dialog.CustomerName,
                    phone: dialog.CustomerPhone,
                    address: dialog.CustomerAddress,
                    creditLimit: dialog.CreditLimit,
                    actorId: _currentUser.UserId,
                    initialBalance: dialog.InitialBalance
                );

                MessageBox.Show($"Customer '{created.Name}' created successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                _selectedCustomer = created;
                await ReloadCustomersAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to create customer: {ex.Message}", "Creation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void EditCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCustomer == null) return;

        var dialog = new CustomerFormDialog(_selectedCustomer);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            try
            {
                var updated = await _customerService.UpdateCustomerAsync(
                    customerId: _selectedCustomer.CustomerId,
                    name: dialog.CustomerName,
                    phone: dialog.CustomerPhone,
                    address: dialog.CustomerAddress,
                    creditLimit: dialog.CreditLimit,
                    isActive: dialog.IsCustomerActive,
                    actorId: _currentUser.UserId
                );

                MessageBox.Show($"Customer '{updated.Name}' updated successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                _selectedCustomer = updated;
                await ReloadCustomersAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to update customer: {ex.Message}", "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

