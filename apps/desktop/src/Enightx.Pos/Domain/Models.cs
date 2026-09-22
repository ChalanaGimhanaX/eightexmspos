using System.Text.Json.Serialization;

namespace Enightx.Pos.Domain;

public enum Role
{
    Cashier = 1,
    Manager = 2,
    Owner = 3,
    ProviderStaff = 4
}

public enum TenderType
{
    CASH,
    CARD,
    QR,
    CREDIT
}

public enum ShiftStatus
{
    Open = 1,
    Closed = 2
}

public enum SaleStatus
{
    Completed = 1,
    Cancelled = 2,
    Refunded = 3
}

public class User
{
    public required string UserId { get; set; }
    public required string Username { get; set; }
    public required string DisplayName { get; set; }
    public Role Role { get; set; }
    public required string PasswordHash { get; set; }
    public required string PasswordSalt { get; set; }
    public string? PinHash { get; set; }
    public string? PinSalt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class Product
{
    public required string ProductId { get; set; }
    public string? CategoryId { get; set; }
    public required string Barcode { get; set; }
    public required string Name { get; set; }
    public string? NameSi { get; set; }
    public string? NameTa { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal CostBasis { get; set; }
    public decimal TaxRate { get; set; }
    public decimal StockOnHand { get; set; }
    public decimal MinStockThreshold { get; set; } = 0.0m;
    public bool IsActive { get; set; } = true;
}

public class SaleLine
{
    public Guid LineId { get; set; } = Guid.NewGuid();
    public Guid SaleId { get; set; }
    public required string ProductId { get; set; }
    public required string ProductName { get; set; }
    public required string Barcode { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal CostBasis { get; set; }
    public decimal DiscountRate { get; set; }
    public decimal DiscountFixed { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
    public Guid? ParentLineId { get; set; }
}

public class Tender
{
    public Guid TenderId { get; set; } = Guid.NewGuid();
    public Guid SaleId { get; set; }
    public TenderType TenderType { get; set; }
    public decimal AmountTendered { get; set; }
    public decimal ChangeGiven { get; set; }
    public string? PaymentReference { get; set; }
}

public class Sale
{
    public Guid SaleId { get; set; } = Guid.NewGuid();
    public required string ReceiptNumber { get; set; }
    public Guid ShiftId { get; set; }
    public required string TenantId { get; set; }
    public required string BranchId { get; set; }
    public required string CounterId { get; set; }
    public required string CashierId { get; set; }
    public string? CustomerId { get; set; }
    public Guid? ParentSaleId { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal GrandTotal { get; set; }
    public SaleStatus Status { get; set; } = SaleStatus.Completed;
    public int ReprintCount { get; set; } = 0;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<SaleLine> Lines { get; set; } = new();
    public List<Tender> Tenders { get; set; } = new();
}

public class StockMovement
{
    public Guid MovementId { get; set; } = Guid.NewGuid();
    public required string ProductId { get; set; }
    public required string MovementType { get; set; } // "SALE", "REFUND", "ADJUSTMENT", "PURCHASE"
    public decimal QuantityChange { get; set; }
    public required string ReferenceId { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

public class CashShift
{
    public Guid ShiftId { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = "TENANT_LK_01";
    public required string BranchId { get; set; }
    public required string CounterId { get; set; }
    public required string CashierId { get; set; }
    public DateTime OpenedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAtUtc { get; set; }
    public decimal OpeningFloat { get; set; }
    public decimal CashReceived { get; set; }
    public decimal ChangeGiven { get; set; }
    public decimal CashRefunds { get; set; }
    public decimal CashIn { get; set; }
    public decimal CashOut { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal? ActualCountedCash { get; set; }
    public decimal? Variance { get; set; }
    public ShiftStatus Status { get; set; } = ShiftStatus.Open;
}

public class AuditEvent
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public required string TenantId { get; set; }
    public required string BranchId { get; set; }
    public required string CounterId { get; set; }
    public required string ActorId { get; set; }
    public required string Action { get; set; } // "USER_LOGIN", "COMMIT_SALE", "REPRINT_RECEIPT", etc.
    public required string DetailsJson { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

public class OutboxEvent
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public required string TenantId { get; set; }
    public required string BranchId { get; set; }
    public required string DeviceId { get; set; }
    public int DeviceGeneration { get; set; }
    public long SourceSequence { get; set; }
    public string SchemaVersion { get; set; } = "1.0";
    public required string ActorId { get; set; }
    public required string PayloadJson { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string? CausalReference { get; set; }
    public string Status { get; set; } = "PENDING"; // "PENDING", "SENT", "ACKNOWLEDGED"
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class Category
{
    public required string CategoryId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class CatalogProductDto
{
    [JsonPropertyName("product_id")]
    public required string ProductId { get; set; }

    [JsonPropertyName("category_id")]
    public string? CategoryId { get; set; }

    [JsonPropertyName("barcode")]
    public required string Barcode { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("name_si")]
    public string? NameSi { get; set; }

    [JsonPropertyName("name_ta")]
    public string? NameTa { get; set; }

    [JsonPropertyName("unit_price")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("cost_basis")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal CostBasis { get; set; }

    [JsonPropertyName("tax_rate")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal TaxRate { get; set; }

    [JsonPropertyName("min_stock_threshold")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal MinStockThreshold { get; set; } = 0.0m;

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; } = true;

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

public class CategoryDto
{
    [JsonPropertyName("category_id")]
    public required string CategoryId { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; } = true;

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

public class CatalogSyncResponseDto
{
    [JsonPropertyName("server_time")]
    public DateTime ServerTime { get; set; }

    [JsonPropertyName("products")]
    public List<CatalogProductDto> Products { get; set; } = new();

    [JsonPropertyName("categories")]
    public List<CategoryDto> Categories { get; set; } = new();

    [JsonPropertyName("deleted_item_ids")]
    public List<string> DeletedItemIds { get; set; } = new();

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }
}

public class CatalogSyncResult
{
    public bool Success { get; set; }
    public int ProductsUpdated { get; set; }
    public int CategoriesUpdated { get; set; }
    public int ItemsDeleted { get; set; }
    public DateTime? ServerTimeUtc { get; set; }
    public bool NetworkOffline { get; set; }
    public string? ErrorMessage { get; set; }
}

public class SyncPushResult
{
    public bool Success { get; set; }
    public int PushedCount { get; set; }
    public long AcknowledgedSequence { get; set; }
    public bool NetworkOffline { get; set; }
    public string? ErrorMessage { get; set; }
}

public class SyncPushApiResponse
{
    [JsonPropertyName("batch_id")]
    public Guid BatchId { get; set; }

    [JsonPropertyName("acknowledged_sequence")]
    public long AcknowledgedSequence { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "acknowledged";
}

public class HeldCartItem
{
    public required string ProductId { get; set; }
    public required string Barcode { get; set; }
    public required string ProductName { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountRate { get; set; }
    public decimal DiscountFixed { get; set; }
    public decimal TaxRate { get; set; }
    public decimal LineTotal { get; set; }
    public string? OverrideReason { get; set; }
    public bool IsPriceOverridden { get; set; }
}

public class HeldCart
{
    public Guid HeldCartId { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = "TENANT_LK_01";
    public required string BranchId { get; set; }
    public required string CounterId { get; set; }
    public required string CashierId { get; set; }
    public string? CustomerReference { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal GrandTotal { get; set; }
    public DateTime HeldAtUtc { get; set; } = DateTime.UtcNow;
    public List<HeldCartItem> Items { get; set; } = new();
}

public class ShiftCashMovement
{
    public Guid MovementId { get; set; } = Guid.NewGuid();
    public Guid ShiftId { get; set; }
    public required string MovementType { get; set; } // "CASH_IN", "CASH_OUT"
    public decimal Amount { get; set; }
    public required string Reason { get; set; }
    public required string ActorId { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

public class GoodsReceiptLine
{
    public Guid LineId { get; set; } = Guid.NewGuid();
    public Guid ReceiptId { get; set; }
    public required string ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineTotalCost { get; set; }
}

public class GoodsReceipt
{
    public Guid ReceiptId { get; set; } = Guid.NewGuid();
    public required string TenantId { get; set; }
    public required string BranchId { get; set; }
    public required string SupplierName { get; set; }
    public required string InvoiceReference { get; set; }
    public required string ReceivedByUserId { get; set; }
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public decimal TotalCost { get; set; }
    public string? Notes { get; set; }
    public List<GoodsReceiptLine> Lines { get; set; } = new();
}

public class TenderSummary
{
    public TenderType TenderType { get; set; }
    public int TransactionCount { get; set; }
    public decimal TotalTendered { get; set; }
    public decimal ChangeGiven { get; set; }
    public decimal NetAmount { get; set; }
}

public class CashDrawerReconciliation
{
    public decimal OpeningFloat { get; set; }
    public decimal CashReceived { get; set; }
    public decimal ChangeGiven { get; set; }
    public decimal NetCashSales { get; set; }
    public decimal CashRefunds { get; set; }
    public decimal CashIn { get; set; }
    public decimal CashOut { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal? ActualCountedCash { get; set; }
    public decimal? Variance { get; set; }
    public string Status { get; set; } = "Open";
}

public class GrossProfitSummary
{
    public decimal Revenue { get; set; }
    public decimal CostOfGoodsSold { get; set; }
    public decimal GrossProfit { get; set; }
    public decimal GrossMarginPercent { get; set; }
    public string Disclaimer { get; set; } = "Gross Profit only (excludes operating and store expenses).";
}

public class ShiftReport
{
    public Guid ShiftId { get; set; }
    public required string BranchId { get; set; }
    public required string CounterId { get; set; }
    public required string CashierId { get; set; }
    public required string CashierName { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public ShiftStatus Status { get; set; }

    public int TotalSalesCount { get; set; }
    public decimal GrossSales { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal NetSales { get; set; }

    public int TotalRefundCount { get; set; }
    public decimal TotalRefundAmount { get; set; }

    public List<TenderSummary> TenderSummaries { get; set; } = new();
    public CashDrawerReconciliation DrawerReconciliation { get; set; } = new();
    public List<ShiftCashMovement> CashMovements { get; set; } = new();
    public GrossProfitSummary ProfitSummary { get; set; } = new();
}

public class DailyReport
{
    public DateTime DateUtc { get; set; }
    public required string BranchId { get; set; }
    public int ShiftsCount { get; set; }
    public int CompletedSalesCount { get; set; }
    public decimal GrossSales { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal NetSales { get; set; }
    public int RefundCount { get; set; }
    public decimal TotalRefundAmount { get; set; }
    public List<TenderSummary> TenderSummaries { get; set; } = new();
    public decimal TotalCashIn { get; set; }
    public decimal TotalCashOut { get; set; }
    public decimal NetDrawerCashChange { get; set; }
    public GrossProfitSummary ProfitSummary { get; set; } = new();
}

public class InventoryValuationItem
{
    public required string ProductId { get; set; }
    public required string Barcode { get; set; }
    public required string Name { get; set; }
    public decimal StockOnHand { get; set; }
    public decimal CostBasis { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal ValuationAtCost { get; set; }
    public decimal ValuationAtRetail { get; set; }
}

public class InventorySummaryReport
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public int TotalProducts { get; set; }
    public int LowStockProducts { get; set; }
    public int NegativeStockProducts { get; set; }
    public decimal TotalValuationAtCost { get; set; }
    public decimal TotalValuationAtRetail { get; set; }
    public List<InventoryValuationItem> Items { get; set; } = new();
}

public class Customer
{
    public required string CustomerId { get; set; }
    public required string Name { get; set; }
    public required string Phone { get; set; }
    public string? Address { get; set; }
    public decimal CreditLimit { get; set; }
    public decimal OutstandingBalance { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class CustomerLedgerEntry
{
    public Guid EntryId { get; set; } = Guid.NewGuid();
    public required string CustomerId { get; set; }
    public required string EntryType { get; set; } // "CREDIT_SALE", "DEBT_PAYMENT", "INITIAL_BALANCE", "SALE_CANCELLED"
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? ReferenceId { get; set; }
    public Guid? ShiftId { get; set; }
    public string? PaymentMethod { get; set; } // "CASH", "CARD", "CREDIT"
    public string? Notes { get; set; }
    public required string ActorId { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

public enum TransferStatus
{
    Draft = 1,
    InTransit = 2,
    Received = 3,
    Cancelled = 4
}

public class TransferLineItem
{
    public Guid TransferItemId { get; set; } = Guid.NewGuid();
    public string TransferId { get; set; } = string.Empty;
    public required string ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public decimal DispatchedQuantity { get; set; }
    public decimal? ReceivedQuantity { get; set; }
    public decimal DiscrepancyQuantity { get; set; } = 0m;
    public decimal UnitCost { get; set; } = 0m;
    public string? Notes { get; set; }
}

public class TransferItem : TransferLineItem { }

public class Transfer
{
    public string TransferId { get; set; } = Guid.NewGuid().ToString();
    public required string TransferNumber { get; set; }
    public string TenantId { get; set; } = "TENANT_LK_01";
    public required string SourceBranchId { get; set; }
    public required string DestBranchId { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.InTransit;
    public required string DispatchedBy { get; set; }
    public DateTime DispatchedAtUtc { get; set; } = DateTime.UtcNow;
    public string? ReceivedBy { get; set; }
    public DateTime? ReceivedAtUtc { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancellationReason { get; set; }
    public decimal TotalDispatchedQuantity { get; set; }
    public decimal? TotalReceivedQuantity { get; set; }
    public bool HasDiscrepancy { get; set; }
    public string? DiscrepancyNotes { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<TransferLineItem> Lines { get; set; } = new();
}

public enum StockCountStatus
{
    InProgress = 1,
    Completed = 2,
    Cancelled = 3
}

public class StockCountItem
{
    public Guid ItemId { get; set; } = Guid.NewGuid();
    public required string SessionId { get; set; }
    public required string ProductId { get; set; }
    public required string ProductName { get; set; }
    public required string Barcode { get; set; }
    public decimal SnapshotStock { get; set; }
    public decimal? CountedQuantity { get; set; }
    public decimal VarianceQuantity { get; set; }
    public decimal CostBasis { get; set; }
    public decimal VarianceValue { get; set; }
    public bool IsCounted { get; set; }
    public string? CountedBy { get; set; }
    public DateTime? CountedAtUtc { get; set; }
    public string? Notes { get; set; }
}

public class StockCountSession
{
    public required string SessionId { get; set; }
    public required string TenantId { get; set; }
    public required string BranchId { get; set; }
    public StockCountStatus Status { get; set; } = StockCountStatus.InProgress;
    public required string StartedBy { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CompletedBy { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancellationReason { get; set; }
    public string? Notes { get; set; }

    public int TotalItemsCounted { get; set; }
    public decimal TotalVarianceQuantity { get; set; }
    public decimal TotalVarianceValue { get; set; }
    public int LinesWithVarianceCount { get; set; }

    public List<StockCountItem> Items { get; set; } = new();
}

public class StockCountAdjustmentJournal
{
    public required string SessionId { get; set; }
    public required string TenantId { get; set; }
    public required string BranchId { get; set; }
    public DateTime ExecutedAtUtc { get; set; } = DateTime.UtcNow;
    public required string ExecutedBy { get; set; }
    public decimal TotalVarianceQuantity { get; set; }
    public decimal TotalVarianceValue { get; set; }
    public List<StockCountJournalLine> Lines { get; set; } = new();
}

public class StockCountJournalLine
{
    public required string ProductId { get; set; }
    public required string ProductName { get; set; }
    public required string Barcode { get; set; }
    public decimal SnapshotStock { get; set; }
    public decimal CountedQuantity { get; set; }
    public decimal VarianceQuantity { get; set; }
    public decimal CostBasis { get; set; }
    public decimal VarianceValue { get; set; }
    public string AdjustmentReason { get; set; } = "STOCK_COUNT_ADJUSTMENT";
}

public enum LicenseStatus
{
    Unlicensed = 0,
    Active = 1,
    GracePeriod = 2,
    ExpiredLockedOut = 3
}

public class LicenceEntitlement
{
    public string EntitlementId { get; set; } = Guid.NewGuid().ToString();
    public required string TenantId { get; set; }
    public required string Plan { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public int GracePeriodDays { get; set; } = 7;
    public int MaxDevices { get; set; } = 1;
    public List<string> Features { get; set; } = new();
    public int Revision { get; set; } = 1;
    public string SignatureHex { get; set; } = string.Empty;
    public string RawPayloadBase64 { get; set; } = string.Empty;
    public string RawToken { get; set; } = string.Empty;
    public string Signature
    {
        get => !string.IsNullOrEmpty(SignatureHex) ? SignatureHex : string.Empty;
        set => SignatureHex = value;
    }
    public DateTime AppliedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LicenseStatusInfo
{
    public required LicenseStatus Status { get; set; }
    public bool IsSaleAllowed { get; set; }
    public string? TenantId { get; set; }
    public string? Plan { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? GracePeriodExpiresAtUtc { get; set; }
    public int DaysRemaining { get; set; }
    public string Message { get; set; } = string.Empty;
    public LicenceEntitlement? ActiveEntitlement { get; set; }
}

public class EntitlementPayloadDto
{
    [JsonPropertyName("entitlement_id")]
    public string? EntitlementId { get; set; }

    [JsonPropertyName("tenant_id")]
    public required string TenantId { get; set; }

    [JsonPropertyName("plan")]
    public required string Plan { get; set; }

    [JsonPropertyName("issued_at")]
    public required string IssuedAt { get; set; }

    [JsonPropertyName("expires_at")]
    public required string ExpiresAt { get; set; }

    [JsonPropertyName("grace_period_days")]
    public int GracePeriodDays { get; set; } = 7;

    [JsonPropertyName("max_devices")]
    public int MaxDevices { get; set; } = 1;

    [JsonPropertyName("features")]
    public List<string>? Features { get; set; }

    [JsonPropertyName("revision")]
    public int Revision { get; set; } = 1;
}




