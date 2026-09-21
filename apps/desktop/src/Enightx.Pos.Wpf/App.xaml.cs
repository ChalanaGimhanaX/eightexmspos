using System.Windows;
using Enightx.Pos.Domain;
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
    public static ISyncService SyncService { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
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
        SyncService = new SyncService(Database);
        PrinterService = new MemoryPrinterService(); // WindowsReceiptPrinter can be injected in production
        ReceiptService = new ReceiptService(Database, SaleService, PrinterService);

        await SeedDefaultDataAsync();
    }

    private static async Task SeedDefaultDataAsync()
    {
        using var conn = Database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users;";
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        if (count == 0)
        {
            await AuthService.CreateUserAsync("admin", "Store Manager", "admin123", Role.Manager);
            await AuthService.CreateUserAsync("cashier1", "Cashier 01", "cashier123", Role.Cashier);

            await CatalogService.AddProductAsync(new Product
            {
                ProductId = "prod_001",
                Barcode = "4792001001",
                Name = "Brake Pad Front Set (Toyota)",
                NameSi = "ඉදිරිපස බ්‍රේක් පෑඩ් කට්ටලය",
                NameTa = "முன் பிரேக் பேட் தொகுப்பு",
                UnitPrice = 4500.00m,
                CostBasis = 3200.00m,
                TaxRate = 0.18m,
                StockOnHand = 25.0m,
                IsActive = true
            });

            await CatalogService.AddProductAsync(new Product
            {
                ProductId = "prod_002",
                Barcode = "4792001002",
                Name = "Oil Filter Element (Denso)",
                NameSi = "ඔයිල් ෆිල්ටරය",
                NameTa = "எண்ணெய் வடிகட்டி",
                UnitPrice = 1850.00m,
                CostBasis = 1200.00m,
                TaxRate = 0.18m,
                StockOnHand = 50.0m,
                IsActive = true
            });

            await CatalogService.AddProductAsync(new Product
            {
                ProductId = "prod_003",
                Barcode = "4792001003",
                Name = "Spark Plug Iridium (NGK)",
                NameSi = "ස්පාර්ක් ප්ලග්",
                NameTa = "ஸ்பார்க் பிளக்",
                UnitPrice = 2200.00m,
                CostBasis = 1500.00m,
                TaxRate = 0.18m,
                StockOnHand = 40.0m,
                IsActive = true
            });

            await CatalogService.AddProductAsync(new Product
            {
                ProductId = "prod_004",
                Barcode = "4792001004",
                Name = "Synthetic Engine Oil 4L (Mobil 1)",
                NameSi = "එන්ජින් ඔයිල් 4L",
                NameTa = "என்ஜின் எண்ணெய் 4L",
                UnitPrice = 14500.00m,
                CostBasis = 11000.00m,
                TaxRate = 0.18m,
                StockOnHand = 15.0m,
                IsActive = true
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Database?.Dispose();
        base.OnExit(e);
    }
}
