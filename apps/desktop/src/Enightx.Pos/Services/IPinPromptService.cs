using Enightx.Pos.Domain;

namespace Enightx.Pos.Services;

public record PinPromptResult
{
    public required bool Success { get; init; }
    public string? Pin { get; init; }
    public User? AuthorizedUser { get; init; }
    public string? FailureReason { get; init; }

    public static PinPromptResult Succeeded(User authorizer, string? pin = null) =>
        new() { Success = true, AuthorizedUser = authorizer, Pin = pin };

    public static PinPromptResult WithPin(string pin) =>
        new() { Success = true, Pin = pin };

    public static PinPromptResult Cancelled(string reason = "User cancelled PIN prompt.") =>
        new() { Success = false, FailureReason = reason };

    public static PinPromptResult Failed(string reason) =>
        new() { Success = false, FailureReason = reason };
}

public interface IPinPromptService
{
    /// <summary>
    /// Displays a PIN prompt dialog to the user or delegates to a headless prompt handler.
    /// </summary>
    Task<PinPromptResult> PromptPinAsync(
        Role requiredRole,
        string operationName,
        string? tenantId = null,
        string? branchId = null,
        string? counterId = null);

    /// <summary>
    /// Overload matching title, message, minimumRole signature.
    /// </summary>
    Task<PinPromptResult> PromptPinAsync(string title, string message, Role minimumRole)
        => PromptPinAsync(minimumRole, string.IsNullOrWhiteSpace(message) ? title : $"{title}: {message}", null, null, null);
}
