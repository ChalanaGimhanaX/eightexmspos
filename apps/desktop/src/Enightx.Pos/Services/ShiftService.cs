using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface IShiftService
{
    Task<CashShift> OpenShiftAsync(string branchId, string counterId, string cashierId, decimal openingFloat, string tenantId);
    Task<CashShift> CloseShiftAsync(Guid shiftId, decimal actualCountedCash, string actorId, string tenantId);
    Task<CashShift?> GetActiveShiftAsync(string branchId, string counterId);
    Task<CashShift?> GetShiftByIdAsync(Guid shiftId);
    Task<List<CashShift>> GetShiftsAsync(string branchId, string? counterId = null, int limit = 20);
    Task RecordCashMovementAsync(Guid shiftId, decimal amount, bool isCashIn, string reason, string actorId, string tenantId, string branchId, string counterId);
    Task<List<ShiftCashMovement>> GetCashMovementsForShiftAsync(Guid shiftId);
}

public class ShiftService : IShiftService
{
    private readonly PosDatabase _db;

    public ShiftService(PosDatabase db)
    {
        _db = db;
    }

    public async Task<CashShift> OpenShiftAsync(string branchId, string counterId, string cashierId, decimal openingFloat, string tenantId)
    {
        if (openingFloat < 0)
        {
            throw new ArgumentException("Opening float cannot be negative.", nameof(openingFloat));
        }

        var active = await GetActiveShiftAsync(branchId, counterId);
        if (active != null)
        {
            throw new PosException($"An active shift ({active.ShiftId}) is already open on counter {counterId}.");
        }

        var shiftTenant = string.IsNullOrWhiteSpace(tenantId) ? "TENANT_LK_01" : tenantId;
        var shift = new CashShift
        {
            ShiftId = Guid.NewGuid(),
            TenantId = shiftTenant,
            BranchId = branchId,
            CounterId = counterId,
            CashierId = cashierId,
            OpenedAtUtc = DateTime.UtcNow,
            OpeningFloat = MoneyCalculator.Round(openingFloat),
            CashReceived = 0m,
            ChangeGiven = 0m,
            CashRefunds = 0m,
            CashIn = 0m,
            CashOut = 0m,
            ExpectedCash = MoneyCalculator.Round(openingFloat),
            Status = ShiftStatus.Open
        };

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO shifts (
                shift_id, tenant_id, branch_id, counter_id, cashier_id, opened_at_utc, opening_float,
                cash_received, change_given, cash_refunds, cash_in, cash_out, expected_cash, status
            ) VALUES (
                $id, $tid, $bid, $cid, $uid, $opened, $float, 0, 0, 0, 0, 0, $exp, 1
            );
        ";
        cmd.Parameters.AddWithValue("$id", shift.ShiftId.ToString());
        cmd.Parameters.AddWithValue("$tid", shift.TenantId);
        cmd.Parameters.AddWithValue("$bid", shift.BranchId);
        cmd.Parameters.AddWithValue("$cid", shift.CounterId);
        cmd.Parameters.AddWithValue("$uid", shift.CashierId);
        cmd.Parameters.AddWithValue("$opened", shift.OpenedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$float", shift.OpeningFloat);
        cmd.Parameters.AddWithValue("$exp", shift.ExpectedCash);
        await cmd.ExecuteNonQueryAsync();

        // Audit open shift
        using var auditCmd = conn.CreateCommand();
        auditCmd.Transaction = tx;
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, 'OPEN_SHIFT', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", shift.TenantId);
        auditCmd.Parameters.AddWithValue("$bid", branchId);
        auditCmd.Parameters.AddWithValue("$cid", counterId);
        auditCmd.Parameters.AddWithValue("$actor", cashierId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            ShiftId = shift.ShiftId,
            OpeningFloat = shift.OpeningFloat
        }));
        auditCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();

        tx.Commit();
        return shift;
    }

    public async Task<CashShift> CloseShiftAsync(Guid shiftId, decimal actualCountedCash, string actorId, string tenantId)
    {
        if (actualCountedCash < 0)
        {
            throw new ArgumentException("Actual counted cash cannot be negative.", nameof(actualCountedCash));
        }

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Fetch shift
        using var getCmd = conn.CreateCommand();
        getCmd.Transaction = tx;
        getCmd.CommandText = @"
            SELECT shift_id, branch_id, counter_id, cashier_id, opened_at_utc, closed_at_utc,
                   opening_float, cash_received, change_given, cash_refunds, cash_in, cash_out,
                   expected_cash, actual_counted_cash, variance, status, tenant_id
            FROM shifts WHERE shift_id = $id;
        ";
        getCmd.Parameters.AddWithValue("$id", shiftId.ToString());

        CashShift? shift = null;
        using (var reader = await getCmd.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                shift = MapShift(reader);
            }
        }

        if (shift == null) throw new PosException("Shift not found.");
        if (shift.Status != ShiftStatus.Open) throw new PosException("Shift is already closed.");

        var effectiveTenant = string.IsNullOrWhiteSpace(tenantId) ? shift.TenantId : tenantId;

        // Calculate expected cash and variance (A10)
        var expectedCash = MoneyCalculator.CalculateShiftExpectedCash(
            shift.OpeningFloat,
            shift.CashReceived,
            shift.ChangeGiven,
            shift.CashRefunds,
            shift.CashIn,
            shift.CashOut
        );
        var counted = MoneyCalculator.Round(actualCountedCash);
        var variance = counted - expectedCash;

        shift.ExpectedCash = expectedCash;
        shift.ActualCountedCash = counted;
        shift.Variance = variance;
        shift.ClosedAtUtc = DateTime.UtcNow;
        shift.Status = ShiftStatus.Closed;

        using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = @"
            UPDATE shifts SET
                expected_cash = $exp,
                actual_counted_cash = $counted,
                variance = $var,
                closed_at_utc = $closed,
                status = 2
            WHERE shift_id = $id;
        ";
        updateCmd.Parameters.AddWithValue("$exp", expectedCash);
        updateCmd.Parameters.AddWithValue("$counted", counted);
        updateCmd.Parameters.AddWithValue("$var", variance);
        updateCmd.Parameters.AddWithValue("$closed", shift.ClosedAtUtc.Value.ToString("o"));
        updateCmd.Parameters.AddWithValue("$id", shift.ShiftId.ToString());
        await updateCmd.ExecuteNonQueryAsync();

        // Audit close shift
        using var auditCmd = conn.CreateCommand();
        auditCmd.Transaction = tx;
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, 'CLOSE_SHIFT', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", effectiveTenant);
        auditCmd.Parameters.AddWithValue("$bid", shift.BranchId);
        auditCmd.Parameters.AddWithValue("$cid", shift.CounterId);
        auditCmd.Parameters.AddWithValue("$actor", actorId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            ShiftId = shift.ShiftId,
            ExpectedCash = expectedCash,
            ActualCountedCash = counted,
            Variance = variance
        }));
        auditCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();

        tx.Commit();
        return shift;
    }

    public async Task<CashShift?> GetActiveShiftAsync(string branchId, string counterId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT shift_id, branch_id, counter_id, cashier_id, opened_at_utc, closed_at_utc,
                   opening_float, cash_received, change_given, cash_refunds, cash_in, cash_out,
                   expected_cash, actual_counted_cash, variance, status, tenant_id
            FROM shifts WHERE branch_id = $bid AND counter_id = $cid AND status = 1;
        ";
        cmd.Parameters.AddWithValue("$bid", branchId);
        cmd.Parameters.AddWithValue("$cid", counterId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return MapShift(reader);
    }

    public async Task RecordCashMovementAsync(Guid shiftId, decimal amount, bool isCashIn, string reason, string actorId, string tenantId, string branchId, string counterId)
    {
        var amt = MoneyCalculator.Round(amount);
        if (amt <= 0) throw new ArgumentException("Amount must be greater than zero.", nameof(amount));
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is mandatory for drawer cash movements (A08).", nameof(reason));
        }

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        if (isCashIn)
        {
            cmd.CommandText = "UPDATE shifts SET cash_in = cash_in + $amt WHERE shift_id = $id AND status = 1;";
        }
        else
        {
            cmd.CommandText = "UPDATE shifts SET cash_out = cash_out + $amt WHERE shift_id = $id AND status = 1;";
        }
        cmd.Parameters.AddWithValue("$amt", amt);
        cmd.Parameters.AddWithValue("$id", shiftId.ToString());

        var affected = await cmd.ExecuteNonQueryAsync();
        if (affected == 0) throw new ShiftClosedException("Shift is not open or not found.");

        // Record in shift_cash_movements table
        using var moveCmd = conn.CreateCommand();
        moveCmd.Transaction = tx;
        moveCmd.CommandText = @"
            INSERT INTO shift_cash_movements (movement_id, shift_id, movement_type, amount, reason, actor_id, occurred_at_utc)
            VALUES ($mid, $sid, $mtype, $amt, $reason, $actor, $occurred);
        ";
        var moveId = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;
        moveCmd.Parameters.AddWithValue("$mid", moveId.ToString());
        moveCmd.Parameters.AddWithValue("$sid", shiftId.ToString());
        moveCmd.Parameters.AddWithValue("$mtype", isCashIn ? "CASH_IN" : "CASH_OUT");
        moveCmd.Parameters.AddWithValue("$amt", amt);
        moveCmd.Parameters.AddWithValue("$reason", reason);
        moveCmd.Parameters.AddWithValue("$actor", actorId);
        moveCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
        await moveCmd.ExecuteNonQueryAsync();

        // Audit cash movement
        using var auditCmd = conn.CreateCommand();
        auditCmd.Transaction = tx;
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, $act, $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", tenantId);
        auditCmd.Parameters.AddWithValue("$bid", branchId);
        auditCmd.Parameters.AddWithValue("$cid", counterId);
        auditCmd.Parameters.AddWithValue("$actor", actorId);
        auditCmd.Parameters.AddWithValue("$act", isCashIn ? "SHIFT_CASH_IN" : "SHIFT_CASH_OUT");
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            MovementId = moveId,
            ShiftId = shiftId,
            Amount = amt,
            Reason = reason
        }));
        auditCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();

        tx.Commit();
    }

    public async Task<CashShift?> GetShiftByIdAsync(Guid shiftId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT shift_id, branch_id, counter_id, cashier_id, opened_at_utc, closed_at_utc,
                   opening_float, cash_received, change_given, cash_refunds, cash_in, cash_out,
                   expected_cash, actual_counted_cash, variance, status, tenant_id
            FROM shifts WHERE shift_id = $id;
        ";
        cmd.Parameters.AddWithValue("$id", shiftId.ToString());

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return MapShift(reader);
    }

    public async Task<List<CashShift>> GetShiftsAsync(string branchId, string? counterId = null, int limit = 20)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        if (string.IsNullOrEmpty(counterId))
        {
            cmd.CommandText = @"
                SELECT shift_id, branch_id, counter_id, cashier_id, opened_at_utc, closed_at_utc,
                       opening_float, cash_received, change_given, cash_refunds, cash_in, cash_out,
                       expected_cash, actual_counted_cash, variance, status, tenant_id
                FROM shifts WHERE branch_id = $bid
                ORDER BY opened_at_utc DESC LIMIT $lim;
            ";
            cmd.Parameters.AddWithValue("$bid", branchId);
            cmd.Parameters.AddWithValue("$lim", limit);
        }
        else
        {
            cmd.CommandText = @"
                SELECT shift_id, branch_id, counter_id, cashier_id, opened_at_utc, closed_at_utc,
                       opening_float, cash_received, change_given, cash_refunds, cash_in, cash_out,
                       expected_cash, actual_counted_cash, variance, status, tenant_id
                FROM shifts WHERE branch_id = $bid AND counter_id = $cid
                ORDER BY opened_at_utc DESC LIMIT $lim;
            ";
            cmd.Parameters.AddWithValue("$bid", branchId);
            cmd.Parameters.AddWithValue("$cid", counterId);
            cmd.Parameters.AddWithValue("$lim", limit);
        }

        var list = new List<CashShift>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(MapShift(reader));
        }
        return list;
    }

    public async Task<List<ShiftCashMovement>> GetCashMovementsForShiftAsync(Guid shiftId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT movement_id, shift_id, movement_type, amount, reason, actor_id, occurred_at_utc
            FROM shift_cash_movements
            WHERE shift_id = $sid
            ORDER BY occurred_at_utc ASC;
        ";
        cmd.Parameters.AddWithValue("$sid", shiftId.ToString());

        var list = new List<ShiftCashMovement>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ShiftCashMovement
            {
                MovementId = Guid.Parse(reader.GetString(0)),
                ShiftId = Guid.Parse(reader.GetString(1)),
                MovementType = reader.GetString(2),
                Amount = reader.GetDecimal(3),
                Reason = reader.GetString(4),
                ActorId = reader.GetString(5),
                OccurredAtUtc = DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
            });
        }
        return list;
    }

    private static CashShift MapShift(SqliteDataReader reader)
    {
        return new CashShift
        {
            ShiftId = Guid.Parse(reader.GetString(0)),
            BranchId = reader.GetString(1),
            CounterId = reader.GetString(2),
            CashierId = reader.GetString(3),
            OpenedAtUtc = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            ClosedAtUtc = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            OpeningFloat = reader.GetDecimal(6),
            CashReceived = reader.GetDecimal(7),
            ChangeGiven = reader.GetDecimal(8),
            CashRefunds = reader.GetDecimal(9),
            CashIn = reader.GetDecimal(10),
            CashOut = reader.GetDecimal(11),
            ExpectedCash = reader.GetDecimal(12),
            ActualCountedCash = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
            Variance = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
            Status = (ShiftStatus)reader.GetInt32(15),
            TenantId = reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetString(16) : "TENANT_LK_01"
        };
    }
}
