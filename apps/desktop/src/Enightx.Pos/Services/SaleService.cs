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

public record RefundLineRequest(
    Guid LineId,
    decimal QuantityToRefund
);

public record RefundSaleCommand(
    string TenantId,
    string BranchId,
    string CounterId,
    string CashierId,
    Guid ShiftId,
    Guid OriginalSaleId,
    List<RefundLineRequest> Items,
    string Reason,
    bool ReturnStockToInventory = true
);

public record CancelSaleCommand(
    string TenantId,
    string BranchId,
    string CounterId,
    string CashierId,
    Guid ShiftId,
    Guid SaleId,
    string Reason
);

public interface ISaleService
{
    Task<Sale> CommitSaleAsync(
        CreateSaleCommand command,
        Func<SqliteConnection, SqliteTransaction, Task>? failureHook = null
    );
    Task<Sale> RefundSaleAsync(RefundSaleCommand command);
    Task<Sale> CancelSaleAsync(CancelSaleCommand command);
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

        // 1. Calculate items and snapshots, check stock shortage
        var saleLines = new List<SaleLine>();
        var stockMovements = new List<StockMovement>();
        bool hadStockShortage = false;
        var shortageItems = new List<object>();

        foreach (var item in command.Items)
        {
            var product = await _catalog.GetProductByIdAsync(item.ProductId);
            if (product == null)
            {
                throw new PosException($"Product '{item.ProductId}' not found.");
            }

            var currentStock = await _catalog.GetStockOnHandAsync(item.ProductId);
            if (currentStock < item.Quantity)
            {
                hadStockShortage = true;
                shortageItems.Add(new
                {
                    ProductId = item.ProductId,
                    StockOnHand = currentStock,
                    RequestedQuantity = item.Quantity,
                    Shortage = item.Quantity - currentStock
                });
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

        // 2. Validate tenders and calculate change (order-independent)
        var totalTendered = MoneyCalculator.Round(command.Tenders.Sum(t => t.AmountTendered));
        if (totalTendered < grandTotal)
        {
            throw new InsufficientTenderException(
                $"Total tendered ({totalTendered:F2}) is less than grand total ({grandTotal:F2})."
            );
        }

        var totalNonCashTendered = MoneyCalculator.Round(
            command.Tenders.Where(t => t.TenderType != TenderType.CASH).Sum(t => t.AmountTendered)
        );
        if (totalNonCashTendered > grandTotal)
        {
            throw new PosException("Non-cash tenders cannot exceed grand total.");
        }

        var cashNeeded = grandTotal - totalNonCashTendered;
        var totalCashTendered = MoneyCalculator.Round(
            command.Tenders.Where(t => t.TenderType == TenderType.CASH).Sum(t => t.AmountTendered)
        );

        if (totalCashTendered < cashNeeded)
        {
            throw new InsufficientTenderException(
                $"Cash tendered ({totalCashTendered:F2}) is less than cash amount needed ({cashNeeded:F2})."
            );
        }

        var totalCashChange = totalCashTendered > cashNeeded ? totalCashTendered - cashNeeded : 0m;
        var remainingCashChangeToAssign = totalCashChange;

        var tenderEntities = new List<Tender>();
        foreach (var t in command.Tenders)
        {
            var amount = MoneyCalculator.Round(t.AmountTendered);
            var change = 0m;
            if (t.TenderType == TenderType.CASH && remainingCashChangeToAssign > 0)
            {
                change = Math.Min(amount, remainingCashChangeToAssign);
                remainingCashChangeToAssign -= change;
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
                    cashier_id, customer_id, parent_sale_id, subtotal, discount_total, tax_total, grand_total,
                    status, reprint_count, created_at_utc
                ) VALUES (
                    $id, $rcpt, $sid, $tid, $bid, $cid, $uid, $cust, null, $sub, $disc, $tax, $grand, 1, 0, $created
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

            // Record stock movements and update stock on hand (allows negative stock, A06)
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

            // Record audit event (including stock shortage warning if present)
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
                LinesCount = saleLines.Count,
                StockShortageWarning = hadStockShortage,
                Shortages = shortageItems
            }));
            auditCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            await auditCmd.ExecuteNonQueryAsync();

            // Record outbox event for sync with full metadata (actor_id, occurred_at_utc, causal_reference)
            var outboxPayload = JsonSerializer.Serialize(new
            {
                sale_id = saleId,
                receipt_number = receiptNumber,
                shift_id = command.ShiftId,
                customer_id = command.CustomerId,
                stock_shortage_warning = hadStockShortage,
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
                    source_sequence, schema_version, payload_json, actor_id, occurred_at_utc,
                    causal_reference, status, created_at_utc
                ) VALUES (
                    $eid, $tid, $bid, $did, 1, $seq, '1.0', $payload, $actor, $occurred, null, 'PENDING', $created
                );
            ";
            outboxCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            outboxCmd.Parameters.AddWithValue("$tid", command.TenantId);
            outboxCmd.Parameters.AddWithValue("$bid", command.BranchId);
            outboxCmd.Parameters.AddWithValue("$did", command.CounterId);
            outboxCmd.Parameters.AddWithValue("$seq", nextSeq);
            outboxCmd.Parameters.AddWithValue("$payload", outboxPayload);
            outboxCmd.Parameters.AddWithValue("$actor", command.CashierId);
            outboxCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
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

    public async Task<Sale> RefundSaleAsync(RefundSaleCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            throw new ArgumentException("A reason is required when refunding a sale (A08).", nameof(command.Reason));
        }
        if (command.Items == null || command.Items.Count == 0)
        {
            throw new PosException("At least one item must be specified for a refund.");
        }

        var origSale = await GetSaleByIdAsync(command.OriginalSaleId);
        if (origSale == null)
        {
            throw new PosException($"Original sale '{command.OriginalSaleId}' not found.");
        }
        if (origSale.Status == SaleStatus.Cancelled)
        {
            throw new PosException("Cannot refund a cancelled sale.");
        }
        if (origSale.Status == SaleStatus.Refunded)
        {
            throw new PosException("Sale has already been fully refunded.");
        }
        // Cross-counter refund origin check (A11)
        if (origSale.BranchId != command.BranchId || origSale.CounterId != command.CounterId)
        {
            throw new PosException($"Cross-counter refund not permitted offline. Sale originated on branch {origSale.BranchId} counter {origSale.CounterId}.");
        }

        var refundLines = new List<SaleLine>();
        var stockMovements = new List<StockMovement>();

        foreach (var req in command.Items)
        {
            var origLine = origSale.Lines.FirstOrDefault(l => l.LineId == req.LineId);
            if (origLine == null)
            {
                throw new PosException($"Line '{req.LineId}' not found on original sale.");
            }
            if (req.QuantityToRefund <= 0 || req.QuantityToRefund > origLine.Quantity)
            {
                throw new PosException($"Invalid refund quantity ({req.QuantityToRefund}) for '{origLine.ProductName}'. Original quantity was {origLine.Quantity}.");
            }

            var calc = MoneyCalculator.CalculateLine(
                req.QuantityToRefund,
                origLine.UnitPrice,
                origLine.DiscountRate,
                0m,
                origLine.TaxRate
            );

            var line = new SaleLine
            {
                LineId = Guid.NewGuid(),
                ProductId = origLine.ProductId,
                ProductName = origLine.ProductName,
                Barcode = origLine.Barcode,
                Quantity = req.QuantityToRefund,
                UnitPrice = origLine.UnitPrice,
                DiscountRate = origLine.DiscountRate,
                DiscountFixed = 0m,
                DiscountAmount = calc.DiscountAmount,
                TaxRate = origLine.TaxRate,
                TaxAmount = calc.TaxAmount,
                LineTotal = calc.LineTotal
            };
            refundLines.Add(line);

            if (command.ReturnStockToInventory)
            {
                stockMovements.Add(new StockMovement
                {
                    MovementId = Guid.NewGuid(),
                    ProductId = origLine.ProductId,
                    MovementType = "REFUND",
                    QuantityChange = req.QuantityToRefund,
                    ReferenceId = ""
                });
            }
        }

        var refundSubtotal = MoneyCalculator.Round(refundLines.Sum(l => l.Quantity * l.UnitPrice));
        var refundDiscountTotal = MoneyCalculator.Round(refundLines.Sum(l => l.DiscountAmount));
        var refundTaxTotal = MoneyCalculator.Round(refundLines.Sum(l => l.TaxAmount));
        var refundGrandTotal = MoneyCalculator.Round(refundLines.Sum(l => l.LineTotal));

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
                throw new ShiftClosedException("Cannot refund sale: shift is not open.");
            }

            // Sequence allocation
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
            var receiptNumber = $"{bPrefix}{command.BranchId}-{cPrefix}{command.CounterId}-REF{nextSeq:D6}";

            var refundSaleId = Guid.NewGuid();
            var nowUtc = DateTime.UtcNow;

            var refundTender = new Tender
            {
                TenderId = Guid.NewGuid(),
                SaleId = refundSaleId,
                TenderType = TenderType.CASH,
                AmountTendered = refundGrandTotal,
                ChangeGiven = 0m,
                PaymentReference = $"REF_ORIG_{origSale.ReceiptNumber}"
            };

            var refundSale = new Sale
            {
                SaleId = refundSaleId,
                ReceiptNumber = receiptNumber,
                ShiftId = command.ShiftId,
                TenantId = command.TenantId,
                BranchId = command.BranchId,
                CounterId = command.CounterId,
                CashierId = command.CashierId,
                ParentSaleId = origSale.SaleId,
                Subtotal = refundSubtotal,
                DiscountTotal = refundDiscountTotal,
                TaxTotal = refundTaxTotal,
                GrandTotal = refundGrandTotal,
                Status = SaleStatus.Refunded,
                ReprintCount = 0,
                CreatedAtUtc = nowUtc,
                Lines = refundLines,
                Tenders = new List<Tender> { refundTender }
            };

            // Insert refund sale record
            using var saleCmd = conn.CreateCommand();
            saleCmd.Transaction = tx;
            saleCmd.CommandText = @"
                INSERT INTO sales (
                    sale_id, receipt_number, shift_id, tenant_id, branch_id, counter_id,
                    cashier_id, customer_id, parent_sale_id, subtotal, discount_total, tax_total, grand_total,
                    status, reprint_count, created_at_utc
                ) VALUES (
                    $id, $rcpt, $sid, $tid, $bid, $cid, $uid, null, $parent, $sub, $disc, $tax, $grand, 3, 0, $created
                );
            ";
            saleCmd.Parameters.AddWithValue("$id", refundSale.SaleId.ToString());
            saleCmd.Parameters.AddWithValue("$rcpt", refundSale.ReceiptNumber);
            saleCmd.Parameters.AddWithValue("$sid", refundSale.ShiftId.ToString());
            saleCmd.Parameters.AddWithValue("$tid", refundSale.TenantId);
            saleCmd.Parameters.AddWithValue("$bid", refundSale.BranchId);
            saleCmd.Parameters.AddWithValue("$cid", refundSale.CounterId);
            saleCmd.Parameters.AddWithValue("$uid", refundSale.CashierId);
            saleCmd.Parameters.AddWithValue("$parent", origSale.SaleId.ToString());
            saleCmd.Parameters.AddWithValue("$sub", refundSale.Subtotal);
            saleCmd.Parameters.AddWithValue("$disc", refundSale.DiscountTotal);
            saleCmd.Parameters.AddWithValue("$tax", refundSale.TaxTotal);
            saleCmd.Parameters.AddWithValue("$grand", refundSale.GrandTotal);
            saleCmd.Parameters.AddWithValue("$created", refundSale.CreatedAtUtc.ToString("o"));
            await saleCmd.ExecuteNonQueryAsync();

            // Insert refund lines
            foreach (var line in refundLines)
            {
                line.SaleId = refundSaleId;
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
                lineCmd.Parameters.AddWithValue("$sid", refundSaleId.ToString());
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

            // Insert refund tender
            using var tCmd = conn.CreateCommand();
            tCmd.Transaction = tx;
            tCmd.CommandText = @"
                INSERT INTO tenders (tender_id, sale_id, tender_type, amount_tendered, change_given, payment_reference)
                VALUES ($tid, $sid, $ttype, $amt, 0, $pref);
            ";
            tCmd.Parameters.AddWithValue("$tid", refundTender.TenderId.ToString());
            tCmd.Parameters.AddWithValue("$sid", refundSaleId.ToString());
            tCmd.Parameters.AddWithValue("$ttype", refundTender.TenderType.ToString());
            tCmd.Parameters.AddWithValue("$amt", refundTender.AmountTendered);
            tCmd.Parameters.AddWithValue("$pref", (object?)refundTender.PaymentReference ?? DBNull.Value);
            await tCmd.ExecuteNonQueryAsync();

            // Record stock movements (restores inventory)
            foreach (var sm in stockMovements)
            {
                sm.ReferenceId = refundSaleId.ToString();
                using var smCmd = conn.CreateCommand();
                smCmd.Transaction = tx;
                smCmd.CommandText = @"
                    INSERT INTO stock_movements (movement_id, product_id, movement_type, quantity_change, reference_id, occurred_at_utc)
                    VALUES ($mid, $pid, 'REFUND', $qchange, $ref, $occurred);

                    UPDATE products SET stock_on_hand = stock_on_hand + $qchange WHERE product_id = $pid;
                ";
                smCmd.Parameters.AddWithValue("$mid", sm.MovementId.ToString());
                smCmd.Parameters.AddWithValue("$pid", sm.ProductId);
                smCmd.Parameters.AddWithValue("$qchange", sm.QuantityChange);
                smCmd.Parameters.AddWithValue("$ref", sm.ReferenceId);
                smCmd.Parameters.AddWithValue("$occurred", sm.OccurredAtUtc.ToString("o"));
                await smCmd.ExecuteNonQueryAsync();
            }

            // Update shift cash: increment cash_refunds (A10)
            using var shiftUpdCmd = conn.CreateCommand();
            shiftUpdCmd.Transaction = tx;
            shiftUpdCmd.CommandText = @"
                UPDATE shifts SET cash_refunds = cash_refunds + $refAmt WHERE shift_id = $sid;
            ";
            shiftUpdCmd.Parameters.AddWithValue("$refAmt", refundGrandTotal);
            shiftUpdCmd.Parameters.AddWithValue("$sid", command.ShiftId.ToString());
            await shiftUpdCmd.ExecuteNonQueryAsync();

            // Log audit event for refund (A08)
            using var auditCmd = conn.CreateCommand();
            auditCmd.Transaction = tx;
            auditCmd.CommandText = @"
                INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                VALUES ($eid, $tid, $bid, $cid, $actor, 'REFUND_SALE', $details, $occurred);
            ";
            auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            auditCmd.Parameters.AddWithValue("$tid", command.TenantId);
            auditCmd.Parameters.AddWithValue("$bid", command.BranchId);
            auditCmd.Parameters.AddWithValue("$cid", command.CounterId);
            auditCmd.Parameters.AddWithValue("$actor", command.CashierId);
            auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
            {
                OriginalSaleId = origSale.SaleId,
                OriginalReceiptNumber = origSale.ReceiptNumber,
                RefundSaleId = refundSaleId,
                RefundReceiptNumber = receiptNumber,
                RefundAmount = refundGrandTotal,
                Reason = command.Reason,
                StockRestored = command.ReturnStockToInventory
            }));
            auditCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            await auditCmd.ExecuteNonQueryAsync();

            // Outbox event for sync
            var outboxPayload = JsonSerializer.Serialize(new
            {
                refund_sale_id = refundSaleId,
                original_sale_id = origSale.SaleId,
                receipt_number = receiptNumber,
                original_receipt_number = origSale.ReceiptNumber,
                shift_id = command.ShiftId,
                subtotal = $"{refundSubtotal:F2}",
                discount_total = $"{refundDiscountTotal:F2}",
                tax_total = $"{refundTaxTotal:F2}",
                grand_total = $"{refundGrandTotal:F2}",
                reason = command.Reason,
                lines = refundLines.Select(l => new
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
                    $eid, $tid, $bid, $did, 1, $seq, '1.0', $payload, $actor, $occurred, $causal, 'PENDING', $created
                );
            ";
            outboxCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            outboxCmd.Parameters.AddWithValue("$tid", command.TenantId);
            outboxCmd.Parameters.AddWithValue("$bid", command.BranchId);
            outboxCmd.Parameters.AddWithValue("$did", command.CounterId);
            outboxCmd.Parameters.AddWithValue("$seq", nextSeq);
            outboxCmd.Parameters.AddWithValue("$payload", outboxPayload);
            outboxCmd.Parameters.AddWithValue("$actor", command.CashierId);
            outboxCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            outboxCmd.Parameters.AddWithValue("$causal", origSale.SaleId.ToString());
            outboxCmd.Parameters.AddWithValue("$created", nowUtc.ToString("o"));
            await outboxCmd.ExecuteNonQueryAsync();

            tx.Commit();
            return refundSale;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    public async Task<Sale> CancelSaleAsync(CancelSaleCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            throw new ArgumentException("A reason is required when cancelling a sale (A08).", nameof(command.Reason));
        }

        var sale = await GetSaleByIdAsync(command.SaleId);
        if (sale == null)
        {
            throw new PosException($"Sale '{command.SaleId}' not found.");
        }
        if (sale.Status != SaleStatus.Completed)
        {
            throw new PosException($"Cannot cancel sale with status '{sale.Status}'. Only completed sales can be cancelled.");
        }
        if (sale.BranchId != command.BranchId || sale.CounterId != command.CounterId)
        {
            throw new PosException("Cross-counter cancellation not permitted offline.");
        }

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
                throw new ShiftClosedException("Cannot cancel sale: shift is not open.");
            }

            // 1. Update sale status to Cancelled (2) - ORIGINAL RECORD IS PRESERVED
            using var updSaleCmd = conn.CreateCommand();
            updSaleCmd.Transaction = tx;
            updSaleCmd.CommandText = "UPDATE sales SET status = 2 WHERE sale_id = $id;";
            updSaleCmd.Parameters.AddWithValue("$id", sale.SaleId.ToString());
            await updSaleCmd.ExecuteNonQueryAsync();
            sale.Status = SaleStatus.Cancelled;

            // 2. Reversal stock movements
            var nowUtc = DateTime.UtcNow;
            foreach (var line in sale.Lines)
            {
                using var smCmd = conn.CreateCommand();
                smCmd.Transaction = tx;
                smCmd.CommandText = @"
                    INSERT INTO stock_movements (movement_id, product_id, movement_type, quantity_change, reference_id, occurred_at_utc)
                    VALUES ($mid, $pid, 'CANCELLATION', $qchange, $ref, $occurred);

                    UPDATE products SET stock_on_hand = stock_on_hand + $qchange WHERE product_id = $pid;
                ";
                smCmd.Parameters.AddWithValue("$mid", Guid.NewGuid().ToString());
                smCmd.Parameters.AddWithValue("$pid", line.ProductId);
                smCmd.Parameters.AddWithValue("$qchange", line.Quantity);
                smCmd.Parameters.AddWithValue("$ref", sale.SaleId.ToString());
                smCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
                await smCmd.ExecuteNonQueryAsync();
            }

            // 3. Reverse shift cash effects: net cash received on this sale is added to cash_refunds (money returned)
            var netCashFromSale = sale.Tenders
                .Where(t => t.TenderType == TenderType.CASH)
                .Sum(t => t.AmountTendered - t.ChangeGiven);

            if (netCashFromSale > 0)
            {
                using var shiftUpdCmd = conn.CreateCommand();
                shiftUpdCmd.Transaction = tx;
                shiftUpdCmd.CommandText = "UPDATE shifts SET cash_refunds = cash_refunds + $amt WHERE shift_id = $sid;";
                shiftUpdCmd.Parameters.AddWithValue("$amt", netCashFromSale);
                shiftUpdCmd.Parameters.AddWithValue("$sid", command.ShiftId.ToString());
                await shiftUpdCmd.ExecuteNonQueryAsync();
            }

            // 4. Audit event
            using var auditCmd = conn.CreateCommand();
            auditCmd.Transaction = tx;
            auditCmd.CommandText = @"
                INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                VALUES ($eid, $tid, $bid, $cid, $actor, 'CANCEL_SALE', $details, $occurred);
            ";
            auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            auditCmd.Parameters.AddWithValue("$tid", command.TenantId);
            auditCmd.Parameters.AddWithValue("$bid", command.BranchId);
            auditCmd.Parameters.AddWithValue("$cid", command.CounterId);
            auditCmd.Parameters.AddWithValue("$actor", command.CashierId);
            auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
            {
                SaleId = sale.SaleId,
                ReceiptNumber = sale.ReceiptNumber,
                Reason = command.Reason,
                NetCashRefunded = netCashFromSale
            }));
            auditCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            await auditCmd.ExecuteNonQueryAsync();

            // 5. Outbox event
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

            var outboxPayload = JsonSerializer.Serialize(new
            {
                cancellation_id = Guid.NewGuid(),
                sale_id = sale.SaleId,
                receipt_number = sale.ReceiptNumber,
                shift_id = command.ShiftId,
                reason = command.Reason,
                net_cash_refunded = $"{netCashFromSale:F2}"
            });

            using var outboxCmd = conn.CreateCommand();
            outboxCmd.Transaction = tx;
            outboxCmd.CommandText = @"
                INSERT INTO outbox_events (
                    event_id, tenant_id, branch_id, device_id, device_generation,
                    source_sequence, schema_version, payload_json, actor_id, occurred_at_utc,
                    causal_reference, status, created_at_utc
                ) VALUES (
                    $eid, $tid, $bid, $did, 1, $seq, '1.0', $payload, $actor, $occurred, $causal, 'PENDING', $created
                );
            ";
            outboxCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            outboxCmd.Parameters.AddWithValue("$tid", command.TenantId);
            outboxCmd.Parameters.AddWithValue("$bid", command.BranchId);
            outboxCmd.Parameters.AddWithValue("$did", command.CounterId);
            outboxCmd.Parameters.AddWithValue("$seq", nextSeq);
            outboxCmd.Parameters.AddWithValue("$payload", outboxPayload);
            outboxCmd.Parameters.AddWithValue("$actor", command.CashierId);
            outboxCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            outboxCmd.Parameters.AddWithValue("$causal", sale.SaleId.ToString());
            outboxCmd.Parameters.AddWithValue("$created", nowUtc.ToString("o"));
            await outboxCmd.ExecuteNonQueryAsync();

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
                   cashier_id, customer_id, parent_sale_id, subtotal, discount_total, tax_total, grand_total,
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
            ParentSaleId = reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
            Subtotal = reader.GetDecimal(9),
            DiscountTotal = reader.GetDecimal(10),
            TaxTotal = reader.GetDecimal(11),
            GrandTotal = reader.GetDecimal(12),
            Status = (SaleStatus)reader.GetInt32(13),
            ReprintCount = reader.GetInt32(14),
            CreatedAtUtc = DateTime.Parse(reader.GetString(15), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
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
