using System.Net;
using System.Text.Json;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class SyncWorkerTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _sale;

    public SyncWorkerTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _catalog = new CatalogService(_db);
        _shift = new ShiftService(_db);
        _sale = new SaleService(_db, _catalog);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task PushPendingBatchesAsync_NoEvents_ReturnsSuccessWithoutNetworkCall()
    {
        bool called = false;
        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http);

        var result = await sync.PushPendingBatchesAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.PushedCount);
        Assert.False(called);
    }

    [Fact]
    public async Task PushPendingBatchesAsync_Online_SerializesAndAcknowledgesEvents()
    {
        var cashier = await _auth.CreateUserAsync("sunil", "Sunil Perera", "Pass#123", Role.Cashier);
        var product = new Product
        {
            ProductId = "prod_fuse_10a",
            Barcode = "4798881110",
            Name = "Blade Fuse 10A",
            UnitPrice = 120.00m,
            CostBasis = 60.00m,
            TaxRate = 0.0m,
            StockOnHand = 100m
        };
        await _catalog.AddProductAsync(product);

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 2000.00m, "TENANT_LK_01");

        // Commit sale 1
        await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 2.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 240.00m) }
        ));

        // Commit sale 2
        await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 120.00m) }
        ));

        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            var responseJson = JsonSerializer.Serialize(new
            {
                batch_id = Guid.NewGuid(),
                acknowledged_sequence = 2,
                status = "acknowledged"
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.eightexms.site");

        var pushResult = await sync.PushPendingBatchesAsync();

        Assert.True(pushResult.Success);
        Assert.Equal(2, pushResult.PushedCount);
        Assert.Equal(2L, pushResult.AcknowledgedSequence);

        // Verify captured JSON structure
        Assert.NotNull(capturedBody);
        using (var doc = JsonDocument.Parse(capturedBody))
        {
            var root = doc.RootElement;
            Assert.Equal("TENANT_LK_01", root.GetProperty("tenant_id").GetString());
            Assert.Equal("C01", root.GetProperty("source_device_id").GetString());
            Assert.Equal(2, root.GetProperty("batch_sequence").GetInt32());
            var events = root.GetProperty("events");
            Assert.Equal(2, events.GetArrayLength());

            var ev1 = events[0];
            Assert.Equal(1, ev1.GetProperty("source_sequence").GetInt32());
            Assert.Equal("1.0", ev1.GetProperty("schema_version").GetString());
            Assert.Equal("B01-C01-000001", ev1.GetProperty("payload").GetProperty("receipt_number").GetString());
        }

        // Verify outbox events are marked ACKNOWLEDGED in SQLite
        var remaining = await sync.GetPendingEventsAsync();
        Assert.Empty(remaining);

        var lastAck = await sync.GetLastAcknowledgedSequenceAsync("B01", "C01");
        Assert.Equal(2L, lastAck);
    }

    [Fact]
    public async Task PushPendingBatchesAsync_NetworkOffline_FailsGracefullyAndRetainsPendingEvents()
    {
        var cashier = await _auth.CreateUserAsync("anura", "Anura Kumara", "Pass#123", Role.Cashier);
        var product = new Product
        {
            ProductId = "prod_cable_tie",
            Barcode = "4798882220",
            Name = "Cable Ties 100pk",
            UnitPrice = 450.00m,
            CostBasis = 250.00m,
            TaxRate = 0.0m,
            StockOnHand = 20m
        };
        await _catalog.AddProductAsync(product);

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 1000.00m, "TENANT_LK_01");

        await _sale.CommitSaleAsync(new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 450.00m) }
        ));

        var handler = new MockHttpMessageHandler((HttpRequestMessage _) =>
            throw new HttpRequestException("Socket closed - no internet"));

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.eightexms.site");

        // POS checkout must never hang or crash
        var result = await sync.PushPendingBatchesAsync();

        Assert.False(result.Success);
        Assert.True(result.NetworkOffline);
        Assert.Equal(0, result.PushedCount);

        // Pending events must remain in SQLite for future retry
        var pending = await sync.GetPendingEventsAsync();
        Assert.Single(pending);
        Assert.Equal("PENDING", pending[0].Status);
    }

    [Fact]
    public async Task SyncBackgroundWorker_Starts_ExecutesCycle_And_StopsCleanly()
    {
        bool pushCalled = false;
        bool pullCalled = false;

        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            if (req.RequestUri?.ToString().Contains("/push") == true)
            {
                pushCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"batch_id\":\"00000000-0000-0000-0000-000000000000\",\"acknowledged_sequence\":0,\"status\":\"acknowledged\"}", System.Text.Encoding.UTF8, "application/json")
                };
            }
            if (req.RequestUri?.ToString().Contains("/catalog") == true)
            {
                pullCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"server_time\":\"2026-09-21T12:00:00Z\",\"products\":[],\"categories\":[],\"deleted_item_ids\":[]}", System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http);

        using var worker = new SyncBackgroundWorker(sync, interval: TimeSpan.FromMilliseconds(50));
        Assert.False(worker.IsRunning);

        worker.Start();
        Assert.True(worker.IsRunning);

        // Allow at least 1 cycle to execute
        for (int i = 0; i < 20 && !pushCalled && !pullCalled; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(pushCalled || pullCalled);
        Assert.True(worker.IsOnline);

        worker.Stop();
        Assert.False(worker.IsRunning);
    }

    [Fact]
    public async Task SyncBackgroundWorker_OfflineProbe_SkipsNetworkCall()
    {
        bool networkHit = false;
        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            networkHit = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http);

        // Probe explicitly reports offline
        using var worker = new SyncBackgroundWorker(
            sync,
            interval: TimeSpan.FromMilliseconds(50),
            connectivityProbe: () => Task.FromResult(false)
        );

        await worker.SyncCycleAsync();

        Assert.False(worker.IsOnline);
        Assert.False(networkHit);
    }

    [Fact]
    public async Task CashierBilling_ConcurrentWithCatalogSync_DoesNotLockOrBlock()
    {
        // 1. Prepare cashier, shift, product
        var cashier = await _auth.CreateUserAsync("chaminda", "Chaminda Vaas", "Pass#123", Role.Cashier);
        var prod1 = new Product
        {
            ProductId = "prod_oil_filter",
            Barcode = "4797771110",
            Name = "Standard Oil Filter",
            UnitPrice = 1500.00m,
            CostBasis = 900.00m,
            TaxRate = 0.18m,
            StockOnHand = 50m
        };
        await _catalog.AddProductAsync(prod1);

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        // 2. Launch concurrent catalog sync updates and cashier sales simultaneously
        var tasks = new List<Task>();

        // Catalog sync updates 10 items in a loop
        tasks.Add(Task.Run(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                await _catalog.ApplyCatalogUpdatesAsync(new CatalogSyncResponseDto
                {
                    ServerTime = DateTime.UtcNow,
                    Products = new List<CatalogProductDto>
                    {
                        new()
                        {
                            ProductId = $"prod_catalog_{i}",
                            Barcode = $"479000{i:D4}",
                            Name = $"Sync Part #{i}",
                            UnitPrice = 1000m + i * 100m,
                            CostBasis = 600m,
                            TaxRate = 0.18m,
                            IsActive = true,
                            UpdatedAt = DateTime.UtcNow
                        }
                    }
                });
                await Task.Delay(5);
            }
        }));

        // Cashier commits 5 sales simultaneously
        tasks.Add(Task.Run(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                var sale = await _sale.CommitSaleAsync(new CreateSaleCommand(
                    TenantId: "TENANT_LK_01",
                    BranchId: "B01",
                    CounterId: "C01",
                    CashierId: cashier.UserId,
                    ShiftId: shift.ShiftId,
                    Items: new List<CreateSaleLineRequest> { new(prod1.ProductId, 1.0m) },
                    Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 1770.00m) }
                ));
                Assert.NotNull(sale);
                await Task.Delay(5);
            }
        }));

        // Both background catalog sync and cashier sales must complete with 0 locking errors
        await Task.WhenAll(tasks);

        // Verify sales count
        var totalSales = await _sale.GetSaleCountAsync();
        Assert.Equal(5, totalSales);

        // Verify synced products were inserted
        var allProds = await _catalog.GetAllActiveProductsAsync();
        Assert.True(allProds.Count >= 6); // prod1 + 5 synced parts
    }
}
