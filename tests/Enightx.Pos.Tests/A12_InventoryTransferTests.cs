using Enightx.Pos.Common;
using Enightx.Pos.Services;
using Xunit;

namespace Enightx.Pos.Tests;

public class A12_InventoryTransferTests
{
    [Fact]
    public void A12_BranchTransferIncomplete_StaysInTransitUntilReceived()
    {
        var transferService = new TransferService();

        var sourceStock = new BranchStockItem
        {
            BranchId = "branch-colombo",
            ProductId = "PROD-BISCUIT-400G",
            StockOnHand = 50.00m,
            StockInTransit = 0.00m
        };

        var destStock = new BranchStockItem
        {
            BranchId = "branch-kandy",
            ProductId = "PROD-BISCUIT-400G",
            StockOnHand = 10.00m,
            StockInTransit = 0.00m
        };

        // 1. Request transfer
        var transfer = transferService.RequestTransfer(
            sourceBranch: "branch-colombo",
            destBranch: "branch-kandy",
            productId: "PROD-BISCUIT-400G",
            quantity: 20.00m
        );
        Assert.Equal(TransferStatus.Requested, transfer.Status);

        // 2. Dispatch transfer
        transferService.DispatchTransfer(transfer, sourceStock, destStock);

        // A12 Validation:
        // Source stock is deducted
        Assert.Equal(30.00m, sourceStock.StockOnHand);
        // Goods do NOT appear at destination yet (stays in-transit)
        Assert.Equal(10.00m, destStock.StockOnHand);
        Assert.Equal(20.00m, destStock.StockInTransit);
        Assert.Equal(TransferStatus.Dispatched, transfer.Status);

        // 3. Receive transfer at destination
        transferService.ReceiveTransfer(transfer, destStock);

        // Goods now added to destination stock on hand, cleared from transit
        Assert.Equal(30.00m, destStock.StockOnHand);
        Assert.Equal(0.00m, destStock.StockInTransit);
        Assert.Equal(TransferStatus.Received, transfer.Status);
    }

    [Fact]
    public void A12_InsufficientSourceStock_TransferRejected()
    {
        var transferService = new TransferService();
        var sourceStock = new BranchStockItem
        {
            BranchId = "branch-colombo",
            ProductId = "PROD-SOAP-100G",
            StockOnHand = 5.00m,
            StockInTransit = 0.00m
        };
        var destStock = new BranchStockItem
        {
            BranchId = "branch-galle",
            ProductId = "PROD-SOAP-100G",
            StockOnHand = 0.00m,
            StockInTransit = 0.00m
        };

        var transfer = transferService.RequestTransfer("branch-colombo", "branch-galle", "PROD-SOAP-100G", 15.00m);

        Assert.Throws<InsufficientStockException>(() =>
            transferService.DispatchTransfer(transfer, sourceStock, destStock)
        );
    }
}

