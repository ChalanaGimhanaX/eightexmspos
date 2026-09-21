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
    private decimal _amountTendered;
    private string _errorMessage = "";
    private bool _isProcessing;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<Sale, PrintResult>? PaymentCompleted;

    public decimal GrandTotal { get; }
    public BillingViewModel BillingContext { get; }

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

    public decimal ChangeDue => AmountTendered >= GrandTotal ? AmountTendered - GrandTotal : 0m;
    public bool CanComplete => AmountTendered >= GrandTotal && !_isProcessing;

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

    public PaymentViewModel(BillingViewModel billingContext, ISaleService saleService, IReceiptService receiptService)
    {
        BillingContext = billingContext;
        GrandTotal = billingContext.GrandTotal;
        AmountTendered = GrandTotal; // Default to exact cash
        _saleService = saleService;
        _receiptService = receiptService;
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
        if (AmountTendered < GrandTotal)
        {
            ErrorMessage = $"Tendered amount (LKR {AmountTendered:F2}) is insufficient. Total is LKR {GrandTotal:F2}.";
            return;
        }

        try
        {
            IsProcessing = true;
            ErrorMessage = "";

            var lineRequests = BillingContext.CartItems.Select(i => new CreateSaleLineRequest(
                ProductId: i.ProductId,
                Quantity: i.Quantity,
                PriceOverride: i.UnitPrice,
                OverrideReason: i.OverrideReason,
                DiscountRate: i.DiscountRate
            )).ToList();

            var tenderRequests = new List<CreateTenderRequest>
            {
                new(TenderType: TenderType.CASH, AmountTendered: AmountTendered)
            };

            var cmd = new CreateSaleCommand(
                TenantId: "TENANT_LK_01",
                BranchId: BillingContext.CurrentShift.BranchId,
                CounterId: BillingContext.CurrentShift.CounterId,
                CashierId: BillingContext.CurrentUser.UserId,
                ShiftId: BillingContext.CurrentShift.ShiftId,
                Items: lineRequests,
                Tenders: tenderRequests
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
