using Enightx.Pos.Common;

namespace Enightx.Pos.Services;

public enum TransferStatus
{
    Requested = 1,
    Dispatched = 2,
    Received = 3,
    Cancelled = 4
}

public class BranchStockItem
{
    public required string BranchId { get; set; }
    public required string ProductId { get; set; }
    public decimal StockOnHand { get; set; }
    public decimal StockInTransit { get; set; }
}

public class DesktopStockTransfer
{
    public Guid TransferId { get; set; } = Guid.NewGuid();
    public required string SourceBranchId { get; set; }
    public required string DestBranchId { get; set; }
    public required string ProductId { get; set; }
    public decimal Quantity { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.Requested;
    public DateTime? DispatchedAtUtc { get; set; }
    public DateTime? ReceivedAtUtc { get; set; }
}

public interface ITransferService
{
    DesktopStockTransfer RequestTransfer(string sourceBranch, string destBranch, string productId, decimal quantity);
    void DispatchTransfer(DesktopStockTransfer transfer, BranchStockItem sourceStock, BranchStockItem destStock);
    void ReceiveTransfer(DesktopStockTransfer transfer, BranchStockItem destStock);
}

public class TransferService : ITransferService
{
    public DesktopStockTransfer RequestTransfer(string sourceBranch, string destBranch, string productId, decimal quantity)
    {
        if (sourceBranch == destBranch)
            throw new PosException("Source and destination branch cannot be the same.");

        var qty = MoneyCalculator.Round(quantity);
        if (qty <= 0)
            throw new PosException("Transfer quantity must be greater than zero.");

        return new DesktopStockTransfer
        {
            TransferId = Guid.NewGuid(),
            SourceBranchId = sourceBranch,
            DestBranchId = destBranch,
            ProductId = productId,
            Quantity = qty,
            Status = TransferStatus.Requested
        };
    }

    public void DispatchTransfer(DesktopStockTransfer transfer, BranchStockItem sourceStock, BranchStockItem destStock)
    {
        if (transfer.Status != TransferStatus.Requested)
            throw new PosException($"Cannot dispatch transfer in status '{transfer.Status}'.");

        if (sourceStock.StockOnHand < transfer.Quantity)
            throw new InsufficientStockException($"Source branch stock ({sourceStock.StockOnHand}) is insufficient for transfer quantity ({transfer.Quantity}).");

        // A12: Deduct from source branch, place into in-transit at destination
        sourceStock.StockOnHand = MoneyCalculator.Round(sourceStock.StockOnHand - transfer.Quantity);
        destStock.StockInTransit = MoneyCalculator.Round(destStock.StockInTransit + transfer.Quantity);

        transfer.Status = TransferStatus.Dispatched;
        transfer.DispatchedAtUtc = DateTime.UtcNow;
    }

    public void ReceiveTransfer(DesktopStockTransfer transfer, BranchStockItem destStock)
    {
        if (transfer.Status != TransferStatus.Dispatched)
            throw new PosException($"Cannot receive transfer in status '{transfer.Status}'. Must be Dispatched.");

        // A12: Remove from in-transit, add to destination stock on hand
        destStock.StockInTransit = MoneyCalculator.Round(destStock.StockInTransit - transfer.Quantity);
        destStock.StockOnHand = MoneyCalculator.Round(destStock.StockOnHand + transfer.Quantity);

        transfer.Status = TransferStatus.Received;
        transfer.ReceivedAtUtc = DateTime.UtcNow;
    }
}

