using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface ICatalogService
{
    Task AddProductAsync(Product product);
    Task<Product?> GetProductByBarcodeAsync(string barcode);
    Task<Product?> GetProductByIdAsync(string productId);
    Task<decimal> GetStockOnHandAsync(string productId);
    Task AdjustStockAsync(string productId, decimal quantityChange, string reason, User actor, string tenantId, string branchId, string counterId);
}

public class CatalogService : ICatalogService
{
    private readonly PosDatabase _db;

    public CatalogService(PosDatabase db)
    {
        _db = db;
    }

    public async Task AddProductAsync(Product product)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO products (product_id, barcode, name, name_si, name_ta, unit_price, cost_basis, tax_rate, stock_on_hand, is_active)
            VALUES ($id, $bcode, $name, $nsi, $nta, $uprice, $cbasis, $trate, $soh, $active);
        ";
        cmd.Parameters.AddWithValue("$id", product.ProductId);
        cmd.Parameters.AddWithValue("$bcode", product.Barcode);
        cmd.Parameters.AddWithValue("$name", product.Name);
        cmd.Parameters.AddWithValue("$nsi", (object?)product.NameSi ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nta", (object?)product.NameTa ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uprice", product.UnitPrice);
        cmd.Parameters.AddWithValue("$cbasis", product.CostBasis);
        cmd.Parameters.AddWithValue("$trate", product.TaxRate);
        cmd.Parameters.AddWithValue("$soh", product.StockOnHand);
        cmd.Parameters.AddWithValue("$active", product.IsActive ? 1 : 0);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Product?> GetProductByBarcodeAsync(string barcode)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT product_id, barcode, name, name_si, name_ta, unit_price, cost_basis, tax_rate, stock_on_hand, is_active
            FROM products WHERE barcode = $bcode AND is_active = 1;
        ";
        cmd.Parameters.AddWithValue("$bcode", barcode.Trim());

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return MapProduct(reader);
    }

    public async Task<Product?> GetProductByIdAsync(string productId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT product_id, barcode, name, name_si, name_ta, unit_price, cost_basis, tax_rate, stock_on_hand, is_active
            FROM products WHERE product_id = $id AND is_active = 1;
        ";
        cmd.Parameters.AddWithValue("$id", productId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return MapProduct(reader);
    }

    public async Task<decimal> GetStockOnHandAsync(string productId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT stock_on_hand FROM products WHERE product_id = $id;";
        cmd.Parameters.AddWithValue("$id", productId);
        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value ? Convert.ToDecimal(result) : 0m;
    }

    public async Task AdjustStockAsync(
        string productId,
        decimal quantityChange,
        string reason,
        User actor,
        string tenantId,
        string branchId,
        string counterId)
    {
        // A09: Manual stock adjustments allowed ONLY for Owner and Manager.
        if (actor.Role != Role.Owner && actor.Role != Role.Manager)
        {
            throw new UnauthorizedActionException(
                $"Cashier role '{actor.Username}' is not authorized to adjust manual stock. Owner or Manager authorization required."
            );
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason must be provided for manual stock adjustments.", nameof(reason));
        }

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // 1. Update product stock on hand
        using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = @"
            UPDATE products SET stock_on_hand = stock_on_hand + $qty
            WHERE product_id = $id;
        ";
        updateCmd.Parameters.AddWithValue("$qty", quantityChange);
        updateCmd.Parameters.AddWithValue("$id", productId);
        await updateCmd.ExecuteNonQueryAsync();

        // 2. Insert stock movement record
        using var movCmd = conn.CreateCommand();
        movCmd.Transaction = tx;
        movCmd.CommandText = @"
            INSERT INTO stock_movements (movement_id, product_id, movement_type, quantity_change, reference_id, occurred_at_utc)
            VALUES ($mid, $pid, 'ADJUSTMENT', $qty, $ref, $occurred);
        ";
        var adjId = Guid.NewGuid().ToString();
        movCmd.Parameters.AddWithValue("$mid", adjId);
        movCmd.Parameters.AddWithValue("$pid", productId);
        movCmd.Parameters.AddWithValue("$qty", quantityChange);
        movCmd.Parameters.AddWithValue("$ref", $"ADJ_{reason.Replace(' ', '_')}");
        movCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await movCmd.ExecuteNonQueryAsync();

        // 3. Log audit event
        using var auditCmd = conn.CreateCommand();
        auditCmd.Transaction = tx;
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, 'MANUAL_STOCK_ADJUSTMENT', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", tenantId);
        auditCmd.Parameters.AddWithValue("$bid", branchId);
        auditCmd.Parameters.AddWithValue("$cid", counterId);
        auditCmd.Parameters.AddWithValue("$actor", actor.UserId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            ProductId = productId,
            QuantityChange = quantityChange,
            Reason = reason,
            AdjustmentId = adjId
        }));
        auditCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();

        tx.Commit();
    }

    private static Product MapProduct(SqliteDataReader reader)
    {
        return new Product
        {
            ProductId = reader.GetString(0),
            Barcode = reader.GetString(1),
            Name = reader.GetString(2),
            NameSi = reader.IsDBNull(3) ? null : reader.GetString(3),
            NameTa = reader.IsDBNull(4) ? null : reader.GetString(4),
            UnitPrice = reader.GetDecimal(5),
            CostBasis = reader.GetDecimal(6),
            TaxRate = reader.GetDecimal(7),
            StockOnHand = reader.GetDecimal(8),
            IsActive = reader.GetInt32(9) == 1
        };
    }
}
