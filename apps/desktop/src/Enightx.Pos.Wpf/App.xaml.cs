using System.Windows;
using Enightx.Pos.Storage;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf;

public partial class App : Application
{
    public static PosDatabase Database { get; private set; } = null!;
    public static IAuthService AuthService { get; private set; } = null!;
    public static ICatalogService CatalogService { get; private set; } = null!;
    public static IShiftService ShiftService { get; private set; } = null!;
    public static ISaleService SaleService { get; private set; } = null!;
    public static IPrinterService PrinterService { get; private set; } = null!;
    public static IReceiptService ReceiptService { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Standard local SQLite database path per counter
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = System.IO.Path.Combine(appData, "EnightxPOS", "enightx_local.db");

        Database = PosDatabase.CreateFile(dbPath);
        AuthService = new AuthService(Database);
        CatalogService = new CatalogService(Database);
        ShiftService = new ShiftService(Database);
        SaleService = new SaleService(Database, CatalogService);
        PrinterService = new MemoryPrinterService(); // WindowsReceiptPrinter can be injected in production
        ReceiptService = new ReceiptService(Database, SaleService, PrinterService);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Database?.Dispose();
        base.OnExit(e);
    }
}
