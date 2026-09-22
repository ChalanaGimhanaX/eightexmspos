using Enightx.Pos.Domain;

namespace Enightx.Pos.Services;

public record AuthorizationResult
{
    public required bool IsAuthorized { get; init; }
    public User? AuthorizingUser { get; init; }
    public string? FailureReason { get; init; }
    public bool ElevationRequired { get; init; }

    public static AuthorizationResult DirectApproval(User user) =>
        new() { IsAuthorized = true, AuthorizingUser = user, ElevationRequired = false };

    public static AuthorizationResult ElevatedApproval(User authorizer) =>
        new() { IsAuthorized = true, AuthorizingUser = authorizer, ElevationRequired = true };

    public static AuthorizationResult Denied(string reason, bool elevationRequired = true) =>
        new() { IsAuthorized = false, FailureReason = reason, ElevationRequired = elevationRequired };

    public static AuthorizationResult Authorized(User user) => DirectApproval(user);
}

public interface IAuthorizationGateService
{
    /// <summary>
    /// Checks if currentUser meets requiredRole. If not, prompts for supervisor PIN.
    /// Logs MANAGER_PIN_VERIFIED or MANAGER_PIN_FAILED in audit_events.
    /// </summary>
    Task<bool> AuthorizeOperationAsync(
        User currentUser,
        Role requiredRole,
        string operationName,
        string? tenantId = null,
        string? branchId = null,
        string? counterId = null);

    /// <summary>
    /// Returns detailed authorization outcome including the authorizer User object.
    /// </summary>
    Task<AuthorizationResult> AuthorizeWithResultAsync(
        User currentUser,
        Role requiredRole,
        string operationName,
        string? tenantId = null,
        string? branchId = null,
        string? counterId = null);

    /// <summary>
    /// Alias for AuthorizeWithResultAsync.
    /// </summary>
    Task<AuthorizationResult> AuthorizeAsync(
        User currentUser,
        Role requiredRole,
        string operationName,
        string? tenantId = null,
        string? branchId = null,
        string? counterId = null) => AuthorizeWithResultAsync(currentUser, requiredRole, operationName, tenantId, branchId, counterId);

    /// <summary>
    /// Cached authorizing user from the most recent successful elevation or direct authorization.
    /// </summary>
    User? LastAuthorizingUser { get; }
}
