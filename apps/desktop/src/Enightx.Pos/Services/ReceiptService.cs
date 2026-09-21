using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface IPrinterService
{
    Task PrintAsync(string printableText, bool simulateHardwareFailure = false);
}

public class MemoryPrinterService : IPrinterService
{
    public List<string> PrintedJobs { get; } = new();

    public Task PrintAsync(string printableText, bool simulateHardwareFailure = false)
    {
        if (simulateHardwareFailure)
        {
            throw new PrinterException("Receipt printer out of paper or communication timeout.");
        }
        PrintedJobs.Add(printableText);
        return Task.CompletedTask;
    }
}

public record PrintResult(bool Success, string? ErrorMessage, string ReceiptContent, bool CanReprint);

public interface IReceiptService
{
    string FormatReceipt(Sale sale, bool isReprint = false);
    Task<PrintResult> PrintSaleReceiptAsync(Guid saleId, bool simulateHardwareFailure = false);
    Task<PrintResult> ReprintSaleReceiptAsync(Guid saleId, string actorId, string reason, bool simulateHardwareFailure = false);
}

public class ReceiptService : IReceiptService
{
    private readonly PosDatabase _db;
    private readonly ISaleService _saleService;
    private readonly IPrinterService _printerService;

    public ReceiptService(PosDatabase db, ISaleService saleService, IPrinterService printerService)
    {
        _db = db;
        _saleService = saleService;
        _printerService = printerService;
    }

    public string FormatReceipt(Sale sale, bool isReprint = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========================================");
        sb.AppendLine("           ENIGHTX POS SYSTEM           ");
        sb.AppendLine("         Nimal Motors (Pvt) Ltd         ");
        sb.AppendLine("       No. 124 Galle Road, Colombo      ");
        sb.AppendLine("            Tel: 011-2345678            ");
        sb.AppendLine("========================================");

        if (isReprint)
        {
            sb.AppendLine("   *** DUPLICATE / REPRINT RECEIPT ***  ");
            sb.AppendLine($"   Reprint Count: {sale.ReprintCount}   ");
            sb.AppendLine($"   Reprinted At: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("----------------------------------------");
        }

        sb.AppendLine($"Receipt No : {sale.ReceiptNumber}");
        sb.AppendLine($"Date / Time: {sale.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Branch     : {sale.BranchId}   Counter: {sale.CounterId}");
        sb.AppendLine($"Cashier    : {sale.CashierId}");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine("Item                  Qty  Price  Amount");
        sb.AppendLine("----------------------------------------");

        foreach (var l in sale.Lines)
        {
            var name = l.ProductName.Length > 18 ? l.ProductName[..18] : l.ProductName.PadRight(18);
            sb.AppendLine($"{name} {l.Quantity,4:F0} {l.UnitPrice,6:F2} {l.LineTotal,7:F2}");
            if (l.DiscountAmount > 0)
            {
                sb.AppendLine($"  (Discount: -LKR {l.DiscountAmount:F2})");
            }
            if (l.TaxAmount > 0)
            {
                sb.AppendLine($"  (VAT 18% incl: LKR {l.TaxAmount:F2})");
            }
        }

        sb.AppendLine("----------------------------------------");
        sb.AppendLine($"Subtotal     : LKR {sale.Subtotal,15:F2}");
        if (sale.DiscountTotal > 0)
        {
            sb.AppendLine($"Discounts    : LKR -{sale.DiscountTotal,14:F2}");
        }
        if (sale.TaxTotal > 0)
        {
            sb.AppendLine($"Tax (VAT 18%): LKR {sale.TaxTotal,15:F2}");
        }
        sb.AppendLine($"TOTAL        : LKR {sale.GrandTotal,15:F2}");
        sb.AppendLine("----------------------------------------");

        foreach (var t in sale.Tenders)
        {
            sb.AppendLine($"Paid ({t.TenderType}) : LKR {t.AmountTendered,15:F2}");
            if (t.ChangeGiven > 0)
            {
                sb.AppendLine($"Change Given : LKR {t.ChangeGiven,15:F2}");
            }
        }

        sb.AppendLine("========================================");
        sb.AppendLine("   Thank you for your business!         ");
        sb.AppendLine("   Exchange within 7 days with receipt  ");
        sb.AppendLine("========================================");

        return sb.ToString();
    }

    public async Task<PrintResult> PrintSaleReceiptAsync(Guid saleId, bool simulateHardwareFailure = false)
    {
        var sale = await _saleService.GetSaleByIdAsync(saleId);
        if (sale == null) throw new PosException($"Sale '{saleId}' not found.");

        var text = FormatReceipt(sale, isReprint: false);

        try
        {
            await _printerService.PrintAsync(text, simulateHardwareFailure);
            return new PrintResult(Success: true, ErrorMessage: null, ReceiptContent: text, CanReprint: true);
        }
        catch (PrinterException ex)
        {
            // Log audit event for printer failure (A01)
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                VALUES ($eid, $tid, $bid, $cid, $actor, 'RECEIPT_PRINT_FAILED', $details, $occurred);
            ";
            cmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$tid", sale.TenantId);
            cmd.Parameters.AddWithValue("$bid", sale.BranchId);
            cmd.Parameters.AddWithValue("$cid", sale.CounterId);
            cmd.Parameters.AddWithValue("$actor", sale.CashierId);
            cmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
            {
                SaleId = saleId,
                ReceiptNumber = sale.ReceiptNumber,
                Error = ex.Message
            }));
            cmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();

            return new PrintResult(Success: false, ErrorMessage: ex.Message, ReceiptContent: text, CanReprint: true);
        }
    }

    public async Task<PrintResult> ReprintSaleReceiptAsync(Guid saleId, string actorId, string reason, bool simulateHardwareFailure = false)
    {
        var sale = await _saleService.GetSaleByIdAsync(saleId);
        if (sale == null) throw new PosException($"Sale '{saleId}' not found.");

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Increment reprint count on existing sale (never create a duplicate sale)
        using var updCmd = conn.CreateCommand();
        updCmd.Transaction = tx;
        updCmd.CommandText = "UPDATE sales SET reprint_count = reprint_count + 1 WHERE sale_id = $id RETURNING reprint_count;";
        updCmd.Parameters.AddWithValue("$id", saleId.ToString());
        var newCount = Convert.ToInt32(await updCmd.ExecuteScalarAsync());
        sale.ReprintCount = newCount;

        // Log audit event for reprint (A01)
        using var auditCmd = conn.CreateCommand();
        auditCmd.Transaction = tx;
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, 'REPRINT_RECEIPT', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", sale.TenantId);
        auditCmd.Parameters.AddWithValue("$bid", sale.BranchId);
        auditCmd.Parameters.AddWithValue("$cid", sale.CounterId);
        auditCmd.Parameters.AddWithValue("$actor", actorId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            SaleId = saleId,
            ReceiptNumber = sale.ReceiptNumber,
            ReprintCount = newCount,
            Reason = reason
        }));
        auditCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();

        tx.Commit();

        var text = FormatReceipt(sale, isReprint: true);
        await _printerService.PrintAsync(text, simulateHardwareFailure);

        return new PrintResult(Success: true, ErrorMessage: null, ReceiptContent: text, CanReprint: true);
    }
}
