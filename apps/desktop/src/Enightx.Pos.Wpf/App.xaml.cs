using System.Windows;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;
using Enightx.Pos.Services;
using Enightx.Pos.Themes;
using Enightx.Pos.Wpf.Services;
using Enightx.Pos.Wpf.Themes;

namespace Enightx.Pos.Wpf;

public partial class App : Application
{
    public static IThemeManager ThemeManager { get; private set; } = null!;
    public static PosDatabase Database { get; private set; } = null!;
    public static IAuthService AuthService { get; private set; } = null!;
    public static IAuthorizationGateService AuthorizationGateService { get; private set; } = null!;
    public static ICatalogService CatalogService { get; private set; } = null!;
    public static IShiftService ShiftService { get; private set; } = null!;
    public static ISaleService SaleService { get; private set; } = null!;
    public static IPrinterService PrinterService { get; private set; } = null!;
    public static IReceiptService ReceiptService { get; private set; } = null!;
    public static ISyncService SyncService { get; private set; } = null!;
    public static IUpdateService UpdateService { get; private set; } = null!;
    public static IHeldCartService HeldCartService { get; private set; } = null!;
    public static IGoodsReceivingService GoodsReceivingService { get; private set; } = null!;
    public static IReportService ReportService { get; private set; } = null!;
    public static ICustomerService CustomerService { get; private set; } = null!;
    public static ISyncBackgroundWorker? SyncWorker { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize ThemeManager with WPF Resource Applier
        var applier = new WpfThemeResourceApplier();
        var savedTheme = LoadSavedThemePreference();
        Pos.Themes.ThemeManager.Initialize(applier, savedTheme);
        ThemeManager = Pos.Themes.ThemeManager.Current;

        if (savedTheme == Theme.Dark)
        {
            ThemeManager.SetTheme(Theme.Dark);
        }

        ThemeManager.ThemeChanged += (_, args) => SaveThemePreference(args.NewTheme);

        // A power loss during replacement requires recovery before opening business data.
        var installResult = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnightxPOS", "updates-v2", "install-result.json");
        if (System.IO.File.Exists(installResult))
        {
            using var state = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(installResult));
            var status = state.RootElement.GetProperty("status").GetString();
            if (status == "installing")
            {
                MessageBox.Show($"An update was interrupted. Restore the saved application backup before starting billing. Recovery details: {installResult}", "Update recovery required");
                Shutdown();
                return;
            }
            if (status == "installed" && state.RootElement.GetProperty("version").GetString() != typeof(App).Assembly.GetName().Version?.ToString(3))
            {
                MessageBox.Show($"The running version does not match the installed release. Check your shortcut and installation. Details: {installResult}", "Update verification failed");
                Shutdown();
                return;
            }
        }

        // Standard local SQLite database path per counter
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = System.IO.Path.Combine(appData, "EnightxPOS", "enightx_local.db");

        Database = PosDatabase.CreateFile(dbPath);
        AuthService = new AuthService(Database);
        var pinPrompt = new WpfPinPromptService(AuthService);
        AuthorizationGateService = new AuthorizationGateService(AuthService, pinPrompt, Database);
        CatalogService = new CatalogService(Database);
        ShiftService = new ShiftService(Database);
        CustomerService = new CustomerService(Database, ShiftService);
        SaleService = new SaleService(Database, CatalogService);
        HeldCartService = new HeldCartService(Database);
        GoodsReceivingService = new GoodsReceivingService(Database, CatalogService);
        ReportService = new ReportService(Database);
        var apiBase = Environment.GetEnvironmentVariable("ENIGHTX_API_URL") ?? "https://posapi.eightexms.site";
        SyncService = new SyncService(Database, CatalogService, apiBaseUrl: apiBase);
        SyncWorker = new SyncBackgroundWorker(SyncService);
        SyncWorker.Start();
        PrinterService = new MemoryPrinterService(); // WindowsReceiptPrinter can be injected in production
        ReceiptService = new ReceiptService(Database, SaleService, PrinterService);

        var currentVer = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        UpdateService = new UpdateService(currentVersion: currentVer);

        SeedDefaultDataAsync().GetAwaiter().GetResult();

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    private static async Task SeedDefaultDataAsync()
    {
        try
        {
            using var conn = Database.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM users WHERE username = 'admin';";
            var adminCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            if (adminCount == 0)
            {
                await AuthService.CreateUserAsync("admin", "Store Manager", "admin123", Role.Manager, "1234");
            }

            cmd.CommandText = "SELECT COUNT(*) FROM users WHERE username = 'cashier1';";
            var cashierCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            if (cashierCount == 0)
            {
                await AuthService.CreateUserAsync("cashier1", "Cashier 01", "cashier123", Role.Cashier, "5678");
            }

            cmd.CommandText = "SELECT COUNT(*) FROM customers;";
            var custCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            if (custCount == 0)
            {
                await CustomerService.CreateCustomerAsync("Kamal Jayawardena", "0771234567", "No. 45, Galle Road, Colombo", 25000.00m, "admin", 5000.00m);
                await CustomerService.CreateCustomerAsync("Nihal Motors (Perera)", "0719876543", "12 Temple Rd, Negombo", 50000.00m, "admin", 12500.00m);
                await CustomerService.CreateCustomerAsync("Samantha Fernando", "0755554444", "Kandy Rd, Kiribathgoda", 15000.00m, "admin", 0.00m);
            }

            cmd.CommandText = "SELECT COUNT(*) FROM products;";
            var prodCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            if (prodCount == 0)
            {
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error during seeding: {ex.Message}");
        }
    }

    private static Theme LoadSavedThemePreference()
    {
        try
        {
            var prefFile = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EnightxPOS", "theme_preference.txt");

            if (System.IO.File.Exists(prefFile))
            {
                var text = System.IO.File.ReadAllText(prefFile).Trim();
                if (Enum.TryParse<Theme>(text, true, out var theme))
                {
                    return theme;
                }
            }
        }
        catch
        {
            // Fall back to Light if preference read fails
        }
        return Theme.Light;
    }

    public static void SaveThemePreference(Theme theme)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EnightxPOS");
            System.IO.Directory.CreateDirectory(dir);
            var prefFile = System.IO.Path.Combine(dir, "theme_preference.txt");
            System.IO.File.WriteAllText(prefFile, theme.ToString());
        }
        catch
        {
            // Suppress file IO exceptions
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SyncWorker?.Dispose();
        Database?.Dispose();
        base.OnExit(e);
    }
}
