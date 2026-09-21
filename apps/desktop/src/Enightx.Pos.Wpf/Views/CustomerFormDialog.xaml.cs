using System.Windows;
using Enightx.Pos.Domain;

namespace Enightx.Pos.Wpf.Views;

public partial class CustomerFormDialog : Window
{
    private readonly bool _isEdit;

    public string CustomerName { get; private set; } = "";
    public string CustomerPhone { get; private set; } = "";
    public string? CustomerAddress { get; private set; }
    public decimal CreditLimit { get; private set; }
    public decimal InitialBalance { get; private set; }
    public bool IsCustomerActive { get; private set; } = true;

    public CustomerFormDialog(Customer? existingCustomer = null)
    {
        InitializeComponent();
        if (existingCustomer != null)
        {
            _isEdit = true;
            DialogTitleText.Text = "Edit Customer";
            NameBox.Text = existingCustomer.Name;
            PhoneBox.Text = existingCustomer.Phone;
            AddressBox.Text = existingCustomer.Address ?? "";
            CreditLimitBox.Text = existingCustomer.CreditLimit.ToString("F2");
            ActiveCheckBox.IsChecked = existingCustomer.IsActive;
            InitialBalancePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        var phone = PhoneBox.Text?.Trim() ?? "";
        var addr = AddressBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Please enter a customer name.");
            NameBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(phone))
        {
            ShowError("Please enter a phone number.");
            PhoneBox.Focus();
            return;
        }

        if (!decimal.TryParse(CreditLimitBox.Text, out var limit) || limit < 0)
        {
            ShowError("Please enter a valid credit limit (>= 0).");
            CreditLimitBox.Focus();
            return;
        }

        decimal initBal = 0m;
        if (!_isEdit && (!decimal.TryParse(InitialBalanceBox.Text, out initBal) || initBal < 0))
        {
            ShowError("Please enter a valid initial balance (>= 0).");
            InitialBalanceBox.Focus();
            return;
        }

        CustomerName = name;
        CustomerPhone = phone;
        CustomerAddress = string.IsNullOrWhiteSpace(addr) ? null : addr;
        CreditLimit = limit;
        InitialBalance = initBal;
        IsCustomerActive = ActiveCheckBox.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

