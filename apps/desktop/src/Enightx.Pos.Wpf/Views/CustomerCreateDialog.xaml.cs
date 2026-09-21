using System.Windows;

namespace Enightx.Pos.Wpf.Views;

public partial class CustomerCreateDialog : Window
{
    public string CustomerName => NameInput.Text.Trim();
    public string CustomerPhone => PhoneInput.Text.Trim();
    public string? CustomerNic => string.IsNullOrWhiteSpace(NicInput.Text) ? null : NicInput.Text.Trim();
    public decimal CreditLimit { get; private set; }

    public CustomerCreateDialog()
    {
        InitializeComponent();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CustomerName))
        {
            MessageBox.Show("Please enter the customer's name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(CustomerPhone))
        {
            MessageBox.Show("Please enter the customer's phone number.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(CreditLimitInput.Text.Trim(), out var limit) || limit < 0)
        {
            MessageBox.Show("Please enter a valid positive credit limit.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CreditLimit = limit;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

