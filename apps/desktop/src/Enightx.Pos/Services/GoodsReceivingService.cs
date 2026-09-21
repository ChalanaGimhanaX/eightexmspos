using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public record GoodsReceivingItemRequest(
    string ProductId,
    decimal Quantity,
    decimal UnitCost
);

public record GoodsReceivingCommand(
    string TenantId,
    string BranchId,
    string SupplierName,
    string InvoiceReference,
    User Actor,
    List<GoodsReceivingItemRequest> Items,
    string? Notes = null
);

public interface IGoodsReceivingService
{
    Task<GoodsReceipt> ReceiveGoodsAsync(GoodsReceivingCommand command);
    Task<List<GoodsReceipt>> GetGoodsReceiptsAsync(string branchId, int limit = 50);
    Task<GoodsReceipt?> GetGoodsReceiptByIdAsync(Guid receiptId);
    Task<List<StockMovement>> GetStockMovementsAsync(string? productId = null, string? movementType = null, int limit = 100);
}

public class GoodsReceivingService : IGoodsReceivingService
{
    private readonly PosDatabase _db;
    private readonly ICatalogService _catalog;

    public GoodsReceivingService(PosDatabase db, ICatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<GoodsReceipt> ReceiveGoodsAsync(GoodsReceivingCommand command)
    {
        // Manager / Owner role verification (A09 permissions)
        if (command.Actor.Role != Role.Owner && command.Actor.Role != Role.Manager)
        {
            throw new UnauthorizedActionException(
                $"Cashier '{command.Actor.Username}' is not authorized to receive inventory. Owner or Manager authorization required."
            );
        }

        if (string.IsNullOrWhiteSpace(command.SupplierName))
        {
            throw new ArgumentException("Supplier name is required.", nameof(command.SupplierName));
        }
        if (string.IsNullOrWhiteSpace(command.InvoiceReference))
        {
            throw new ArgumentException("Invoice / delivery reference is required.", nameof(command.InvoiceReference));
        }
        if (command.Items == null || command.Items.Count == 0)
        {
            throw new PosException("Cannot receive inventory with no items.");
        }

        var receiptId = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;
        var receiptLines = new List<GoodsReceiptLine>();
        var stockMovements = new List<StockMovement>();
        decimal totalCost = 0m;

        // Preload products and validate items before opening transaction to avoid SQLite concurrency lock
        var loadedProducts = new Dictionary<string, Product>();
        foreach (var item in command.Items)
        {
            if (item.Quantity <= 0)
            {
                throw new PosException($"Received quantity must be greater than zero for product '{item.ProductId}'.");
            }
            if (item.UnitCost < 0)
            {
                throw new PosException($"Unit cost cannot be negative for product '{item.ProductId}'.");
            }

            if (!loadedProducts.ContainsKey(item.ProductId))
            {
                var product = await _catalog.GetProductByIdAsync(item.ProductId);
                if (product == null)
                {
                    throw new PosException($"Product '{item.ProductId}' was not found.");
                }
                loadedProducts[item.ProductId] = product;
            }
        }

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            foreach (var item in command.Items)
            {
                var product = loadedProducts[item.ProductId];
                var roundedUnitCost = MoneyCalculator.Round(item.UnitCost);
                var lineTotalCost = MoneyCalculator.Round(item.Quantity * roundedUnitCost);
                totalCost += lineTotalCost;

                var line = new GoodsReceiptLine
                {
                    LineId = Guid.NewGuid(),
                    ReceiptId = receiptId,
                    ProductId = item.ProductId,
                    Quantity = item.Quantity,
                    UnitCost = roundedUnitCost,
                    LineTotalCost = lineTotalCost
                };
                receiptLines.Add(line);

                // Calculate updated cost basis via moving weighted average
                var newCostBasis = MoneyCalculator.CalculateMovingWeightedAverageCost(
                    product.StockOnHand,
                    product.CostBasis,
                    item.Quantity,
                    roundedUnitCost
                );

                // Update product in-memory copy
                product.StockOnHand += item.Quantity;
                product.CostBasis = newCostBasis;

                // 1. Update product stock on hand and cost basis
                using var prodUpdCmd = conn.CreateCommand();
                prodUpdCmd.Transaction = tx;
                prodUpdCmd.CommandText = @"
                    UPDATE products SET
                        stock_on_hand = stock_on_hand + $qty,
                        cost_basis = $cbasis
                    WHERE product_id = $pid;
                ";
                prodUpdCmd.Parameters.AddWithValue("$qty", item.Quantity);
                prodUpdCmd.Parameters.AddWithValue("$cbasis", newCostBasis);
                prodUpdCmd.Parameters.AddWithValue("$pid", item.ProductId);
                await prodUpdCmd.ExecuteNonQueryAsync();

                // 2. Record stock movement
                var movementId = Guid.NewGuid();
                using var smCmd = conn.CreateCommand();
                smCmd.Transaction = tx;
                smCmd.CommandText = @"
                    INSERT INTO stock_movements (movement_id, product_id, movement_type, quantity_change, reference_id, occurred_at_utc)
                    VALUES ($mid, $pid, 'RECEIVING', $qchange, $ref, $occurred);
                ";
                smCmd.Parameters.AddWithValue("$mid", movementId.ToString());
                smCmd.Parameters.AddWithValue("$pid", item.ProductId);
                smCmd.Parameters.AddWithValue("$qchange", item.Quantity);
                smCmd.Parameters.AddWithValue("$ref", receiptId.ToString());
                smCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
                await smCmd.ExecuteNonQueryAsync();

                stockMovements.Add(new StockMovement
                {
                    MovementId = movementId,
                    ProductId = item.ProductId,
                    MovementType = "RECEIVING",
                    QuantityChange = item.Quantity,
                    ReferenceId = receiptId.ToString(),
                    OccurredAtUtc = nowUtc
                });
            }

            totalCost = MoneyCalculator.Round(totalCost);

            var receipt = new GoodsReceipt
            {
                ReceiptId = receiptId,
                TenantId = command.TenantId,
                BranchId = command.BranchId,
                SupplierName = command.SupplierName.Trim(),
                InvoiceReference = command.InvoiceReference.Trim(),
                ReceivedByUserId = command.Actor.UserId,
                ReceivedAtUtc = nowUtc,
                TotalCost = totalCost,
                Notes = string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim(),
                Lines = receiptLines
            };

            // Insert goods receipt header
            using var grCmd = conn.CreateCommand();
            grCmd.Transaction = tx;
            grCmd.CommandText = @"
                INSERT INTO goods_receipts (
                    receipt_id, tenant_id, branch_id, supplier_name, invoice_reference,
                    received_by_user_id, received_at_utc, total_cost, notes
                ) VALUES (
                    $id, $tid, $bid, $sup, $inv, $uid, $rec, $cost, $notes
                );
            ";
            grCmd.Parameters.AddWithValue("$id", receipt.ReceiptId.ToString());
            grCmd.Parameters.AddWithValue("$tid", receipt.TenantId);
            grCmd.Parameters.AddWithValue("$bid", receipt.BranchId);
            grCmd.Parameters.AddWithValue("$sup", receipt.SupplierName);
            grCmd.Parameters.AddWithValue("$inv", receipt.InvoiceReference);
            grCmd.Parameters.AddWithValue("$uid", receipt.ReceivedByUserId);
            grCmd.Parameters.AddWithValue("$rec", receipt.ReceivedAtUtc.ToString("o"));
            grCmd.Parameters.AddWithValue("$cost", receipt.TotalCost);
            grCmd.Parameters.AddWithValue("$notes", (object?)receipt.Notes ?? DBNull.Value);
            await grCmd.ExecuteNonQueryAsync();

            // Insert goods receipt lines
            foreach (var line in receiptLines)
            {
                using var grlCmd = conn.CreateCommand();
                grlCmd.Transaction = tx;
                grlCmd.CommandText = @"
                    INSERT INTO goods_receipt_lines (
                        line_id, receipt_id, product_id, quantity, unit_cost, line_total_cost
                    ) VALUES (
                        $lid, $rid, $pid, $qty, $cost, $tot
                    );
                ";
                grlCmd.Parameters.AddWithValue("$lid", line.LineId.ToString());
                grlCmd.Parameters.AddWithValue("$rid", line.ReceiptId.ToString());
                grlCmd.Parameters.AddWithValue("$pid", line.ProductId);
                grlCmd.Parameters.AddWithValue("$qty", line.Quantity);
                grlCmd.Parameters.AddWithValue("$cost", line.UnitCost);
                grlCmd.Parameters.AddWithValue("$tot", line.LineTotalCost);
                await grlCmd.ExecuteNonQueryAsync();
            }

            // Audit event
            using var auditCmd = conn.CreateCommand();
            auditCmd.Transaction = tx;
            auditCmd.CommandText = @"
                INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                VALUES ($eid, $tid, $bid, 'MAIN', $actor, 'GOODS_RECEIVING', $details, $occurred);
            ";
            auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            auditCmd.Parameters.AddWithValue("$tid", command.TenantId);
            auditCmd.Parameters.AddWithValue("$bid", command.BranchId);
            auditCmd.Parameters.AddWithValue("$actor", command.Actor.UserId);
            auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
            {
                ReceiptId = receipt.ReceiptId,
                SupplierName = receipt.SupplierName,
                InvoiceReference = receipt.InvoiceReference,
                LineCount = receiptLines.Count,
                TotalCost = receipt.TotalCost
            }));
            auditCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            await auditCmd.ExecuteNonQueryAsync();

            // Outbox event for cloud sync
            using var seqCmd = conn.CreateCommand();
            seqCmd.Transaction = tx;
            seqCmd.CommandText = @"
                INSERT INTO receipt_sequences (branch_id, counter_id, last_sequence)
                VALUES ($bid, 'MAIN', 1)
                ON CONFLICT(branch_id, counter_id) DO UPDATE SET last_sequence = last_sequence + 1
                RETURNING last_sequence;
            ";
            seqCmd.Parameters.AddWithValue("$bid", command.BranchId);
            var nextSeq = Convert.ToInt64(await seqCmd.ExecuteScalarAsync());

            var outboxPayload = JsonSerializer.Serialize(new
            {
                receipt_id = receipt.ReceiptId,
                tenant_id = receipt.TenantId,
                branch_id = receipt.BranchId,
                supplier_name = receipt.SupplierName,
                invoice_reference = receipt.InvoiceReference,
                received_by_user_id = receipt.ReceivedByUserId,
                received_at_utc = receipt.ReceivedAtUtc.ToString("o"),
                total_cost = $"{receipt.TotalCost:F2}",
                notes = receipt.Notes,
                lines = receiptLines.Select(l => new
                {
                    line_id = l.LineId,
                    product_id = l.ProductId,
                    quantity = $"{l.Quantity:F2}",
                    unit_cost = $"{l.UnitCost:F2}",
                    line_total_cost = $"{l.LineTotalCost:F2}"
                })
            });

            using var outboxCmd = conn.CreateCommand();
            outboxCmd.Transaction = tx;
            outboxCmd.CommandText = @"
                INSERT INTO outbox_events (
                    event_id, tenant_id, branch_id, device_id, device_generation,
                    source_sequence, schema_version, payload_json, actor_id, occurred_at_utc,
                    causal_reference, status, created_at_utc
                ) VALUES (
                    $eid, $tid, $bid, 'MAIN', 1, $seq, '1.0', $payload, $actor, $occurred, $causal, 'PENDING', $created
                );
            ";
            outboxCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            outboxCmd.Parameters.AddWithValue("$tid", receipt.TenantId);
            outboxCmd.Parameters.AddWithValue("$bid", receipt.BranchId);
            outboxCmd.Parameters.AddWithValue("$seq", nextSeq);
            outboxCmd.Parameters.AddWithValue("$payload", outboxPayload);
            outboxCmd.Parameters.AddWithValue("$actor", command.Actor.UserId);
            outboxCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            outboxCmd.Parameters.AddWithValue("$causal", receipt.ReceiptId.ToString());
            outboxCmd.Parameters.AddWithValue("$created", nowUtc.ToString("o"));
            await outboxCmd.ExecuteNonQueryAsync();

            tx.Commit();
            return receipt;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    public async Task<List<GoodsReceipt>> GetGoodsReceiptsAsync(string branchId, int limit = 50)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT receipt_id, tenant_id, branch_id, supplier_name, invoice_reference,
                   received_by_user_id, received_at_utc, total_cost, notes
            FROM goods_receipts
            WHERE branch_id = $bid
            ORDER BY received_at_utc DESC
            LIMIT $lim;
        ";
        cmd.Parameters.AddWithValue("$bid", branchId);
        cmd.Parameters.AddWithValue("$lim", limit);

        var list = new List<GoodsReceipt>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new GoodsReceipt
            {
                ReceiptId = Guid.Parse(reader.GetString(0)),
                TenantId = reader.GetString(1),
                BranchId = reader.GetString(2),
                SupplierName = reader.GetString(3),
                InvoiceReference = reader.GetString(4),
                ReceivedByUserId = reader.GetString(5),
                ReceivedAtUtc = DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
                TotalCost = reader.GetDecimal(7),
                Notes = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }
        return list;
    }

    public async Task<GoodsReceipt?> GetGoodsReceiptByIdAsync(Guid receiptId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT receipt_id, tenant_id, branch_id, supplier_name, invoice_reference,
                   received_by_user_id, received_at_utc, total_cost, notes
            FROM goods_receipts
            WHERE receipt_id = $id;
        ";
        cmd.Parameters.AddWithValue("$id", receiptId.ToString());

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var receipt = new GoodsReceipt
        {
            ReceiptId = Guid.Parse(reader.GetString(0)),
            TenantId = reader.GetString(1),
            BranchId = reader.GetString(2),
            SupplierName = reader.GetString(3),
            InvoiceReference = reader.GetString(4),
            ReceivedByUserId = reader.GetString(5),
            ReceivedAtUtc = DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            TotalCost = reader.GetDecimal(7),
            Notes = reader.IsDBNull(8) ? null : reader.GetString(8)
        };

        // Fetch lines
        using var lineCmd = conn.CreateCommand();
        lineCmd.CommandText = @"
            SELECT line_id, receipt_id, product_id, quantity, unit_cost, line_total_cost
            FROM goods_receipt_lines WHERE receipt_id = $rid;
        ";
        lineCmd.Parameters.AddWithValue("$rid", receiptId.ToString());
        using var lReader = await lineCmd.ExecuteReaderAsync();
        while (await lReader.ReadAsync())
        {
            receipt.Lines.Add(new GoodsReceiptLine
            {
                LineId = Guid.Parse(lReader.GetString(0)),
                ReceiptId = receiptId,
                ProductId = lReader.GetString(2),
                Quantity = lReader.GetDecimal(3),
                UnitCost = lReader.GetDecimal(4),
                LineTotalCost = lReader.GetDecimal(5)
            });
        }

        return receipt;
    }

    public async Task<List<StockMovement>> GetStockMovementsAsync(string? productId = null, string? movementType = null, int limit = 100)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();

        var query = "SELECT movement_id, product_id, movement_type, quantity_change, reference_id, occurred_at_utc FROM stock_movements WHERE 1=1";
        if (!string.IsNullOrEmpty(productId))
        {
            query += " AND product_id = $pid";
            cmd.Parameters.AddWithValue("$pid", productId);
        }
        if (!string.IsNullOrEmpty(movementType))
        {
            query += " AND movement_type = $mtype";
            cmd.Parameters.AddWithValue("$mtype", movementType);
        }
        query += " ORDER BY occurred_at_utc DESC LIMIT $lim;";
        cmd.Parameters.AddWithValue("$lim", limit);

        cmd.CommandText = query;

        var list = new List<StockMovement>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new StockMovement
            {
                MovementId = Guid.Parse(reader.GetString(0)),
                ProductId = reader.GetString(1),
                MovementType = reader.GetString(2),
                QuantityChange = reader.GetDecimal(3),
                ReferenceId = reader.GetString(4),
                OccurredAtUtc = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
            });
        }
        return list;
    }
}
