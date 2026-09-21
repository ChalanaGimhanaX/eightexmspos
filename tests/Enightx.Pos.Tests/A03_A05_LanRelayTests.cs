using System.Text.Json;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;
using Xunit;

namespace Enightx.Pos.Tests;

public class A03_A05_LanRelayTests : IDisposable
{
    private readonly PosDatabase _db;

    public A03_A05_LanRelayTests()
    {
        _db = PosDatabase.CreateInMemory();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task A03_InternetFails_RelayRemains_CountersExchangeUpdatesLocally()
    {
        var relay = new LanRelayService(_db);

        var peerEvent = new LanPeerEvent
        {
            EventId = Guid.NewGuid(),
            TenantId = "tenant-01",
            BranchId = "branch-01",
            DeviceId = "counter-02",
            DeviceGeneration = 1,
            SourceSequence = 1,
            SchemaVersion = "1.0",
            ActorId = "cashier_2",
            PayloadJson = JsonSerializer.Serialize(new { SaleId = Guid.NewGuid(), GrandTotal = 1500.00m }),
            OccurredAtUtc = DateTime.UtcNow
        };

        // Ingest peer event locally over LAN
        var accepted = await relay.IngestPeerEventAsync(peerEvent);
        Assert.True(accepted);
        Assert.Equal(1, relay.IngestedEventsCount);
        Assert.Equal(0, relay.DuplicateEventsDroppedCount);
    }

    [Fact]
    public async Task A04_RelayAndInternetFail_CountersBillIndependently_OutboxPreserved()
    {
        var syncService = new SyncService(_db);
        var relay = new LanRelayService(_db);

        // Counter records sales to local outbox while completely isolated
        var saleEvent = new OutboxEvent
        {
            EventId = Guid.NewGuid(),
            TenantId = "tenant-01",
            BranchId = "branch-01",
            DeviceId = "counter-01",
            DeviceGeneration = 1,
            SourceSequence = 1,
            SchemaVersion = "1.0",
            ActorId = "cashier_1",
            PayloadJson = JsonSerializer.Serialize(new { SaleId = Guid.NewGuid(), GrandTotal = 2500.00m }),
            OccurredAtUtc = DateTime.UtcNow
        };

        await syncService.SaveOutboxEventAsync(saleEvent);

        // Verify outbox contains pending event
        var pendingEvents = await syncService.GetPendingEventsAsync();
        Assert.Single(pendingEvents);
        Assert.Equal(1, pendingEvents[0].SourceSequence);

        // Export for LAN sync when relay becomes available
        var lanEvents = await relay.ExportOutboxForLanSyncAsync();
        Assert.Single(lanEvents);
        Assert.Equal("counter-01", lanEvents[0].DeviceId);
    }

    [Fact]
    public async Task A05_EventDuplicatedOverLanAndCloud_ExactlyOneBusinessEffect()
    {
        var relay = new LanRelayService(_db);

        var peerEvent = new LanPeerEvent
        {
            EventId = Guid.NewGuid(),
            TenantId = "tenant-01",
            BranchId = "branch-01",
            DeviceId = "counter-02",
            DeviceGeneration = 1,
            SourceSequence = 42,
            SchemaVersion = "1.0",
            ActorId = "cashier_2",
            PayloadJson = JsonSerializer.Serialize(new { SaleId = Guid.NewGuid(), GrandTotal = 3000.00m }),
            OccurredAtUtc = DateTime.UtcNow
        };

        // First arrival (e.g. via LAN relay)
        var firstResult = await relay.IngestPeerEventAsync(peerEvent);
        Assert.True(firstResult);
        Assert.Equal(1, relay.IngestedEventsCount);

        // Duplicate arrival (e.g. via Cloud sync or network retry)
        var secondResult = await relay.IngestPeerEventAsync(peerEvent);
        Assert.False(secondResult); // Deduplicated! Exactly one business effect.
        Assert.Equal(1, relay.IngestedEventsCount);
        Assert.Equal(1, relay.DuplicateEventsDroppedCount);
    }
}

