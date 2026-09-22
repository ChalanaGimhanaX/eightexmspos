using System;
using System.Threading.Tasks;
using System.Windows;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Wpf.Views;

namespace Enightx.Pos.Wpf.Services;

public class WpfPinPromptService : IPinPromptService
{
    private readonly IAuthService _authService;

    public WpfPinPromptService(IAuthService authService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    public Task<PinPromptResult> PromptPinAsync(
        Role requiredRole,
        string operationName,
        string? tenantId = null,
        string? branchId = null,
        string? counterId = null)
    {
        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            return Application.Current.Dispatcher.Invoke(() =>
                PromptPinAsync(requiredRole, operationName, tenantId, branchId, counterId));
        }

        var effTenant = tenantId ?? "TENANT_LK_01";
        var effBranch = branchId ?? "B01";
        var effCounter = counterId ?? "C01";

        var dialog = new ManagerPinDialog(
            _authService,
            actionDescription: operationName,
            requiredRole: requiredRole,
            tenantId: effTenant,
            branchId: effBranch,
            counterId: effCounter
        );

        if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible)
        {
            dialog.Owner = Application.Current.MainWindow;
        }

        var result = dialog.ShowDialog();
        if (result == true && dialog.AuthorizedUser != null)
        {
            return Task.FromResult(PinPromptResult.Succeeded(dialog.AuthorizedUser));
        }

        return Task.FromResult(PinPromptResult.Cancelled("User cancelled PIN authorization dialog."));
    }

    public Task<PinPromptResult> PromptPinAsync(string title, string message, Role minimumRole)
    {
        return PromptPinAsync(minimumRole, string.IsNullOrWhiteSpace(message) ? title : $"{title}: {message}");
    }
}
