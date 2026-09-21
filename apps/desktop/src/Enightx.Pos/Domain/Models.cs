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
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class Product
{
    public required string ProductId { get; set; }
    public required string Barcode { get; set; }
    public required string Name { get; set; }
    public string? NameSi { get; set; }
    public string? NameTa { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal CostBasis { get; set; }
    public decimal TaxRate { get; set; }
    public decimal StockOnHand { get; set; }
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
    public decimal DiscountRate { get; set; }
    public decimal DiscountFixed { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
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

public class Customer
{
    public required string CustomerId { get; set; }
    public required string TenantId { get; set; }
    public required string Name { get; set; }
    public required string Phone { get; set; }
    public string? Email { get; set; }
    public string? NicOrBrn { get; set; }
    public decimal CreditLimit { get; set; }
    public decimal CurrentBalance { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class CustomerLedgerEntry
{
    public Guid EntryId { get; set; } = Guid.NewGuid();
    public required string TenantId { get; set; }
    public required string CustomerId { get; set; }
    public required string BranchId { get; set; }
    public required string CounterId { get; set; }
    public required string EntryType { get; set; } // "INVOICE", "SETTLEMENT"
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? ReferenceId { get; set; }
    public string? PaymentMethod { get; set; }
    public required string ActorId { get; set; }
    public string? Notes { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

