using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public record HoldCartCommand(
    string TenantId,
    string BranchId,
    string CounterId,
    string CashierId,
    string? CustomerReference,
    List<HeldCartItem> Items
);

public interface IHeldCartService
{
    Task<HeldCart> HoldCartAsync(HoldCartCommand command);
    Task<List<HeldCart>> GetHeldCartsAsync(string branchId, string counterId);
    Task<HeldCart?> GetHeldCartByIdAsync(Guid heldCartId);
    Task<HeldCart> RecallCartAsync(Guid heldCartId, string actorId);
    Task DeleteHeldCartAsync(Guid heldCartId, string actorId, string? reason = null);
}

public class HeldCartService : IHeldCartService
{
    private readonly PosDatabase _db;

    public HeldCartService(PosDatabase db)
    {
        _db = db;
    }

    public async Task<HeldCart> HoldCartAsync(HoldCartCommand command)
    {
        if (command.Items == null || command.Items.Count == 0)
        {
            throw new PosException("Cannot hold an empty cart.");
        }

        var subtotal = MoneyCalculator.Round(command.Items.Sum(i => i.Quantity * i.UnitPrice));
        var discountTotal = MoneyCalculator.Round(command.Items.Sum(i =>
        {
            var calc = MoneyCalculator.CalculateLine(i.Quantity, i.UnitPrice, i.DiscountRate, i.DiscountFixed, i.TaxRate);
            return calc.DiscountAmount;
        }));
        var taxTotal = MoneyCalculator.Round(command.Items.Sum(i =>
        {
            var calc = MoneyCalculator.CalculateLine(i.Quantity, i.UnitPrice, i.DiscountRate, i.DiscountFixed, i.TaxRate);
            return calc.TaxAmount;
        }));
        var grandTotal = MoneyCalculator.Round(command.Items.Sum(i =>
        {
            var calc = MoneyCalculator.CalculateLine(i.Quantity, i.UnitPrice, i.DiscountRate, i.DiscountFixed, i.TaxRate);
            return calc.LineTotal;
        }));

        var heldCart = new HeldCart
        {
            HeldCartId = Guid.NewGuid(),
            TenantId = command.TenantId,
            BranchId = command.BranchId,
            CounterId = command.CounterId,
            CashierId = command.CashierId,
            CustomerReference = string.IsNullOrWhiteSpace(command.CustomerReference) ? null : command.CustomerReference.Trim(),
            Subtotal = subtotal,
            DiscountTotal = discountTotal,
            TaxTotal = taxTotal,
            GrandTotal = grandTotal,
            HeldAtUtc = DateTime.UtcNow,
            Items = command.Items
        };

        var cartJson = JsonSerializer.Serialize(heldCart.Items);

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // 1. Insert into held_carts
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO held_carts (
                held_cart_id, tenant_id, branch_id, counter_id, cashier_id, customer_reference,
                subtotal, discount_total, tax_total, grand_total, held_at_utc, cart_json
            ) VALUES (
                $id, $tid, $bid, $cid, $uid, $ref, $sub, $disc, $tax, $grand, $held, $json
            );
        ";
        cmd.Parameters.AddWithValue("$id", heldCart.HeldCartId.ToString());
        cmd.Parameters.AddWithValue("$tid", heldCart.TenantId);
        cmd.Parameters.AddWithValue("$bid", heldCart.BranchId);
        cmd.Parameters.AddWithValue("$cid", heldCart.CounterId);
        cmd.Parameters.AddWithValue("$uid", heldCart.CashierId);
        cmd.Parameters.AddWithValue("$ref", (object?)heldCart.CustomerReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sub", heldCart.Subtotal);
        cmd.Parameters.AddWithValue("$disc", heldCart.DiscountTotal);
        cmd.Parameters.AddWithValue("$tax", heldCart.TaxTotal);
        cmd.Parameters.AddWithValue("$grand", heldCart.GrandTotal);
        cmd.Parameters.AddWithValue("$held", heldCart.HeldAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$json", cartJson);
        await cmd.ExecuteNonQueryAsync();

        // 2. Audit event
        using var auditCmd = conn.CreateCommand();
        auditCmd.Transaction = tx;
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, 'HOLD_CART', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", command.TenantId);
        auditCmd.Parameters.AddWithValue("$bid", command.BranchId);
        auditCmd.Parameters.AddWithValue("$cid", command.CounterId);
        auditCmd.Parameters.AddWithValue("$actor", command.CashierId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            HeldCartId = heldCart.HeldCartId,
            CustomerReference = heldCart.CustomerReference,
            ItemCount = heldCart.Items.Count,
            GrandTotal = heldCart.GrandTotal
        }));
        auditCmd.Parameters.AddWithValue("$occurred", heldCart.HeldAtUtc.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();

        tx.Commit();
        return heldCart;
    }

    public async Task<List<HeldCart>> GetHeldCartsAsync(string branchId, string counterId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT held_cart_id, branch_id, counter_id, cashier_id, customer_reference,
                   subtotal, discount_total, tax_total, grand_total, held_at_utc, cart_json, tenant_id
            FROM held_carts
            WHERE branch_id = $bid AND counter_id = $cid
            ORDER BY held_at_utc DESC;
        ";
        cmd.Parameters.AddWithValue("$bid", branchId);
        cmd.Parameters.AddWithValue("$cid", counterId);

        var list = new List<HeldCart>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(MapHeldCart(reader));
        }
        return list;
    }

    public async Task<HeldCart?> GetHeldCartByIdAsync(Guid heldCartId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT held_cart_id, branch_id, counter_id, cashier_id, customer_reference,
                   subtotal, discount_total, tax_total, grand_total, held_at_utc, cart_json, tenant_id
            FROM held_carts
            WHERE held_cart_id = $id;
        ";
        cmd.Parameters.AddWithValue("$id", heldCartId.ToString());

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return MapHeldCart(reader);
    }

    public async Task<HeldCart> RecallCartAsync(Guid heldCartId, string actorId)
    {
        var heldCart = await GetHeldCartByIdAsync(heldCartId);
        if (heldCart == null)
        {
            throw new PosException($"Held cart '{heldCartId}' was not found.");
        }

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Remove from held_carts upon recall with concurrency guard
        using var delCmd = conn.CreateCommand();
        delCmd.Transaction = tx;
        delCmd.CommandText = "DELETE FROM held_carts WHERE held_cart_id = $id;";
        delCmd.Parameters.AddWithValue("$id", heldCartId.ToString());
        var deleted = await delCmd.ExecuteNonQueryAsync();
        if (deleted == 0)
        {
            throw new PosException($"Held cart '{heldCartId}' has already been recalled or removed.");
        }

        // Audit recall
        using var auditCmd = conn.CreateCommand();
        auditCmd.Transaction = tx;
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, 'RECALL_CART', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", heldCart.TenantId);
        auditCmd.Parameters.AddWithValue("$bid", heldCart.BranchId);
        auditCmd.Parameters.AddWithValue("$cid", heldCart.CounterId);
        auditCmd.Parameters.AddWithValue("$actor", actorId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            HeldCartId = heldCart.HeldCartId,
            CustomerReference = heldCart.CustomerReference,
            ItemCount = heldCart.Items.Count,
            GrandTotal = heldCart.GrandTotal
        }));
        auditCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();

        tx.Commit();
        return heldCart;
    }

    public async Task DeleteHeldCartAsync(Guid heldCartId, string actorId, string? reason = null)
    {
        var heldCart = await GetHeldCartByIdAsync(heldCartId);
        if (heldCart == null)
        {
            return;
        }

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        using var delCmd = conn.CreateCommand();
        delCmd.Transaction = tx;
        delCmd.CommandText = "DELETE FROM held_carts WHERE held_cart_id = $id;";
        delCmd.Parameters.AddWithValue("$id", heldCartId.ToString());
        var affected = await delCmd.ExecuteNonQueryAsync();
        if (affected == 0)
        {
            return;
        }

        using var auditCmd = conn.CreateCommand();
        auditCmd.Transaction = tx;
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, 'DISCARD_HELD_CART', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", heldCart.TenantId);
        auditCmd.Parameters.AddWithValue("$bid", heldCart.BranchId);
        auditCmd.Parameters.AddWithValue("$cid", heldCart.CounterId);
        auditCmd.Parameters.AddWithValue("$actor", actorId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            HeldCartId = heldCart.HeldCartId,
            Reason = reason ?? "Customer abandoned or cashier cancelled held cart"
        }));
        auditCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();

        tx.Commit();
    }

    private static HeldCart MapHeldCart(SqliteDataReader reader)
    {
        var cartJson = reader.GetString(10);
        var items = JsonSerializer.Deserialize<List<HeldCartItem>>(cartJson) ?? new List<HeldCartItem>();

        return new HeldCart
        {
            HeldCartId = Guid.Parse(reader.GetString(0)),
            BranchId = reader.GetString(1),
            CounterId = reader.GetString(2),
            CashierId = reader.GetString(3),
            CustomerReference = reader.IsDBNull(4) ? null : reader.GetString(4),
            Subtotal = reader.GetDecimal(5),
            DiscountTotal = reader.GetDecimal(6),
            TaxTotal = reader.GetDecimal(7),
            GrandTotal = reader.GetDecimal(8),
            HeldAtUtc = DateTime.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            Items = items,
            TenantId = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : "TENANT_LK_01"
        };
    }
}

