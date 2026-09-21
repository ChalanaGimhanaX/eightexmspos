using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

    public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public static MockHttpMessageHandler FromSync(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new MockHttpMessageHandler(req => Task.FromResult(handler(req)));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _handler(request);
    }
}

public class CatalogSyncTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly CatalogService _catalog;

    public CatalogSyncTests()
    {
        _db = PosDatabase.CreateInMemory();
        _catalog = new CatalogService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task CatalogSync_InsertsNewCategoriesAndProducts()
    {
        var now = DateTime.UtcNow;
        var response = new CatalogSyncResponseDto
        {
            ServerTime = now,
            Categories = new List<CategoryDto>
            {
                new()
                {
                    CategoryId = "cat_suspension",
                    Name = "Suspension Parts",
                    Description = "Shock absorbers and mounts",
                    IsActive = true,
                    UpdatedAt = now
                }
            },
            Products = new List<CatalogProductDto>
            {
                new()
                {
                    ProductId = "prod_shock_01",
                    CategoryId = "cat_suspension",
                    Barcode = "4790001001",
                    Name = "Front Shock Absorber (Civic)",
                    NameSi = "ඉදිරිපස ෂොක් ඇබ්සෝබර්",
                    NameTa = "முன் அதிர்ச்சி உறிஞ்சி",
                    UnitPrice = 12500.00m,
                    CostBasis = 8500.00m,
                    TaxRate = 0.18m,
                    IsActive = true,
                    UpdatedAt = now
                }
            },
            DeletedItemIds = new List<string>()
        };

        await _catalog.ApplyCatalogUpdatesAsync(response);

        // Verify product in database
        var prod = await _catalog.GetProductByBarcodeAsync("4790001001");
        Assert.NotNull(prod);
        Assert.Equal("prod_shock_01", prod.ProductId);
        Assert.Equal("cat_suspension", prod.CategoryId);
        Assert.Equal("Front Shock Absorber (Civic)", prod.Name);
        Assert.Equal(12500.00m, prod.UnitPrice);
        Assert.Equal(8500.00m, prod.CostBasis);
        Assert.Equal(0.18m, prod.TaxRate);
        Assert.Equal(0.0m, prod.StockOnHand);
        Assert.True(prod.IsActive);

        // Verify categories
        var categories = await _catalog.GetAllActiveCategoriesAsync();
        Assert.Single(categories);
        Assert.Equal("cat_suspension", categories[0].CategoryId);
        Assert.Equal("Suspension Parts", categories[0].Name);

        // Verify sync cursor
        var lastSync = await _catalog.GetLastCatalogSyncTimeAsync();
        Assert.NotNull(lastSync);
        Assert.Equal(now.ToString("o"), lastSync.Value.ToString("o"));
    }

    [Fact]
    public async Task CatalogSync_PreservesLocalStockOnHand_OnPriceOrNameUpdate()
    {
        // 1. Existing product with local stock from previous operations
        var existing = new Product
        {
            ProductId = "prod_battery_12v",
            Barcode = "4790002002",
            Name = "12V 45Ah Battery",
            UnitPrice = 22000.00m,
            CostBasis = 17000.00m,
            TaxRate = 0.18m,
            StockOnHand = 37.0m, // Local inventory on counter
            IsActive = true
        };
        await _catalog.AddProductAsync(existing);

        // Verify initial stock
        var initialStock = await _catalog.GetStockOnHandAsync(existing.ProductId);
        Assert.Equal(37.0m, initialStock);

        // 2. Server sends price increase and name change
        var now = DateTime.UtcNow;
        var response = new CatalogSyncResponseDto
        {
            ServerTime = now,
            Products = new List<CatalogProductDto>
            {
                new()
                {
                    ProductId = "prod_battery_12v",
                    Barcode = "4790002002",
                    Name = "12V 45Ah Maintenance-Free Battery",
                    UnitPrice = 24500.00m, // Price increased
                    CostBasis = 19000.00m,
                    TaxRate = 0.18m,
                    IsActive = true,
                    UpdatedAt = now
                }
            }
        };

        await _catalog.ApplyCatalogUpdatesAsync(response);

        // 3. Product should reflect updated price and name, but KEEP 37.0m stock!
        var updated = await _catalog.GetProductByIdAsync("prod_battery_12v");
        Assert.NotNull(updated);
        Assert.Equal("12V 45Ah Maintenance-Free Battery", updated.Name);
        Assert.Equal(24500.00m, updated.UnitPrice);
        Assert.Equal(37.0m, updated.StockOnHand); // CRITICAL: Local stock was NOT wiped to 0
    }

    [Fact]
    public async Task CatalogSync_SoftDeletesProducts_BySettingIsActiveZero()
    {
        var prod = new Product
        {
            ProductId = "prod_obsolete_part",
            Barcode = "4790003003",
            Name = "Discontinued Carburetor Gasket",
            UnitPrice = 350.00m,
            CostBasis = 180.00m,
            TaxRate = 0.0m,
            StockOnHand = 5.0m,
            IsActive = true
        };
        await _catalog.AddProductAsync(prod);

        // Server signals item was deleted
        var now = DateTime.UtcNow;
        var response = new CatalogSyncResponseDto
        {
            ServerTime = now,
            Products = new List<CatalogProductDto>(),
            Categories = new List<CategoryDto>(),
            DeletedItemIds = new List<string> { "prod_obsolete_part" }
        };

        await _catalog.ApplyCatalogUpdatesAsync(response);

        // Cashier searching by barcode should not find it (active=0)
        var byBarcode = await _catalog.GetProductByBarcodeAsync("4790003003");
        Assert.Null(byBarcode);

        // Searching by ID with active=1 should not find it
        var byId = await _catalog.GetProductByIdAsync("prod_obsolete_part");
        Assert.Null(byId);

        // Row remains in database (for foreign key integrity of past receipts)
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_active FROM products WHERE product_id = 'prod_obsolete_part';";
        var isActive = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(0, isActive);
    }

    [Fact]
    public async Task PullCatalogUpdatesAsync_Online_AppliesDataAndSetsCursor()
    {
        var serverNow = DateTime.UtcNow;
        var fakeCatalogResponse = new CatalogSyncResponseDto
        {
            ServerTime = serverNow,
            Categories = new List<CategoryDto>
            {
                new() { CategoryId = "cat_electrical", Name = "Electrical", IsActive = true, UpdatedAt = serverNow }
            },
            Products = new List<CatalogProductDto>
            {
                new()
                {
                    ProductId = "prod_relay_01",
                    CategoryId = "cat_electrical",
                    Barcode = "4799990001",
                    Name = "Horn Relay 12V 30A",
                    UnitPrice = 850.00m,
                    CostBasis = 500.00m,
                    TaxRate = 0.18m,
                    IsActive = true,
                    UpdatedAt = serverNow
                }
            },
            DeletedItemIds = new List<string>()
        };

        HttpRequestMessage? capturedRequest = null;
        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            capturedRequest = req;
            var json = JsonSerializer.Serialize(fakeCatalogResponse);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.eightexms.site");

        var result = await sync.PullCatalogUpdatesAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.ProductsUpdated);
        Assert.Equal(1, result.CategoriesUpdated);
        Assert.Equal(0, result.ItemsDeleted);
        Assert.False(result.NetworkOffline);
        Assert.NotNull(capturedRequest);
        Assert.Contains("/api/v1/sync/catalog", capturedRequest.RequestUri?.ToString());

        // Verify product now available locally
        var prod = await _catalog.GetProductByBarcodeAsync("4799990001");
        Assert.NotNull(prod);
        Assert.Equal("Horn Relay 12V 30A", prod.Name);
    }

    [Fact]
    public async Task PullCatalogUpdatesAsync_NetworkOffline_FailsSilently()
    {
        var handler = new MockHttpMessageHandler((HttpRequestMessage _) =>
            throw new HttpRequestException("Host unreachable / Network is down"));

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.eightexms.site");

        // POS should never crash on network failure
        var result = await sync.PullCatalogUpdatesAsync();

        Assert.False(result.Success);
        Assert.True(result.NetworkOffline);
        Assert.Contains("Network is down", result.ErrorMessage);
    }
}
