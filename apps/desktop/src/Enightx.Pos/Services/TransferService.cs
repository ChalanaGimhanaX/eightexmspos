using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface ITransferService
{
    Task<Transfer> DispatchTransferAsync(
        string sourceBranchId,
        string destBranchId,
        List<TransferLineItem> lines,
        string dispatchedBy,
        string tenantId,
        string? notes = null
    );

    Task<Transfer> ReceiveTransferAsync(
        string transferId,
        string receivingBranchId,
        string receivedBy,
        Dictionary<string, decimal>? receivedQuantities = null
    );

    Task<Transfer> CancelTransferAsync(
        string transferId,
        string cancelledBy,
        string reason
    );

    Task<List<Transfer>> GetTransfersAsync(
        string? branchId = null,
        TransferStatus? status = null
    );

    Task<Transfer?> GetTransferByIdAsync(string transferId);
}

public class TransferService : ITransferService
{
    public const string MovementTypeTransferOut = "TRANSFER_OUT";
    public const string MovementTypeTransferIn = "TRANSFER_IN";
    public const string MovementTypeTransferCancelReturn = "TRANSFER_CANCEL_RETURN";

    private readonly PosDatabase _db;
    private readonly ICatalogService _catalog;

    public TransferService(PosDatabase db, ICatalogService catalog)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<Transfer> DispatchTransferAsync(
        string sourceBranchId,
        string destBranchId,
        List<TransferLineItem> lines,
        string dispatchedBy,
        string tenantId,
        string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(sourceBranchId))
            throw new ArgumentException("Source branch ID is required.", nameof(sourceBranchId));
        if (string.IsNullOrWhiteSpace(destBranchId))
            throw new ArgumentException("Destination branch ID is required.", nameof(destBranchId));
        if (sourceBranchId.Trim().Equals(destBranchId.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source and destination branch cannot be the same.", nameof(destBranchId));
        if (string.IsNullOrWhiteSpace(dispatchedBy))
            throw new ArgumentException("Dispatched by user is required.", nameof(dispatchedBy));
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (lines == null || lines.Count == 0)
            throw new ArgumentException("Transfer must contain at least one line item.", nameof(lines));

        var nowUtc = DateTime.UtcNow;
        var transferId = Guid.NewGuid().ToString();
        var transferNumber = $"TRF-{nowUtc:yyyyMMdd}-{Guid.NewGuid():N}"[..24].ToUpperInvariant();

        // 1. Validate lines and populate product snapshots
        var processedLines = new List<TransferLineItem>();
        decimal totalDispatched = 0m;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.ProductId))
                throw new ArgumentException("Product ID cannot be empty on transfer line.", nameof(lines));
            if (line.DispatchedQuantity <= 0)
                throw new ArgumentException($"Dispatched quantity must be greater than zero for product '{line.ProductId}'.", nameof(lines));

            var product = await _catalog.GetProductByIdAsync(line.ProductId);
            if (product == null)
            {
                throw new KeyNotFoundException($"Product '{line.ProductId}' was not found in catalog.");
            }

            var item = new TransferLineItem
            {
                TransferItemId = line.TransferItemId != Guid.Empty ? line.TransferItemId : Guid.NewGuid(),
                TransferId = transferId,
                ProductId = line.ProductId,
                ProductName = string.IsNullOrWhiteSpace(line.ProductName) ? product.Name : line.ProductName,
                Barcode = string.IsNullOrWhiteSpace(line.Barcode) ? product.Barcode : line.Barcode,
                DispatchedQuantity = line.DispatchedQuantity,
                ReceivedQuantity = null,
                DiscrepancyQuantity = 0m,
                UnitCost = line.UnitCost > 0 ? line.UnitCost : product.CostBasis,
                Notes = line.Notes
            };

            processedLines.Add(item);
            totalDispatched += item.DispatchedQuantity;
        }

        var transfer = new Transfer
        {
            TransferId = transferId,
            TransferNumber = transferNumber,
            TenantId = tenantId.Trim(),
            SourceBranchId = sourceBranchId.Trim(),
            DestBranchId = destBranchId.Trim(),
            Status = TransferStatus.InTransit,
            DispatchedBy = dispatchedBy.Trim(),
            DispatchedAtUtc = nowUtc,
            TotalDispatchedQuantity = totalDispatched,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            Lines = processedLines
        };

        // 2. Transactional execution: Save transfer, deduct stock, log movement & audit
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            // Insert header
            using var cmdHeader = conn.CreateCommand();
            cmdHeader.Transaction = tx;
            cmdHeader.CommandText = @"
                INSERT INTO transfers (
                    transfer_id, transfer_number, tenant_id, source_branch_id, dest_branch_id,
                    status, dispatched_by, dispatched_at_utc, total_dispatched_quantity,
                    has_discrepancy, notes, created_at_utc, updated_at_utc
                ) VALUES (
                    $id, $num, $tid, $src, $dst,
                    $status, $dby, $dat, $totd,
                    0, $notes, $cat, $uat
                );
            ";
            cmdHeader.Parameters.AddWithValue("$id", transfer.TransferId);
            cmdHeader.Parameters.AddWithValue("$num", transfer.TransferNumber);
            cmdHeader.Parameters.AddWithValue("$tid", transfer.TenantId);
            cmdHeader.Parameters.AddWithValue("$src", transfer.SourceBranchId);
            cmdHeader.Parameters.AddWithValue("$dst", transfer.DestBranchId);
            cmdHeader.Parameters.AddWithValue("$status", (int)transfer.Status);
            cmdHeader.Parameters.AddWithValue("$dby", transfer.DispatchedBy);
            cmdHeader.Parameters.AddWithValue("$dat", transfer.DispatchedAtUtc.ToString("o"));
            cmdHeader.Parameters.AddWithValue("$totd", transfer.TotalDispatchedQuantity);
            cmdHeader.Parameters.AddWithValue("$notes", (object?)transfer.Notes ?? DBNull.Value);
            cmdHeader.Parameters.AddWithValue("$cat", transfer.CreatedAtUtc.ToString("o"));
            cmdHeader.Parameters.AddWithValue("$uat", transfer.UpdatedAtUtc.ToString("o"));
            await cmdHeader.ExecuteNonQueryAsync();

            // Insert lines, deduct source stock, log stock movement
            foreach (var line in processedLines)
            {
                using var cmdLine = conn.CreateCommand();
                cmdLine.Transaction = tx;
                cmdLine.CommandText = @"
                    INSERT INTO transfer_items (
                        transfer_item_id, transfer_id, product_id, product_name, barcode,
                        dispatched_quantity, received_quantity, discrepancy_quantity, unit_cost, notes
                    ) VALUES (
                        $lid, $tid, $pid, $pname, $bcode,
                        $dqty, NULL, 0, $ucost, $notes
                    );

                    UPDATE products
                    SET stock_on_hand = stock_on_hand - $dqty
                    WHERE product_id = $pid;

                    INSERT INTO stock_movements (
                        movement_id, product_id, movement_type, quantity_change, reference_id, occurred_at_utc
                    ) VALUES (
                        $mid, $pid, $mtype, -$dqty, $ref, $occurred
                    );
                ";
                cmdLine.Parameters.AddWithValue("$lid", line.TransferItemId.ToString());
                cmdLine.Parameters.AddWithValue("$tid", transfer.TransferId);
                cmdLine.Parameters.AddWithValue("$pid", line.ProductId);
                cmdLine.Parameters.AddWithValue("$pname", line.ProductName);
                cmdLine.Parameters.AddWithValue("$bcode", line.Barcode);
                cmdLine.Parameters.AddWithValue("$dqty", line.DispatchedQuantity);
                cmdLine.Parameters.AddWithValue("$ucost", line.UnitCost);
                cmdLine.Parameters.AddWithValue("$notes", (object?)line.Notes ?? DBNull.Value);
                cmdLine.Parameters.AddWithValue("$mid", Guid.NewGuid().ToString());
                cmdLine.Parameters.AddWithValue("$mtype", MovementTypeTransferOut);
                cmdLine.Parameters.AddWithValue("$ref", transfer.TransferId);
                cmdLine.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
                await cmdLine.ExecuteNonQueryAsync();
            }

            // Audit event
            using var cmdAudit = conn.CreateCommand();
            cmdAudit.Transaction = tx;
            cmdAudit.CommandText = @"
                INSERT INTO audit_events (
                    event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc
                ) VALUES (
                    $eid, $tid, $bid, 'MAIN', $actor, 'TRANSFER_DISPATCHED', $details, $occurred
                );
            ";
            cmdAudit.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            cmdAudit.Parameters.AddWithValue("$tid", transfer.TenantId);
            cmdAudit.Parameters.AddWithValue("$bid", transfer.SourceBranchId);
            cmdAudit.Parameters.AddWithValue("$actor", transfer.DispatchedBy);
            cmdAudit.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
            {
                transfer_id = transfer.TransferId,
                transfer_number = transfer.TransferNumber,
                source_branch_id = transfer.SourceBranchId,
                dest_branch_id = transfer.DestBranchId,
                total_dispatched_quantity = transfer.TotalDispatchedQuantity,
                line_count = processedLines.Count
            }));
            cmdAudit.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            await cmdAudit.ExecuteNonQueryAsync();

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }

        return transfer;
    }

    public async Task<Transfer> ReceiveTransferAsync(
        string transferId,
        string receivingBranchId,
        string receivedBy,
        Dictionary<string, decimal>? receivedQuantities = null)
    {
        if (string.IsNullOrWhiteSpace(transferId))
            throw new ArgumentException("Transfer ID is required.", nameof(transferId));
        if (string.IsNullOrWhiteSpace(receivingBranchId))
            throw new ArgumentException("Receiving branch ID is required.", nameof(receivingBranchId));
        if (string.IsNullOrWhiteSpace(receivedBy))
            throw new ArgumentException("Received by user is required.", nameof(receivedBy));

        var transfer = await GetTransferByIdAsync(transferId);
        if (transfer == null)
        {
            throw new KeyNotFoundException($"Transfer with ID '{transferId}' was not found.");
        }

        // Functional Guardrail: Transfers stay in in-transit status until explicitly received
        if (transfer.Status == TransferStatus.Received)
        {
            throw new InvalidOperationException($"Transfer '{transferId}' has already been received.");
        }
        if (transfer.Status == TransferStatus.Cancelled)
        {
            throw new InvalidOperationException($"Transfer '{transferId}' has been cancelled and cannot be received.");
        }
        if (transfer.Status != TransferStatus.InTransit)
        {
            throw new InvalidOperationException($"Transfer '{transferId}' is not in-transit (current status: {transfer.Status}).");
        }

        // Destination branch verification
        if (!transfer.DestBranchId.Equals(receivingBranchId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Transfer destination branch '{transfer.DestBranchId}' does not match receiving branch '{receivingBranchId}'."
            );
        }

        var nowUtc = DateTime.UtcNow;
        decimal totalReceived = 0m;
        bool hasDiscrepancy = false;
        var discrepancyDetails = new List<string>();

        foreach (var line in transfer.Lines)
        {
            decimal actualQty;
            if (receivedQuantities != null && receivedQuantities.TryGetValue(line.ProductId, out var customQty))
            {
                if (customQty < 0)
                {
                    throw new ArgumentException($"Received quantity cannot be negative for product '{line.ProductId}'.", nameof(receivedQuantities));
                }
                actualQty = customQty;
            }
            else
            {
                actualQty = line.DispatchedQuantity;
            }

            line.ReceivedQuantity = actualQty;
            line.DiscrepancyQuantity = line.DispatchedQuantity - actualQty;
            totalReceived += actualQty;

            if (line.DiscrepancyQuantity != 0m)
            {
                hasDiscrepancy = true;
                discrepancyDetails.Add($"{line.ProductName}: dispatched {line.DispatchedQuantity}, received {actualQty} (diff: {-line.DiscrepancyQuantity:+#;-#;0})");
            }
        }

        transfer.Status = TransferStatus.Received;
        transfer.ReceivedBy = receivedBy.Trim();
        transfer.ReceivedAtUtc = nowUtc;
        transfer.TotalReceivedQuantity = totalReceived;
        transfer.HasDiscrepancy = hasDiscrepancy;
        transfer.DiscrepancyNotes = hasDiscrepancy ? string.Join("; ", discrepancyDetails) : null;
        transfer.UpdatedAtUtc = nowUtc;

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            // Update header
            using var cmdHeader = conn.CreateCommand();
            cmdHeader.Transaction = tx;
            cmdHeader.CommandText = @"
                UPDATE transfers SET
                    status = $status,
                    received_by = $rby,
                    received_at_utc = $rat,
                    total_received_quantity = $totr,
                    has_discrepancy = $hasd,
                    discrepancy_notes = $dnotes,
                    updated_at_utc = $uat
                WHERE transfer_id = $id;
            ";
            cmdHeader.Parameters.AddWithValue("$status", (int)transfer.Status);
            cmdHeader.Parameters.AddWithValue("$rby", transfer.ReceivedBy);
            cmdHeader.Parameters.AddWithValue("$rat", transfer.ReceivedAtUtc.Value.ToString("o"));
            cmdHeader.Parameters.AddWithValue("$totr", transfer.TotalReceivedQuantity.Value);
            cmdHeader.Parameters.AddWithValue("$hasd", transfer.HasDiscrepancy ? 1 : 0);
            cmdHeader.Parameters.AddWithValue("$dnotes", (object?)transfer.DiscrepancyNotes ?? DBNull.Value);
            cmdHeader.Parameters.AddWithValue("$uat", transfer.UpdatedAtUtc.ToString("o"));
            cmdHeader.Parameters.AddWithValue("$id", transfer.TransferId);
            await cmdHeader.ExecuteNonQueryAsync();

            // Update lines, increment destination stock, log stock movements
            foreach (var line in transfer.Lines)
            {
                var receivedQty = line.ReceivedQuantity ?? line.DispatchedQuantity;

                using var cmdLine = conn.CreateCommand();
                cmdLine.Transaction = tx;
                cmdLine.CommandText = @"
                    UPDATE transfer_items SET
                        received_quantity = $rqty,
                        discrepancy_quantity = $disc
                    WHERE transfer_item_id = $lid;
                ";
                cmdLine.Parameters.AddWithValue("$rqty", receivedQty);
                cmdLine.Parameters.AddWithValue("$disc", line.DiscrepancyQuantity);
                cmdLine.Parameters.AddWithValue("$lid", line.TransferItemId.ToString());
                await cmdLine.ExecuteNonQueryAsync();

                if (receivedQty > 0)
                {
                    using var cmdStock = conn.CreateCommand();
                    cmdStock.Transaction = tx;
                    cmdStock.CommandText = @"
                        UPDATE products
                        SET stock_on_hand = stock_on_hand + $rqty
                        WHERE product_id = $pid;

                        INSERT INTO stock_movements (
                            movement_id, product_id, movement_type, quantity_change, reference_id, occurred_at_utc
                        ) VALUES (
                            $mid, $pid, $mtype, $rqty, $ref, $occurred
                        );
                    ";
                    cmdStock.Parameters.AddWithValue("$rqty", receivedQty);
                    cmdStock.Parameters.AddWithValue("$pid", line.ProductId);
                    cmdStock.Parameters.AddWithValue("$mid", Guid.NewGuid().ToString());
                    cmdStock.Parameters.AddWithValue("$mtype", MovementTypeTransferIn);
                    cmdStock.Parameters.AddWithValue("$ref", transfer.TransferId);
                    cmdStock.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
                    await cmdStock.ExecuteNonQueryAsync();
                }
            }

            // Audit event
            using var cmdAudit = conn.CreateCommand();
            cmdAudit.Transaction = tx;
            cmdAudit.CommandText = @"
                INSERT INTO audit_events (
                    event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc
                ) VALUES (
                    $eid, $tid, $bid, 'MAIN', $actor, 'TRANSFER_RECEIVED', $details, $occurred
                );
            ";
            cmdAudit.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            cmdAudit.Parameters.AddWithValue("$tid", transfer.TenantId);
            cmdAudit.Parameters.AddWithValue("$bid", receivingBranchId);
            cmdAudit.Parameters.AddWithValue("$actor", receivedBy);
            cmdAudit.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
            {
                transfer_id = transfer.TransferId,
                transfer_number = transfer.TransferNumber,
                receiving_branch_id = receivingBranchId,
                total_received_quantity = transfer.TotalReceivedQuantity,
                has_discrepancy = transfer.HasDiscrepancy,
                discrepancy_notes = transfer.DiscrepancyNotes
            }));
            cmdAudit.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            await cmdAudit.ExecuteNonQueryAsync();

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }

        return transfer;
    }

    public async Task<Transfer> CancelTransferAsync(string transferId, string cancelledBy, string reason)
    {
        if (string.IsNullOrWhiteSpace(transferId))
            throw new ArgumentException("Transfer ID is required.", nameof(transferId));
        if (string.IsNullOrWhiteSpace(cancelledBy))
            throw new ArgumentException("Cancelled by user is required.", nameof(cancelledBy));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Cancellation reason is required.", nameof(reason));

        var transfer = await GetTransferByIdAsync(transferId);
        if (transfer == null)
        {
            throw new KeyNotFoundException($"Transfer with ID '{transferId}' was not found.");
        }

        if (transfer.Status == TransferStatus.Received)
        {
            throw new InvalidOperationException($"Transfer '{transferId}' has already been received and cannot be cancelled.");
        }
        if (transfer.Status == TransferStatus.Cancelled)
        {
            throw new InvalidOperationException($"Transfer '{transferId}' is already cancelled.");
        }
        if (transfer.Status != TransferStatus.InTransit)
        {
            throw new InvalidOperationException($"Transfer '{transferId}' cannot be cancelled because it is not in-transit (current status: {transfer.Status}).");
        }

        var nowUtc = DateTime.UtcNow;
        transfer.Status = TransferStatus.Cancelled;
        transfer.CancelledBy = cancelledBy.Trim();
        transfer.CancelledAtUtc = nowUtc;
        transfer.CancellationReason = reason.Trim();
        transfer.UpdatedAtUtc = nowUtc;

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            // Update header
            using var cmdHeader = conn.CreateCommand();
            cmdHeader.Transaction = tx;
            cmdHeader.CommandText = @"
                UPDATE transfers SET
                    status = $status,
                    cancelled_by = $cby,
                    cancelled_at_utc = $cat,
                    cancellation_reason = $creason,
                    updated_at_utc = $uat
                WHERE transfer_id = $id;
            ";
            cmdHeader.Parameters.AddWithValue("$status", (int)transfer.Status);
            cmdHeader.Parameters.AddWithValue("$cby", transfer.CancelledBy);
            cmdHeader.Parameters.AddWithValue("$cat", transfer.CancelledAtUtc.Value.ToString("o"));
            cmdHeader.Parameters.AddWithValue("$creason", transfer.CancellationReason);
            cmdHeader.Parameters.AddWithValue("$uat", transfer.UpdatedAtUtc.ToString("o"));
            cmdHeader.Parameters.AddWithValue("$id", transfer.TransferId);
            await cmdHeader.ExecuteNonQueryAsync();

            // Restore source stock & log movement
            foreach (var line in transfer.Lines)
            {
                using var cmdStock = conn.CreateCommand();
                cmdStock.Transaction = tx;
                cmdStock.CommandText = @"
                    UPDATE products
                    SET stock_on_hand = stock_on_hand + $dqty
                    WHERE product_id = $pid;

                    INSERT INTO stock_movements (
                        movement_id, product_id, movement_type, quantity_change, reference_id, occurred_at_utc
                    ) VALUES (
                        $mid, $pid, $mtype, $dqty, $ref, $occurred
                    );
                ";
                cmdStock.Parameters.AddWithValue("$dqty", line.DispatchedQuantity);
                cmdStock.Parameters.AddWithValue("$pid", line.ProductId);
                cmdStock.Parameters.AddWithValue("$mid", Guid.NewGuid().ToString());
                cmdStock.Parameters.AddWithValue("$mtype", MovementTypeTransferCancelReturn);
                cmdStock.Parameters.AddWithValue("$ref", transfer.TransferId);
                cmdStock.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
                await cmdStock.ExecuteNonQueryAsync();
            }

            // Audit event
            using var cmdAudit = conn.CreateCommand();
            cmdAudit.Transaction = tx;
            cmdAudit.CommandText = @"
                INSERT INTO audit_events (
                    event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc
                ) VALUES (
                    $eid, $tid, $bid, 'MAIN', $actor, 'TRANSFER_CANCELLED', $details, $occurred
                );
            ";
            cmdAudit.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            cmdAudit.Parameters.AddWithValue("$tid", transfer.TenantId);
            cmdAudit.Parameters.AddWithValue("$bid", transfer.SourceBranchId);
            cmdAudit.Parameters.AddWithValue("$actor", cancelledBy);
            cmdAudit.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
            {
                transfer_id = transfer.TransferId,
                transfer_number = transfer.TransferNumber,
                cancelled_by = cancelledBy,
                reason = reason
            }));
            cmdAudit.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            await cmdAudit.ExecuteNonQueryAsync();

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }

        return transfer;
    }

    public async Task<List<Transfer>> GetTransfersAsync(string? branchId = null, TransferStatus? status = null)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();

        var sql = @"
            SELECT
                transfer_id, transfer_number, tenant_id, source_branch_id, dest_branch_id,
                status, dispatched_by, dispatched_at_utc, received_by, received_at_utc,
                cancelled_by, cancelled_at_utc, cancellation_reason,
                total_dispatched_quantity, total_received_quantity,
                has_discrepancy, discrepancy_notes, notes, created_at_utc, updated_at_utc
            FROM transfers
            WHERE 1=1
        ";

        if (!string.IsNullOrWhiteSpace(branchId))
        {
            sql += " AND (source_branch_id = $bid OR dest_branch_id = $bid)";
            cmd.Parameters.AddWithValue("$bid", branchId.Trim());
        }

        if (status.HasValue)
        {
            sql += " AND status = $status";
            cmd.Parameters.AddWithValue("$status", (int)status.Value);
        }

        sql += " ORDER BY dispatched_at_utc DESC;";
        cmd.CommandText = sql;

        var transfers = new List<Transfer>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            transfers.Add(MapTransferHeader(reader));
        }
        reader.Close();

        // Populate lines
        foreach (var t in transfers)
        {
            t.Lines = await GetTransferLinesAsync(conn, t.TransferId);
        }

        return transfers;
    }

    public async Task<Transfer?> GetTransferByIdAsync(string transferId)
    {
        if (string.IsNullOrWhiteSpace(transferId)) return null;

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                transfer_id, transfer_number, tenant_id, source_branch_id, dest_branch_id,
                status, dispatched_by, dispatched_at_utc, received_by, received_at_utc,
                cancelled_by, cancelled_at_utc, cancellation_reason,
                total_dispatched_quantity, total_received_quantity,
                has_discrepancy, discrepancy_notes, notes, created_at_utc, updated_at_utc
            FROM transfers
            WHERE transfer_id = $id;
        ";
        cmd.Parameters.AddWithValue("$id", transferId.Trim());

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var transfer = MapTransferHeader(reader);
        reader.Close();

        transfer.Lines = await GetTransferLinesAsync(conn, transfer.TransferId);
        return transfer;
    }

    private static async Task<List<TransferLineItem>> GetTransferLinesAsync(SqliteConnection conn, string transferId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                transfer_item_id, transfer_id, product_id, product_name, barcode,
                dispatched_quantity, received_quantity, discrepancy_quantity, unit_cost, notes
            FROM transfer_items
            WHERE transfer_id = $tid;
        ";
        cmd.Parameters.AddWithValue("$tid", transferId);

        var lines = new List<TransferLineItem>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(new TransferLineItem
            {
                TransferItemId = Guid.Parse(reader.GetString(0)),
                TransferId = reader.GetString(1),
                ProductId = reader.GetString(2),
                ProductName = reader.GetString(3),
                Barcode = reader.GetString(4),
                DispatchedQuantity = reader.GetDecimal(5),
                ReceivedQuantity = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                DiscrepancyQuantity = reader.GetDecimal(7),
                UnitCost = reader.GetDecimal(8),
                Notes = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }
        return lines;
    }

    private static Transfer MapTransferHeader(SqliteDataReader reader)
    {
        return new Transfer
        {
            TransferId = reader.GetString(0),
            TransferNumber = reader.GetString(1),
            TenantId = reader.GetString(2),
            SourceBranchId = reader.GetString(3),
            DestBranchId = reader.GetString(4),
            Status = (TransferStatus)reader.GetInt32(5),
            DispatchedBy = reader.GetString(6),
            DispatchedAtUtc = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            ReceivedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
            ReceivedAtUtc = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            CancelledBy = reader.IsDBNull(10) ? null : reader.GetString(10),
            CancelledAtUtc = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            CancellationReason = reader.IsDBNull(12) ? null : reader.GetString(12),
            TotalDispatchedQuantity = reader.GetDecimal(13),
            TotalReceivedQuantity = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
            HasDiscrepancy = reader.GetInt32(15) == 1,
            DiscrepancyNotes = reader.IsDBNull(16) ? null : reader.GetString(16),
            Notes = reader.IsDBNull(17) ? null : reader.GetString(17),
            CreatedAtUtc = DateTime.Parse(reader.GetString(18), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            UpdatedAtUtc = DateTime.Parse(reader.GetString(19), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
        };
    }
}
