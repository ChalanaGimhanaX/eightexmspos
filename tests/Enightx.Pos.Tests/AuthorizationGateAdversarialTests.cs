using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class AuthorizationGateAdversarialTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _authService;

    public AuthorizationGateAdversarialTests()
    {
        _db = PosDatabase.CreateInMemory();
        _authService = new AuthService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private class TestPinPromptService : IPinPromptService
    {
        public Func<Role, string, Task<PinPromptResult>> PromptFunc { get; set; } =
            (_, _) => Task.FromResult(PinPromptResult.Cancelled());

        public int CallCount { get; private set; }
        public Role? LastRole { get; private set; }
        public string? LastOperation { get; private set; }

        public async Task<PinPromptResult> PromptPinAsync(
            Role requiredRole,
            string operationName,
            string? tenantId = null,
            string? branchId = null,
            string? counterId = null)
        {
            CallCount++;
            LastRole = requiredRole;
            LastOperation = operationName;
            return await PromptFunc(requiredRole, operationName);
        }

        public Task<PinPromptResult> PromptPinAsync(string title, string message, Role minimumRole)
            => PromptPinAsync(minimumRole, string.IsNullOrWhiteSpace(message) ? title : $"{title}: {message}");
    }

    // -------------------------------------------------------------------------
    // 1. CASHIER ENTERING ANOTHER CASHIER'S PIN
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CashierEnteringAnotherCashiersPin_ForManagerOperation_StrictlyRejectedAndLogsFailed()
    {
        // Arrange
        var cashier1 = await _authService.CreateUserAsync("cashier_active_1", "Cashier One", "Pass1!", Role.Cashier, "1234");
        var cashier2 = await _authService.CreateUserAsync("cashier_active_2", "Cashier Two", "Pass2!", Role.Cashier, "5678");
        var manager = await _authService.CreateUserAsync("manager_real", "Store Manager", "MgrPass1!", Role.Manager, "9999");

        var prompt = new TestPinPromptService
        {
            // Cashier 1 attempts operation, but enters Cashier 2's valid PIN
            PromptFunc = (_, _) => Task.FromResult(PinPromptResult.WithPin("5678"))
        };

        var gate = new AuthorizationGateService(_authService, prompt, _db);

        // Act
        var result = await gate.AuthorizeWithResultAsync(cashier1, Role.Manager, "Price Override");

        // Assert
        Assert.False(result.IsAuthorized, "Cashier entering another cashier's PIN must be strictly rejected.");
        Assert.Null(gate.LastAuthorizingUser);
        Assert.Null(result.AuthorizingUser);

        // Verify audit log: MANAGER_PIN_FAILED logged
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT action, actor_id, details_json
            FROM audit_events
            WHERE action = 'MANAGER_PIN_FAILED'
            ORDER BY occurred_at_utc DESC LIMIT 1;
        ";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "MANAGER_PIN_FAILED event must be recorded in audit log.");
        Assert.Equal("MANAGER_PIN_FAILED", reader.GetString(0));

        var detailsJson = reader.GetString(2);
        Assert.Contains(cashier1.UserId, detailsJson);
        Assert.Contains("Price Override", detailsJson);

        // Verify NO MANAGER_PIN_VERIFIED was emitted anywhere in audit_events
        using var cmdVerified = conn.CreateCommand();
        cmdVerified.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED';";
        var verifiedCount = Convert.ToInt32(await cmdVerified.ExecuteScalarAsync());
        Assert.Equal(0, verifiedCount);
    }

    // -------------------------------------------------------------------------
    // 2. INVALID PIN ATTEMPTS, BLANK/WHITESPACE PIN, CANCELLED DIALOG
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("0000", "Wrong numeric PIN")]
    [InlineData("wrong_alpha", "Alphanumeric invalid PIN")]
    [InlineData("!@#$%", "Special characters invalid PIN")]
    public async Task InvalidPinAttempts_StrictlyRejectedAndAudited(string wrongPin, string description)
    {
        var cashier = await _authService.CreateUserAsync($"cashier_{Guid.NewGuid():N}", "Cashier", "Pass1!", Role.Cashier);
        await _authService.CreateUserAsync($"mgr_{Guid.NewGuid():N}", "Manager", "Pass2!", Role.Manager, "1111");

        var prompt = new TestPinPromptService
        {
            PromptFunc = (_, _) => Task.FromResult(PinPromptResult.WithPin(wrongPin))
        };
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        var result = await gate.AuthorizeWithResultAsync(cashier, Role.Manager, $"Test {description}");

        Assert.False(result.IsAuthorized);
        Assert.Null(gate.LastAuthorizingUser);

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_FAILED';";
        var failCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.True(failCount >= 1, "Failed PIN attempt must log MANAGER_PIN_FAILED.");

        using var cmdVerified = conn.CreateCommand();
        cmdVerified.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED';";
        Assert.Equal(0, Convert.ToInt32(await cmdVerified.ExecuteScalarAsync()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task BlankOrWhitespacePin_StrictlyRejectedAndAudited(string emptyPin)
    {
        var cashier = await _authService.CreateUserAsync($"cashier_{Guid.NewGuid():N}", "Cashier", "Pass1!", Role.Cashier);
        await _authService.CreateUserAsync($"mgr_{Guid.NewGuid():N}", "Manager", "Pass2!", Role.Manager, "2222");

        var prompt = new TestPinPromptService
        {
            PromptFunc = (_, _) => Task.FromResult(PinPromptResult.WithPin(emptyPin))
        };
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        var result = await gate.AuthorizeWithResultAsync(cashier, Role.Manager, "Excessive Discount");

        Assert.False(result.IsAuthorized);
        Assert.Null(gate.LastAuthorizingUser);

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_FAILED';";
        var failCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.True(failCount >= 1, "Blank or whitespace PIN must log MANAGER_PIN_FAILED.");

        using var cmdVerified = conn.CreateCommand();
        cmdVerified.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED';";
        Assert.Equal(0, Convert.ToInt32(await cmdVerified.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task CancelledDialog_StrictlyRejectedAndAudited()
    {
        var cashier = await _authService.CreateUserAsync("cashier_cancel", "Cashier", "Pass1!", Role.Cashier);
        await _authService.CreateUserAsync("mgr_cancel", "Manager", "Pass2!", Role.Manager, "3333");

        var prompt = new TestPinPromptService
        {
            PromptFunc = (_, _) => Task.FromResult(PinPromptResult.Cancelled("User clicked cancel"))
        };
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        var result = await gate.AuthorizeWithResultAsync(cashier, Role.Manager, "Stock Adjustment");

        Assert.False(result.IsAuthorized);
        Assert.Null(gate.LastAuthorizingUser);

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_FAILED';";
        Assert.True(Convert.ToInt32(await cmd.ExecuteScalarAsync()) >= 1);

        using var cmdVerified = conn.CreateCommand();
        cmdVerified.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED';";
        Assert.Equal(0, Convert.ToInt32(await cmdVerified.ExecuteScalarAsync()));
    }

    // -------------------------------------------------------------------------
    // 3. DEACTIVATED STAFF MEMBER'S PIN
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeactivatedStaffMemberPin_StrictlyRejectedEvenWithCorrectPin()
    {
        var cashier = await _authService.CreateUserAsync("cashier_deact_test", "Cashier", "Pass1!", Role.Cashier);
        var manager = await _authService.CreateUserAsync("mgr_deact_test", "Terminated Manager", "Pass2!", Role.Manager, "7788");

        // Deactivate manager in DB
        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE users SET is_active = 0 WHERE user_id = $id;";
            cmd.Parameters.AddWithValue("$id", manager.UserId);
            await cmd.ExecuteNonQueryAsync();
        }

        var prompt = new TestPinPromptService
        {
            PromptFunc = (_, _) => Task.FromResult(PinPromptResult.WithPin("7788"))
        };
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        var result = await gate.AuthorizeWithResultAsync(cashier, Role.Manager, "Refund Issue");

        Assert.False(result.IsAuthorized, "Deactivated staff member's PIN must be strictly rejected.");
        Assert.Null(gate.LastAuthorizingUser);

        using var conn2 = _db.CreateConnection();
        using var cmdFail = conn2.CreateCommand();
        cmdFail.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_FAILED';";
        Assert.True(Convert.ToInt32(await cmdFail.ExecuteScalarAsync()) >= 1);

        using var cmdVerified = conn2.CreateCommand();
        cmdVerified.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED';";
        Assert.Equal(0, Convert.ToInt32(await cmdVerified.ExecuteScalarAsync()));
    }

    // -------------------------------------------------------------------------
    // 4. MANAGER_PIN_VERIFIED EMISSION DISCIPLINE
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ManagerPinVerified_OnlyEmittedUponValidPbkdf2Match_ForActiveStaffWithRoleGreaterOrEqualRequired()
    {
        var cashier = await _authService.CreateUserAsync("cashier_p_test", "Cashier", "Pass1!", Role.Cashier);
        var manager = await _authService.CreateUserAsync("mgr_p_test", "Manager", "Pass2!", Role.Manager, "4455");
        var owner = await _authService.CreateUserAsync("owner_p_test", "Owner", "Pass3!", Role.Owner, "9900");

        var prompt = new TestPinPromptService();
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        // Case A: Valid Manager PIN for Manager operation -> Exactly 1 MANAGER_PIN_VERIFIED
        prompt.PromptFunc = (_, _) => Task.FromResult(PinPromptResult.WithPin("4455"));
        var resultA = await gate.AuthorizeWithResultAsync(cashier, Role.Manager, "Price Override");
        Assert.True(resultA.IsAuthorized);
        Assert.Equal(manager.UserId, resultA.AuthorizingUser?.UserId);

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED';";
            Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }

        // Case B: Valid Manager PIN for Owner operation -> Rejected, NO new MANAGER_PIN_VERIFIED
        prompt.PromptFunc = (_, _) => Task.FromResult(PinPromptResult.WithPin("4455"));
        var resultB = await gate.AuthorizeWithResultAsync(cashier, Role.Owner, "Owner Configuration");
        Assert.False(resultB.IsAuthorized);

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED';";
            // Count must still be exactly 1 from Case A
            Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }

        // Case C: Valid Owner PIN for Manager operation -> Exactly 1 new MANAGER_PIN_VERIFIED (Owner >= Manager)
        prompt.PromptFunc = (_, _) => Task.FromResult(PinPromptResult.WithPin("9900"));
        var resultC = await gate.AuthorizeWithResultAsync(cashier, Role.Manager, "Refund Issue");
        Assert.True(resultC.IsAuthorized);
        Assert.Equal(owner.UserId, resultC.AuthorizingUser?.UserId);

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED';";
            Assert.Equal(2, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }
    }

    // -------------------------------------------------------------------------
    // 5. ADVERSARIAL VULNERABILITY EXPOSURES & FINDINGS
    // -------------------------------------------------------------------------

    [Fact]
    public async Task VULNERABILITY_RoleSpoofing_UserClaimingManagerRole_MissingFromDatabase_DirectlyBypassesGate()
    {
        // Remediation: A forged User object claiming Role.Manager is passed directly to the gate.
        // This user DOES NOT EXIST in the SQLite database.
        var spoofedManager = new User
        {
            UserId = "usr_ghost_spoof",
            Username = "ghost_manager",
            DisplayName = "Ghost Impostor",
            Role = Role.Manager,
            PasswordHash = "fake",
            PasswordSalt = "fake",
            IsActive = true
        };

        var prompt = new TestPinPromptService();
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        // Act
        var result = await gate.AuthorizeWithResultAsync(spoofedManager, Role.Manager, "Price Override");

        // Assert: Unauthenticated user missing from database must be strictly rejected
        Assert.False(result.IsAuthorized, "Unauthenticated user missing from database must be strictly rejected.");
        Assert.Null(result.AuthorizingUser);

        // Audit event MANAGER_PIN_FAILED must be logged for the spoofed attempt
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_FAILED' AND actor_id = 'usr_ghost_spoof';";
        var failCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.True(failCount >= 1, "Failed direct authorization must log MANAGER_PIN_FAILED.");

        using var cmdVerified = conn.CreateCommand();
        cmdVerified.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED';";
        Assert.Equal(0, Convert.ToInt32(await cmdVerified.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task VULNERABILITY_RoleSpoofing_UserInDatabaseAsCashier_ClaimingManagerInMemory_DirectlyBypassesGate()
    {
        // Remediation: Cashier account exists in DB with role=Cashier (1).
        // The in-memory User object has its Role property mutated/spoofed to Role.Manager (2).
        var cashier = await _authService.CreateUserAsync("cashier_tamper", "Cashier Tampered", "Pass1!", Role.Cashier);

        var tamperedUser = new User
        {
            UserId = cashier.UserId,
            Username = cashier.Username,
            DisplayName = cashier.DisplayName,
            Role = Role.Manager, // Tampered to Manager in memory
            PasswordHash = cashier.PasswordHash,
            PasswordSalt = cashier.PasswordSalt,
            IsActive = true
        };

        var prompt = new TestPinPromptService();
        var gate = new AuthorizationGateService(_authService, prompt, _db);

        var result = await gate.AuthorizeWithResultAsync(tamperedUser, Role.Manager, "Manual Stock Adjustment");

        // Assert: The gate checks the database to verify the user's actual stored role, rejecting the in-memory elevation.
        Assert.False(result.IsAuthorized, "Cashier with tampered in-memory Role must be rejected by database role verification.");
        Assert.Null(result.AuthorizingUser);

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_FAILED' AND actor_id = $uid;";
        cmd.Parameters.AddWithValue("$uid", cashier.UserId);
        var failCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.True(failCount >= 1, "Tampered role attempt must log MANAGER_PIN_FAILED.");

        using var cmdVerified = conn.CreateCommand();
        cmdVerified.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED';";
        Assert.Equal(0, Convert.ToInt32(await cmdVerified.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task VULNERABILITY_ManagerPinVerified_EmittedWithoutPbkdf2Verification_WhenAuthorizedUserReturned()
    {
        // Remediation: A prompt returns PinPromptResult.Succeeded(fakeUser) without a PIN.
        var cashier = await _authService.CreateUserAsync("cashier_spoof_pbkdf2", "Cashier", "Pass1!", Role.Cashier);

        var fakeAuthorizer = new User
        {
            UserId = "usr_unverified_mgr",
            Username = "unverified_mgr",
            DisplayName = "Unverified Manager",
            Role = Role.Manager,
            PasswordHash = "dummy",
            PasswordSalt = "dummy",
            IsActive = true
        };

        var prompt = new TestPinPromptService
        {
            PromptFunc = (_, _) => Task.FromResult(PinPromptResult.Succeeded(fakeAuthorizer))
        };

        var gate = new AuthorizationGateService(_authService, prompt, _db);
        var result = await gate.AuthorizeWithResultAsync(cashier, Role.Manager, "Refund Issue");

        // Assert that the gate NEVER logged MANAGER_PIN_VERIFIED because NO PBKDF2 hash was computed!
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED' AND actor_id = 'usr_unverified_mgr';";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        Assert.Equal(0, count); // Confirms MANAGER_PIN_VERIFIED is strictly NOT emitted without PBKDF2 match!
    }

    [Fact]
    public async Task VULNERABILITY_OwnerAction_ManagerPinInDialog_EmitsManagerPinVerifiedBeforeBeingDenied()
    {
        // Simulation of ManagerPinDialog behavior with remediation:
        // ManagerPinDialog passes `minimumRole: reqRole` (Role.Owner) when calling AuthService.VerifyPinAsync.
        // For an Owner-level action, when a Manager enters their PIN, VerifyPinAsync rejects it.
        var cashier = await _authService.CreateUserAsync("cashier_dlg_test", "Cashier", "Pass1!", Role.Cashier);
        var manager = await _authService.CreateUserAsync("mgr_dlg_test", "Store Manager", "Pass2!", Role.Manager, "7711");

        var prompt = new TestPinPromptService
        {
            PromptFunc = async (reqRole, op) =>
            {
                try
                {
                    // ManagerPinDialog.AttemptAuthorizeAsync now passes minimumRole: reqRole
                    var authorizer = await _authService.VerifyPinAsync("7711", minimumRole: reqRole, actionDescription: op);
                    return PinPromptResult.Succeeded(authorizer);
                }
                catch (UnauthorizedActionException ex)
                {
                    // Dialog displays error and does not return Succeeded
                    return PinPromptResult.Failed(ex.Message);
                }
            }
        };

        var gate = new AuthorizationGateService(_authService, prompt, _db);

        // Act: Cashier attempts Owner-level action with Manager's PIN
        var result = await gate.AuthorizeWithResultAsync(cashier, Role.Owner, "Owner System Config");

        // Rejected
        Assert.False(result.IsAuthorized);

        // In the database, MANAGER_PIN_VERIFIED was NOT emitted for the Manager!
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED' AND actor_id = $mgrId;";
        cmd.Parameters.AddWithValue("$mgrId", manager.UserId);
        var verifiedCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        Assert.Equal(0, verifiedCount);

        // Verify that MANAGER_PIN_FAILED was logged
        using var cmdFail = conn.CreateCommand();
        cmdFail.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_FAILED';";
        var failCount = Convert.ToInt32(await cmdFail.ExecuteScalarAsync());
        Assert.True(failCount >= 1, "Failed PIN attempt must be audited as MANAGER_PIN_FAILED.");
    }

    [Fact]
    public async Task VULNERABILITY_ManagerPinDialog_EmitsDuplicateManagerPinVerifiedAuditEvents()
    {
        // When ManagerPinDialog verifies a Manager PIN for a Manager action:
        // 1. AuthService.VerifyPinAsync writes MANAGER_PIN_VERIFIED
        // 2. AuthorizationGateService does NOT write a duplicate event
        var cashier = await _authService.CreateUserAsync("cashier_dup_test", "Cashier", "Pass1!", Role.Cashier);
        var manager = await _authService.CreateUserAsync("mgr_dup_test", "Store Manager", "Pass2!", Role.Manager, "8822");

        var prompt = new TestPinPromptService
        {
            PromptFunc = async (reqRole, op) =>
            {
                var authorizer = await _authService.VerifyPinAsync("8822", minimumRole: reqRole, actionDescription: op);
                return PinPromptResult.Succeeded(authorizer);
            }
        };

        var gate = new AuthorizationGateService(_authService, prompt, _db);
        var result = await gate.AuthorizeWithResultAsync(cashier, Role.Manager, "Price Override");

        Assert.True(result.IsAuthorized);

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'MANAGER_PIN_VERIFIED' AND actor_id = $mgrId;";
        cmd.Parameters.AddWithValue("$mgrId", manager.UserId);
        var verifiedCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        // Exactly 1 audit event (deduplicated)
        Assert.Equal(1, verifiedCount);
    }
}
