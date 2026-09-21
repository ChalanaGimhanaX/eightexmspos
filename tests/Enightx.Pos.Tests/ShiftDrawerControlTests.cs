using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class ShiftDrawerControlTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly ShiftService _shift;
    private readonly CatalogService _catalog;
    private readonly SaleService _sale;

    public ShiftDrawerControlTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _shift = new ShiftService(_db);
        _catalog = new CatalogService(_db);
        _sale = new SaleService(_db, _catalog);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task OpenShift_SetsOpeningFloatAndInitialExpectedCash()
    {
        var cashier = await _auth.CreateUserAsync("anoma", "Anoma Silva", "Pass#123", Role.Cashier);

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 7500.00m, "TENANT_LK_01");

        Assert.NotNull(shift);
        Assert.Equal(7500.00m, shift.OpeningFloat);
        Assert.Equal(7500.00m, shift.ExpectedCash);
        Assert.Equal(0m, shift.CashReceived);
        Assert.Equal(0m, shift.ChangeGiven);
        Assert.Equal(0m, shift.CashRefunds);
        Assert.Equal(0m, shift.CashIn);
        Assert.Equal(0m, shift.CashOut);
        Assert.Equal(ShiftStatus.Open, shift.Status);

        // Cannot open a second shift on the same counter while one is open
        await Assert.ThrowsAsync<PosException>(() =>
            _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01")
        );
    }

    [Fact]
    public async Task CashIn_And_CashOut_TrackCumulativeTotals_AndRecordIndividualMovements()
    {
        var cashier = await _auth.CreateUserAsync("priyantha", "Priyantha K", "Pass#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        // 1. Cash In: 3,000 LKR (Bank float addition)
        await _shift.RecordCashMovementAsync(
            shift.ShiftId,
            3000.00m,
            isCashIn: true,
            reason: "Bank float addition 500x6",
            actorId: cashier.UserId,
            tenantId: "TENANT_LK_01",
            branchId: "B01",
            counterId: "C01"
        );

        // 2. Cash In: 1,500 LKR (Another float addition)
        await _shift.RecordCashMovementAsync(
            shift.ShiftId,
            1500.00m,
            isCashIn: true,
            reason: "Extra coin roll",
            actorId: cashier.UserId,
            tenantId: "TENANT_LK_01",
            branchId: "B01",
            counterId: "C01"
        );

        // 3. Cash Out: 2,000 LKR (Vendor payout)
        await _shift.RecordCashMovementAsync(
            shift.ShiftId,
            2000.00m,
            isCashIn: false,
            reason: "Tea and refreshments delivery",
            actorId: cashier.UserId,
            tenantId: "TENANT_LK_01",
            branchId: "B01",
            counterId: "C01"
        );

        // 4. Verify updated shift state
        var activeShift = await _shift.GetActiveShiftAsync("B01", "C01");
        Assert.NotNull(activeShift);
        Assert.Equal(4500.00m, activeShift.CashIn); // 3000 + 1500
        Assert.Equal(2000.00m, activeShift.CashOut);

        // 5. Verify movements list
        var movements = await _shift.GetCashMovementsForShiftAsync(shift.ShiftId);
        Assert.Equal(3, movements.Count);

        Assert.Equal("CASH_IN", movements[0].MovementType);
        Assert.Equal(3000.00m, movements[0].Amount);
        Assert.Equal("Bank float addition 500x6", movements[0].Reason);

        Assert.Equal("CASH_IN", movements[1].MovementType);
        Assert.Equal(1500.00m, movements[1].Amount);

        Assert.Equal("CASH_OUT", movements[2].MovementType);
        Assert.Equal(2000.00m, movements[2].Amount);
        Assert.Equal("Tea and refreshments delivery", movements[2].Reason);

        // 6. Invalid amount should throw ArgumentException
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _shift.RecordCashMovementAsync(shift.ShiftId, 0m, true, "Invalid", cashier.UserId, "TENANT_LK_01", "B01", "C01")
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _shift.RecordCashMovementAsync(shift.ShiftId, -500m, true, "Negative", cashier.UserId, "TENANT_LK_01", "B01", "C01")
        );
    }

    [Fact]
    public async Task ShiftClosing_CalculatesExactVariance_ForOverage_Shortage_AndBalanced()
    {
        var cashier = await _auth.CreateUserAsync("duminda", "Duminda F", "Pass#123", Role.Cashier);

        // Test Scenario 1: Balanced (Counted == Expected)
        // Opening: 10,000, Cash In: 1,000, Cash Out: 500 => Expected: 10,500
        var shift1 = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 10000.00m, "TENANT_LK_01");
        await _shift.RecordCashMovementAsync(shift1.ShiftId, 1000m, true, "Float top-up", cashier.UserId, "TENANT_LK_01", "B01", "C01");
        await _shift.RecordCashMovementAsync(shift1.ShiftId, 500m, false, "Office supplies", cashier.UserId, "TENANT_LK_01", "B01", "C01");

        var closed1 = await _shift.CloseShiftAsync(shift1.ShiftId, actualCountedCash: 10500.00m, actorId: cashier.UserId, tenantId: "TENANT_LK_01");
        Assert.Equal(10500.00m, closed1.ExpectedCash);
        Assert.Equal(10500.00m, closed1.ActualCountedCash);
        Assert.Equal(0.00m, closed1.Variance);
        Assert.Equal(ShiftStatus.Closed, closed1.Status);

        // Test Scenario 2: Overage (Counted > Expected)
        var shift2 = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");
        var closed2 = await _shift.CloseShiftAsync(shift2.ShiftId, actualCountedCash: 5200.00m, actorId: cashier.UserId, tenantId: "TENANT_LK_01");
        Assert.Equal(5000.00m, closed2.ExpectedCash);
        Assert.Equal(5200.00m, closed2.ActualCountedCash);
        Assert.Equal(200.00m, closed2.Variance); // +200 overage

        // Test Scenario 3: Shortage (Counted < Expected)
        var shift3 = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");
        var closed3 = await _shift.CloseShiftAsync(shift3.ShiftId, actualCountedCash: 4850.00m, actorId: cashier.UserId, tenantId: "TENANT_LK_01");
        Assert.Equal(5000.00m, closed3.ExpectedCash);
        Assert.Equal(4850.00m, closed3.ActualCountedCash);
        Assert.Equal(-150.00m, closed3.Variance); // -150 shortage
    }

    [Fact]
    public async Task ClosedShift_RejectsSubsequentOperations()
    {
        var cashier = await _auth.CreateUserAsync("jagath", "Jagath W", "Pass#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        await _shift.CloseShiftAsync(shift.ShiftId, 5000.00m, cashier.UserId, "TENANT_LK_01");

        // Attempting to close again throws PosException
        await Assert.ThrowsAsync<PosException>(() =>
            _shift.CloseShiftAsync(shift.ShiftId, 5000.00m, cashier.UserId, "TENANT_LK_01")
        );

        // Attempting cash movement on closed shift throws ShiftClosedException
        await Assert.ThrowsAsync<ShiftClosedException>(() =>
            _shift.RecordCashMovementAsync(shift.ShiftId, 500m, true, "Late deposit", cashier.UserId, "TENANT_LK_01", "B01", "C01")
        );
    }

    [Fact]
    public async Task ShiftValidation_RejectsNegativeFloatCountedCashAndEmptyMovementReason()
    {
        var cashier = await _auth.CreateUserAsync("valid_cashier", "Valid Cashier", "Pass#123", Role.Cashier);

        // 1. Negative opening float
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _shift.OpenShiftAsync("B01", "C01", cashier.UserId, -100m, "TENANT_LK_01")
        );

        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 1000m, "TENANT_LK_01");

        // 2. Empty cash movement reason
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _shift.RecordCashMovementAsync(shift.ShiftId, 500m, true, "   ", cashier.UserId, "TENANT_LK_01", "B01", "C01")
        );

        // 3. Negative actual counted cash on close
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _shift.CloseShiftAsync(shift.ShiftId, -50m, cashier.UserId, "TENANT_LK_01")
        );
    }
}

