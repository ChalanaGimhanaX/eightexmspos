using System.Text.Json;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class SyncServiceTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _sale;
    private readonly SyncService _sync;

    public SyncServiceTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _catalog = new CatalogService(_db);
        _shift = new ShiftService(_db);
        _sale = new SaleService(_db, _catalog);
        _sync = new SyncService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task OutboxEvents_ContainMetadata_And_CanBeAcknowledged()
    {
        var cashier = await _auth.CreateUserAsync("kamal", "Kamal Silva", "Pass#123", Role.Cashier);
        var product = new Product
        {
            ProductId = "prod_bulb_sync",
            Barcode = "8907778889990",
            Name = "Indicator Bulb 12V 10W",
            UnitPrice = 250.00m,
            CostBasis = 150.00m,
            TaxRate = 0.0m,
            StockOnHand = 50m
        };
        await _catalog.AddProductAsync(product);

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        // Commit sale 1
        var sale1 = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 2.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 500.00m) }
        ));

        // Commit sale 2
        var sale2 = await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 250.00m) }
        ));

        // Retrieve pending outbox events
        var pending = await _sync.GetPendingEventsAsync();
        Assert.Equal(2, pending.Count);

        // Sequence numbers must be monotonic (1 and 2)
        Assert.Equal(1L, pending[0].SourceSequence);
        Assert.Equal(2L, pending[1].SourceSequence);

        // Metadata must be present
        Assert.Equal(cashier.UserId, pending[0].ActorId);
        Assert.True(pending[0].OccurredAtUtc <= DateTime.UtcNow);
        Assert.Equal("PENDING", pending[0].Status);

        // Parse payload to verify valid structure
        using (var doc = JsonDocument.Parse(pending[0].PayloadJson))
        {
            var root = doc.RootElement;
            Assert.Equal("B01-C01-000001", root.GetProperty("receipt_number").GetString());
            Assert.Equal("500.00", root.GetProperty("grand_total").GetString());
        }

        // Acknowledge event 1
        await _sync.MarkEventsAcknowledgedAsync(new[] { pending[0].EventId });

        // Pending should now only have event 2
        var remainingPending = await _sync.GetPendingEventsAsync();
        Assert.Single(remainingPending);
        Assert.Equal(2L, remainingPending[0].SourceSequence);

        // Last acknowledged sequence for B01/C01 should be 1
        var lastSeq = await _sync.GetLastAcknowledgedSequenceAsync("B01", "C01");
        Assert.Equal(1L, lastSeq);

        // Acknowledge event 2
        await _sync.MarkEventsAcknowledgedAsync(new[] { pending[1].EventId });
        var afterAllAck = await _sync.GetPendingEventsAsync();
        Assert.Empty(afterAllAck);

        var finalSeq = await _sync.GetLastAcknowledgedSequenceAsync("B01", "C01");
        Assert.Equal(2L, finalSeq);
    }
}
