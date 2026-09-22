using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class CatalogAndValuationChallengerTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly CatalogService _catalog;
    private readonly ReportService _report;
    private readonly string _tempDbPath;

    public CatalogAndValuationChallengerTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"enightx_challenger_{Guid.NewGuid():N}.db");
        _db = new PosDatabase($"Data Source={_tempDbPath}");
        _db.Initialize();
        _catalog = new CatalogService(_db);
        _report = new ReportService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { }
        }
    }

    [Fact]
    public async Task Challenger_01_AddProduct_CustomMinStockThreshold_PersistsAcrossDatabaseReopen()
    {
        // 1. Add product with custom MinStockThreshold
        var product = new Product
        {
            ProductId = "prod_challenger_persist_01",
            Barcode = "CHALL_BC_001",
            Name = "Heavy Duty Timing Belt",
            NameSi = "ටයිමින් බෙල්ට්",
            NameTa = "டைமிங் பெல்ட்",
            UnitPrice = 8750.50m,
            CostBasis = 5200.25m,
            TaxRate = 0.18m,
            StockOnHand = 18.5m,
            MinStockThreshold = 14.75m,
            IsActive = true
        };

        await _catalog.AddProductAsync(product);

        // Verify in current session
        var loaded = await _catalog.GetProductByIdAsync("prod_challenger_persist_01");
        Assert.NotNull(loaded);
        Assert.Equal(14.75m, loaded.MinStockThreshold);
        Assert.Equal(18.5m, loaded.StockOnHand);

        // 2. Simulate complete restart: Open a completely new connection/service instance to the same SQLite file
        using (var db2 = new PosDatabase($"Data Source={_tempDbPath}"))
        {
            db2.Initialize(); // verify idempotent init
            var catalog2 = new CatalogService(db2);

            var reloaded = await catalog2.GetProductByIdAsync("prod_challenger_persist_01");
            Assert.NotNull(reloaded);
            Assert.Equal("Heavy Duty Timing Belt", reloaded.Name);
            Assert.Equal("CHALL_BC_001", reloaded.Barcode);
            Assert.Equal(8750.50m, reloaded.UnitPrice);
            Assert.Equal(5200.25m, reloaded.CostBasis);
            Assert.Equal(0.18m, reloaded.TaxRate);
            Assert.Equal(18.5m, reloaded.StockOnHand);
            Assert.Equal(14.75m, reloaded.MinStockThreshold);
            Assert.True(reloaded.IsActive);

            var reloadedByBarcode = await catalog2.GetProductByBarcodeAsync("CHALL_BC_001");
            Assert.NotNull(reloadedByBarcode);
            Assert.Equal("prod_challenger_persist_01", reloadedByBarcode.ProductId);
            Assert.Equal(14.75m, reloadedByBarcode.MinStockThreshold);

            var allActive = await catalog2.GetAllActiveProductsAsync();
            var itemInList = allActive.FirstOrDefault(p => p.ProductId == "prod_challenger_persist_01");
            Assert.NotNull(itemInList);
            Assert.Equal(14.75m, itemInList.MinStockThreshold);
        }
    }

    [Fact]
    public async Task Challenger_02_UpdateProduct_PriceBarcodeMinThreshold_PreservesStockOnHand_EvenUnderNegativeAndFractionalStock()
    {
        // 1. Create product with initial stock
        var product = new Product
        {
            ProductId = "prod_challenger_stock_protect_01",
            Barcode = "CHALL_BC_002_INIT",
            Name = "Synthetic Engine Oil 5W-30",
            UnitPrice = 4500.00m,
            CostBasis = 3100.00m,
            StockOnHand = 10.0m,
            MinStockThreshold = 5.0m,
            IsActive = true
        };
        await _catalog.AddProductAsync(product);

        // 2. Simulate manual inventory movement driving stock to negative fractional (-3.25)
        var user = new User
        {
            UserId = "usr_mgr_01",
            Username = "manager",
            DisplayName = "Store Manager",
            Role = Role.Manager,
            PasswordHash = "dummy_hash",
            PasswordSalt = "dummy_salt",
            IsActive = true
        };
        await _catalog.AdjustStockAsync(
            "prod_challenger_stock_protect_01",
            -13.25m,
            "Negative stock stress test",
            user,
            "TENANT_LK_01",
            "B01",
            "C01"
        );

        var currentStock = await _catalog.GetStockOnHandAsync("prod_challenger_stock_protect_01");
        Assert.Equal(-3.25m, currentStock);

        // Record stock movements count before metadata update
        int movementCountBefore = await GetMovementCountAsync("prod_challenger_stock_protect_01");

        // 3. Caller prepares product update with radically different metadata AND attempts to overwrite StockOnHand to 9999m
        product.Barcode = "CHALL_BC_002_NEW";
        product.Name = "Ultra Synthetic Engine Oil 5W-30 (Upgraded Formula)";
        product.UnitPrice = 5200.75m;
        product.CostBasis = 3600.50m;
        product.TaxRate = 0.18m;
        product.MinStockThreshold = 20.0m;
        product.StockOnHand = 9999.0m; // Adversarial attempt: this must be IGNORED by UpdateProductAsync

        await _catalog.UpdateProductAsync(product);

        // 4. Verify that metadata updated but stock_on_hand remained strictly -3.25m
        var reloaded = await _catalog.GetProductByIdAsync("prod_challenger_stock_protect_01");
        Assert.NotNull(reloaded);
        Assert.Equal("Ultra Synthetic Engine Oil 5W-30 (Upgraded Formula)", reloaded.Name);
        Assert.Equal("CHALL_BC_002_NEW", reloaded.Barcode);
        Assert.Equal(5200.75m, reloaded.UnitPrice);
        Assert.Equal(3600.50m, reloaded.CostBasis);
        Assert.Equal(20.0m, reloaded.MinStockThreshold);
        Assert.Equal(-3.25m, reloaded.StockOnHand); // CRITICAL: Local inventory preserved

        var stockDirect = await _catalog.GetStockOnHandAsync("prod_challenger_stock_protect_01");
        Assert.Equal(-3.25m, stockDirect);

        // Verify barcode lookup works with NEW barcode and returns negative stock
        var byBarcode = await _catalog.GetProductByBarcodeAsync("CHALL_BC_002_NEW");
        Assert.NotNull(byBarcode);
        Assert.Equal(-3.25m, byBarcode.StockOnHand);

        // Verify old barcode is no longer active
        var byOldBarcode = await _catalog.GetProductByBarcodeAsync("CHALL_BC_002_INIT");
        Assert.Null(byOldBarcode);

        // Verify no phantom stock movement records were created by metadata update
        int movementCountAfter = await GetMovementCountAsync("prod_challenger_stock_protect_01");
        Assert.Equal(movementCountBefore, movementCountAfter);
    }

    [Fact]
    public async Task Challenger_03_SoftDelete_SetsIsActiveZero_ExcludesFromCashier_VisibleInIncludeInactive()
    {
        var product = new Product
        {
            ProductId = "prod_challenger_delete_01",
            Barcode = "CHALL_BC_003",
            Name = "Incandescent Bulb 12V 5W",
            UnitPrice = 250.00m,
            CostBasis = 110.00m,
            StockOnHand = 12.0m,
            MinStockThreshold = 4.0m,
            IsActive = true
        };
        await _catalog.AddProductAsync(product);

        // Assert it exists before deletion
        Assert.NotNull(await _catalog.GetProductByIdAsync("prod_challenger_delete_01"));
        Assert.NotNull(await _catalog.GetProductByBarcodeAsync("CHALL_BC_003"));
        Assert.Contains(await _catalog.GetAllActiveProductsAsync(), p => p.ProductId == "prod_challenger_delete_01");

        // Act: Soft-delete
        await _catalog.DeleteProductAsync("prod_challenger_delete_01");

        // 1. Direct DB verification: row still exists, is_active is 0, stock_on_hand is untouched
        using (var conn = _db.CreateConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT is_active, stock_on_hand FROM products WHERE product_id = 'prod_challenger_delete_01';";
            using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0, reader.GetInt32(0));
            Assert.Equal(12.0m, reader.GetDecimal(1));
        }

        // 2. Cashier queries return null
        Assert.Null(await _catalog.GetProductByIdAsync("prod_challenger_delete_01"));
        Assert.Null(await _catalog.GetProductByBarcodeAsync("CHALL_BC_003"));

        // 3. GetAllActiveProductsAsync excludes the item
        var activeList = await _catalog.GetAllActiveProductsAsync();
        Assert.DoesNotContain(activeList, p => p.ProductId == "prod_challenger_delete_01");

        // 4. GetAllProductsAsync(includeInactive: false) excludes the item
        var nonInactiveList = await _catalog.GetAllProductsAsync(includeInactive: false);
        Assert.DoesNotContain(nonInactiveList, p => p.ProductId == "prod_challenger_delete_01");

        // 5. GetAllProductsAsync(includeInactive: true) includes the item with IsActive = false
        var allList = await _catalog.GetAllProductsAsync(includeInactive: true);
        var found = allList.FirstOrDefault(p => p.ProductId == "prod_challenger_delete_01");
        Assert.NotNull(found);
        Assert.False(found.IsActive);
        Assert.Equal(12.0m, found.StockOnHand);
        Assert.Equal(4.0m, found.MinStockThreshold);

        // 6. Delete on already soft-deleted product is idempotent and does not throw
        await _catalog.DeleteProductAsync("prod_challenger_delete_01");
    }

    [Fact]
    public async Task Challenger_04_Reactivate_SoftDeletedProduct_AndModifyMetadata_RestoresActiveLookup()
    {
        // 1. Create and then soft-delete
        var product = new Product
        {
            ProductId = "prod_challenger_reactivate_01",
            Barcode = "CHALL_BC_004",
            Name = "Seasonal Antifreeze Coolant",
            UnitPrice = 1800.00m,
            CostBasis = 1200.00m,
            StockOnHand = 25.0m,
            MinStockThreshold = 10.0m,
            IsActive = true
        };
        await _catalog.AddProductAsync(product);
        await _catalog.DeleteProductAsync("prod_challenger_reactivate_01");

        Assert.Null(await _catalog.GetProductByIdAsync("prod_challenger_reactivate_01"));

        // 2. Retrieve via manager view (includeInactive: true)
        var allProducts = await _catalog.GetAllProductsAsync(includeInactive: true);
        var inactiveProd = allProducts.First(p => p.ProductId == "prod_challenger_reactivate_01");
        Assert.False(inactiveProd.IsActive);

        // 3. Re-activate the product while updating unit price, barcode, and min threshold
        inactiveProd.IsActive = true;
        inactiveProd.Name = "Seasonal Antifreeze Coolant (Re-introduced)";
        inactiveProd.UnitPrice = 2100.00m;
        inactiveProd.Barcode = "CHALL_BC_004_REACTIVATED";
        inactiveProd.MinStockThreshold = 15.0m;
        inactiveProd.StockOnHand = 999m; // Attempted modification must be ignored

        await _catalog.UpdateProductAsync(inactiveProd);

        // 4. Assert product is fully restored to active operations
        var reactivatedById = await _catalog.GetProductByIdAsync("prod_challenger_reactivate_01");
        Assert.NotNull(reactivatedById);
        Assert.True(reactivatedById.IsActive);
        Assert.Equal("Seasonal Antifreeze Coolant (Re-introduced)", reactivatedById.Name);
        Assert.Equal("CHALL_BC_004_REACTIVATED", reactivatedById.Barcode);
        Assert.Equal(2100.00m, reactivatedById.UnitPrice);
        Assert.Equal(15.0m, reactivatedById.MinStockThreshold);
        Assert.Equal(25.0m, reactivatedById.StockOnHand); // Stock preserved!

        var reactivatedByBarcode = await _catalog.GetProductByBarcodeAsync("CHALL_BC_004_REACTIVATED");
        Assert.NotNull(reactivatedByBarcode);
        Assert.Equal("prod_challenger_reactivate_01", reactivatedByBarcode.ProductId);

        var activeList = await _catalog.GetAllActiveProductsAsync();
        Assert.Contains(activeList, p => p.ProductId == "prod_challenger_reactivate_01" && p.IsActive);

        // 5. Update metadata while KEEPING product inactive
        await _catalog.DeleteProductAsync("prod_challenger_reactivate_01");
        reactivatedById = await _catalog.GetProductByIdAsync("prod_challenger_reactivate_01");
        Assert.Null(reactivatedById); // Confirmed inactive

        var inactiveList = await _catalog.GetAllProductsAsync(includeInactive: true);
        var target = inactiveList.First(p => p.ProductId == "prod_challenger_reactivate_01");
        target.UnitPrice = 2500.00m;
        target.IsActive = false; // keep inactive

        await _catalog.UpdateProductAsync(target);

        var updatedInactive = (await _catalog.GetAllProductsAsync(includeInactive: true))
            .First(p => p.ProductId == "prod_challenger_reactivate_01");
        Assert.False(updatedInactive.IsActive);
        Assert.Equal(2500.00m, updatedInactive.UnitPrice);
        Assert.Equal(25.0m, updatedInactive.StockOnHand);
    }

    [Fact]
    public async Task Challenger_05_InventoryValuationReport_ParityWithSummaryReport_AndExcludesSoftDeletedItems()
    {
        // 1. Seed diverse catalog items
        // Item 1: Normal positive stock
        await _catalog.AddProductAsync(new Product
        {
            ProductId = "val_01",
            Barcode = "VBC01",
            Name = "Air Filter",
            UnitPrice = 2450.50m,
            CostBasis = 1600.25m,
            StockOnHand = 12.0m,
            IsActive = true
        });

        // Item 2: Zero stock (should count as low stock: 0 <= 5)
        await _catalog.AddProductAsync(new Product
        {
            ProductId = "val_02",
            Barcode = "VBC02",
            Name = "Cabin Filter",
            UnitPrice = 1800.00m,
            CostBasis = 1100.00m,
            StockOnHand = 0.0m,
            IsActive = true
        });

        // Item 3: Low stock at threshold boundary (exactly 5 <= 5)
        await _catalog.AddProductAsync(new Product
        {
            ProductId = "val_03",
            Barcode = "VBC03",
            Name = "Wiper Fluid 1L",
            UnitPrice = 450.00m,
            CostBasis = 250.00m,
            StockOnHand = 5.0m,
            IsActive = true
        });

        // Item 4: Normal stock just above low stock boundary (6 > 5)
        await _catalog.AddProductAsync(new Product
        {
            ProductId = "val_04",
            Barcode = "VBC04",
            Name = "Brake Fluid DOT4",
            UnitPrice = 950.00m,
            CostBasis = 600.00m,
            StockOnHand = 6.0m,
            IsActive = true
        });

        // Item 5: Negative stock (oversold: -2.5 units, should count as negative stock, NOT low stock)
        await _catalog.AddProductAsync(new Product
        {
            ProductId = "val_05",
            Barcode = "VBC05",
            Name = "Wheel Nut M12",
            UnitPrice = 300.00m,
            CostBasis = 150.00m,
            StockOnHand = -2.5m,
            IsActive = true
        });

        // Item 6: Inactive / Soft-deleted item with huge stock and valuation
        await _catalog.AddProductAsync(new Product
        {
            ProductId = "val_06_deleted",
            Barcode = "VBC06",
            Name = "Discontinued Alloy Rim",
            UnitPrice = 45000.00m,
            CostBasis = 30000.00m,
            StockOnHand = 50.0m,
            IsActive = true
        });
        await _catalog.DeleteProductAsync("val_06_deleted"); // Soft delete it!

        // 2. Execute both report generation calls
        var summaryReport = await _report.GenerateInventorySummaryReportAsync();
        var valuationReport = await _report.GenerateInventoryValuationReportAsync();

        // 3. Assert exact parity between the two report methods
        Assert.Equal(summaryReport.TotalProducts, valuationReport.TotalProducts);
        Assert.Equal(summaryReport.LowStockProducts, valuationReport.LowStockProducts);
        Assert.Equal(summaryReport.NegativeStockProducts, valuationReport.NegativeStockProducts);
        Assert.Equal(summaryReport.TotalValuationAtCost, valuationReport.TotalValuationAtCost);
        Assert.Equal(summaryReport.TotalValuationAtRetail, valuationReport.TotalValuationAtRetail);
        Assert.Equal(summaryReport.Items.Count, valuationReport.Items.Count);

        // 4. Assert soft-deleted item is completely excluded from reports
        Assert.Equal(5, valuationReport.TotalProducts); // Only the 5 active items
        Assert.DoesNotContain(valuationReport.Items, i => i.ProductId == "val_06_deleted");

        // 5. Assert stock health metrics
        // Low stock (0 <= soh <= 5): val_02 (0), val_03 (5) = 2 items
        Assert.Equal(2, valuationReport.LowStockProducts);
        // Negative stock (soh < 0): val_05 (-2.5) = 1 item
        Assert.Equal(1, valuationReport.NegativeStockProducts);

        // 6. Assert exact mathematical valuation calculations:
        // Item 1: 12.0 * 1600.25 = 19,203.00 ; 12.0 * 2450.50 = 29,406.00
        // Item 2: 0.0 * 1100.00 = 0.00 ; 0.0 * 1800.00 = 0.00
        // Item 3: 5.0 * 250.00 = 1,250.00 ; 5.0 * 450.00 = 2,250.00
        // Item 4: 6.0 * 600.00 = 3,600.00 ; 6.0 * 950.00 = 5,700.00
        // Item 5: -2.5 * 150.00 = -375.00 ; -2.5 * 300.00 = -750.00
        // Total Cost = 19,203 + 0 + 1,250 + 3,600 - 375 = 23,678.00 LKR
        // Total Retail = 29,406 + 0 + 2,250 + 5,700 - 750 = 36,606.00 LKR
        decimal expectedCost = 23678.00m;
        decimal expectedRetail = 36606.00m;

        Assert.Equal(expectedCost, valuationReport.TotalValuationAtCost);
        Assert.Equal(expectedRetail, valuationReport.TotalValuationAtRetail);

        // Verify item-by-item alignment
        for (int i = 0; i < summaryReport.Items.Count; i++)
        {
            var sItem = summaryReport.Items[i];
            var vItem = valuationReport.Items[i];
            Assert.Equal(sItem.ProductId, vItem.ProductId);
            Assert.Equal(sItem.Barcode, vItem.Barcode);
            Assert.Equal(sItem.Name, vItem.Name);
            Assert.Equal(sItem.StockOnHand, vItem.StockOnHand);
            Assert.Equal(sItem.CostBasis, vItem.CostBasis);
            Assert.Equal(sItem.UnitPrice, vItem.UnitPrice);
            Assert.Equal(sItem.ValuationAtCost, vItem.ValuationAtCost);
            Assert.Equal(sItem.ValuationAtRetail, vItem.ValuationAtRetail);
        }
    }

    [Fact]
    public async Task Challenger_06_UpdateProduct_DuplicateBarcodeCollision_ThrowsSqliteException()
    {
        var p1 = new Product { ProductId = "coll_01", Barcode = "COLL_BC_A", Name = "Part A", UnitPrice = 100m, CostBasis = 50m };
        var p2 = new Product { ProductId = "coll_02", Barcode = "COLL_BC_B", Name = "Part B", UnitPrice = 200m, CostBasis = 100m };

        await _catalog.AddProductAsync(p1);
        await _catalog.AddProductAsync(p2);

        // Updating p2 to have p1's barcode must fail UNIQUE constraint
        p2.Barcode = "COLL_BC_A";
        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await _catalog.UpdateProductAsync(p2);
        });

        // Updating p1 while keeping its OWN barcode must succeed
        p1.Name = "Part A (Updated Name Only)";
        var ex = await Record.ExceptionAsync(async () => await _catalog.UpdateProductAsync(p1));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-100.0)]
    public async Task Challenger_07_Validation_NegativeMinStockThreshold_ThrowsArgumentException(decimal invalidMinStock)
    {
        var p = new Product
        {
            ProductId = "prod_neg_min",
            Barcode = "NEG_BC",
            Name = "Invalid Item",
            UnitPrice = 100m,
            CostBasis = 50m,
            MinStockThreshold = invalidMinStock
        };

        // Add
        await Assert.ThrowsAsync<ArgumentException>(async () => await _catalog.AddProductAsync(p));

        // Update
        p.MinStockThreshold = 10m;
        await _catalog.AddProductAsync(p);

        p.MinStockThreshold = invalidMinStock;
        await Assert.ThrowsAsync<ArgumentException>(async () => await _catalog.UpdateProductAsync(p));
    }

    [Fact]
    public async Task Challenger_08_Stress_FiftySequentialMetadataUpdates_StockRemainsInvariant()
    {
        var product = new Product
        {
            ProductId = "prod_stress_50",
            Barcode = "STRESS_BC_INITIAL",
            Name = "Stress Iteration Item",
            UnitPrice = 1000m,
            CostBasis = 700m,
            StockOnHand = 77.77m,
            MinStockThreshold = 10m,
            IsActive = true
        };
        await _catalog.AddProductAsync(product);

        for (int i = 1; i <= 50; i++)
        {
            product.Name = $"Stress Iteration Item #{i}";
            product.Barcode = $"STRESS_BC_{i:D3}";
            product.UnitPrice = 1000m + i;
            product.CostBasis = 700m + (i * 0.5m);
            product.MinStockThreshold = 10m + (i * 0.1m);
            product.StockOnHand = 9999m; // Attempted corruption

            await _catalog.UpdateProductAsync(product);

            var check = await _catalog.GetStockOnHandAsync("prod_stress_50");
            Assert.Equal(77.77m, check);
        }

        var final = await _catalog.GetProductByIdAsync("prod_stress_50");
        Assert.NotNull(final);
        Assert.Equal("Stress Iteration Item #50", final.Name);
        Assert.Equal("STRESS_BC_050", final.Barcode);
        Assert.Equal(1050m, final.UnitPrice);
        Assert.Equal(725m, final.CostBasis);
        Assert.Equal(15m, final.MinStockThreshold);
        Assert.Equal(77.77m, final.StockOnHand);
    }

    private async Task<int> GetMovementCountAsync(string productId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM stock_movements WHERE product_id = $id;";
        cmd.Parameters.AddWithValue("$id", productId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
