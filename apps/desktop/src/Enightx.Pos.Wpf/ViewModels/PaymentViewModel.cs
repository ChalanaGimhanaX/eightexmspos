using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.ViewModels;

public class PaymentViewModel : INotifyPropertyChanged
{
    private readonly ISaleService _saleService;
    private readonly IReceiptService _receiptService;
    private readonly ICustomerService? _customerService;

    private decimal _amountTendered;
    private decimal _cashAmount;
    private decimal _creditAmount;
    private string _selectedTenderType = "CASH";
    private Customer? _selectedCustomer;
    private string _errorMessage = "";
    private bool _isProcessing;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<Sale, PrintResult>? PaymentCompleted;

    public decimal GrandTotal { get; }
    public BillingViewModel BillingContext { get; }
    public ObservableCollection<Customer> Customers { get; } = new();

    public string SelectedTenderType
    {
        get => _selectedTenderType;
        set
        {
            if (_selectedTenderType != value)
            {
                _selectedTenderType = value;
                if ((IsCreditSelected || IsSplitSelected) && _selectedCustomer == null && Customers.Count > 0)
                {
                    _selectedCustomer = Customers[0];
                    OnPropertyChanged(nameof(SelectedCustomer));
                    OnPropertyChanged(nameof(CustomerAvailableCredit));
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCashSelected));
                OnPropertyChanged(nameof(IsCardSelected));
                OnPropertyChanged(nameof(IsCreditSelected));
                OnPropertyChanged(nameof(IsSplitSelected));
                OnPropertyChanged(nameof(CanComplete));
                OnPropertyChanged(nameof(ChangeDue));
            }
        }
    }

    public bool IsCashSelected => SelectedTenderType == "CASH";
    public bool IsCardSelected => SelectedTenderType == "CARD";
    public bool IsCreditSelected => SelectedTenderType == "CREDIT";
    public bool IsSplitSelected => SelectedTenderType == "SPLIT";

    public Customer? SelectedCustomer
    {
        get => _selectedCustomer;
        set
        {
            if (_selectedCustomer != value)
            {
                _selectedCustomer = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CustomerAvailableCredit));
                OnPropertyChanged(nameof(CanComplete));
            }
        }
    }

    public decimal CustomerAvailableCredit =>
        SelectedCustomer == null ? 0m : Math.Max(0m, SelectedCustomer.CreditLimit - SelectedCustomer.OutstandingBalance);

    public decimal AmountTendered
    {
        get => _amountTendered;
        set
        {
            _amountTendered = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChangeDue));
            OnPropertyChanged(nameof(CanComplete));
        }
    }

    public decimal CashAmount
    {
        get => _cashAmount;
        set
        {
            _cashAmount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChangeDue));
            OnPropertyChanged(nameof(CanComplete));
        }
    }

    public decimal CreditAmount
    {
        get => _creditAmount;
        set
        {
            _creditAmount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChangeDue));
            OnPropertyChanged(nameof(CanComplete));
        }
    }

    public decimal ChangeDue
    {
        get
        {
            if (IsCashSelected)
            {
                return AmountTendered >= GrandTotal ? AmountTendered - GrandTotal : 0m;
            }
            if (IsSplitSelected)
            {
                var totalPaid = CashAmount + CreditAmount;
                return totalPaid > GrandTotal ? totalPaid - GrandTotal : 0m;
            }
            return 0m;
        }
    }

    public bool CanComplete
    {
        get
        {
            if (_isProcessing) return false;

            if (IsCashSelected)
            {
                return AmountTendered >= GrandTotal;
            }
            if (IsCardSelected)
            {
                return true;
            }
            if (IsCreditSelected)
            {
                if (SelectedCustomer == null || !SelectedCustomer.IsActive) return false;
                return GrandTotal <= CustomerAvailableCredit;
            }
            if (IsSplitSelected)
            {
                if (SelectedCustomer == null || !SelectedCustomer.IsActive) return false;
                if (CreditAmount <= 0m || CreditAmount > CustomerAvailableCredit || CreditAmount > GrandTotal) return false;
                return (CashAmount + CreditAmount) >= GrandTotal;
            }
            return false;
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set { _isProcessing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanComplete)); }
    }

    public PaymentViewModel(
        BillingViewModel billingContext,
        ISaleService saleService,
        IReceiptService receiptService,
        ICustomerService? customerService = null)
    {
        BillingContext = billingContext;
        GrandTotal = billingContext.GrandTotal;
        AmountTendered = GrandTotal; // Default to exact cash
        CashAmount = 0m;
        CreditAmount = GrandTotal;
        _saleService = saleService;
        _receiptService = receiptService;
        _customerService = customerService;

        LoadCustomers();
    }

    private async void LoadCustomers()
    {
        if (_customerService == null) return;
        try
        {
            var list = await _customerService.GetAllCustomersAsync(activeOnly: true);
            Customers.Clear();
            foreach (var c in list)
            {
                Customers.Add(c);
            }
            if ((IsCreditSelected || IsSplitSelected) && Customers.Count > 0 && SelectedCustomer == null)
            {
                SelectedCustomer = Customers[0];
            }
        }
        catch
        {
            // Ignore offline load errors
        }
    }

    public void AddTenderAmount(decimal extra)
    {
        AmountTendered += extra;
    }

    public void SetExactAmount()
    {
        AmountTendered = GrandTotal;
    }

    public async Task CompleteCashSaleAsync()
    {
        await CompleteSaleAsync();
    }

    public async Task CompleteSaleAsync()
    {
        ErrorMessage = "";

        if (IsCashSelected && AmountTendered < GrandTotal)
        {
            ErrorMessage = $"Tendered amount (Rs. {AmountTendered:N2}) is insufficient. Total is Rs. {GrandTotal:N2}.";
            return;
        }

        if (IsCreditSelected)
        {
            if (SelectedCustomer == null)
            {
                ErrorMessage = "Please select a customer for credit sales.";
                return;
            }
            if (GrandTotal > CustomerAvailableCredit)
            {
                ErrorMessage = $"Sale total (Rs. {GrandTotal:N2}) exceeds customer's available credit (Rs. {CustomerAvailableCredit:N2}).";
                return;
            }
        }

        if (IsSplitSelected)
        {
            if (SelectedCustomer == null)
            {
                ErrorMessage = "Please select a customer for split credit sales.";
                return;
            }
            if (CreditAmount <= 0m)
            {
                ErrorMessage = "Credit portion must be greater than zero.";
                return;
            }
            if (CreditAmount > CustomerAvailableCredit)
            {
                ErrorMessage = $"Credit portion (Rs. {CreditAmount:N2}) exceeds customer's available credit (Rs. {CustomerAvailableCredit:N2}).";
                return;
            }
            if (CreditAmount > GrandTotal)
            {
                ErrorMessage = $"Credit portion (Rs. {CreditAmount:N2}) cannot exceed grand total (Rs. {GrandTotal:N2}).";
                return;
            }
            if ((CashAmount + CreditAmount) < GrandTotal)
            {
                ErrorMessage = $"Total tendered (Rs. {CashAmount + CreditAmount:N2}) is less than grand total (Rs. {GrandTotal:N2}).";
                return;
            }
        }

        try
        {
            IsProcessing = true;

            var lineRequests = BillingContext.CartItems.Select(i => new CreateSaleLineRequest(
                ProductId: i.ProductId,
                Quantity: i.Quantity,
                PriceOverride: i.IsPriceOverridden ? i.UnitPrice : null,
                OverrideReason: i.OverrideReason,
                DiscountRate: i.DiscountRate,
                AuthorizingUserId: i.AuthorizingUserId
            )).ToList();

            var tenderRequests = new List<CreateTenderRequest>();
            string? customerId = null;

            if (IsCashSelected)
            {
                tenderRequests.Add(new CreateTenderRequest(TenderType.CASH, AmountTendered));
                customerId = SelectedCustomer?.CustomerId;
            }
            else if (IsCardSelected)
            {
                tenderRequests.Add(new CreateTenderRequest(TenderType.CARD, GrandTotal, "CARD_EXT"));
                customerId = SelectedCustomer?.CustomerId;
            }
            else if (IsCreditSelected)
            {
                tenderRequests.Add(new CreateTenderRequest(TenderType.CREDIT, GrandTotal, $"CREDIT_{SelectedCustomer!.CustomerId}"));
                customerId = SelectedCustomer.CustomerId;
            }
            else if (IsSplitSelected)
            {
                tenderRequests.Add(new CreateTenderRequest(TenderType.CREDIT, CreditAmount, $"CREDIT_{SelectedCustomer!.CustomerId}"));
                tenderRequests.Add(new CreateTenderRequest(TenderType.CASH, CashAmount));
                customerId = SelectedCustomer.CustomerId;
            }

            var authorizerId = BillingContext.CartItems.FirstOrDefault(i => !string.IsNullOrEmpty(i.AuthorizingUserId))?.AuthorizingUserId;

            var cmd = new CreateSaleCommand(
                TenantId: "TENANT_LK_01",
                BranchId: BillingContext.CurrentShift.BranchId,
                CounterId: BillingContext.CurrentShift.CounterId,
                CashierId: BillingContext.CurrentUser.UserId,
                ShiftId: BillingContext.CurrentShift.ShiftId,
                Items: lineRequests,
                Tenders: tenderRequests,
                CustomerId: customerId,
                AuthorizingUserId: authorizerId
            );

            // 1. Commit sale atomically in SQLite
            var sale = await _saleService.CommitSaleAsync(cmd);

            // 2. Print receipt
            var printResult = await _receiptService.PrintSaleReceiptAsync(sale.SaleId);

            // 3. Clear cart
            BillingContext.ClearCart();

            PaymentCompleted?.Invoke(sale, printResult);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Payment failed: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
