using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class ProductManagementTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly CatalogService _catalog;
    private readonly ReportService _report;

    public ProductManagementTests()
    {
        _db = PosDatabase.CreateInMemory();
        _catalog = new CatalogService(_db);
        _report = new ReportService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task AddProduct_WithMinStockThreshold_SavesAndRetrievesCorrectly()
    {
        var product = new Product
        {
            ProductId = "prod_brake_pad_01",
            Barcode = "479001001",
            Name = "Ceramic Brake Pad Set",
            NameSi = "සෙරමික් බ්‍රේක් පෑඩ් කට්ටලය",
            NameTa = "செராமிக் பிரேக் பேட்",
            UnitPrice = 5400.00m,
            CostBasis = 3800.00m,
            TaxRate = 0.18m,
            StockOnHand = 25.0m,
            MinStockThreshold = 8.5m,
            IsActive = true
        };

        await _catalog.AddProductAsync(product);

        // 1. Retrieve by ID
        var byId = await _catalog.GetProductByIdAsync("prod_brake_pad_01");
        Assert.NotNull(byId);
        Assert.Equal("Ceramic Brake Pad Set", byId.Name);
        Assert.Equal(8.5m, byId.MinStockThreshold);
        Assert.Equal(5400.00m, byId.UnitPrice);
        Assert.Equal(3800.00m, byId.CostBasis);
        Assert.Equal(25.0m, byId.StockOnHand);
        Assert.True(byId.IsActive);

        // 2. Retrieve by Barcode
        var byBarcode = await _catalog.GetProductByBarcodeAsync("479001001");
        Assert.NotNull(byBarcode);
        Assert.Equal("prod_brake_pad_01", byBarcode.ProductId);
        Assert.Equal(8.5m, byBarcode.MinStockThreshold);

        // 3. Retrieve all active
        var activeList = await _catalog.GetAllActiveProductsAsync();
        Assert.Contains(activeList, p => p.ProductId == "prod_brake_pad_01" && p.MinStockThreshold == 8.5m);
    }

    [Fact]
    public async Task AddProduct_DefaultMinStockThreshold_DefaultsToZero()
    {
        var product = new Product
        {
            ProductId = "prod_spark_01",
            Barcode = "479002002",
            Name = "Standard Spark Plug",
            UnitPrice = 850.00m,
            CostBasis = 500.00m,
            StockOnHand = 10.0m
        };

        await _catalog.AddProductAsync(product);

        var retrieved = await _catalog.GetProductByIdAsync("prod_spark_01");
        Assert.NotNull(retrieved);
        Assert.Equal(0.0m, retrieved.MinStockThreshold);
    }

    [Fact]
    public async Task UpdateProduct_UpdatesDetailsAndMinStockThreshold_PreservingStockOnHand()
    {
        var product = new Product
        {
            ProductId = "prod_oil_filter_01",
            Barcode = "479003003",
            Name = "Oil Filter Element",
            UnitPrice = 1200.00m,
            CostBasis = 800.00m,
            TaxRate = 0.18m,
            StockOnHand = 42.0m,
            MinStockThreshold = 10.0m,
            IsActive = true
        };
        await _catalog.AddProductAsync(product);

        // Update details and min threshold
        product.Name = "Premium Micro-Filtration Oil Filter";
        product.UnitPrice = 1450.00m;
        product.CostBasis = 950.00m;
        product.MinStockThreshold = 15.0m;
        product.StockOnHand = 999.0m; // Caller attempted to change stock, but UpdateProductAsync must protect stock_on_hand

        await _catalog.UpdateProductAsync(product);

        var updated = await _catalog.GetProductByIdAsync("prod_oil_filter_01");
        Assert.NotNull(updated);
        Assert.Equal("Premium Micro-Filtration Oil Filter", updated.Name);
        Assert.Equal(1450.00m, updated.UnitPrice);
        Assert.Equal(950.00m, updated.CostBasis);
        Assert.Equal(15.0m, updated.MinStockThreshold);
        Assert.Equal(42.0m, updated.StockOnHand); // CRITICAL: Local inventory preserved
    }

    [Fact]
    public async Task DeleteProduct_SoftDeletes_SetsIsActiveToZero_AndExcludesFromCashierLookup()
    {
        var product = new Product
        {
            ProductId = "prod_discontinued_01",
            Barcode = "479004004",
            Name = "Discontinued Wiper Blade",
            UnitPrice = 900.00m,
            CostBasis = 450.00m,
            StockOnHand = 3.0m,
            MinStockThreshold = 2.0m,
            IsActive = true
        };
        await _catalog.AddProductAsync(product);

        // Soft delete
        await _catalog.DeleteProductAsync("prod_discontinued_01");

        // Cashier searches return null
        var byBarcode = await _catalog.GetProductByBarcodeAsync("479004004");
        Assert.Null(byBarcode);

        var byId = await _catalog.GetProductByIdAsync("prod_discontinued_01");
        Assert.Null(byId);

        var activeList = await _catalog.GetAllActiveProductsAsync();
        Assert.DoesNotContain(activeList, p => p.ProductId == "prod_discontinued_01");

        // Manager view including inactive returns the item with IsActive=false
        var allList = await _catalog.GetAllProductsAsync(includeInactive: true);
        var inactiveItem = allList.FirstOrDefault(p => p.ProductId == "prod_discontinued_01");
        Assert.NotNull(inactiveItem);
        Assert.False(inactiveItem.IsActive);

        // Direct DB verification: row exists with is_active = 0
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_active FROM products WHERE product_id = 'prod_discontinued_01';";
        var dbIsActive = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(0, dbIsActive);
    }

    [Fact]
    public async Task DeleteProduct_NonExistentProduct_ThrowsKeyNotFoundException()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            await _catalog.DeleteProductAsync("prod_non_existent");
        });
    }

    [Fact]
    public async Task UpdateProduct_NonExistentProduct_ThrowsKeyNotFoundException()
    {
        var ghostProduct = new Product
        {
            ProductId = "prod_ghost",
            Barcode = "000000",
            Name = "Ghost Item",
            UnitPrice = 100m,
            CostBasis = 50m
        };

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            await _catalog.UpdateProductAsync(ghostProduct);
        });
    }

    [Fact]
    public async Task CatalogSyncCompatibility_PullCatalogUpdates_UpsertsMinStockThreshold_PreservingLocalStock()
    {
        // 1. Product exists locally with stock
        var initial = new Product
        {
            ProductId = "prod_sync_item_01",
            Barcode = "479005005",
            Name = "LED Headlight Bulb H4",
            UnitPrice = 3500.00m,
            CostBasis = 2200.00m,
            StockOnHand = 14.0m,
            MinStockThreshold = 4.0m,
            IsActive = true
        };
        await _catalog.AddProductAsync(initial);

        // 2. Server sends catalog update with revised price, name, and new min threshold
        var now = DateTime.UtcNow;
        var syncResponse = new CatalogSyncResponseDto
        {
            ServerTime = now,
            Products = new List<CatalogProductDto>
            {
                new()
                {
                    ProductId = "prod_sync_item_01",
                    Barcode = "479005005",
                    Name = "LED Headlight Bulb H4 (High Output)",
                    UnitPrice = 3900.00m,
                    CostBasis = 2400.00m,
                    TaxRate = 0.18m,
                    MinStockThreshold = 6.0m,
                    IsActive = true,
                    UpdatedAt = now
                }
            }
        };

        await _catalog.ApplyCatalogUpdatesAsync(syncResponse);

        // 3. Verify sync applied changes and preserved local stock
        var result = await _catalog.GetProductByIdAsync("prod_sync_item_01");
        Assert.NotNull(result);
        Assert.Equal("LED Headlight Bulb H4 (High Output)", result.Name);
        Assert.Equal(3900.00m, result.UnitPrice);
        Assert.Equal(2400.00m, result.CostBasis);
        Assert.Equal(6.0m, result.MinStockThreshold);
        Assert.Equal(14.0m, result.StockOnHand); // CRITICAL: Local stock was NOT overwritten
    }

    [Fact]
    public void DatabaseMigration_IdempotentALTER_DoesNotThrowOnMultipleInits()
    {
        // PosDatabase.Initialize() is called inside CreateInMemory()
        // Executing Initialize() a second time on the same connection string tests idempotency
        var ex = Record.Exception(() => _db.Initialize());
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(-1, 10, 5)]
    [InlineData(100, -1, 5)]
    [InlineData(100, 50, -0.5)]
    public async Task AddProduct_NegativeValues_ThrowsArgumentException(decimal unitPrice, decimal costBasis, decimal minThreshold)
    {
        var product = new Product
        {
            ProductId = "prod_invalid",
            Barcode = "99999",
            Name = "Invalid Product",
            UnitPrice = unitPrice,
            CostBasis = costBasis,
            MinStockThreshold = minThreshold
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await _catalog.AddProductAsync(product);
        });
    }

    [Fact]
    public async Task AddProduct_DuplicateBarcode_ThrowsSqliteException()
    {
        var p1 = new Product
        {
            ProductId = "prod_dup_1",
            Barcode = "BARCODE_UNIQUE_123",
            Name = "Item One",
            UnitPrice = 100m,
            CostBasis = 50m
        };
        await _catalog.AddProductAsync(p1);

        var p2 = new Product
        {
            ProductId = "prod_dup_2",
            Barcode = "BARCODE_UNIQUE_123", // Duplicate barcode
            Name = "Item Two",
            UnitPrice = 200m,
            CostBasis = 100m
        };

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await _catalog.AddProductAsync(p2);
        });
    }

    [Fact]
    public async Task InventoryValuationReport_FacadeDelegatesToInventorySummaryReport()
    {
        var p1 = new Product { ProductId = "val_facade_1", Barcode = "VF01", Name = "Headlight Bulb", UnitPrice = 1200m, CostBasis = 700m, StockOnHand = 15m };
        var p2 = new Product { ProductId = "val_facade_2", Barcode = "VF02", Name = "Fuse 10A", UnitPrice = 150m, CostBasis = 60m, StockOnHand = 2m }; // Low stock (2 <= 5)
        var p3 = new Product { ProductId = "val_facade_3", Barcode = "VF03", Name = "Horn Relay", UnitPrice = 850m, CostBasis = 450m, StockOnHand = -1m }; // Negative stock

        await _catalog.AddProductAsync(p1);
        await _catalog.AddProductAsync(p2);
        await _catalog.AddProductAsync(p3);

        // Act
        var summary = await _report.GenerateInventorySummaryReportAsync();
        var valuation = await _report.GenerateInventoryValuationReportAsync();

        // Assert parity between facade and summary
        Assert.Equal(summary.TotalProducts, valuation.TotalProducts);
        Assert.Equal(summary.LowStockProducts, valuation.LowStockProducts);
        Assert.Equal(summary.NegativeStockProducts, valuation.NegativeStockProducts);
        Assert.Equal(summary.TotalValuationAtCost, valuation.TotalValuationAtCost);
        Assert.Equal(summary.TotalValuationAtRetail, valuation.TotalValuationAtRetail);
        Assert.Equal(summary.Items.Count, valuation.Items.Count);

        // Verification of half-up arithmetic and figures:
        // Valuation @ Cost: (15 * 700) + (2 * 60) + (-1 * 450) = 10500 + 120 - 450 = 10,170.00 LKR
        Assert.Equal(10170.00m, valuation.TotalValuationAtCost);
        // Valuation @ Retail: (15 * 1200) + (2 * 150) + (-1 * 850) = 18000 + 300 - 850 = 17,450.00 LKR
        Assert.Equal(17450.00m, valuation.TotalValuationAtRetail);
        Assert.Equal(1, valuation.LowStockProducts);
        Assert.Equal(1, valuation.NegativeStockProducts);
    }
}

