using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface IStockCountService
{
    Task<StockCountSession> StartSessionAsync(
        string branchId,
        string startedBy,
        string tenantId,
        List<string>? productIds = null,
        string? notes = null
    );

    Task RecordCountItemAsync(
        string sessionId,
        string productId,
        decimal countedQuantity,
        string countedBy
    );

    Task<StockCountSession> CompleteSessionAsync(
        string sessionId,
        string completedBy
    );

    Task<StockCountSession> CancelSessionAsync(
        string sessionId,
        string cancelledBy,
        string reason
    );

    Task<StockCountSession?> GetSessionByIdAsync(string sessionId);

    Task<List<StockCountSession>> GetSessionsAsync(
        string? branchId = null,
        StockCountStatus? status = null
    );

    Task<StockCountAdjustmentJournal> GenerateVarianceJournalAsync(string sessionId);

    Task<StockCountAdjustmentJournal> GenerateAdjustmentJournalAsync(string sessionId);
}

public class StockCountService : IStockCountService
{
    private readonly PosDatabase _db;
    private readonly ICatalogService _catalog;

    public StockCountService(PosDatabase db, ICatalogService catalog)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<StockCountSession> StartSessionAsync(
        string branchId,
        string startedBy,
        string tenantId,
        List<string>? productIds = null,
        string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(branchId)) throw new ArgumentException("Branch ID is required.", nameof(branchId));
        if (string.IsNullOrWhiteSpace(startedBy)) throw new ArgumentException("Started by user ID is required.", nameof(startedBy));
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID is required.", nameof(tenantId));

        var sessionId = $"sc_{Guid.NewGuid():N}";
        var nowUtc = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            // 1. Fetch active products with frozen snapshot cutoff of stock_on_hand and cost_basis
            var productsToSnapshot = new List<Product>();
            using (var prodCmd = conn.CreateCommand())
            {
                prodCmd.Transaction = tx;
                if (productIds != null && productIds.Count > 0)
                {
                    var paramNames = new List<string>();
                    for (int i = 0; i < productIds.Count; i++)
                    {
                        var pName = $"$pid{i}";
                        paramNames.Add(pName);
                        prodCmd.Parameters.AddWithValue(pName, productIds[i]);
                    }
                    prodCmd.CommandText = $@"
                        SELECT product_id, barcode, name, unit_price, cost_basis, tax_rate, stock_on_hand, is_active, category_id, min_stock_threshold
                        FROM products
                        WHERE is_active = 1 AND product_id IN ({string.Join(",", paramNames)})
                        ORDER BY name ASC;
                    ";
                }
                else
                {
                    prodCmd.CommandText = @"
                        SELECT product_id, barcode, name, unit_price, cost_basis, tax_rate, stock_on_hand, is_active, category_id, min_stock_threshold
                        FROM products
                        WHERE is_active = 1
                        ORDER BY name ASC;
                    ";
                }

                using var reader = await prodCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    productsToSnapshot.Add(new Product
                    {
                        ProductId = reader.GetString(0),
                        Barcode = reader.GetString(1),
                        Name = reader.GetString(2),
                        UnitPrice = reader.GetDecimal(3),
                        CostBasis = reader.GetDecimal(4),
                        TaxRate = reader.GetDecimal(5),
                        StockOnHand = reader.GetDecimal(6),
                        IsActive = reader.GetInt32(7) == 1,
                        CategoryId = reader.IsDBNull(8) ? null : reader.GetString(8),
                        MinStockThreshold = reader.IsDBNull(9) ? 0.0m : reader.GetDecimal(9)
                    });
                }
            }

            if (productIds != null && productIds.Count > 0 && productsToSnapshot.Count == 0)
            {
                throw new PosException("None of the specified products were found in the active catalog.");
            }

            // 2. Insert session header
            using var sessCmd = conn.CreateCommand();
            sessCmd.Transaction = tx;
            sessCmd.CommandText = @"
                INSERT INTO stock_count_sessions (
                    session_id, tenant_id, branch_id, status, started_by, started_at_utc,
                    notes, total_items_counted, total_variance_quantity, total_variance_value, lines_with_variance_count
                ) VALUES (
                    $sid, $tid, $bid, 1, $startedBy, $startedAt,
                    $notes, 0, 0.0, 0.0, 0
                );
            ";
            sessCmd.Parameters.AddWithValue("$sid", sessionId);
            sessCmd.Parameters.AddWithValue("$tid", tenantId);
            sessCmd.Parameters.AddWithValue("$bid", branchId);
            sessCmd.Parameters.AddWithValue("$startedBy", startedBy);
            sessCmd.Parameters.AddWithValue("$startedAt", nowUtc.ToString("o"));
            sessCmd.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
            await sessCmd.ExecuteNonQueryAsync();

            // 3. Insert snapshot items (frozen movement cutoff)
            var sessionItems = new List<StockCountItem>();
            foreach (var p in productsToSnapshot)
            {
                var item = new StockCountItem
                {
                    ItemId = Guid.NewGuid(),
                    SessionId = sessionId,
                    ProductId = p.ProductId,
                    ProductName = p.Name,
                    Barcode = p.Barcode,
                    SnapshotStock = p.StockOnHand,
                    CountedQuantity = null,
                    VarianceQuantity = 0.0m,
                    CostBasis = p.CostBasis,
                    VarianceValue = 0.0m,
                    IsCounted = false,
                    CountedBy = null,
                    CountedAtUtc = null,
                    Notes = null
                };
                sessionItems.Add(item);

                using var itemCmd = conn.CreateCommand();
                itemCmd.Transaction = tx;
                itemCmd.CommandText = @"
                    INSERT INTO stock_count_items (
                        item_id, session_id, product_id, product_name, barcode,
                        snapshot_stock, counted_quantity, variance_quantity, cost_basis,
                        variance_value, is_counted, counted_by, counted_at_utc, notes
                    ) VALUES (
                        $iid, $sid, $pid, $pname, $bcode,
                        $snap, NULL, 0.0, $cost,
                        0.0, 0, NULL, NULL, NULL
                    );
                ";
                itemCmd.Parameters.AddWithValue("$iid", item.ItemId.ToString());
                itemCmd.Parameters.AddWithValue("$sid", sessionId);
                itemCmd.Parameters.AddWithValue("$pid", p.ProductId);
                itemCmd.Parameters.AddWithValue("$pname", p.Name);
                itemCmd.Parameters.AddWithValue("$bcode", p.Barcode);
                itemCmd.Parameters.AddWithValue("$snap", p.StockOnHand);
                itemCmd.Parameters.AddWithValue("$cost", p.CostBasis);
                await itemCmd.ExecuteNonQueryAsync();
            }

            // 4. Record audit event
            using var auditCmd = conn.CreateCommand();
            auditCmd.Transaction = tx;
            auditCmd.CommandText = @"
                INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                VALUES ($eid, $tid, $bid, 'MAIN', $actor, 'STOCK_COUNT_STARTED', $details, $occurred);
            ";
            auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            auditCmd.Parameters.AddWithValue("$tid", tenantId);
            auditCmd.Parameters.AddWithValue("$bid", branchId);
            auditCmd.Parameters.AddWithValue("$actor", startedBy);
            auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
            {
                SessionId = sessionId,
                ProductCount = sessionItems.Count,
                Notes = notes
            }));
            auditCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            await auditCmd.ExecuteNonQueryAsync();

            tx.Commit();

            return new StockCountSession
            {
                SessionId = sessionId,
                TenantId = tenantId,
                BranchId = branchId,
                Status = StockCountStatus.InProgress,
                StartedBy = startedBy,
                StartedAtUtc = nowUtc,
                Notes = notes,
                TotalItemsCounted = 0,
                TotalVarianceQuantity = 0m,
                TotalVarianceValue = 0m,
                LinesWithVarianceCount = 0,
                Items = sessionItems
            };
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    public async Task RecordCountItemAsync(
        string sessionId,
        string productId,
        decimal countedQuantity,
        string countedBy)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("Product ID is required.", nameof(productId));
        if (countedQuantity < 0) throw new ArgumentException("Counted quantity cannot be negative.", nameof(countedQuantity));
        if (string.IsNullOrWhiteSpace(countedBy)) throw new ArgumentException("Counted by user ID is required.", nameof(countedBy));

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            // 1. Verify session is InProgress
            using var sessCmd = conn.CreateCommand();
            sessCmd.Transaction = tx;
            sessCmd.CommandText = "SELECT status FROM stock_count_sessions WHERE session_id = $sid;";
            sessCmd.Parameters.AddWithValue("$sid", sessionId);

            StockCountStatus status;
            using (var sReader = await sessCmd.ExecuteReaderAsync())
            {
                if (!await sReader.ReadAsync())
                {
                    throw new KeyNotFoundException($"Stock count session '{sessionId}' was not found.");
                }
                status = (StockCountStatus)sReader.GetInt32(0);
            }

            if (status != StockCountStatus.InProgress)
            {
                throw new InvalidOperationException($"Cannot record counts for session '{sessionId}' because its status is '{status}'.");
            }

            // 2. Fetch or create line item
            using var itemCmd = conn.CreateCommand();
            itemCmd.Transaction = tx;
            itemCmd.CommandText = @"
                SELECT item_id, snapshot_stock, cost_basis
                FROM stock_count_items
                WHERE session_id = $sid AND product_id = $pid;
            ";
            itemCmd.Parameters.AddWithValue("$sid", sessionId);
            itemCmd.Parameters.AddWithValue("$pid", productId);

            Guid itemId;
            decimal snapshotStock;
            decimal costBasis;
            bool exists = false;

            using (var iReader = await itemCmd.ExecuteReaderAsync())
            {
                if (await iReader.ReadAsync())
                {
                    exists = true;
                    itemId = Guid.Parse(iReader.GetString(0));
                    snapshotStock = iReader.GetDecimal(1);
                    costBasis = iReader.GetDecimal(2);
                }
                else
                {
                    itemId = Guid.NewGuid();
                    snapshotStock = 0m;
                    costBasis = 0m;
                }
            }

            if (!exists)
            {
                using var prodCmd = conn.CreateCommand();
                prodCmd.Transaction = tx;
                prodCmd.CommandText = "SELECT name, barcode, stock_on_hand, cost_basis FROM products WHERE product_id = $pid;";
                prodCmd.Parameters.AddWithValue("$pid", productId);
                using var pReader = await prodCmd.ExecuteReaderAsync();
                if (!await pReader.ReadAsync())
                {
                    throw new KeyNotFoundException($"Product '{productId}' was not found in catalog.");
                }
                var pName = pReader.GetString(0);
                var pBarcode = pReader.GetString(1);
                snapshotStock = pReader.GetDecimal(2);
                costBasis = pReader.GetDecimal(3);
                pReader.Close();

                using var insCmd = conn.CreateCommand();
                insCmd.Transaction = tx;
                insCmd.CommandText = @"
                    INSERT INTO stock_count_items (
                        item_id, session_id, product_id, product_name, barcode,
                        snapshot_stock, counted_quantity, variance_quantity, cost_basis,
                        variance_value, is_counted, counted_by, counted_at_utc, notes
                    ) VALUES (
                        $iid, $sid, $pid, $pname, $bcode,
                        $snap, NULL, 0.0, $cost,
                        0.0, 0, NULL, NULL, NULL
                    );
                ";
                insCmd.Parameters.AddWithValue("$iid", itemId.ToString());
                insCmd.Parameters.AddWithValue("$sid", sessionId);
                insCmd.Parameters.AddWithValue("$pid", productId);
                insCmd.Parameters.AddWithValue("$pname", pName);
                insCmd.Parameters.AddWithValue("$bcode", pBarcode);
                insCmd.Parameters.AddWithValue("$snap", snapshotStock);
                insCmd.Parameters.AddWithValue("$cost", costBasis);
                await insCmd.ExecuteNonQueryAsync();
            }

            // 3. Compute variance and rounded variance value (MidpointRounding.AwayFromZero)
            var roundedCountedQty = MoneyCalculator.Round(countedQuantity);
            var varianceQty = roundedCountedQty - snapshotStock;
            var varianceValue = MoneyCalculator.Round(varianceQty * costBasis);
            var nowUtc = DateTime.UtcNow;

            // 4. Update line item
            using var updCmd = conn.CreateCommand();
            updCmd.Transaction = tx;
            updCmd.CommandText = @"
                UPDATE stock_count_items SET
                    counted_quantity = $cqty,
                    variance_quantity = $vqty,
                    variance_value = $vval,
                    is_counted = 1,
                    counted_by = $cby,
                    counted_at_utc = $cat
                WHERE session_id = $sid AND product_id = $pid;
            ";
            updCmd.Parameters.AddWithValue("$cqty", roundedCountedQty);
            updCmd.Parameters.AddWithValue("$vqty", varianceQty);
            updCmd.Parameters.AddWithValue("$vval", varianceValue);
            updCmd.Parameters.AddWithValue("$cby", countedBy);
            updCmd.Parameters.AddWithValue("$cat", nowUtc.ToString("o"));
            updCmd.Parameters.AddWithValue("$sid", sessionId);
            updCmd.Parameters.AddWithValue("$pid", productId);
            await updCmd.ExecuteNonQueryAsync();

            // 5. Update session summary stats
            using var sumCmd = conn.CreateCommand();
            sumCmd.Transaction = tx;
            sumCmd.CommandText = @"
                SELECT
                    COUNT(CASE WHEN is_counted = 1 THEN 1 END),
                    COALESCE(SUM(variance_quantity), 0.0),
                    COALESCE(SUM(variance_value), 0.0),
                    COUNT(CASE WHEN is_counted = 1 AND variance_quantity != 0 THEN 1 END)
                FROM stock_count_items
                WHERE session_id = $sid;
            ";
            sumCmd.Parameters.AddWithValue("$sid", sessionId);

            int totalCounted = 0;
            decimal totalVarQty = 0m;
            decimal totalVarVal = 0m;
            int varLinesCount = 0;

            using (var sumReader = await sumCmd.ExecuteReaderAsync())
            {
                if (await sumReader.ReadAsync())
                {
                    totalCounted = sumReader.GetInt32(0);
                    totalVarQty = sumReader.GetDecimal(1);
                    totalVarVal = MoneyCalculator.Round(sumReader.GetDecimal(2));
                    varLinesCount = sumReader.GetInt32(3);
                }
            }

            using var sessUpdCmd = conn.CreateCommand();
            sessUpdCmd.Transaction = tx;
            sessUpdCmd.CommandText = @"
                UPDATE stock_count_sessions SET
                    total_items_counted = $cnt,
                    total_variance_quantity = $vqty,
                    total_variance_value = $vval,
                    lines_with_variance_count = $vlcnt
                WHERE session_id = $sid;
            ";
            sessUpdCmd.Parameters.AddWithValue("$cnt", totalCounted);
            sessUpdCmd.Parameters.AddWithValue("$vqty", totalVarQty);
            sessUpdCmd.Parameters.AddWithValue("$vval", totalVarVal);
            sessUpdCmd.Parameters.AddWithValue("$vlcnt", varLinesCount);
            sessUpdCmd.Parameters.AddWithValue("$sid", sessionId);
            await sessUpdCmd.ExecuteNonQueryAsync();

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    public async Task<StockCountSession> CompleteSessionAsync(
        string sessionId,
        string completedBy)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(completedBy)) throw new ArgumentException("Completed by user ID is required.", nameof(completedBy));

        var session = await GetSessionByIdAsync(sessionId);
        if (session == null)
        {
            throw new KeyNotFoundException($"Stock count session '{sessionId}' was not found.");
        }

        if (session.Status != StockCountStatus.InProgress)
        {
            throw new InvalidOperationException($"Cannot complete stock count session '{sessionId}' with status '{session.Status}'.");
        }

        // Validate actor role: Owner or Manager required for stock adjustments (A09)
        var actor = await GetUserAsync(completedBy);
        if (actor == null)
        {
            throw new KeyNotFoundException($"User '{completedBy}' was not found.");
        }
        if (actor.Role != Role.Owner && actor.Role != Role.Manager)
        {
            throw new UnauthorizedActionException(
                $"Cashier role '{actor.Username}' is not authorized to complete stock counts. Owner or Manager authorization required."
            );
        }

        // Post stock adjustments via CatalogService.AdjustStockAsync for non-zero variances
        var varianceLines = session.Items
            .Where(i => i.IsCounted && i.VarianceQuantity != 0)
            .ToList();

        foreach (var item in varianceLines)
        {
            await _catalog.AdjustStockAsync(
                productId: item.ProductId,
                quantityChange: item.VarianceQuantity,
                reason: $"STOCK_COUNT_ADJUSTMENT_{session.SessionId}",
                actor: actor,
                tenantId: session.TenantId,
                branchId: session.BranchId,
                counterId: "MAIN",
                authorizer: actor
            );
        }

        var nowUtc = DateTime.UtcNow;
        using (var conn = _db.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                // Update session record to Completed
                using var updCmd = conn.CreateCommand();
                updCmd.Transaction = tx;
                updCmd.CommandText = @"
                    UPDATE stock_count_sessions SET
                        status = 2,
                        completed_by = $cby,
                        completed_at_utc = $cat
                    WHERE session_id = $sid;
                ";
                updCmd.Parameters.AddWithValue("$cby", actor.UserId);
                updCmd.Parameters.AddWithValue("$cat", nowUtc.ToString("o"));
                updCmd.Parameters.AddWithValue("$sid", sessionId);
                await updCmd.ExecuteNonQueryAsync();

                // Audit event
                using var auditCmd = conn.CreateCommand();
                auditCmd.Transaction = tx;
                auditCmd.CommandText = @"
                    INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                    VALUES ($eid, $tid, $bid, 'MAIN', $actor, 'STOCK_COUNT_COMPLETED', $details, $occurred);
                ";
                auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
                auditCmd.Parameters.AddWithValue("$tid", session.TenantId);
                auditCmd.Parameters.AddWithValue("$bid", session.BranchId);
                auditCmd.Parameters.AddWithValue("$actor", actor.UserId);
                auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
                {
                    SessionId = sessionId,
                    CompletedBy = actor.Username,
                    AdjustedItemsCount = varianceLines.Count,
                    TotalVarianceQuantity = session.TotalVarianceQuantity,
                    TotalVarianceValue = session.TotalVarianceValue
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
                seqCmd.Parameters.AddWithValue("$bid", session.BranchId);
                var nextSeq = Convert.ToInt64(await seqCmd.ExecuteScalarAsync());

                var outboxPayload = JsonSerializer.Serialize(new
                {
                    session_id = session.SessionId,
                    tenant_id = session.TenantId,
                    branch_id = session.BranchId,
                    status = "Completed",
                    started_by = session.StartedBy,
                    started_at_utc = session.StartedAtUtc.ToString("o"),
                    completed_by = actor.UserId,
                    completed_at_utc = nowUtc.ToString("o"),
                    total_items_counted = session.TotalItemsCounted,
                    total_variance_quantity = $"{session.TotalVarianceQuantity:F2}",
                    total_variance_value = $"{session.TotalVarianceValue:F2}",
                    lines_with_variance_count = session.LinesWithVarianceCount,
                    lines = session.Items.Where(i => i.IsCounted).Select(i => new
                    {
                        product_id = i.ProductId,
                        product_name = i.ProductName,
                        barcode = i.Barcode,
                        snapshot_stock = $"{i.SnapshotStock:F2}",
                        counted_quantity = $"{i.CountedQuantity:F2}",
                        variance_quantity = $"{i.VarianceQuantity:F2}",
                        cost_basis = $"{i.CostBasis:F2}",
                        variance_value = $"{i.VarianceValue:F2}"
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
                outboxCmd.Parameters.AddWithValue("$tid", session.TenantId);
                outboxCmd.Parameters.AddWithValue("$bid", session.BranchId);
                outboxCmd.Parameters.AddWithValue("$seq", nextSeq);
                outboxCmd.Parameters.AddWithValue("$payload", outboxPayload);
                outboxCmd.Parameters.AddWithValue("$actor", actor.UserId);
                outboxCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
                outboxCmd.Parameters.AddWithValue("$causal", session.SessionId);
                outboxCmd.Parameters.AddWithValue("$created", nowUtc.ToString("o"));
                await outboxCmd.ExecuteNonQueryAsync();

                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

        session.Status = StockCountStatus.Completed;
        session.CompletedBy = actor.UserId;
        session.CompletedAtUtc = nowUtc;
        return session;
    }

    public async Task<StockCountSession> CancelSessionAsync(
        string sessionId,
        string cancelledBy,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(cancelledBy)) throw new ArgumentException("Cancelled by user ID is required.", nameof(cancelledBy));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Cancellation reason is required.", nameof(reason));

        var session = await GetSessionByIdAsync(sessionId);
        if (session == null)
        {
            throw new KeyNotFoundException($"Stock count session '{sessionId}' was not found.");
        }

        if (session.Status != StockCountStatus.InProgress)
        {
            throw new InvalidOperationException($"Cannot cancel stock count session '{sessionId}' with status '{session.Status}'.");
        }

        var actor = await GetUserAsync(cancelledBy);
        var actorId = actor?.UserId ?? cancelledBy;
        var nowUtc = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            using var updCmd = conn.CreateCommand();
            updCmd.Transaction = tx;
            updCmd.CommandText = @"
                UPDATE stock_count_sessions SET
                    status = 3,
                    cancelled_by = $cby,
                    cancelled_at_utc = $cat,
                    cancellation_reason = $reason
                WHERE session_id = $sid;
            ";
            updCmd.Parameters.AddWithValue("$cby", actorId);
            updCmd.Parameters.AddWithValue("$cat", nowUtc.ToString("o"));
            updCmd.Parameters.AddWithValue("$reason", reason.Trim());
            updCmd.Parameters.AddWithValue("$sid", sessionId);
            await updCmd.ExecuteNonQueryAsync();

            // Audit event
            using var auditCmd = conn.CreateCommand();
            auditCmd.Transaction = tx;
            auditCmd.CommandText = @"
                INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                VALUES ($eid, $tid, $bid, 'MAIN', $actor, 'STOCK_COUNT_CANCELLED', $details, $occurred);
            ";
            auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            auditCmd.Parameters.AddWithValue("$tid", session.TenantId);
            auditCmd.Parameters.AddWithValue("$bid", session.BranchId);
            auditCmd.Parameters.AddWithValue("$actor", actorId);
            auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
            {
                SessionId = sessionId,
                Reason = reason
            }));
            auditCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            await auditCmd.ExecuteNonQueryAsync();

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }

        session.Status = StockCountStatus.Cancelled;
        session.CancelledBy = actorId;
        session.CancelledAtUtc = nowUtc;
        session.CancellationReason = reason;
        return session;
    }

    public async Task<StockCountSession?> GetSessionByIdAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT session_id, tenant_id, branch_id, status, started_by, started_at_utc,
                   completed_by, completed_at_utc, cancelled_by, cancelled_at_utc,
                   cancellation_reason, notes, total_items_counted, total_variance_quantity,
                   total_variance_value, lines_with_variance_count
            FROM stock_count_sessions
            WHERE session_id = $sid;
        ";
        cmd.Parameters.AddWithValue("$sid", sessionId);

        StockCountSession? session = null;
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return null;
            session = MapSession(reader);
        }

        // Load items
        using var itemCmd = conn.CreateCommand();
        itemCmd.CommandText = @"
            SELECT item_id, session_id, product_id, product_name, barcode,
                   snapshot_stock, counted_quantity, variance_quantity, cost_basis,
                   variance_value, is_counted, counted_by, counted_at_utc, notes
            FROM stock_count_items
            WHERE session_id = $sid
            ORDER BY product_name ASC;
        ";
        itemCmd.Parameters.AddWithValue("$sid", sessionId);

        using (var iReader = await itemCmd.ExecuteReaderAsync())
        {
            while (await iReader.ReadAsync())
            {
                session.Items.Add(MapItem(iReader));
            }
        }

        return session;
    }

    public async Task<List<StockCountSession>> GetSessionsAsync(
        string? branchId = null,
        StockCountStatus? status = null)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();

        var query = @"
            SELECT session_id, tenant_id, branch_id, status, started_by, started_at_utc,
                   completed_by, completed_at_utc, cancelled_by, cancelled_at_utc,
                   cancellation_reason, notes, total_items_counted, total_variance_quantity,
                   total_variance_value, lines_with_variance_count
            FROM stock_count_sessions
            WHERE 1=1
        ";

        if (!string.IsNullOrEmpty(branchId))
        {
            query += " AND branch_id = $bid";
            cmd.Parameters.AddWithValue("$bid", branchId);
        }
        if (status.HasValue)
        {
            query += " AND status = $st";
            cmd.Parameters.AddWithValue("$st", (int)status.Value);
        }

        query += " ORDER BY started_at_utc DESC;";
        cmd.CommandText = query;

        var list = new List<StockCountSession>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(MapSession(reader));
        }
        return list;
    }

    public async Task<StockCountAdjustmentJournal> GenerateVarianceJournalAsync(string sessionId)
    {
        var session = await GetSessionByIdAsync(sessionId);
        if (session == null)
        {
            throw new KeyNotFoundException($"Stock count session '{sessionId}' was not found.");
        }

        var journal = new StockCountAdjustmentJournal
        {
            SessionId = session.SessionId,
            TenantId = session.TenantId,
            BranchId = session.BranchId,
            ExecutedAtUtc = session.CompletedAtUtc ?? DateTime.UtcNow,
            ExecutedBy = session.CompletedBy ?? session.StartedBy,
            TotalVarianceQuantity = session.TotalVarianceQuantity,
            TotalVarianceValue = session.TotalVarianceValue,
            Lines = session.Items
                .Where(i => i.IsCounted && i.VarianceQuantity != 0)
                .Select(i => new StockCountJournalLine
                {
                    ProductId = i.ProductId,
                    ProductName = i.ProductName,
                    Barcode = i.Barcode,
                    SnapshotStock = i.SnapshotStock,
                    CountedQuantity = i.CountedQuantity ?? 0m,
                    VarianceQuantity = i.VarianceQuantity,
                    CostBasis = i.CostBasis,
                    VarianceValue = i.VarianceValue,
                    AdjustmentReason = "STOCK_COUNT_ADJUSTMENT"
                })
                .ToList()
        };

        return journal;
    }

    public Task<StockCountAdjustmentJournal> GenerateAdjustmentJournalAsync(string sessionId)
    {
        return GenerateVarianceJournalAsync(sessionId);
    }

    private static StockCountSession MapSession(SqliteDataReader reader)
    {
        return new StockCountSession
        {
            SessionId = reader.GetString(0),
            TenantId = reader.GetString(1),
            BranchId = reader.GetString(2),
            Status = (StockCountStatus)reader.GetInt32(3),
            StartedBy = reader.GetString(4),
            StartedAtUtc = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            CompletedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
            CompletedAtUtc = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            CancelledBy = reader.IsDBNull(8) ? null : reader.GetString(8),
            CancelledAtUtc = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            CancellationReason = reader.IsDBNull(10) ? null : reader.GetString(10),
            Notes = reader.IsDBNull(11) ? null : reader.GetString(11),
            TotalItemsCounted = reader.GetInt32(12),
            TotalVarianceQuantity = reader.GetDecimal(13),
            TotalVarianceValue = reader.GetDecimal(14),
            LinesWithVarianceCount = reader.GetInt32(15)
        };
    }

    private static StockCountItem MapItem(SqliteDataReader reader)
    {
        return new StockCountItem
        {
            ItemId = Guid.Parse(reader.GetString(0)),
            SessionId = reader.GetString(1),
            ProductId = reader.GetString(2),
            ProductName = reader.GetString(3),
            Barcode = reader.GetString(4),
            SnapshotStock = reader.GetDecimal(5),
            CountedQuantity = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            VarianceQuantity = reader.GetDecimal(7),
            CostBasis = reader.GetDecimal(8),
            VarianceValue = reader.GetDecimal(9),
            IsCounted = reader.GetInt32(10) == 1,
            CountedBy = reader.IsDBNull(11) ? null : reader.GetString(11),
            CountedAtUtc = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            Notes = reader.IsDBNull(13) ? null : reader.GetString(13)
        };
    }

    private async Task<User?> GetUserAsync(string userIdOrUsername)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT user_id, username, display_name, role, password_hash, password_salt, is_active, created_at_utc, pin_hash, pin_salt
            FROM users
            WHERE user_id = $id OR username = $uname;
        ";
        var norm = userIdOrUsername.Trim();
        cmd.Parameters.AddWithValue("$id", norm);
        cmd.Parameters.AddWithValue("$uname", norm.ToLowerInvariant());

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new User
        {
            UserId = reader.GetString(0),
            Username = reader.GetString(1),
            DisplayName = reader.GetString(2),
            Role = (Role)reader.GetInt32(3),
            PasswordHash = reader.GetString(4),
            PasswordSalt = reader.GetString(5),
            IsActive = reader.GetInt32(6) == 1,
            CreatedAtUtc = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            PinHash = reader.IsDBNull(8) ? null : reader.GetString(8),
            PinSalt = reader.IsDBNull(9) ? null : reader.GetString(9)
        };
    }
}
