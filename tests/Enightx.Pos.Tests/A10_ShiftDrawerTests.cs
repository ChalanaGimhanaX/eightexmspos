using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class A10_ShiftDrawerTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly ShiftService _shift;

    public A10_ShiftDrawerTests()
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
    public async Task A10_ShiftCashCalculation_MatchesHandCalculatedFixture()
    {
        // Fixture values:
        // Opening Float: 5,000.00
        // Cash Received: 25,450.00
        // Change Given: 3,250.00
        // Cash Refunds: 1,200.00
        // Cash In: 2,000.00
        // Cash Out: 4,000.00
        // Expected Cash = 5,000 + 25,450 - 3,250 - 1,200 + 2,000 - 4,000 = 24,000.00 LKR
        // Counted Cash: 23,950.00 LKR
        // Expected Variance = 23,950 - 24,000 = -50.00 LKR (shortage)

        var cashier = await _auth.CreateUserAsync("sunil", "Sunil Fernando", "Sunil#123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        // Simulate sales cash effects on shift
        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE shifts SET
                    cash_received = 25450.00,
                    change_given = 3250.00,
                    cash_refunds = 1200.00
                WHERE shift_id = $id;
            ";
            cmd.Parameters.AddWithValue("$id", shift.ShiftId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        // Cash In (petty cash deposit)
        await _shift.RecordCashMovementAsync(
            shift.ShiftId,
            2000.00m,
            isCashIn: true,
            reason: "Petty cash bank deposit",
            cashier.UserId,
            "TENANT_LK_01",
            "B01",
            "C01"
        );

        // Cash Out (supplier payment / owner withdrawal)
        await _shift.RecordCashMovementAsync(
            shift.ShiftId,
            4000.00m,
            isCashIn: false,
            reason: "Urgent local supplier payout",
            cashier.UserId,
            "TENANT_LK_01",
            "B01",
            "C01"
        );

        // Close shift with actual counted cash = 23,950.00
        var closedShift = await _shift.CloseShiftAsync(
            shift.ShiftId,
            actualCountedCash: 23950.00m,
            actorId: cashier.UserId,
            tenantId: "TENANT_LK_01"
        );

        Assert.Equal(ShiftStatus.Closed, closedShift.Status);
        Assert.Equal(24000.00m, closedShift.ExpectedCash);
        Assert.Equal(23950.00m, closedShift.ActualCountedCash);
        Assert.Equal(-50.00m, closedShift.Variance);
    }
}
