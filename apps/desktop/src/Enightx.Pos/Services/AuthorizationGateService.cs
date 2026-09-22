using System.Text.Json;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public class AuthorizationGateService : IAuthorizationGateService
{
    private readonly IAuthService _authService;
    private readonly IPinPromptService _pinPromptService;
    private readonly PosDatabase? _db;

    public User? LastAuthorizingUser { get; private set; }

    public AuthorizationGateService(
        IAuthService authService,
        IPinPromptService pinPromptService,
        PosDatabase? db = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _pinPromptService = pinPromptService ?? throw new ArgumentNullException(nameof(pinPromptService));
        _db = db;
    }

    public AuthorizationGateService(
        PosDatabase db,
        IPinPromptService pinPromptService)
        : this(new AuthService(db), pinPromptService, db)
    {
    }

    public async Task<bool> AuthorizeOperationAsync(
        User currentUser,
        Role requiredRole,
        string operationName,
        string? tenantId = null,
        string? branchId = null,
        string? counterId = null)
    {
        var result = await AuthorizeWithResultAsync(
            currentUser,
            requiredRole,
            operationName,
            tenantId,
            branchId,
            counterId);

        return result.IsAuthorized;
    }

    public async Task<AuthorizationResult> AuthorizeWithResultAsync(
        User currentUser,
        Role requiredRole,
        string operationName,
        string? tenantId = null,
        string? branchId = null,
        string? counterId = null)
    {
        LastAuthorizingUser = null;

        var effTenant = tenantId ?? "TENANT_LK_01";
        var effBranch = branchId ?? "B01";
        var effCounter = counterId ?? "C01";
        var opName = string.IsNullOrWhiteSpace(operationName) ? "UNSPECIFIED_OPERATION" : operationName.Trim();

        // 1. Guard against null or inactive user
        if (currentUser == null)
        {
            await LogAuditEventAsync(
                effTenant, effBranch, effCounter,
                actorId: "SYSTEM",
                action: "MANAGER_PIN_FAILED",
                details: new
                {
                    CashierId = "UNKNOWN",
                    OperationName = opName,
                    SensitiveAction = opName,
                    RequiredRole = requiredRole.ToString(),
                    Reason = "User is not authenticated."
                });

            return AuthorizationResult.Denied("User is not authenticated.", elevationRequired: false);
        }

        if (!currentUser.IsActive)
        {
            await LogAuditEventAsync(
                effTenant, effBranch, effCounter,
                actorId: currentUser.UserId,
                action: "MANAGER_PIN_FAILED",
                details: new
                {
                    CashierId = currentUser.UserId,
                    CashierUsername = currentUser.Username,
                    OperationName = opName,
                    SensitiveAction = opName,
                    RequiredRole = requiredRole.ToString(),
                    Reason = "User account is deactivated."
                });

            return AuthorizationResult.Denied("User account is inactive.", elevationRequired: false);
        }

        // 2. Direct Role Evaluation: If user possesses required role or higher -> allow without prompting
        if (currentUser.Role >= requiredRole)
        {
            if (_db != null)
            {
                bool dbVerified = false;
                string? failReason = null;
                try
                {
                    using var conn = _db.CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT role, is_active FROM users WHERE user_id = $uid;";
                    cmd.Parameters.AddWithValue("$uid", currentUser.UserId);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        var dbRole = reader.GetInt32(0);
                        var isActive = reader.GetInt32(1) == 1;

                        if (!isActive)
                        {
                            failReason = $"User account '{currentUser.UserId}' is deactivated in database.";
                        }
                        else if (dbRole < (int)requiredRole)
                        {
                            failReason = $"User role in database ({((Role)dbRole)}) does not satisfy required role ({requiredRole}).";
                        }
                        else
                        {
                            dbVerified = true;
                        }
                    }
                    else
                    {
                        failReason = $"User '{currentUser.UserId}' does not exist in database.";
                    }
                }
                catch (Exception ex)
                {
                    failReason = $"Database verification error: {ex.Message}";
                }

                if (!dbVerified)
                {
                    var reason = failReason ?? "Direct role authorization failed: user record invalid, inactive, or role mismatched in database.";
                    await LogAuditEventAsync(
                        effTenant, effBranch, effCounter,
                        actorId: currentUser.UserId,
                        action: "MANAGER_PIN_FAILED",
                        details: new
                        {
                            CashierId = currentUser.UserId,
                            CashierUsername = currentUser.Username,
                            OperationName = opName,
                            SensitiveAction = opName,
                            RequiredRole = requiredRole.ToString(),
                            Reason = reason
                        });

                    return AuthorizationResult.Denied(reason, elevationRequired: false);
                }
            }

            LastAuthorizingUser = currentUser;
            return AuthorizationResult.DirectApproval(currentUser);
        }

        // 3. Insufficient Role -> Trigger PIN Prompt Elevation
        PinPromptResult promptResult;
        try
        {
            promptResult = await _pinPromptService.PromptPinAsync(
                requiredRole,
                opName,
                effTenant,
                effBranch,
                effCounter);
        }
        catch (Exception ex)
        {
            promptResult = PinPromptResult.Failed($"PIN prompt error: {ex.Message}");
        }

        // 4. Handle Cancelled or Failed Dialog Prompt
        if (promptResult == null || !promptResult.Success)
        {
            var failureReason = promptResult?.FailureReason ?? "User cancelled PIN prompt.";
            await LogAuditEventAsync(
                effTenant, effBranch, effCounter,
                actorId: currentUser.UserId,
                action: "MANAGER_PIN_FAILED",
                details: new
                {
                    CashierId = currentUser.UserId,
                    CashierUsername = currentUser.Username,
                    OperationName = opName,
                    SensitiveAction = opName,
                    RequiredRole = requiredRole.ToString(),
                    Reason = failureReason
                });

            return AuthorizationResult.Denied(failureReason, elevationRequired: true);
        }

        // 5. Evaluate AuthorizedUser if directly provided (e.g. from WPF Dialog)
        if (promptResult.AuthorizedUser != null)
        {
            var authorizer = promptResult.AuthorizedUser;

            if (_db != null)
            {
                try
                {
                    using var conn = _db.CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT role, is_active FROM users WHERE user_id = $uid;";
                    cmd.Parameters.AddWithValue("$uid", authorizer.UserId);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        var dbRole = reader.GetInt32(0);
                        var isActive = reader.GetInt32(1) == 1;

                        if (!isActive)
                        {
                            var failReason = $"Authorizing supervisor '{authorizer.DisplayName}' is deactivated in database.";
                            await LogAuditEventAsync(
                                effTenant, effBranch, effCounter,
                                actorId: currentUser.UserId,
                                action: "MANAGER_PIN_FAILED",
                                details: new
                                {
                                    CashierId = currentUser.UserId,
                                    CashierUsername = currentUser.Username,
                                    OperationName = opName,
                                    SensitiveAction = opName,
                                    RequiredRole = requiredRole.ToString(),
                                    AttemptedAuthorizerId = authorizer.UserId,
                                    Reason = failReason
                                });

                            return AuthorizationResult.Denied(failReason);
                        }

                        if (dbRole < (int)requiredRole)
                        {
                            var failReason = $"Authorizing supervisor role in database ({((Role)dbRole)}) is insufficient (requires {requiredRole}).";
                            await LogAuditEventAsync(
                                effTenant, effBranch, effCounter,
                                actorId: currentUser.UserId,
                                action: "MANAGER_PIN_FAILED",
                                details: new
                                {
                                    CashierId = currentUser.UserId,
                                    CashierUsername = currentUser.Username,
                                    OperationName = opName,
                                    SensitiveAction = opName,
                                    RequiredRole = requiredRole.ToString(),
                                    AttemptedAuthorizerId = authorizer.UserId,
                                    AttemptedAuthorizerRole = ((Role)dbRole).ToString(),
                                    Reason = failReason
                                });

                            return AuthorizationResult.Denied(failReason);
                        }
                    }
                }
                catch
                {
                    // Non-fatal if DB read fails
                }
            }

            if (!authorizer.IsActive)
            {
                var failReason = $"Authorizing supervisor '{authorizer.DisplayName}' is deactivated.";
                await LogAuditEventAsync(
                    effTenant, effBranch, effCounter,
                    actorId: currentUser.UserId,
                    action: "MANAGER_PIN_FAILED",
                    details: new
                    {
                        CashierId = currentUser.UserId,
                        CashierUsername = currentUser.Username,
                        OperationName = opName,
                        SensitiveAction = opName,
                        RequiredRole = requiredRole.ToString(),
                        AttemptedAuthorizerId = authorizer.UserId,
                        Reason = failReason
                    });

                return AuthorizationResult.Denied(failReason);
            }

            if (authorizer.Role < requiredRole)
            {
                var failReason = $"Authorizing supervisor role '{authorizer.Role}' is insufficient (requires {requiredRole}).";
                await LogAuditEventAsync(
                    effTenant, effBranch, effCounter,
                    actorId: currentUser.UserId,
                    action: "MANAGER_PIN_FAILED",
                    details: new
                    {
                        CashierId = currentUser.UserId,
                        CashierUsername = currentUser.Username,
                        OperationName = opName,
                        SensitiveAction = opName,
                        RequiredRole = requiredRole.ToString(),
                        AttemptedAuthorizerId = authorizer.UserId,
                        AttemptedAuthorizerRole = authorizer.Role.ToString(),
                        Reason = failReason
                    });

                return AuthorizationResult.Denied(failReason);
            }

            // Note: MANAGER_PIN_VERIFIED is authoritatively emitted by AuthService.VerifyPinAsync upon cryptographic match.
            // Do NOT emit duplicate MANAGER_PIN_VERIFIED here.
            LastAuthorizingUser = authorizer;
            return AuthorizationResult.ElevatedApproval(authorizer);
        }

        // 6. Evaluate PIN via AuthService (e.g. from headless test prompt or PIN entry)
        if (string.IsNullOrWhiteSpace(promptResult.Pin))
        {
            var failReason = "PIN cannot be empty.";
            await LogAuditEventAsync(
                effTenant, effBranch, effCounter,
                actorId: currentUser.UserId,
                action: "MANAGER_PIN_FAILED",
                details: new
                {
                    CashierId = currentUser.UserId,
                    CashierUsername = currentUser.Username,
                    OperationName = opName,
                    SensitiveAction = opName,
                    RequiredRole = requiredRole.ToString(),
                    Reason = failReason
                });

            return AuthorizationResult.Denied(failReason);
        }

        try
        {
            var authorizer = await _authService.VerifyPinAsync(
                pin: promptResult.Pin.Trim(),
                minimumRole: requiredRole,
                tenantId: effTenant,
                branchId: effBranch,
                counterId: effCounter,
                actionDescription: opName,
                cashierId: currentUser.UserId
            );

            LastAuthorizingUser = authorizer;
            return AuthorizationResult.ElevatedApproval(authorizer);
        }
        catch (UnauthorizedActionException ex)
        {
            // VerifyPinAsync already logged MANAGER_PIN_FAILED in audit_events
            LastAuthorizingUser = null;
            return AuthorizationResult.Denied(ex.Message);
        }
        catch (Exception ex)
        {
            LastAuthorizingUser = null;
            await LogAuditEventAsync(
                effTenant, effBranch, effCounter,
                actorId: currentUser.UserId,
                action: "MANAGER_PIN_FAILED",
                details: new
                {
                    CashierId = currentUser.UserId,
                    CashierUsername = currentUser.Username,
                    OperationName = opName,
                    SensitiveAction = opName,
                    RequiredRole = requiredRole.ToString(),
                    Reason = ex.Message
                });

            return AuthorizationResult.Denied(ex.Message);
        }
    }

    private async Task LogAuditEventAsync(
        string tenantId,
        string branchId,
        string counterId,
        string actorId,
        string action,
        object details)
    {
        if (_db == null) return;

        try
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
                VALUES ($eid, $tid, $bid, $cid, $actor, $action, $details, $occurred);
            ";
            cmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$tid", tenantId);
            cmd.Parameters.AddWithValue("$bid", branchId);
            cmd.Parameters.AddWithValue("$cid", counterId);
            cmd.Parameters.AddWithValue("$actor", actorId);
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(details));
            cmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));

            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Ignore audit writing failure in non-fatal paths
        }
    }
}
