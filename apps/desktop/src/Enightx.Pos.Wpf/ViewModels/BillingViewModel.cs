using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.ViewModels;

public class CartItemViewModel : INotifyPropertyChanged
{
    private decimal _quantity;
    private decimal _unitPrice;
    private decimal _discountRate;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string ProductId { get; set; }
    public required string Barcode { get; set; }
    public required string ProductName { get; set; }
    public decimal TaxRate { get; set; }

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity != value && value > 0)
            {
                _quantity = value;
                Recalculate();
                OnPropertyChanged();
            }
        }
    }

    public decimal UnitPrice
    {
        get => _unitPrice;
        set
        {
            if (_unitPrice != value)
            {
                _unitPrice = value;
                Recalculate();
                OnPropertyChanged();
            }
        }
    }

    public decimal DiscountRate
    {
        get => _discountRate;
        set
        {
            if (_discountRate != value)
            {
                _discountRate = value;
                Recalculate();
                OnPropertyChanged();
            }
        }
    }

    public string? OverrideReason { get; set; }
    public bool IsPriceOverridden { get; set; } = false;

    public decimal Subtotal { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal LineTotal { get; private set; }

    public void Recalculate()
    {
        var calc = MoneyCalculator.CalculateLine(Quantity, UnitPrice, DiscountRate, 0m, TaxRate);
        Subtotal = calc.Subtotal;
        DiscountAmount = calc.DiscountAmount;
        TaxAmount = calc.TaxAmount;
        LineTotal = calc.LineTotal;

        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(TaxAmount));
        OnPropertyChanged(nameof(LineTotal));
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class BillingViewModel : INotifyPropertyChanged
{
    private readonly ICatalogService _catalogService;
    private readonly ISaleService _saleService;
    private readonly IHeldCartService? _heldCartService;
    private string _barcodeInput = "";
    private string _statusMessage = "Ready for billing";
    private string _searchQuery = "";
    private Category? _selectedCategory;
    private bool _isCatalogLoading;

    private readonly List<Product> _allProducts = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public User CurrentUser { get; }
    public CashShift CurrentShift { get; }
    public ObservableCollection<CartItemViewModel> CartItems { get; } = new();
    public ObservableCollection<Product> FilteredProducts { get; } = new();
    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<HeldCart> HeldCarts { get; } = new();

    public int HeldCartsCount => HeldCarts.Count;

    public decimal Subtotal => MoneyCalculator.Round(CartItems.Sum(i => i.Subtotal));
    public decimal DiscountTotal => MoneyCalculator.Round(CartItems.Sum(i => i.DiscountAmount));
    public decimal TaxTotal => MoneyCalculator.Round(CartItems.Sum(i => i.TaxAmount));
    public decimal GrandTotal => MoneyCalculator.Round(CartItems.Sum(i => i.LineTotal));

    public string BarcodeInput
    {
        get => _barcodeInput;
        set { _barcodeInput = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery != value)
            {
                _searchQuery = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }
    }

    public Category? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (_selectedCategory != value)
            {
                _selectedCategory = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }
    }

    public bool IsCatalogLoading
    {
        get => _isCatalogLoading;
        set { _isCatalogLoading = value; OnPropertyChanged(); }
    }

    public BillingViewModel(
        User user,
        CashShift shift,
        ICatalogService catalogService,
        ISaleService saleService,
        IHeldCartService? heldCartService = null)
    {
        CurrentUser = user;
        CurrentShift = shift;
        _catalogService = catalogService;
        _saleService = saleService;
        _heldCartService = heldCartService;
    }

    public async Task LoadCatalogAsync()
    {
        try
        {
            IsCatalogLoading = true;
            StatusMessage = "Loading product catalog...";

            var cats = await _catalogService.GetAllActiveCategoriesAsync();
            var prods = await _catalogService.GetAllActiveProductsAsync();

            Categories.Clear();
            Categories.Add(new Category { CategoryId = "all", Name = "All Products", IsActive = true });
            foreach (var c in cats)
            {
                Categories.Add(c);
            }

            _allProducts.Clear();
            _allProducts.AddRange(prods);

            _selectedCategory = Categories[0];
            OnPropertyChanged(nameof(SelectedCategory));

            ApplyFilter();
            StatusMessage = $"Catalog loaded ({_allProducts.Count} products ready)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading catalog: {ex.Message}";
        }
        finally
        {
            IsCatalogLoading = false;
        }
    }

    public void ApplyFilter()
    {
        FilteredProducts.Clear();
        var q = _searchQuery.Trim().ToLowerInvariant();
        var catId = _selectedCategory?.CategoryId;

        foreach (var p in _allProducts)
        {
            if (catId != null && catId != "all" && p.CategoryId != catId)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                bool matchesName = p.Name.ToLowerInvariant().Contains(q);
                bool matchesSi = !string.IsNullOrEmpty(p.NameSi) && p.NameSi.ToLowerInvariant().Contains(q);
                bool matchesTa = !string.IsNullOrEmpty(p.NameTa) && p.NameTa.ToLowerInvariant().Contains(q);
                bool matchesBarcode = p.Barcode.Contains(q);

                if (!matchesName && !matchesSi && !matchesTa && !matchesBarcode)
                {
                    continue;
                }
            }

            FilteredProducts.Add(p);
        }
    }

    public void AddProductToCart(Product product)
    {
        var existing = CartItems.FirstOrDefault(i => i.ProductId == product.ProductId);
        if (existing != null)
        {
            existing.Quantity += 1m;
        }
        else
        {
            var item = new CartItemViewModel
            {
                ProductId = product.ProductId,
                Barcode = product.Barcode,
                ProductName = product.Name,
                UnitPrice = product.UnitPrice,
                TaxRate = product.TaxRate,
                Quantity = 1m
            };
            item.Recalculate();
            CartItems.Add(item);
        }

        StatusMessage = $"Added: {product.Name} (LKR {product.UnitPrice:N2})";
        RefreshTotals();
    }

    public async Task AddItemByBarcodeAsync(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return;

        var product = await _catalogService.GetProductByBarcodeAsync(barcode.Trim());
        if (product == null)
        {
            StatusMessage = $"Product not found for barcode: {barcode}";
            return;
        }

        AddProductToCart(product);
        BarcodeInput = "";
    }

    public void IncrementItem(CartItemViewModel item)
    {
        item.Quantity += 1m;
        RefreshTotals();
    }

    public void DecrementItem(CartItemViewModel item)
    {
        if (item.Quantity > 1m)
        {
            item.Quantity -= 1m;
        }
        else
        {
            CartItems.Remove(item);
        }
        RefreshTotals();
    }

    public void RemoveItem(CartItemViewModel item)
    {
        CartItems.Remove(item);
        RefreshTotals();
    }

    public void ClearCart()
    {
        CartItems.Clear();
        StatusMessage = "Cart cleared";
        RefreshTotals();
    }

    public async Task<bool> HoldCurrentCartAsync(string? customerReference = null)
    {
        if (CartItems.Count == 0)
        {
            StatusMessage = "Cannot hold an empty cart.";
            return false;
        }

        var svc = _heldCartService ?? App.HeldCartService;
        if (svc == null)
        {
            StatusMessage = "Held cart service unavailable.";
            return false;
        }

        var items = CartItems.Select(ci => new HeldCartItem
        {
            ProductId = ci.ProductId,
            Barcode = ci.Barcode,
            ProductName = ci.ProductName,
            Quantity = ci.Quantity,
            UnitPrice = ci.UnitPrice,
            DiscountRate = ci.DiscountRate,
            TaxRate = ci.TaxRate,
            LineTotal = ci.LineTotal,
            OverrideReason = ci.OverrideReason,
            IsPriceOverridden = ci.IsPriceOverridden
        }).ToList();

        var refLabel = string.IsNullOrWhiteSpace(customerReference)
            ? $"Cart at {DateTime.Now:HH:mm}"
            : customerReference.Trim();

        var held = await svc.HoldCartAsync(new HoldCartCommand(
            TenantId: "TENANT_LK_01",
            BranchId: CurrentShift.BranchId,
            CounterId: CurrentShift.CounterId,
            CashierId: CurrentUser.UserId,
            CustomerReference: refLabel,
            Items: items
        ));

        CartItems.Clear();
        RefreshTotals();
        await LoadHeldCartsAsync();
        StatusMessage = $"Cart parked successfully ({held.CustomerReference})";
        return true;
    }

    public async Task<bool> RecallHeldCartAsync(Guid heldCartId)
    {
        var svc = _heldCartService ?? App.HeldCartService;
        if (svc == null) return false;

        if (CartItems.Count > 0)
        {
            StatusMessage = "Please clear or hold the active cart before recalling another cart.";
            return false;
        }

        var heldCart = await svc.RecallCartAsync(heldCartId, CurrentUser.UserId);
        CartItems.Clear();
        foreach (var item in heldCart.Items)
        {
            var vm = new CartItemViewModel
            {
                ProductId = item.ProductId,
                Barcode = item.Barcode,
                ProductName = item.ProductName,
                UnitPrice = item.UnitPrice,
                DiscountRate = item.DiscountRate,
                TaxRate = item.TaxRate,
                Quantity = item.Quantity,
                OverrideReason = item.OverrideReason,
                IsPriceOverridden = item.IsPriceOverridden
            };
            vm.Recalculate();
            CartItems.Add(vm);
        }

        RefreshTotals();
        await LoadHeldCartsAsync();
        StatusMessage = $"Recalled cart: {heldCart.CustomerReference ?? heldCartId.ToString()}";
        return true;
    }

    public async Task DiscardHeldCartAsync(Guid heldCartId, string? reason = null)
    {
        var svc = _heldCartService ?? App.HeldCartService;
        if (svc == null) return;

        await svc.DeleteHeldCartAsync(heldCartId, CurrentUser.UserId, reason);
        await LoadHeldCartsAsync();
        StatusMessage = "Held cart discarded.";
    }

    public async Task LoadHeldCartsAsync()
    {
        var svc = _heldCartService ?? App.HeldCartService;
        if (svc == null) return;

        try
        {
            var list = await svc.GetHeldCartsAsync(CurrentShift.BranchId, CurrentShift.CounterId);
            HeldCarts.Clear();
            foreach (var c in list)
            {
                HeldCarts.Add(c);
            }
            OnPropertyChanged(nameof(HeldCartsCount));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading held carts: {ex.Message}";
        }
    }

    public void VoidItem(CartItemViewModel item, string? reason = null)
    {
        CartItems.Remove(item);
        RefreshTotals();
        StatusMessage = $"Voided: {item.ProductName}" + (!string.IsNullOrEmpty(reason) ? $" ({reason})" : "");
    }

    public async Task SyncCatalogWithCloudAsync()
    {
        try
        {
            StatusMessage = "Syncing catalog with cloud server...";
            if (App.SyncService != null)
            {
                var result = await App.SyncService.PullCatalogUpdatesAsync();
                if (result.Success)
                {
                    await LoadCatalogAsync();
                    StatusMessage = $"Catalog synced! {result.ProductsUpdated} products updated from cloud.";
                }
                else
                {
                    StatusMessage = $"Sync notice: {result.ErrorMessage}";
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sync failed: {ex.Message}";
        }
    }

    public void RefreshTotals()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(DiscountTotal));
        OnPropertyChanged(nameof(TaxTotal));
        OnPropertyChanged(nameof(GrandTotal));
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
