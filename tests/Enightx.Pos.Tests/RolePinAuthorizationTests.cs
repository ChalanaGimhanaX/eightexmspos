using System.Text.Json;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class RolePinAuthorizationTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _saleService;

    public RolePinAuthorizationTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _catalog = new CatalogService(_db);
        _shift = new ShiftService(_db);
        _saleService = new SaleService(_db, _catalog);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task VerifyPin_ValidManagerPin_ReturnsUserAndLogsAuditEvent()
    {
        var manager = await _auth.CreateUserAsync("manager_p", "Perera Manager", "SecureP@ss1", Role.Manager, "4321");

        var authorized = await _auth.VerifyPinAsync(
            pin: "4321",
            minimumRole: Role.Manager,
            tenantId: "TENANT_LK_01",
            branchId: "B01",
            counterId: "C01",
            actionDescription: "Excessive Discount Approval"
        );

        Assert.NotNull(authorized);
        Assert.Equal(manager.UserId, authorized.UserId);
        Assert.Equal(Role.Manager, authorized.Role);

        // Verify audit event was logged in SQLite
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT action, actor_id, details_json FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED';";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("MANAGER_PIN_VERIFIED", reader.GetString(0));
        Assert.Equal(manager.UserId, reader.GetString(1));

        var details = reader.GetString(2);
        Assert.Contains("Excessive Discount Approval", details);
        Assert.Contains(manager.UserId, details);
    }

    [Fact]
    public async Task VerifyPin_InvalidPin_ThrowsUnauthorizedActionExceptionAndLogsFailure()
    {
        await _auth.CreateUserAsync("manager_pin_test", "Test Manager", "P@ss1234", Role.Manager, "9999");

        var ex = await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _auth.VerifyPinAsync(
                pin: "0000", // Wrong PIN
                minimumRole: Role.Manager,
                tenantId: "TENANT_LK_01",
                branchId: "B01",
                counterId: "C01",
                actionDescription: "Manual Price Override"
            )
        );
        Assert.Contains("Invalid PIN", ex.Message);

        // Verify failure audit event was logged
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT action, details_json FROM audit_events WHERE action = 'MANAGER_PIN_FAILED';";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("MANAGER_PIN_FAILED", reader.GetString(0));
        var details = reader.GetString(1);
        Assert.Contains("Manual Price Override", details);
    }

    [Fact]
    public async Task VerifyPin_CashierPin_RejectedForManagerAction()
    {
        // Cashier has valid PIN "7777"
        await _auth.CreateUserAsync("cashier_pin_test", "Test Cashier", "P@ss1234", Role.Cashier, "7777");

        // Attempting to verify cashier PIN for manager-level sensitive action must fail
        var ex = await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _auth.VerifyPinAsync(
                pin: "7777",
                minimumRole: Role.Manager, // Minimum role is Manager
                tenantId: "TENANT_LK_01",
                branchId: "B01",
                counterId: "C01",
                actionDescription: "Stock Adjustment"
            )
        );
        Assert.Contains("Invalid PIN or user lacks required manager/owner authorization", ex.Message);
    }

    [Fact]
    public async Task SetUserPin_UpdatesUserPin_CanVerifyWithNewPin()
    {
        var manager = await _auth.CreateUserAsync("manager_update_pin", "Old Pin Manager", "P@ss1234", Role.Manager, "1111");

        // Old PIN works
        var auth1 = await _auth.VerifyPinAsync("1111", Role.Manager);
        Assert.NotNull(auth1);

        // Update PIN to "8888"
        await _auth.SetUserPinAsync(manager.UserId, "8888");

        // Old PIN now fails
        await Assert.ThrowsAsync<UnauthorizedActionException>(() => _auth.VerifyPinAsync("1111", Role.Manager));

        // New PIN succeeds
        var auth2 = await _auth.VerifyPinAsync("8888", Role.Manager);
        Assert.NotNull(auth2);
        Assert.Equal(manager.UserId, auth2.UserId);
    }

    [Fact]
    public async Task CashierManualStockAdjustment_WithManagerAuthorizer_SucceedsAndAuditsAuthorizer()
    {
        var cashier = await _auth.CreateUserAsync("cashier_stock", "Cashier Stock", "P@ss123", Role.Cashier, "1234");
        var manager = await _auth.CreateUserAsync("mgr_stock", "Manager Authorizer", "MgrPass1", Role.Manager, "5678");

        var product = new Product
        {
            ProductId = "prod_chain_lube",
            Barcode = "7778889990001",
            Name = "Chain Lube Spray 500ml",
            UnitPrice = 2400.00m,
            CostBasis = 1700.00m,
            TaxRate = 0.0m,
            StockOnHand = 20.0m
        };
        await _catalog.AddProductAsync(product);

        // Cashier attempts stock adjustment WITHOUT manager authorization -> Rejected (A09)
        await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _catalog.AdjustStockAsync(
                product.ProductId,
                quantityChange: -2.0m,
                reason: "Damaged container during handling",
                actor: cashier,
                tenantId: "TENANT_LK_01",
                branchId: "B01",
                counterId: "C01",
                authorizer: null
            )
        );

        // Stock unchanged
        Assert.Equal(20.0m, await _catalog.GetStockOnHandAsync(product.ProductId));

        // Cashier performs stock adjustment WITH Manager authorization -> Allowed!
        await _catalog.AdjustStockAsync(
            product.ProductId,
            quantityChange: -2.0m,
            reason: "Damaged container during handling",
            actor: cashier,
            tenantId: "TENANT_LK_01",
            branchId: "B01",
            counterId: "C01",
            authorizer: manager
        );

        // Stock updated
        Assert.Equal(18.0m, await _catalog.GetStockOnHandAsync(product.ProductId));

        // Verify audit event records both Cashier actor and Manager authorizer
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT actor_id, details_json FROM audit_events WHERE action = 'MANUAL_STOCK_ADJUSTMENT' ORDER BY occurred_at_utc DESC LIMIT 1;";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(cashier.UserId, reader.GetString(0));

        var details = reader.GetString(1);
        Assert.Contains(manager.UserId, details);
        Assert.Contains(manager.DisplayName, details);
    }

    [Fact]
    public async Task SensitiveAction_AuthorizingUserId_RecordedInAuditEvent()
    {
        var cashier = await _auth.CreateUserAsync("cashier_override", "Cashier Override", "Pass#123", Role.Cashier);
        var manager = await _auth.CreateUserAsync("mgr_approver", "Approver Manager", "Pass#123", Role.Manager, "9876");

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 2000.00m, "TENANT_LK_01");

        var product = new Product
        {
            ProductId = "prod_wiper_blade",
            Barcode = "2223334445556",
            Name = "Bosch Wiper Blade 22 inch",
            UnitPrice = 3200.00m,
            CostBasis = 2200.00m,
            TaxRate = 0.0m,
            StockOnHand = 15.0m
        };
        await _catalog.AddProductAsync(product);

        // Sale with price override and AuthorizingUserId
        var cmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new(ProductId: product.ProductId, Quantity: 1.0m, PriceOverride: 2800.00m, OverrideReason: "Customer matched competitor price", AuthorizingUserId: manager.UserId)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType: TenderType.CASH, AmountTendered: 3000.00m)
            },
            AuthorizingUserId: manager.UserId
        );

        var sale = await _saleService.CommitSaleAsync(cmd);
        Assert.NotNull(sale);

        // Check audit event details contain AuthorizerId
        using var conn = _db.CreateConnection();
        using var auditCmd = conn.CreateCommand();
        auditCmd.CommandText = "SELECT actor_id, details_json FROM audit_events WHERE action = 'COMMIT_SALE' ORDER BY occurred_at_utc DESC LIMIT 1;";
        using var reader = await auditCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(cashier.UserId, reader.GetString(0));

        var details = reader.GetString(1);
        Assert.Contains(manager.UserId, details);
    }

    [Fact]
    public async Task VerifyPin_OwnerPin_SucceedsForManagerOrOwnerLevelAction()
    {
        var owner = await _auth.CreateUserAsync("shop_owner", "Shop Owner", "OwnerPass#1", Role.Owner, "0077");

        var authorized = await _auth.VerifyPinAsync(
            pin: "0077",
            minimumRole: Role.Manager,
            actionDescription: "Owner Override"
        );

        Assert.NotNull(authorized);
        Assert.Equal(owner.UserId, authorized.UserId);
        Assert.Equal(Role.Owner, authorized.Role);
    }

    [Fact]
    public async Task VerifyPin_FallbackToPasswordWhenPinNotExplicitlySet()
    {
        // User created without explicit PIN (PIN is null), only password "ManagerSec#9"
        var manager = await _auth.CreateUserAsync("mgr_nopin", "Manager Without Pin", "ManagerSec#9", Role.Manager);

        // Entering manager's password as the authorization PIN
        var authorized = await _auth.VerifyPinAsync(
            pin: "ManagerSec#9",
            minimumRole: Role.Manager,
            actionDescription: "Password fallback authorization"
        );

        Assert.NotNull(authorized);
        Assert.Equal(manager.UserId, authorized.UserId);
    }

    [Fact]
    public async Task RefundSale_WithAuthorizerId_RecordedInAuditEvent()
    {
        var cashier = await _auth.CreateUserAsync("cashier_ref", "Cashier Refund", "Pass#123", Role.Cashier);
        var manager = await _auth.CreateUserAsync("mgr_ref_auth", "Manager Approver", "Pass#123", Role.Manager, "3344");

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var product = new Product
        {
            ProductId = "prod_bulb_ref",
            Barcode = "7776665554443",
            Name = "LED Bulb 12V 10W",
            UnitPrice = 1200.00m,
            CostBasis = 800.00m,
            TaxRate = 0.0m,
            StockOnHand = 20.0m
        };
        await _catalog.AddProductAsync(product);

        // 1. Initial sale
        var saleCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 2.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CASH, 2400.00m) }
        );
        var sale = await _saleService.CommitSaleAsync(saleCmd);

        // 2. Refund with AuthorizingUserId
        var refundCmd = new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest>
            {
                new(LineId: sale.Lines[0].LineId, QuantityToRefund: 1.0m)
            },
            Reason: "Customer bought wrong wattage",
            ReturnStockToInventory: true,
            AuthorizingUserId: manager.UserId
        );

        var refundSale = await _saleService.RefundSaleAsync(refundCmd);
        Assert.NotNull(refundSale);
        Assert.Equal(1200.00m, refundSale.GrandTotal);

        // Verify audit event has AuthorizerId
        using var conn = _db.CreateConnection();
        using var auditCmd = conn.CreateCommand();
        auditCmd.CommandText = "SELECT actor_id, details_json FROM audit_events WHERE action = 'REFUND_SALE' ORDER BY occurred_at_utc DESC LIMIT 1;";
        using var reader = await auditCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(cashier.UserId, reader.GetString(0));

        var details = reader.GetString(1);
        Assert.Contains(manager.UserId, details);
    }

    [Fact]
    public async Task AdjustStock_InactiveManagerAuthorizer_ThrowsUnauthorizedActionException()
    {
        var cashier = await _auth.CreateUserAsync("cashier_adj_inact", "Cashier Adj", "pass123", Role.Cashier);
        var manager = await _auth.CreateUserAsync("mgr_adj_inact", "Manager Deactivated", "pass123", Role.Manager, "1122");

        // Deactivate the manager
        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE users SET is_active = 0 WHERE user_id = $uid;";
            cmd.Parameters.AddWithValue("$uid", manager.UserId);
            await cmd.ExecuteNonQueryAsync();
        }
        manager.IsActive = false;

        var product = new Product
        {
            ProductId = "prod_adj_test",
            Barcode = "9988112233445",
            Name = "Adjustment Test Item",
            UnitPrice = 1000m,
            CostBasis = 700m,
            TaxRate = 0m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(product);

        // Inactive manager authorizer must be rejected!
        var ex = await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _catalog.AdjustStockAsync(
                productId: product.ProductId,
                quantityChange: -1m,
                reason: "Spoiled inventory",
                actor: cashier,
                tenantId: "TENANT_LK_01",
                branchId: "B01",
                counterId: "C01",
                authorizer: manager
            )
        );
        Assert.Contains("inactive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PosDatabase_SalesMigration_AddsCustomerIdColumn()
    {
        var tempDbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test_migration_{Guid.NewGuid():N}.db");
        try
        {
            // Create legacy SQLite database without customer_id on sales table
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tempDbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE sales (
                        sale_id TEXT PRIMARY KEY,
                        receipt_number TEXT UNIQUE NOT NULL,
                        shift_id TEXT NOT NULL,
                        tenant_id TEXT NOT NULL,
                        branch_id TEXT NOT NULL,
                        counter_id TEXT NOT NULL,
                        cashier_id TEXT NOT NULL,
                        parent_sale_id TEXT,
                        subtotal NUMERIC NOT NULL,
                        discount_total NUMERIC NOT NULL,
                        tax_total NUMERIC NOT NULL,
                        grand_total NUMERIC NOT NULL,
                        status INTEGER NOT NULL DEFAULT 1,
                        reprint_count INTEGER NOT NULL DEFAULT 0,
                        created_at_utc TEXT NOT NULL
                    );
                ";
                cmd.ExecuteNonQuery();
            }

            // Now open via PosDatabase.CreateFile which runs Initialize() migrations
            using var posDb = PosDatabase.CreateFile(tempDbPath);

            // Verify customer_id column was added to sales
            using (var conn = posDb.CreateConnection())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(sales);";
                using var reader = cmd.ExecuteReader();
                var columnNames = new List<string>();
                while (reader.Read())
                {
                    columnNames.Add(reader.GetString(1));
                }
                Assert.Contains("customer_id", columnNames);
            }
        }
        finally
        {
            if (System.IO.File.Exists(tempDbPath))
            {
                System.IO.File.Delete(tempDbPath);
            }
        }
    }
}
