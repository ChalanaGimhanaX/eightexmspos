using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf;

/// <summary>
/// Headless test container providing service references required by BillingViewModel.
/// </summary>
public static class App
{
    public static IHeldCartService? HeldCartService { get; set; }
    public static ISyncService? SyncService { get; set; }
    public static IAuthorizationGateService? AuthorizationGateService { get; set; }
}
