using System.Windows;

namespace Enightx.Pos.Wpf.Views;

public partial class ReasonPromptDialog : Window
{
    public string ReasonText { get; private set; } = string.Empty;

    public ReasonPromptDialog(string title, string defaultReason = "")
    {
        InitializeComponent();
        PromptTitle.Text = title;
        ReasonInput.Text = defaultReason;
        ReasonInput.Focus();
        ReasonInput.SelectAll();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var text = ReasonInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("A reason is mandatory for audit logging (A08 compliance).", "Reason Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            ReasonInput.Focus();
            return;
        }

        ReasonText = text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

