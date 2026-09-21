using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public record CreateSaleLineRequest(
    string ProductId,
    decimal Quantity,
    decimal? PriceOverride = null,
    string? OverrideReason = null,
    decimal DiscountRate = 0.0m,
    decimal DiscountFixed = 0.0m
);

public record CreateTenderRequest(
    TenderType TenderType,
    decimal AmountTendered,
    string? PaymentReference = null
);

public record CreateSaleCommand(
    string TenantId,
    string BranchId,
    string CounterId,
    string CashierId,
    Guid ShiftId,
    List<CreateSaleLineRequest> Items,
    List<CreateTenderRequest> Tenders,
    string? CustomerId = null
);

public interface ISaleService
{
    Task<Sale> CommitSaleAsync(
        CreateSaleCommand command,
        Func<SqliteConnection, SqliteTransaction, Task>? failureHook = null
    );
    Task<Sale?> GetSaleByIdAsync(Guid saleId);
    Task<int> GetSaleCountAsync();
}

public class SaleService : ISaleService
{
    private readonly PosDatabase _db;
    private readonly ICatalogService _catalog;

    public SaleService(PosDatabase db, ICatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<Sale> CommitSaleAsync(
        CreateSaleCommand command,
        Func<SqliteConnection, SqliteTransaction, Task>? failureHook = null)
    {
        if (command.Items == null || command.Items.Count == 0)
        {
            throw new PosException("Cannot commit a sale with no items.");
        }
        if (command.Tenders == null || command.Tenders.Count == 0)
        {
            throw new PosException("Cannot commit a sale with no tenders.");
        }

        // 1. Calculate items and snapshots
        var saleLines = new List<SaleLine>();
        var stockMovements = new List<StockMovement>();

        foreach (var item in command.Items)
        {
            var product = await _catalog.GetProductByIdAsync(item.ProductId);
            if (product == null)
            {
                throw new PosException($"Product '{item.ProductId}' not found.");
            }

            var unitPrice = item.PriceOverride ?? product.UnitPrice;
            if (item.PriceOverride.HasValue && item.PriceOverride.Value != product.UnitPrice && string.IsNullOrWhiteSpace(item.OverrideReason))
            {
                throw new ArgumentException("A reason is required when overriding product price (A08).");
            }

            var calc = MoneyCalculator.CalculateLine(
                item.Quantity,
                unitPrice,
                item.DiscountRate,
                item.DiscountFixed,
                product.TaxRate
            );

            var line = new SaleLine
            {
                LineId = Guid.NewGuid(),
                ProductId = product.ProductId,
                ProductName = product.Name,
                Barcode = product.Barcode,
                Quantity = item.Quantity,
                UnitPrice = unitPrice,
                DiscountRate = item.DiscountRate,
                DiscountFixed = item.DiscountFixed,
                DiscountAmount = calc.DiscountAmount,
                TaxRate = product.TaxRate,
                TaxAmount = calc.TaxAmount,
                LineTotal = calc.LineTotal
            };
            saleLines.Add(line);

            stockMovements.Add(new StockMovement
            {
                MovementId = Guid.NewGuid(),
                ProductId = product.ProductId,
                MovementType = "SALE",
                QuantityChange = -item.Quantity,
                ReferenceId = "" // Will be populated with SaleId
            });
        }

        var subtotal = MoneyCalculator.Round(saleLines.Sum(l => l.Quantity * l.UnitPrice));
        var discountTotal = MoneyCalculator.Round(saleLines.Sum(l => l.DiscountAmount));
        var taxTotal = MoneyCalculator.Round(saleLines.Sum(l => l.TaxAmount));
        var grandTotal = MoneyCalculator.Round(saleLines.Sum(l => l.LineTotal));

        // 2. Validate tenders and calculate change
        var totalTendered = MoneyCalculator.Round(command.Tenders.Sum(t => t.AmountTendered));
        if (totalTendered < grandTotal)
        {
            throw new InsufficientTenderException(
                $"Total tendered ({totalTendered:F2}) is less than grand total ({grandTotal:F2})."
            );
        }

        var remainingToCover = grandTotal;
        var tenderEntities = new List<Tender>();
        decimal totalCashTendered = 0m;
        decimal totalCashChange = 0m;

        foreach (var t in command.Tenders)
        {
            var amount = MoneyCalculator.Round(t.AmountTendered);
            var change = 0m;

            if (t.TenderType == TenderType.CASH)
            {
                totalCashTendered += amount;
                if (amount > remainingToCover)
                {
                    change = amount - remainingToCover;
                    remainingToCover = 0m;
                }
                else
                {
                    remainingToCover -= amount;
                }
                totalCashChange += change;
            }
            else
            {
                // Non-cash tenders (card, QR, credit) do not return cash change
                if (amount > remainingToCover)
                {
                    throw new PosException("Non-cash tenders cannot exceed outstanding balance.");
                }
                remainingToCover -= amount;
            }

            tenderEntities.Add(new Tender
            {
                TenderId = Guid.NewGuid(),
                TenderType = t.TenderType,
                AmountTendered = amount,
                ChangeGiven = change,
                PaymentReference = t.PaymentReference
            });
        }

        // 3. Begin atomic local SQLite transaction
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            // Verify shift is open
            using var shiftCmd = conn.CreateCommand();
            shiftCmd.Transaction = tx;
            shiftCmd.CommandText = "SELECT status FROM shifts WHERE shift_id = $sid;";
            shiftCmd.Parameters.AddWithValue("$sid", command.ShiftId.ToString());
            var shiftStatusObj = await shiftCmd.ExecuteScalarAsync();
            if (shiftStatusObj == null || Convert.ToInt32(shiftStatusObj) != 1)
            {
                throw new ShiftClosedException("Cannot commit sale: shift is not open.");
            }

            // Allocate sequential receipt number atomically
            using var seqCmd = conn.CreateCommand();
            seqCmd.Transaction = tx;
            seqCmd.CommandText = @"
                INSERT INTO receipt_sequences (branch_id, counter_id, last_sequence)
                VALUES ($bid, $cid, 1)
                ON CONFLICT(branch_id, counter_id) DO UPDATE SET last_sequence = last_sequence + 1
                RETURNING last_sequence;
            ";
            seqCmd.Parameters.AddWithValue("$bid", command.BranchId);
            seqCmd.Parameters.AddWithValue("$cid", command.CounterId);
            var nextSeq = Convert.ToInt64(await seqCmd.ExecuteScalarAsync());

            var bPrefix = command.BranchId.StartsWith("B", StringComparison.OrdinalIgnoreCase) ? "" : "B";
            var cPrefix = command.CounterId.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? "" : "C";
            var receiptNumber = $"{bPrefix}{command.BranchId}-{cPrefix}{command.CounterId}-{nextSeq:D6}";

            var saleId = Guid.NewGuid();
            var nowUtc = DateTime.UtcNow;

            var sale = new Sale
            {
                SaleId = saleId,
                ReceiptNumber = receiptNumber,
                ShiftId = command.ShiftId,
                TenantId = command.TenantId,
                BranchId = command.BranchId,
                CounterId = command.CounterId,
                CashierId = command.CashierId,
                CustomerId = command.CustomerId,
                Subtotal = subtotal,
                DiscountTotal = discountTotal,
                TaxTotal = taxTotal,
                GrandTotal = grandTotal,
                Status = SaleStatus.Completed,
                ReprintCount = 0,
                CreatedAtUtc = nowUtc,
                Lines = saleLines,
                Tenders = tenderEntities
            };

            // Insert sale header
            using var saleCmd = conn.CreateCommand();
            saleCmd.Transaction = tx;
            saleCmd.CommandText = @"
                INSERT INTO sales (
                    sale_id, receipt_number, shift_id, tenant_id, branch_id, counter_id,
                    cashier_id, customer_id, subtotal, discount_total, tax_total, grand_total,
                    status, reprint_count, created_at_utc
                ) VALUES (
                    $id, $rcpt, $sid, $tid, $bid, $cid, $uid, $cust, $sub, $disc, $tax, $grand, 1, 0, $created
                );
            ";
            saleCmd.Parameters.AddWithValue("$id", sale.SaleId.ToString());
            saleCmd.Parameters.AddWithValue("$rcpt", sale.ReceiptNumber);
            saleCmd.Parameters.AddWithValue("$sid", sale.ShiftId.ToString());
            saleCmd.Parameters.AddWithValue("$tid", sale.TenantId);
            saleCmd.Parameters.AddWithValue("$bid", sale.BranchId);
            saleCmd.Parameters.AddWithValue("$cid", sale.CounterId);
            saleCmd.Parameters.AddWithValue("$uid", sale.CashierId);
            saleCmd.Parameters.AddWithValue("$cust", (object?)sale.CustomerId ?? DBNull.Value);
            saleCmd.Parameters.AddWithValue("$sub", sale.Subtotal);
            saleCmd.Parameters.AddWithValue("$disc", sale.DiscountTotal);
            saleCmd.Parameters.AddWithValue("$tax", sale.TaxTotal);
            saleCmd.Parameters.AddWithValue("$grand", sale.GrandTotal);
            saleCmd.Parameters.AddWithValue("$created", sale.CreatedAtUtc.ToString("o"));
            await saleCmd.ExecuteNonQueryAsync();

            // Insert sale lines
            foreach (var line in saleLines)
            {
                line.SaleId = saleId;
                using var lineCmd = conn.CreateCommand();
                lineCmd.Transaction = tx;
                lineCmd.CommandText = @"
                    INSERT INTO sale_lines (
                        line_id, sale_id, product_id, product_name, barcode, quantity, unit_price,
                        discount_rate, discount_fixed, discount_amount, tax_rate, tax_amount, line_total
                    ) VALUES (
                        $lid, $sid, $pid, $pname, $bcode, $qty, $uprice, $drate, $dfix, $damt, $trate, $tamt, $ltot
                    );
                ";
                lineCmd.Parameters.AddWithValue("$lid", line.LineId.ToString());
                lineCmd.Parameters.AddWithValue("$sid", saleId.ToString());
                lineCmd.Parameters.AddWithValue("$pid", line.ProductId);
                lineCmd.Parameters.AddWithValue("$pname", line.ProductName);
                lineCmd.Parameters.AddWithValue("$bcode", line.Barcode);
                lineCmd.Parameters.AddWithValue("$qty", line.Quantity);
                lineCmd.Parameters.AddWithValue("$uprice", line.UnitPrice);
                lineCmd.Parameters.AddWithValue("$drate", line.DiscountRate);
                lineCmd.Parameters.AddWithValue("$dfix", line.DiscountFixed);
                lineCmd.Parameters.AddWithValue("$damt", line.DiscountAmount);
                lineCmd.Parameters.AddWithValue("$trate", line.TaxRate);
                lineCmd.Parameters.AddWithValue("$tamt", line.TaxAmount);
                lineCmd.Parameters.AddWithValue("$ltot", line.LineTotal);
                await lineCmd.ExecuteNonQueryAsync();
            }

            // Insert tenders
            foreach (var t in tenderEntities)
            {
                t.SaleId = saleId;
                using var tCmd = conn.CreateCommand();
                tCmd.Transaction = tx;
                tCmd.CommandText = @"
                    INSERT INTO tenders (tender_id, sale_id, tender_type, amount_tendered, change_given, payment_reference)
                    VALUES ($tid, $sid, $ttype, $amt, $chg, $pref);
                ";
                tCmd.Parameters.AddWithValue("$tid", t.TenderId.ToString());
                tCmd.Parameters.AddWithValue("$sid", saleId.ToString());
                tCmd.Parameters.AddWithValue("$ttype", t.TenderType.ToString());
                tCmd.Parameters.AddWithValue("$amt", t.AmountTendered);
                tCmd.Parameters.AddWithValue("$chg", t.ChangeGiven);
                tCmd.Parameters.AddWithValue("$pref", (object?)t.PaymentReference ?? DBNull.Value);
                await tCmd.ExecuteNonQueryAsync();
            }

            // Record stock movements and update stock on hand
            foreach (var sm in stockMovements)
            {
                sm.ReferenceId = saleId.ToString();
                using var smCmd = conn.CreateCommand();
                smCmd.Transaction = tx;
                smCmd.CommandText = @"
                    INSERT INTO stock_movements (movement_id, product_id, movement_type, quantity_change, reference_id, occurred_at_utc)
                    VALUES ($mid, $pid, 'SALE', $qchange, $ref, $occurred);

                    UPDATE products SET stock_on_hand = stock_on_hand + $qchange WHERE product_id = $pid;
                ";
                smCmd.Parameters.AddWithValue("$mid", sm.MovementId.ToString());
                smCmd.Parameters.AddWithValue("$pid", sm.ProductId);
                smCmd.Parameters.AddWithValue("$qchange", sm.QuantityChange);
                smCmd.Parameters.AddWithValue("$ref", sm.ReferenceId);
                smCmd.Parameters.AddWithValue("$occurred", sm.OccurredAtUtc.ToString("o"));
                await smCmd.ExecuteNonQueryAsync();
            }

            // Update shift cash totals if cash tender exists
            if (totalCashTendered > 0)
            {
                using var shiftUpdCmd = conn.CreateCommand();
                shiftUpdCmd.Transaction = tx;
                shiftUpdCmd.CommandText = @"
                    UPDATE shifts SET
                        cash_received = cash_received + $rec,
                        change_given = change_given + $chg
                    WHERE shift_id = $sid;
                ";
                shiftUpdCmd.Parameters.AddWithValue("$rec", totalCashTendered);
                shiftUpdCmd.Parameters.AddWithValue("$chg", totalCashChange);
                shiftUpdCmd.Parameters.AddWithValue("$sid", command.ShiftId.ToString());
                await shiftUpdCmd.ExecuteNonQueryAsync();
            }

            // Record audit event
            using var auditCmd = conn.CreateCommand();
            auditCmd.Transaction = tx;
            auditCmd.CommandText = @"
                INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                VALUES ($eid, $tid, $bid, $cid, $actor, 'COMMIT_SALE', $details, $occurred);
            ";
            auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            auditCmd.Parameters.AddWithValue("$tid", command.TenantId);
            auditCmd.Parameters.AddWithValue("$bid", command.BranchId);
            auditCmd.Parameters.AddWithValue("$cid", command.CounterId);
            auditCmd.Parameters.AddWithValue("$actor", command.CashierId);
            auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
            {
                SaleId = saleId,
                ReceiptNumber = receiptNumber,
                GrandTotal = grandTotal,
                LinesCount = saleLines.Count
            }));
            auditCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            await auditCmd.ExecuteNonQueryAsync();

            // Record outbox event for sync
            var outboxPayload = JsonSerializer.Serialize(new
            {
                sale_id = saleId,
                receipt_number = receiptNumber,
                shift_id = command.ShiftId,
                customer_id = command.CustomerId,
                subtotal = $"{subtotal:F2}",
                discount_total = $"{discountTotal:F2}",
                tax_total = $"{taxTotal:F2}",
                grand_total = $"{grandTotal:F2}",
                lines = saleLines.Select(l => new
                {
                    line_id = l.LineId,
                    product_id = l.ProductId,
                    product_name = l.ProductName,
                    barcode = l.Barcode,
                    quantity = $"{l.Quantity:F2}",
                    unit_price = $"{l.UnitPrice:F2}",
                    discount_rate = $"{l.DiscountRate:F4}",
                    discount_fixed = $"{l.DiscountFixed:F2}",
                    tax_rate = $"{l.TaxRate:F4}",
                    line_total = $"{l.LineTotal:F2}"
                }),
                tenders = tenderEntities.Select(t => new
                {
                    tender_id = t.TenderId,
                    tender_type = t.TenderType.ToString(),
                    amount_tendered = $"{t.AmountTendered:F2}",
                    change_given = $"{t.ChangeGiven:F2}",
                    payment_reference = t.PaymentReference
                })
            });

            using var outboxCmd = conn.CreateCommand();
            outboxCmd.Transaction = tx;
            outboxCmd.CommandText = @"
                INSERT INTO outbox_events (
                    event_id, tenant_id, branch_id, device_id, device_generation,
                    source_sequence, schema_version, payload_json, status, created_at_utc
                ) VALUES (
                    $eid, $tid, $bid, $did, 1, $seq, '1.0', $payload, 'PENDING', $created
                );
            ";
            outboxCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            outboxCmd.Parameters.AddWithValue("$tid", command.TenantId);
            outboxCmd.Parameters.AddWithValue("$bid", command.BranchId);
            outboxCmd.Parameters.AddWithValue("$did", command.CounterId);
            outboxCmd.Parameters.AddWithValue("$seq", nextSeq);
            outboxCmd.Parameters.AddWithValue("$payload", outboxPayload);
            outboxCmd.Parameters.AddWithValue("$created", nowUtc.ToString("o"));
            await outboxCmd.ExecuteNonQueryAsync();

            // Hook for testing crash recovery / mid-transaction failures (A02)
            if (failureHook != null)
            {
                await failureHook(conn, tx);
            }

            tx.Commit();
            return sale;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    public async Task<Sale?> GetSaleByIdAsync(Guid saleId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT sale_id, receipt_number, shift_id, tenant_id, branch_id, counter_id,
                   cashier_id, customer_id, subtotal, discount_total, tax_total, grand_total,
                   status, reprint_count, created_at_utc
            FROM sales WHERE sale_id = $id;
        ";
        cmd.Parameters.AddWithValue("$id", saleId.ToString());

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var sale = new Sale
        {
            SaleId = Guid.Parse(reader.GetString(0)),
            ReceiptNumber = reader.GetString(1),
            ShiftId = Guid.Parse(reader.GetString(2)),
            TenantId = reader.GetString(3),
            BranchId = reader.GetString(4),
            CounterId = reader.GetString(5),
            CashierId = reader.GetString(6),
            CustomerId = reader.IsDBNull(7) ? null : reader.GetString(7),
            Subtotal = reader.GetDecimal(8),
            DiscountTotal = reader.GetDecimal(9),
            TaxTotal = reader.GetDecimal(10),
            GrandTotal = reader.GetDecimal(11),
            Status = (SaleStatus)reader.GetInt32(12),
            ReprintCount = reader.GetInt32(13),
            CreatedAtUtc = DateTime.Parse(reader.GetString(14))
        };

        // Fetch lines
        using var lineCmd = conn.CreateCommand();
        lineCmd.CommandText = @"
            SELECT line_id, product_id, product_name, barcode, quantity, unit_price,
                   discount_rate, discount_fixed, discount_amount, tax_rate, tax_amount, line_total
            FROM sale_lines WHERE sale_id = $sid;
        ";
        lineCmd.Parameters.AddWithValue("$sid", saleId.ToString());
        using var lReader = await lineCmd.ExecuteReaderAsync();
        while (await lReader.ReadAsync())
        {
            sale.Lines.Add(new SaleLine
            {
                LineId = Guid.Parse(lReader.GetString(0)),
                SaleId = saleId,
                ProductId = lReader.GetString(1),
                ProductName = lReader.GetString(2),
                Barcode = lReader.GetString(3),
                Quantity = lReader.GetDecimal(4),
                UnitPrice = lReader.GetDecimal(5),
                DiscountRate = lReader.GetDecimal(6),
                DiscountFixed = lReader.GetDecimal(7),
                DiscountAmount = lReader.GetDecimal(8),
                TaxRate = lReader.GetDecimal(9),
                TaxAmount = lReader.GetDecimal(10),
                LineTotal = lReader.GetDecimal(11)
            });
        }

        // Fetch tenders
        using var tCmd = conn.CreateCommand();
        tCmd.CommandText = "SELECT tender_id, tender_type, amount_tendered, change_given, payment_reference FROM tenders WHERE sale_id = $sid;";
        tCmd.Parameters.AddWithValue("$sid", saleId.ToString());
        using var tReader = await tCmd.ExecuteReaderAsync();
        while (await tReader.ReadAsync())
        {
            sale.Tenders.Add(new Tender
            {
                TenderId = Guid.Parse(tReader.GetString(0)),
                SaleId = saleId,
                TenderType = Enum.Parse<TenderType>(tReader.GetString(1)),
                AmountTendered = tReader.GetDecimal(2),
                ChangeGiven = tReader.GetDecimal(3),
                PaymentReference = tReader.IsDBNull(4) ? null : tReader.GetString(4)
            });
        }

        return sale;
    }

    public async Task<int> GetSaleCountAsync()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sales;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
