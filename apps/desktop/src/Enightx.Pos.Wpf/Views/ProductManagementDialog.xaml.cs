using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class ProductManagementDialog : Window
{
    private readonly ICatalogService _catalogService;
    private readonly User? _currentUser;
    private List<Product> _allProducts = new();
    private List<Category> _categories = new();
    private Product? _selectedProduct;
    private bool _isNewMode = true;

    public bool HasModifiedData { get; private set; }

    public ProductManagementDialog(ICatalogService catalogService) : this(catalogService, null)
    {
    }

    public ProductManagementDialog(ICatalogService catalogService, User? currentUser)
    {
        InitializeComponent();
        _catalogService = catalogService;
        _currentUser = currentUser;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadCategoriesAsync();
        await ReloadProductsAsync();
        SetNewProductMode();
    }

    private async Task LoadCategoriesAsync()
    {
        try
        {
            _categories = await _catalogService.GetAllActiveCategoriesAsync();
            CategoryCombo.ItemsSource = _categories;
        }
        catch (Exception ex)
        {
            ShowError($"Failed to load categories: {ex.Message}");
        }
    }

    public async Task ReloadProductsAsync()
    {
        try
        {
            var showInactive = ShowInactiveCheck.IsChecked == true;
            _allProducts = await _catalogService.GetAllProductsAsync(includeInactive: showInactive);
            ApplyFilter();

            if (_selectedProduct != null)
            {
                var refreshed = _allProducts.FirstOrDefault(p => p.ProductId == _selectedProduct.ProductId);
                if (refreshed != null)
                {
                    ProductsListBox.SelectedItem = refreshed;
                }
            }
        }
        catch (Exception ex)
        {
            ShowError($"Failed to load products: {ex.Message}");
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allProducts
            : _allProducts.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                       p.Barcode.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        ProductsListBox.ItemsSource = filtered;
        ProductCountText.Text = $"{filtered.Count} items";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void FilterChanged(object sender, RoutedEventArgs e) => await ReloadProductsAsync();

    private void ProductsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProductsListBox.SelectedItem is Product product)
        {
            _selectedProduct = product;
            _isNewMode = false;
            PopulateForm(product);
        }
    }

    private void PopulateForm(Product product)
    {
        ClearError();
        FormTitleText.Text = $"Edit Product: {product.Name}";
        BarcodeBox.Text = product.Barcode;
        NameBox.Text = product.Name;
        NameSiBox.Text = product.NameSi ?? "";
        NameTaBox.Text = product.NameTa ?? "";
        CategoryCombo.SelectedValue = product.CategoryId;
        UnitPriceBox.Text = product.UnitPrice.ToString("F2");
        CostBasisBox.Text = product.CostBasis.ToString("F2");
        TaxRateBox.Text = (product.TaxRate * 100m).ToString("F2");
        StockOnHandBox.Text = product.StockOnHand.ToString("0.##");
        StockOnHandBox.IsReadOnly = true;
        MinThresholdBox.Text = product.MinStockThreshold.ToString("0.##");
        IsActiveCheckBox.IsChecked = product.IsActive;

        DeleteButton.Visibility = product.IsActive ? Visibility.Visible : Visibility.Collapsed;
        LowStockAlertBadge.Visibility = (product.StockOnHand <= product.MinStockThreshold) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetNewProductMode()
    {
        _selectedProduct = null;
        _isNewMode = true;
        ClearError();

        FormTitleText.Text = "Add New Product";
        BarcodeBox.Text = "";
        NameBox.Text = "";
        NameSiBox.Text = "";
        NameTaBox.Text = "";
        CategoryCombo.SelectedIndex = -1;
        UnitPriceBox.Text = "0.00";
        CostBasisBox.Text = "0.00";
        TaxRateBox.Text = "0.00";
        StockOnHandBox.Text = "0.00";
        StockOnHandBox.IsReadOnly = false; // Initial stock permitted for brand new items
        MinThresholdBox.Text = "5.00";
        IsActiveCheckBox.IsChecked = true;

        DeleteButton.Visibility = Visibility.Collapsed;
        LowStockAlertBadge.Visibility = Visibility.Collapsed;
        BarcodeBox.Focus();
    }

    private void NewProduct_Click(object sender, RoutedEventArgs e)
    {
        ProductsListBox.SelectedItem = null;
        SetNewProductMode();
    }

    private void GenerateBarcode_Click(object sender, RoutedEventArgs e)
    {
        var randomNum = new Random().Next(10000000, 99999999);
        BarcodeBox.Text = $"479{randomNum}";
    }

    private async void SaveProduct_Click(object sender, RoutedEventArgs e)
    {
        ClearError();

        var barcode = BarcodeBox.Text?.Trim() ?? "";
        var name = NameBox.Text?.Trim() ?? "";
        var nameSi = string.IsNullOrWhiteSpace(NameSiBox.Text) ? null : NameSiBox.Text.Trim();
        var nameTa = string.IsNullOrWhiteSpace(NameTaBox.Text) ? null : NameTaBox.Text.Trim();
        var categoryId = CategoryCombo.SelectedValue?.ToString();

        if (string.IsNullOrWhiteSpace(barcode))
        {
            ShowError("Please enter or generate a barcode.");
            BarcodeBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Please enter a product name.");
            NameBox.Focus();
            return;
        }

        if (!decimal.TryParse(UnitPriceBox.Text, out var unitPrice) || unitPrice < 0)
        {
            ShowError("Please enter a valid unit price (>= 0).");
            UnitPriceBox.Focus();
            return;
        }

        if (!decimal.TryParse(CostBasisBox.Text, out var costBasis) || costBasis < 0)
        {
            ShowError("Please enter a valid cost basis (>= 0).");
            CostBasisBox.Focus();
            return;
        }

        if (!decimal.TryParse(TaxRateBox.Text, out var taxPercent) || taxPercent < 0 || taxPercent > 100)
        {
            ShowError("Please enter a valid tax rate percentage (0 - 100).");
            TaxRateBox.Focus();
            return;
        }
        var taxRate = MoneyCalculator.Round(taxPercent / 100m);

        if (!decimal.TryParse(MinThresholdBox.Text, out var minThreshold) || minThreshold < 0)
        {
            ShowError("Please enter a valid minimum stock threshold (>= 0).");
            MinThresholdBox.Focus();
            return;
        }

        try
        {
            if (_isNewMode)
            {
                decimal initialStock = 0m;
                _ = decimal.TryParse(StockOnHandBox.Text, out initialStock);

                var newProduct = new Product
                {
                    ProductId = $"prod_{Guid.NewGuid():N}",
                    Barcode = barcode,
                    Name = name,
                    NameSi = nameSi,
                    NameTa = nameTa,
                    CategoryId = categoryId,
                    UnitPrice = MoneyCalculator.Round(unitPrice),
                    CostBasis = MoneyCalculator.Round(costBasis),
                    TaxRate = taxRate,
                    StockOnHand = Math.Max(0m, initialStock),
                    MinStockThreshold = minThreshold,
                    IsActive = IsActiveCheckBox.IsChecked == true
                };

                await _catalogService.AddProductAsync(newProduct);
                MessageBox.Show($"Product '{newProduct.Name}' added successfully!", "Product Created", MessageBoxButton.OK, MessageBoxImage.Information);
                _selectedProduct = newProduct;
            }
            else if (_selectedProduct != null)
            {
                _selectedProduct.Barcode = barcode;
                _selectedProduct.Name = name;
                _selectedProduct.NameSi = nameSi;
                _selectedProduct.NameTa = nameTa;
                _selectedProduct.CategoryId = categoryId;
                _selectedProduct.UnitPrice = MoneyCalculator.Round(unitPrice);
                _selectedProduct.CostBasis = MoneyCalculator.Round(costBasis);
                _selectedProduct.TaxRate = taxRate;
                _selectedProduct.MinStockThreshold = minThreshold;
                _selectedProduct.IsActive = IsActiveCheckBox.IsChecked == true;

                await _catalogService.UpdateProductAsync(_selectedProduct);
                MessageBox.Show($"Product '{_selectedProduct.Name}' updated successfully!", "Product Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            HasModifiedData = true;
            await ReloadProductsAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to save product: {ex.Message}");
        }
    }

    private async void DeleteProduct_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProduct == null) return;

        var confirm = MessageBox.Show(
            $"Are you sure you want to deactivate product '{_selectedProduct.Name}'?\nIt will be hidden from cashier billing but preserved in historic records.",
            "Confirm Deactivation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            try
            {
                await _catalogService.DeleteProductAsync(_selectedProduct.ProductId);
                HasModifiedData = true;
                MessageBox.Show("Product deactivated successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                SetNewProductMode();
                await ReloadProductsAsync();
            }
            catch (Exception ex)
            {
                ShowError($"Failed to deactivate product: {ex.Message}");
            }
        }
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        ErrorText.Text = "";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = HasModifiedData;
        Close();
    }
}

