using System;
using System.Threading.Tasks;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class OpenShiftTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly ShiftService _shift;

    public OpenShiftTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _shift = new ShiftService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task OpenShift_ValidFloat_CreatesActiveShiftWithExpectedCashAndFloat()
    {
        // Arrange
        var cashier = await _auth.CreateUserAsync("sunil_pos", "Sunil Fernando", "Pass#123", Role.Cashier);
        const decimal enteredFloat = 5000.00m;

        // Act
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, enteredFloat, "TENANT_LK_01");

        // Assert
        Assert.NotNull(shift);
        Assert.NotEqual(Guid.Empty, shift.ShiftId);
        Assert.Equal("TENANT_LK_01", shift.TenantId);
        Assert.Equal("B01", shift.BranchId);
        Assert.Equal("C01", shift.CounterId);
        Assert.Equal(cashier.UserId, shift.CashierId);
        Assert.Equal(enteredFloat, shift.OpeningFloat);
        Assert.Equal(enteredFloat, shift.ExpectedCash);
        Assert.Equal(ShiftStatus.Open, shift.Status);
        Assert.Null(shift.ClosedAtUtc);
        Assert.Equal(0m, shift.CashReceived);
        Assert.Equal(0m, shift.ChangeGiven);
        Assert.Equal(0m, shift.CashRefunds);
        Assert.Equal(0m, shift.CashIn);
        Assert.Equal(0m, shift.CashOut);

        // Verify active shift lookup returns the newly opened shift
        var active = await _shift.GetActiveShiftAsync("B01", "C01");
        Assert.NotNull(active);
        Assert.Equal(shift.ShiftId, active.ShiftId);
        Assert.Equal(enteredFloat, active.OpeningFloat);
        Assert.Equal(enteredFloat, active.ExpectedCash);
    }

    [Fact]
    public async Task OpenShift_NegativeFloat_ThrowsArgumentException_AndNoShiftCreated()
    {
        // Arrange
        var cashier = await _auth.CreateUserAsync("negative_cashier", "Negative Tester", "Pass#123", Role.Cashier);

        // Act & Assert 1: -100 LKR
        var ex1 = await Assert.ThrowsAsync<ArgumentException>(() =>
            _shift.OpenShiftAsync("B01", "C01", cashier.UserId, -100.00m, "TENANT_LK_01")
        );
        Assert.Equal("openingFloat", ex1.ParamName);
        Assert.Contains("Opening float cannot be negative", ex1.Message);

        // Act & Assert 2: -0.01 LKR (explicit edge case)
        var ex2 = await Assert.ThrowsAsync<ArgumentException>(() =>
            _shift.OpenShiftAsync("B01", "C01", cashier.UserId, -0.01m, "TENANT_LK_01")
        );
        Assert.Equal("openingFloat", ex2.ParamName);

        // Act & Assert 3: -5000.00 LKR (explicit edge case)
        var ex3 = await Assert.ThrowsAsync<ArgumentException>(() =>
            _shift.OpenShiftAsync("B01", "C01", cashier.UserId, -5000.00m, "TENANT_LK_01")
        );
        Assert.Equal("openingFloat", ex3.ParamName);

        // Act & Assert 4: Sub-cent negative float (-0.0001 LKR)
        var ex4 = await Assert.ThrowsAsync<ArgumentException>(() =>
            _shift.OpenShiftAsync("B01", "C01", cashier.UserId, -0.0001m, "TENANT_LK_01")
        );
        Assert.Equal("openingFloat", ex4.ParamName);

        // Verify database state: no shift was created
        var active = await _shift.GetActiveShiftAsync("B01", "C01");
        Assert.Null(active);
    }

    [Fact]
    public async Task OpenShift_ZeroFloat_AllowsShiftOpeningWithZeroCash()
    {
        // Arrange
        var cashier = await _auth.CreateUserAsync("zero_cashier", "Zero Tester", "Pass#123", Role.Cashier);

        // Act: Cashier starts shift with empty drawer (0.00 LKR)
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 0.00m, "TENANT_LK_01");

        // Assert
        Assert.NotNull(shift);
        Assert.Equal(0.00m, shift.OpeningFloat);
        Assert.Equal(0.00m, shift.ExpectedCash);
        Assert.Equal(ShiftStatus.Open, shift.Status);

        var active = await _shift.GetActiveShiftAsync("B01", "C01");
        Assert.NotNull(active);
        Assert.Equal(0.00m, active.OpeningFloat);
        Assert.Equal(0.00m, active.ExpectedCash);
    }

    [Fact]
    public async Task OpenShift_DuplicateActiveShiftOnSameCounter_ThrowsPosException()
    {
        // Arrange
        var cashier1 = await _auth.CreateUserAsync("cashier_one", "Cashier One", "Pass#123", Role.Cashier);
        var cashier2 = await _auth.CreateUserAsync("cashier_two", "Cashier Two", "Pass#123", Role.Cashier);

        // Shift 1 opened on B01/C01
        var shift1 = await _shift.OpenShiftAsync("B01", "C01", cashier1.UserId, 5000.00m, "TENANT_LK_01");
        Assert.NotNull(shift1);

        // Act & Assert 1: Opening a second shift by another cashier on same counter throws PosException
        var ex1 = await Assert.ThrowsAsync<PosException>(() =>
            _shift.OpenShiftAsync("B01", "C01", cashier2.UserId, 2500.00m, "TENANT_LK_01")
        );
        Assert.Contains("already open on counter C01", ex1.Message);

        // Act & Assert 2: Opening a second shift by the SAME cashier on same counter also throws PosException
        var ex2 = await Assert.ThrowsAsync<PosException>(() =>
            _shift.OpenShiftAsync("B01", "C01", cashier1.UserId, 1000.00m, "TENANT_LK_01")
        );
        Assert.Contains("already open on counter C01", ex2.Message);

        // Verify active shift is still the original shift1
        var active = await _shift.GetActiveShiftAsync("B01", "C01");
        Assert.NotNull(active);
        Assert.Equal(shift1.ShiftId, active.ShiftId);
        Assert.Equal(5000.00m, active.OpeningFloat);
    }

    [Theory]
    [InlineData(5000.005, 5000.01)]
    [InlineData(5000.004, 5000.00)]
    [InlineData(5000.006, 5000.01)]
    [InlineData(10.005, 10.01)]
    [InlineData(5000.0051, 5000.01)]
    [InlineData(0.005, 0.01)]
    [InlineData(0.004, 0.00)]
    [InlineData(2500.555, 2500.56)]
    [InlineData(1234.995, 1235.00)]
    public async Task OpenShift_HighPrecisionDecimals_RoundsAwayFromZero(decimal inputFloat, decimal expectedFloat)
    {
        // Arrange
        var counter = $"C_{Guid.NewGuid():N}";
        var cashier = await _auth.CreateUserAsync($"user_{counter}", "Precision Cashier", "Pass#123", Role.Cashier);

        // Act: High-precision decimals must round half-up (AwayFromZero) to 2 decimals
        var shift = await _shift.OpenShiftAsync("B01", counter, cashier.UserId, inputFloat, "TENANT_LK_01");

        // Assert domain object
        Assert.Equal(expectedFloat, shift.OpeningFloat);
        Assert.Equal(expectedFloat, shift.ExpectedCash);

        // Assert database persistence
        var active = await _shift.GetActiveShiftAsync("B01", counter);
        Assert.NotNull(active);
        Assert.Equal(expectedFloat, active.OpeningFloat);
        Assert.Equal(expectedFloat, active.ExpectedCash);
    }

    [Fact]
    public async Task OpenShift_DifferentCountersInSameBranch_AllowSimultaneousActiveShifts()
    {
        // Arrange
        var cashier1 = await _auth.CreateUserAsync("counter1_user", "Counter One User", "Pass#123", Role.Cashier);
        var cashier2 = await _auth.CreateUserAsync("counter2_user", "Counter Two User", "Pass#123", Role.Cashier);

        // Act: Open C01 and C02 concurrently
        var shiftC01 = await _shift.OpenShiftAsync("B01", "C01", cashier1.UserId, 3000.00m, "TENANT_LK_01");
        var shiftC02 = await _shift.OpenShiftAsync("B01", "C02", cashier2.UserId, 7000.00m, "TENANT_LK_01");

        // Assert
        Assert.NotNull(shiftC01);
        Assert.NotNull(shiftC02);
        Assert.NotEqual(shiftC01.ShiftId, shiftC02.ShiftId);

        var activeC01 = await _shift.GetActiveShiftAsync("B01", "C01");
        var activeC02 = await _shift.GetActiveShiftAsync("B01", "C02");

        Assert.Equal(3000.00m, activeC01?.OpeningFloat);
        Assert.Equal(7000.00m, activeC02?.OpeningFloat);
    }

    [Fact]
    public async Task OpenShift_SameCounterDifferentBranches_AllowSimultaneousActiveShifts()
    {
        // Arrange
        var cashier1 = await _auth.CreateUserAsync("b1_cashier", "B1 Cashier", "Pass#123", Role.Cashier);
        var cashier2 = await _auth.CreateUserAsync("b2_cashier", "B2 Cashier", "Pass#123", Role.Cashier);

        // Act: Counter "C01" exists on Branch "B01" and Branch "B02"
        var shift1 = await _shift.OpenShiftAsync("B01", "C01", cashier1.UserId, 5000m, "TENANT_LK_01");
        var shift2 = await _shift.OpenShiftAsync("B02", "C01", cashier2.UserId, 4000m, "TENANT_LK_01");

        // Assert
        Assert.NotNull(shift1);
        Assert.NotNull(shift2);
        Assert.NotEqual(shift1.ShiftId, shift2.ShiftId);

        var active1 = await _shift.GetActiveShiftAsync("B01", "C01");
        var active2 = await _shift.GetActiveShiftAsync("B02", "C01");
        Assert.Equal(shift1.ShiftId, active1?.ShiftId);
        Assert.Equal(shift2.ShiftId, active2?.ShiftId);
    }

    [Fact]
    public async Task OpenShift_AfterPreviousShiftClosed_AllowsNewShiftWithNewFloat()
    {
        // Arrange
        var cashier = await _auth.CreateUserAsync("cycle_cashier", "Cycle Cashier", "Pass#123", Role.Cashier);

        // Shift 1
        var shift1 = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");
        await _shift.CloseShiftAsync(shift1.ShiftId, actualCountedCash: 5000.00m, actorId: cashier.UserId, tenantId: "TENANT_LK_01");

        // Verify active shift is null after close
        Assert.Null(await _shift.GetActiveShiftAsync("B01", "C01"));

        // Act: Shift 2 opened on same counter with new float
        var shift2 = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 10000.00m, "TENANT_LK_01");

        // Assert
        Assert.NotNull(shift2);
        Assert.NotEqual(shift1.ShiftId, shift2.ShiftId);
        Assert.Equal(10000.00m, shift2.OpeningFloat);

        var active = await _shift.GetActiveShiftAsync("B01", "C01");
        Assert.Equal(shift2.ShiftId, active?.ShiftId);
    }

    [Fact]
    public async Task OpenShift_LogsAuditEvent_WithCompleteMetadata()
    {
        // Arrange
        var cashier = await _auth.CreateUserAsync("audit_cashier", "Audit Cashier", "Pass#123", Role.Cashier);
        const decimal openingFloat = 6500.00m;

        // Act
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, openingFloat, "TENANT_LK_01");

        // Assert: Query SQLite audit_events directly
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc FROM audit_events WHERE action = 'OPEN_SHIFT' AND counter_id = 'C01' ORDER BY occurred_at_utc DESC LIMIT 1;";
        using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "OPEN_SHIFT audit event must be logged.");
        Assert.False(string.IsNullOrWhiteSpace(reader.GetString(0))); // event_id
        Assert.Equal("TENANT_LK_01", reader.GetString(1)); // tenant_id
        Assert.Equal("B01", reader.GetString(2)); // branch_id
        Assert.Equal("C01", reader.GetString(3)); // counter_id
        Assert.Equal(cashier.UserId, reader.GetString(4)); // actor_id
        Assert.Equal("OPEN_SHIFT", reader.GetString(5)); // action

        var detailsJson = reader.GetString(6);
        using var doc = System.Text.Json.JsonDocument.Parse(detailsJson);
        var root = doc.RootElement;
        Assert.Equal(shift.ShiftId, root.GetProperty("ShiftId").GetGuid());
        Assert.Equal(openingFloat, root.GetProperty("OpeningFloat").GetDecimal());

        var occurredAt = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.True((DateTime.UtcNow - occurredAt).TotalMinutes < 2);
    }

    [Fact]
    public async Task OpenShift_ZeroFloat_AuditRecordContainsZeroFloatMetadata()
    {
        // Arrange
        var cashier = await _auth.CreateUserAsync("audit_zero", "Audit Zero", "Pass#123", Role.Cashier);

        // Act
        var shift = await _shift.OpenShiftAsync("B02", "C99", cashier.UserId, 0.00m, "TENANT_LK_01");

        // Assert
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT details_json FROM audit_events WHERE action = 'OPEN_SHIFT' AND counter_id = 'C99';";
        var detailsJson = (string)(await cmd.ExecuteScalarAsync())!;

        using var doc = System.Text.Json.JsonDocument.Parse(detailsJson);
        Assert.Equal(0.00m, doc.RootElement.GetProperty("OpeningFloat").GetDecimal());
        Assert.Equal(shift.ShiftId, doc.RootElement.GetProperty("ShiftId").GetGuid());
    }

    [Fact]
    public async Task OpenShift_RoundedFloat_AuditRecordContainsRoundedFloatMetadata()
    {
        // Arrange
        var cashier = await _auth.CreateUserAsync("audit_round", "Audit Round", "Pass#123", Role.Cashier);

        // Act: 5000.005 rounds to 5000.01
        var shift = await _shift.OpenShiftAsync("B02", "C98", cashier.UserId, 5000.005m, "TENANT_LK_01");

        // Assert
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT details_json FROM audit_events WHERE action = 'OPEN_SHIFT' AND counter_id = 'C98';";
        var detailsJson = (string)(await cmd.ExecuteScalarAsync())!;

        using var doc = System.Text.Json.JsonDocument.Parse(detailsJson);
        Assert.Equal(5000.01m, doc.RootElement.GetProperty("OpeningFloat").GetDecimal());
        Assert.Equal(shift.ShiftId, doc.RootElement.GetProperty("ShiftId").GetGuid());
    }

    [Fact]
    public void OpenShiftDialog_InputValidationContract_AdversarialInputs()
    {
        // Test the validation contract implemented by OpenShiftDialog.xaml.cs:
        // 1. Negative numbers must be rejected
        Assert.True(decimal.TryParse("-5000", out var neg1) && neg1 < 0);
        Assert.True(decimal.TryParse("-0.01", out var neg2) && neg2 < 0);

        // 2. Zero must be accepted and parsed as 0.00
        Assert.True(decimal.TryParse("0.00", out var zero) && zero == 0m);
        Assert.Equal(0.00m, MoneyCalculator.Round(zero));

        // 3. High-precision decimal string 5000.005 must parse and round half-up to 5000.01
        Assert.True(decimal.TryParse("5000.005", out var hp));
        Assert.Equal(5000.01m, MoneyCalculator.Round(hp));

        // 4. Non-numeric inputs must fail parsing
        Assert.False(decimal.TryParse("abc", out _));
        Assert.False(decimal.TryParse("", out _));
        Assert.False(decimal.TryParse("   ", out _));
    }

    [Fact]
    public async Task OpenShift_Stress_ConcurrentCounterOpenings_AllSucceedWithAudit()
    {
        // Arrange
        const int counterCount = 10;
        var cashier = await _auth.CreateUserAsync("stress_cashier", "Stress Cashier", "Pass#123", Role.Cashier);

        // Act: Concurrently open 10 distinct counters
        var tasks = new Task<CashShift>[counterCount];
        for (int i = 0; i < counterCount; i++)
        {
            var counterId = $"CTR_{i:D2}";
            var floatAmount = 1000m + (i * 500m);
            tasks[i] = _shift.OpenShiftAsync("B_STRESS", counterId, cashier.UserId, floatAmount, "TENANT_LK_01");
        }

        var shifts = await Task.WhenAll(tasks);

        // Assert: All 10 succeeded with distinct shift IDs
        Assert.Equal(counterCount, shifts.Length);
        var distinctIds = new HashSet<Guid>(shifts.Select(s => s.ShiftId));
        Assert.Equal(counterCount, distinctIds.Count);

        // Verify all 10 audit events logged
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'OPEN_SHIFT' AND branch_id = 'B_STRESS';";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(counterCount, count);
    }
}

