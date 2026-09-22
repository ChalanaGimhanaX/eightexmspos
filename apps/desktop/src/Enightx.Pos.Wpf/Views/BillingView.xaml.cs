using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Enightx.Pos.Domain;
using Enightx.Pos.Wpf.ViewModels;

namespace Enightx.Pos.Wpf.Views;

public partial class BillingView : UserControl
{
    public event Action? RequestPayment;
    public event Action<string>? RequestPaymentWithTender;

    public BillingView()
    {
        InitializeComponent();
    }

    private async void BillingView_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            await vm.LoadCatalogAsync();
            await vm.LoadHeldCartsAsync();
            BarcodeInputBox.Focus();
        }
    }

    private async void AddItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            await vm.AddItemByBarcodeAsync(BarcodeInputBox.Text);
            BarcodeInputBox.Focus();
        }
    }

    private async void BarcodeInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is BillingViewModel vm)
        {
            await vm.AddItemByBarcodeAsync(BarcodeInputBox.Text);
            BarcodeInputBox.Focus();
        }
    }

    private void ProductCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is Product product && DataContext is BillingViewModel vm)
        {
            vm.AddProductToCart(product);
        }
    }

    private void CategoryFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is Category cat && DataContext is BillingViewModel vm)
        {
            vm.SelectedCategory = (vm.SelectedCategory == cat) ? null : cat;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            vm.SearchQuery = SearchBox.Text;
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
    }

    private void IncrementQty_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is CartItemViewModel item && DataContext is BillingViewModel vm)
        {
            vm.IncrementItem(item);
        }
    }

    private void DecrementQty_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is CartItemViewModel item && DataContext is BillingViewModel vm)
        {
            vm.DecrementItem(item);
        }
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is CartItemViewModel item && DataContext is BillingViewModel vm)
        {
            vm.RemoveItem(item);
        }
    }

    private void PayCash_Click(object sender, RoutedEventArgs e)
    {
        RequestPaymentWithTender?.Invoke("CASH");
        RequestPayment?.Invoke();
    }

    private void PayCard_Click(object sender, RoutedEventArgs e)
    {
        RequestPaymentWithTender?.Invoke("CARD");
        RequestPayment?.Invoke();
    }

    private void PaySplit_Click(object sender, RoutedEventArgs e)
    {
        RequestPaymentWithTender?.Invoke("SPLIT");
        RequestPayment?.Invoke();
    }

    private void ClearCart_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            if (vm.CartItems.Count > 0)
            {
                var result = MessageBox.Show(
                    "Are you sure you want to clear the active cart?",
                    "Clear Cart",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );
                if (result == MessageBoxResult.Yes)
                {
                    vm.ClearCart();
                }
            }
        }
    }

    private async void HoldCart_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            if (vm.CartItems.Count == 0)
            {
                MessageBox.Show("Cannot hold an empty cart.", "Empty Cart", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var success = await vm.HoldCurrentCartAsync();
            if (success)
            {
                MessageBox.Show("Current bill has been parked successfully.", "Bill Parked", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private async void RecallHeldCarts_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            await vm.LoadHeldCartsAsync();
            var dialog = new HeldCartsDialog(vm)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
        }
    }

    private async void OverridePrice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is CartItemViewModel item && DataContext is BillingViewModel vm)
        {
            var dialog = new Window
            {
                Title = "Price Override Authorization",
                Width = 420,
                Height = 310,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush")
            };

            var sp = new StackPanel { Margin = new Thickness(20) };
            sp.Children.Add(new TextBlock
            {
                Text = $"Override Price for: {item.ProductName}",
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            });
            sp.Children.Add(new TextBlock
            {
                Text = $"Current Price: Rs. {item.UnitPrice:#,##0.00}",
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
            });

            sp.Children.Add(new TextBlock { Text = "New Unit Price (Rs.):", Margin = new Thickness(0, 0, 0, 4), Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush") });
            var priceBox = new TextBox { Height = 40, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(priceBox);

            sp.Children.Add(new TextBlock { Text = "Reason (Required):", Margin = new Thickness(0, 0, 0, 4), Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush") });
            var reasonBox = new TextBox { Height = 40, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
            sp.Children.Add(reasonBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = new Button { Content = "Cancel", Width = 90, Height = 38, Margin = new Thickness(0, 0, 8, 0) };
            cancelBtn.Click += (_, _) => dialog.DialogResult = false;
            var confirmBtn = new Button { Content = "Confirm", Width = 90, Height = 38 };
            confirmBtn.Click += (_, _) => dialog.DialogResult = true;

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(confirmBtn);
            sp.Children.Add(btnPanel);

            dialog.Content = sp;
            priceBox.Focus();

            if (dialog.ShowDialog() == true)
            {
                if (!decimal.TryParse(priceBox.Text?.Trim(), out var newPrice) || newPrice < 0)
                {
                    MessageBox.Show("Please enter a valid non-negative price.", "Invalid Price", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var reason = reasonBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(reason))
                {
                    MessageBox.Show("An override reason is required.", "Reason Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                bool success = await vm.OverrideItemPriceAsync(item, newPrice, reason);
                if (!success)
                {
                    MessageBox.Show(vm.StatusMessage, "Price Override Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    private async void ApplyDiscount_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BillingViewModel vm)
        {
            if (vm.CartItems.Count == 0)
            {
                MessageBox.Show("Cannot apply discount to an empty cart.", "Empty Cart", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new Window
            {
                Title = "Apply Cart Discount",
                Width = 400,
                Height = 280,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush")
            };

            var sp = new StackPanel { Margin = new Thickness(20) };
            sp.Children.Add(new TextBlock
            {
                Text = "Apply Discount to Active Cart",
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            });
            sp.Children.Add(new TextBlock
            {
                Text = "Discounts > 10% require Manager PIN approval.",
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
            });

            sp.Children.Add(new TextBlock { Text = "Discount Percentage (0 - 100%):", Margin = new Thickness(0, 0, 0, 4), Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush") });
            var rateBox = new TextBox { Height = 40, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(rateBox);

            sp.Children.Add(new TextBlock { Text = "Reason (Optional):", Margin = new Thickness(0, 0, 0, 4), Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush") });
            var reasonBox = new TextBox { Height = 40, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
            sp.Children.Add(reasonBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = new Button { Content = "Cancel", Width = 90, Height = 38, Margin = new Thickness(0, 0, 8, 0) };
            cancelBtn.Click += (_, _) => dialog.DialogResult = false;
            var confirmBtn = new Button { Content = "Apply", Width = 90, Height = 38 };
            confirmBtn.Click += (_, _) => dialog.DialogResult = true;

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(confirmBtn);
            sp.Children.Add(btnPanel);

            dialog.Content = sp;
            rateBox.Focus();

            if (dialog.ShowDialog() == true)
            {
                if (!decimal.TryParse(rateBox.Text?.Trim(), out var percent) || percent < 0 || percent > 100)
                {
                    MessageBox.Show("Please enter a valid percentage between 0 and 100.", "Invalid Discount", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var discountRate = percent / 100m;
                var reason = reasonBox.Text?.Trim();

                bool success = await vm.ApplyCartDiscountAsync(discountRate, reason);
                if (!success)
                {
                    MessageBox.Show(vm.StatusMessage, "Discount Authorization Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }
}
