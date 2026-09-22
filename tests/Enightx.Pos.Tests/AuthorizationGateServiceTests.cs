using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class AuthorizationGateServiceTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _authService;

    public AuthorizationGateServiceTests()
    {
        _db = PosDatabase.CreateInMemory();
        _authService = new AuthService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private class MockPinPromptService : IPinPromptService
    {
        public Func<string, Role, Task<string?>> Handler { get; set; } = (_, _) => Task.FromResult<string?>(null);
        public int CallCount { get; private set; }
        public string? LastOperationName { get; private set; }
        public Role? LastRequiredRole { get; private set; }

        public async Task<PinPromptResult> PromptPinAsync(
            Role requiredRole,
            string operationName,
            string? tenantId = null,
            string? branchId = null,
            string? counterId = null)
        {
            CallCount++;
            LastOperationName = operationName;
            LastRequiredRole = requiredRole;

            var pin = await Handler(operationName, requiredRole);
            if (pin == null)
            {
                return PinPromptResult.Cancelled("User cancelled PIN prompt.");
            }

            return PinPromptResult.WithPin(pin);
        }

        public Task<PinPromptResult> PromptPinAsync(string title, string message, Role minimumRole)
            => PromptPinAsync(minimumRole, string.IsNullOrWhiteSpace(message) ? title : $"{title}: {message}");
    }

    // -------------------------------------------------------------------------
    // 1. AUTO-PASS SPECIFICATION (Role >= RequiredRole)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AuthorizeOperation_OwnerRequestingManagerOp_AutoPassesWithoutPinPrompt()
    {
        // Arrange
        var owner = await _authService.CreateUserAsync("owner1", "Shop Owner", "OwnerPass123", Role.Owner, "9999");
        var prompt = new MockPinPromptService();
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        // Act
        bool result = await gate.AuthorizeOperationAsync(owner, Role.Manager, "Price Override");

        // Assert
        Assert.True(result, "Owner must auto-pass Manager operations.");
        Assert.Equal(0, prompt.CallCount); // Zero UI dialog interaction
        Assert.NotNull(gate.LastAuthorizingUser);
        Assert.Equal(owner.UserId, gate.LastAuthorizingUser.UserId);
    }

    [Fact]
    public async Task AuthorizeOperation_ManagerRequestingManagerOp_AutoPassesWithoutPinPrompt()
    {
        // Arrange
        var manager = await _authService.CreateUserAsync("mgr1", "Store Manager", "MgrPass123", Role.Manager, "1234");
        var prompt = new MockPinPromptService();
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        // Act
        bool result = await gate.AuthorizeOperationAsync(manager, Role.Manager, "Excessive Discount");

        // Assert
        Assert.True(result, "Manager must auto-pass Manager operations.");
        Assert.Equal(0, prompt.CallCount);
        Assert.NotNull(gate.LastAuthorizingUser);
        Assert.Equal(manager.UserId, gate.LastAuthorizingUser.UserId);
    }

    // -------------------------------------------------------------------------
    // 2. ESCALATION REQUIRED (Cashier Requesting Manager Op)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AuthorizeOperation_CashierWithValidManagerPin_AuthorizesAndLogsVerifiedAudit()
    {
        // Arrange
        var cashier = await _authService.CreateUserAsync("cashier1", "Cashier Nimal", "CashierPass1", Role.Cashier, "1111");
        var manager = await _authService.CreateUserAsync("mgr2", "Manager Sunil", "MgrPass123", Role.Manager, "4321");

        var prompt = new MockPinPromptService
        {
            Handler = (op, role) => Task.FromResult<string?>("4321") // Enters valid Manager PIN
        };
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        // Act
        bool result = await gate.AuthorizeOperationAsync(cashier, Role.Manager, "Refund Issue");

        // Assert
        Assert.True(result);
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal("Refund Issue", prompt.LastOperationName);
        Assert.Equal(Role.Manager, prompt.LastRequiredRole);
        Assert.NotNull(gate.LastAuthorizingUser);
        Assert.Equal(manager.UserId, gate.LastAuthorizingUser.UserId);

        // Assert SQLite audit_events contains MANAGER_PIN_VERIFIED
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT action, actor_id, details_json 
            FROM audit_events 
            WHERE action = 'MANAGER_PIN_VERIFIED' 
            ORDER BY occurred_at_utc DESC LIMIT 1;
        ";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Audit event MANAGER_PIN_VERIFIED must be logged.");
        Assert.Equal("MANAGER_PIN_VERIFIED", reader.GetString(0));
        Assert.Equal(manager.UserId, reader.GetString(1));

        var details = reader.GetString(2);
        Assert.Contains("Refund Issue", details);
        Assert.Contains(manager.UserId, details);
    }

    // -------------------------------------------------------------------------
    // 3. INVALID PIN REJECTION & AUDIT LOGGING
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AuthorizeOperation_CashierWithInvalidPin_RejectsAndLogsFailedAudit()
    {
        // Arrange
        var cashier = await _authService.CreateUserAsync("cashier2", "Cashier Kamal", "Pass123", Role.Cashier);
        await _authService.CreateUserAsync("mgr3", "Manager Perera", "Pass123", Role.Manager, "8888");

        var prompt = new MockPinPromptService
        {
            Handler = (op, role) => Task.FromResult<string?>("0000") // Wrong PIN
        };
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        // Act
        bool result = await gate.AuthorizeOperationAsync(cashier, Role.Manager, "Manual Price Override");

        // Assert
        Assert.False(result);
        Assert.Equal(1, prompt.CallCount);
        Assert.Null(gate.LastAuthorizingUser);

        // Assert SQLite audit_events contains MANAGER_PIN_FAILED
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT action, details_json 
            FROM audit_events 
            WHERE action = 'MANAGER_PIN_FAILED' 
            ORDER BY occurred_at_utc DESC LIMIT 1;
        ";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Audit event MANAGER_PIN_FAILED must be logged.");
        Assert.Equal("MANAGER_PIN_FAILED", reader.GetString(0));

        var details = reader.GetString(1);
        Assert.Contains("Manual Price Override", details);
    }

    // -------------------------------------------------------------------------
    // 4. NON-MANAGER PIN (PEER CASHIER PIN) CANNOT AUTHORIZE
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AuthorizeOperation_PeerCashierPin_RejectedForManagerOperation()
    {
        // Arrange
        var cashier1 = await _authService.CreateUserAsync("cashier_op", "Operator Cashier", "Pass1", Role.Cashier);
        var cashier2 = await _authService.CreateUserAsync("cashier_peer", "Peer Cashier", "Pass2", Role.Cashier, "7777");

        var prompt = new MockPinPromptService
        {
            Handler = (op, role) => Task.FromResult<string?>("7777") // Valid PIN, but user is only Cashier
        };
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        // Act
        bool result = await gate.AuthorizeOperationAsync(cashier1, Role.Manager, "Stock Adjustment");

        // Assert
        Assert.False(result, "Peer Cashier PIN must NEVER authorize a Manager-level operation.");
        Assert.Null(gate.LastAuthorizingUser);

        // Audit failure verification
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_FAILED';";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.True(count >= 1);
    }

    // -------------------------------------------------------------------------
    // 5. CANCELLED DIALOG REJECTION & AUDIT LOGGING
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AuthorizeOperation_CancelledDialog_RejectsOperationAndLogsAudit()
    {
        // Arrange
        var cashier = await _authService.CreateUserAsync("cashier3", "Cashier Ruwan", "Pass1", Role.Cashier);
        var prompt = new MockPinPromptService
        {
            Handler = (op, role) => Task.FromResult<string?>(null) // User clicks 'Cancel'
        };
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        // Act
        bool result = await gate.AuthorizeOperationAsync(cashier, Role.Manager, "Price Override");

        // Assert
        Assert.False(result);
        Assert.Equal(1, prompt.CallCount);
        Assert.Null(gate.LastAuthorizingUser);

        // Assert cancellation audit event logged
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT action, details_json 
            FROM audit_events 
            WHERE action = 'MANAGER_PIN_FAILED' OR action = 'AUTHORIZATION_CANCELLED'
            ORDER BY occurred_at_utc DESC LIMIT 1;
        ";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var details = reader.GetString(1);
        Assert.Contains("Price Override", details);
    }

    // -------------------------------------------------------------------------
    // 6. ADVERSARIAL & EDGE CASES
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AuthorizeOperation_EmptyOrWhitespacePin_RejectedWithoutDatabaseError(string? invalidPin)
    {
        var cashier = await _authService.CreateUserAsync("cashier_adv", "Adv Cashier", "Pass1", Role.Cashier);
        var prompt = new MockPinPromptService
        {
            Handler = (_, _) => Task.FromResult(invalidPin)
        };
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        bool result = await gate.AuthorizeOperationAsync(cashier, Role.Manager, "Discount Limit");
        Assert.False(result);
        Assert.Null(gate.LastAuthorizingUser);
    }

    [Fact]
    public async Task AuthorizeOperation_NullCurrentUser_GracefullyReturnsFalse()
    {
        var prompt = new MockPinPromptService();
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        bool result = await gate.AuthorizeOperationAsync(null!, Role.Manager, "Any Operation");
        Assert.False(result);
        Assert.Equal(0, prompt.CallCount);
    }

    [Fact]
    public async Task AuthorizeOperation_DeactivatedManagerPin_Rejected()
    {
        var cashier = await _authService.CreateUserAsync("cashier_deact", "Cashier", "P1", Role.Cashier);
        var manager = await _authService.CreateUserAsync("mgr_deact", "Inactive Mgr", "P2", Role.Manager, "5566");

        // Deactivate manager
        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE users SET is_active = 0 WHERE user_id = $id;";
            cmd.Parameters.AddWithValue("$id", manager.UserId);
            await cmd.ExecuteNonQueryAsync();
        }

        var prompt = new MockPinPromptService
        {
            Handler = (_, _) => Task.FromResult<string?>("5566")
        };
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        bool result = await gate.AuthorizeOperationAsync(cashier, Role.Manager, "Stock Adjustment");
        Assert.False(result);
        Assert.Null(gate.LastAuthorizingUser);
    }

    [Fact]
    public async Task AuthorizeOperation_ManagerAttemptingOwnerAction_RequiresOwnerElevation()
    {
        var manager = await _authService.CreateUserAsync("mgr_elev", "Manager User", "Pass1", Role.Manager, "4444");
        var owner = await _authService.CreateUserAsync("owner_elev", "Owner User", "Pass2", Role.Owner, "8888");

        // Attempt A: Manager enters own PIN for Owner operation -> REJECTED
        var promptA = new MockPinPromptService
        {
            Handler = (_, _) => Task.FromResult<string?>("4444")
        };
        var gateA = new AuthorizationGateService(_authService, promptA, _db);
        bool resultA = await gateA.AuthorizeOperationAsync(manager, Role.Owner, "Cloud Branch Config");
        Assert.False(resultA, "Manager PIN must not authorize Owner operation");

        // Attempt B: Owner enters PIN for Owner operation -> APPROVED
        var promptB = new MockPinPromptService
        {
            Handler = (_, _) => Task.FromResult<string?>("8888")
        };
        var gateB = new AuthorizationGateService(_authService, promptB, _db);
        bool resultB = await gateB.AuthorizeOperationAsync(manager, Role.Owner, "Cloud Branch Config");
        Assert.True(resultB, "Owner PIN must authorize Owner operation");
        Assert.NotNull(gateB.LastAuthorizingUser);
        Assert.Equal(owner.UserId, gateB.LastAuthorizingUser.UserId);
    }

    [Fact]
    public async Task AuthorizeWithResultAsync_ReturnsStructuredDetails()
    {
        var cashier = await _authService.CreateUserAsync("cashier_struct", "Cashier", "Pass1", Role.Cashier);
        var manager = await _authService.CreateUserAsync("mgr_struct", "Manager", "Pass2", Role.Manager, "1212");

        var prompt = new MockPinPromptService
        {
            Handler = (_, _) => Task.FromResult<string?>("1212")
        };
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        var authResult = await gate.AuthorizeWithResultAsync(cashier, Role.Manager, "Structure Test");
        Assert.True(authResult.IsAuthorized);
        Assert.True(authResult.ElevationRequired);
        Assert.NotNull(authResult.AuthorizingUser);
        Assert.Equal(manager.UserId, authResult.AuthorizingUser.UserId);
    }

    [Fact]
    public async Task AuthorizeWithResultAsync_DeactivatedManagerInDatabase_DirectCall_StrictlyRejectedAndAudited()
    {
        var manager = await _authService.CreateUserAsync("mgr_direct_deact", "Direct Deact Mgr", "Pass1", Role.Manager, "3333");

        // Deactivate in DB while in-memory user object claims active
        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE users SET is_active = 0 WHERE user_id = $id;";
            cmd.Parameters.AddWithValue("$id", manager.UserId);
            await cmd.ExecuteNonQueryAsync();
        }

        var prompt = new MockPinPromptService();
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        var result = await gate.AuthorizeWithResultAsync(manager, Role.Manager, "Deact Direct Check");
        Assert.False(result.IsAuthorized);
        Assert.Contains("deactivated", result.FailureReason, StringComparison.OrdinalIgnoreCase);

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_FAILED' AND actor_id = $id;";
            cmd.Parameters.AddWithValue("$id", manager.UserId);
            var failCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.True(failCount >= 1);
        }
    }

    [Fact]
    public async Task AuthorizeWithResultAsync_CashierClaimingManager_DirectCall_StrictlyRejectedAndAudited()
    {
        var cashier = await _authService.CreateUserAsync("cashier_spoof_unit", "Cashier Unit", "Pass1", Role.Cashier);

        // Mutate in-memory role to Manager
        var spoofedUser = new User
        {
            UserId = cashier.UserId,
            Username = cashier.Username,
            DisplayName = cashier.DisplayName,
            Role = Role.Manager, // Claims Manager
            PasswordHash = cashier.PasswordHash,
            PasswordSalt = cashier.PasswordSalt,
            IsActive = true
        };

        var prompt = new MockPinPromptService();
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        var result = await gate.AuthorizeWithResultAsync(spoofedUser, Role.Manager, "Price Override");
        Assert.False(result.IsAuthorized);

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_FAILED' AND actor_id = $id;";
            cmd.Parameters.AddWithValue("$id", cashier.UserId);
            var failCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.True(failCount >= 1);
        }
    }

    [Fact]
    public async Task AuthorizeWithResultAsync_ElevatedApproval_DeduplicatedAuditLog_ExactlyOneManagerPinVerified()
    {
        var cashier = await _authService.CreateUserAsync("cashier_audit_dedup", "Cashier Audit", "Pass1", Role.Cashier);
        var manager = await _authService.CreateUserAsync("mgr_audit_dedup", "Manager Audit", "Pass2", Role.Manager, "7733");

        var prompt = new MockPinPromptService
        {
            Handler = (_, _) => Task.FromResult<string?>("7733")
        };
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        var result = await gate.AuthorizeWithResultAsync(cashier, Role.Manager, "Deduplication Test");
        Assert.True(result.IsAuthorized);

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED' AND actor_id = $id;";
            cmd.Parameters.AddWithValue("$id", manager.UserId);
            var verifiedCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, verifiedCount); // Strictly 1, not 2
        }
    }
}
