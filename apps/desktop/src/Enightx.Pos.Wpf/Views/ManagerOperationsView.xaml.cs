using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Domain;

namespace Enightx.Pos.Wpf.Views;

public partial class ManagerOperationsView : UserControl
{
    private readonly User _currentUser;
    private readonly CashShift _currentShift;

    public event Action? ShiftClosed;

    public ManagerOperationsView()
    {
        InitializeComponent();
        _currentUser = new User
        {
            UserId = "mgr_default",
            Username = "manager",
            DisplayName = "Store Manager",
            Role = Role.Manager,
            PasswordHash = string.Empty,
            PasswordSalt = string.Empty
        };
        _currentShift = new CashShift
        {
            BranchId = "B01",
            CounterId = "C01",
            CashierId = "mgr_default"
        };
    }

    public ManagerOperationsView(User currentUser, CashShift currentShift)
    {
        InitializeComponent();
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _currentShift = currentShift ?? throw new ArgumentNullException(nameof(currentShift));
    }

    private void CashIn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CashMovementDialog(App.ShiftService, _currentUser, _currentShift)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private void Reconcile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ShiftReportDialog(App.ReportService, _currentShift.ShiftId)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private void Refund_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RefundDialog(App.SaleService, _currentUser, _currentShift)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private void Receiving_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GoodsReceivingDialog(App.GoodsReceivingService, App.CatalogService, _currentUser, _currentShift.BranchId)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private void DailyReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DailyReportDialog(App.ReportService, _currentShift.BranchId)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private void Customers_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CustomerManagementDialog(App.CustomerService, _currentUser, _currentShift)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private void CloseShift_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CloseShiftDialog(App.ShiftService, _currentUser, _currentShift)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            ShiftClosed?.Invoke();
        }
    }
}
