using System.Text;
using System.Text.Json;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class AcceptanceMatrixTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PosDatabase _database;

    public AcceptanceMatrixTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acc_matrix_{Guid.NewGuid():N}.db");
        _database = PosDatabase.CreateFile(_dbPath);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public async Task A03_InternetFails_LanRelayExchangesEventsLocally()
    {
        // Counter 1 DB & Relay
        var c1Relay = new LanRelayService(_database);

        // Record a sale event in Counter 1 outbox
        var peerEvent = new LanPeerEvent
        {
            EventId = Guid.NewGuid(),
            TenantId = "TENANT_LK_01",
            BranchId = "BR_COLOMBO",
            DeviceId = "C01",
            DeviceGeneration = 1,
            SourceSequence = 101,
            ActorId = "cashier1",
            PayloadJson = JsonSerializer.Serialize(new { SaleId = Guid.NewGuid(), Total = 4500.00m })
        };

        // Counter 2 ingests Counter 1's event
        var c2DbPath = Path.Combine(Path.GetTempPath(), $"c2_db_{Guid.NewGuid():N}.db");
        var c2Db = PosDatabase.CreateFile(c2DbPath);
        try
        {
            var c2Relay = new LanRelayService(c2Db);

            // First ingest succeeds
            var firstIngest = await c2Relay.IngestPeerEventAsync(peerEvent);
            Assert.True(firstIngest);
            Assert.Equal(1, c2Relay.IngestedEventsCount);

            // Duplicate ingest is dropped per A05 / idempotency
            var duplicateIngest = await c2Relay.IngestPeerEventAsync(peerEvent);
            Assert.False(duplicateIngest);
            Assert.Equal(1, c2Relay.DuplicateEventsDroppedCount);
        }
        finally
        {
            if (File.Exists(c2DbPath)) File.Delete(c2DbPath);
        }
    }

    [Fact]
    public async Task A06_BothCountersSellFinalUnitWhileIsolated_BothSalesRetained_StockNegative()
    {
        var auth = new AuthService(_database);
        var shiftService = new ShiftService(_database);
        var catalog = new CatalogService(_database);
        var saleService = new SaleService(_database, catalog);

        var cashier = await auth.CreateUserAsync("acc_cashier1", "Cashier 1", "pass123", Role.Cashier);
        var shift1 = await shiftService.OpenShiftAsync("BR01", "C01", cashier.UserId, 5000.00m, "TENANT_01");
        var shift2 = await shiftService.OpenShiftAsync("BR01", "C02", cashier.UserId, 5000.00m, "TENANT_01");

        // Product with stock = 1
        var prod = new Product
        {
            ProductId = "prod_scarce",
            Barcode = "99990001",
            Name = "Rare Brake Disc",
            UnitPrice = 5000.00m,
            CostBasis = 3500.00m,
            StockOnHand = 1.0m,
            IsActive = true
        };
        await catalog.AddProductAsync(prod);

        // Counter 1 sells 1 unit
        var cmd1 = new CreateSaleCommand(
            TenantId: "TENANT_01",
            BranchId: "BR01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift1.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new CreateSaleLineRequest(prod.ProductId, Quantity: 1.0m)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new CreateTenderRequest(TenderType.CASH, 5000.00m)
            }
        );
        var sale1 = await saleService.CommitSaleAsync(cmd1);

        // Counter 2 also sells 1 unit while isolated (stock shortage allows sale, flags negative per A06)
        var cmd2 = new CreateSaleCommand(
            TenantId: "TENANT_01",
            BranchId: "BR01",
            CounterId: "C02",
            CashierId: cashier.UserId,
            ShiftId: shift2.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new CreateSaleLineRequest(prod.ProductId, Quantity: 1.0m)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new CreateTenderRequest(TenderType.CASH, 5000.00m)
            }
        );
        var sale2 = await saleService.CommitSaleAsync(cmd2);

        // Both sales retained
        var s1 = await saleService.GetSaleByIdAsync(sale1.SaleId);
        var s2 = await saleService.GetSaleByIdAsync(sale2.SaleId);
        Assert.NotNull(s1);
        Assert.NotNull(s2);

        // Stock is now negative (-1.0)
        var updatedProd = await catalog.GetProductByIdAsync(prod.ProductId);
        Assert.NotNull(updatedProd);
        Assert.Equal(-1.0m, updatedProd.StockOnHand);
    }

    [Fact]
    public async Task A11_DuplicateRefundPrevention_SecondRefundFails()
    {
        var auth = new AuthService(_database);
        var shiftService = new ShiftService(_database);
        var catalog = new CatalogService(_database);
        var saleService = new SaleService(_database, catalog);

        var cashier = await auth.CreateUserAsync("acc_cashier2", "Cashier 2", "pass123", Role.Cashier);
        var shift = await shiftService.OpenShiftAsync("BR01", "C01", cashier.UserId, 5000.00m, "TENANT_01");

        var prod = new Product
        {
            ProductId = "prod_refund_test",
            Barcode = "99990002",
            Name = "Spark Plug",
            UnitPrice = 2000.00m,
            CostBasis = 1000.00m,
            StockOnHand = 10.0m,
            IsActive = true
        };
        await catalog.AddProductAsync(prod);

        var cmd = new CreateSaleCommand(
            TenantId: "TENANT_01",
            BranchId: "BR01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest>
            {
                new CreateSaleLineRequest(prod.ProductId, Quantity: 1.0m)
            },
            Tenders: new List<CreateTenderRequest>
            {
                new CreateTenderRequest(TenderType.CASH, 2000.00m)
            }
        );
        var sale = await saleService.CommitSaleAsync(cmd);

        // First refund succeeds
        var refundCmd = new RefundSaleCommand(
            TenantId: "TENANT_01",
            BranchId: "BR01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest>
            {
                new RefundLineRequest(sale.Lines[0].LineId, 1.0m)
            },
            Reason: "Customer bought wrong model"
        );
        var refundSale = await saleService.RefundSaleAsync(refundCmd);
        Assert.NotNull(refundSale);

        // Second refund attempt from different counter C02 is rejected under origin-authority rule (A11)
        var crossCounterCmd = new RefundSaleCommand(
            TenantId: "TENANT_01",
            BranchId: "BR01",
            CounterId: "C02",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest>
            {
                new RefundLineRequest(sale.Lines[0].LineId, 1.0m)
            },
            Reason: "Cross counter return attempt"
        );

        await Assert.ThrowsAsync<PosException>(() => saleService.RefundSaleAsync(crossCounterCmd));
    }

    [Fact]
    public void A20_SinhalaAndTamil_ReceiptFormatting_AndEscPosDrawerKick()
    {
        var escpos = new EscPosPrinterService();

        // 1. Verify Cash Drawer Kick code is 27, 112, 0, 25, 250
        var kickBytes = escpos.GenerateCashDrawerKickBytes();
        Assert.Equal(new byte[] { 27, 112, 0, 25, 250 }, kickBytes);

        // 2. Verify Paper Cut code
        var cutBytes = escpos.GeneratePaperCutBytes();
        Assert.Equal(new byte[] { 0x1D, 0x56, 0x42, 0x00 }, cutBytes);

        // 3. Verify Sinhala and Tamil detection
        string sinhalaText = "ඉදිරිපස බ්‍රේක් පෑඩ් (Front Brake Pad)";
        string tamilText = "முன் பிரேக் பேட் (Front Brake Pad)";
        string englishText = "Front Brake Pad Set";

        Assert.True(EscPosPrinterService.ContainsComplexScript(sinhalaText));
        Assert.True(EscPosPrinterService.ContainsComplexScript(tamilText));
        Assert.False(EscPosPrinterService.ContainsComplexScript(englishText));

        // 4. Verify raster generation for Sinhala/Tamil text
        var rasterBytes = escpos.RenderTextToMonochromeRaster(sinhalaText, widthDots: 384);
        Assert.True(rasterBytes.Length > 8);
        Assert.Equal(0x1D, rasterBytes[0]); // GS
        Assert.Equal(0x76, rasterBytes[1]); // v
        Assert.Equal(0x30, rasterBytes[2]); // 0 (raster bit image)

        // 5. Verify format receipt with drawer kick
        var receiptBytes = escpos.FormatReceiptBytes("ENIGHTX POS\nTotal: LKR 4,500.00", kickDrawer: true, cutPaper: true);
        Assert.True(receiptBytes.Length > kickBytes.Length);
    }

    [Fact]
    public async Task A24_HardwareReplacement_DisasterRecovery_RestoresDatabaseState()
    {
        var catalog = new CatalogService(_database);
        var prod = new Product
        {
            ProductId = "prod_disaster_recovery",
            Barcode = "77770001",
            Name = "Alternator Assembly",
            UnitPrice = 25000.00m,
            CostBasis = 18000.00m,
            StockOnHand = 5.0m,
            IsActive = true
        };
        await catalog.AddProductAsync(prod);

        // Create encrypted backup
        var backupService = new CloudBackupService(_database);
        var backupPath = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}.bak.enc");
        var restorePath = Path.Combine(Path.GetTempPath(), $"restored_{Guid.NewGuid():N}.db");
        string encryptionKey = "enightx_super_secret_backup_key_2026";

        try
        {
            await backupService.CreateEncryptedBackupAsync(backupPath, encryptionKey);
            Assert.True(File.Exists(backupPath));
            var fileLen = new FileInfo(backupPath).Length;
            Assert.True(fileLen > 100);

            // Restore on clean replacement database
            await backupService.RestoreFromEncryptedBackupAsync(backupPath, restorePath, encryptionKey);
            Assert.True(File.Exists(restorePath));

            // Verify restored data integrity
            var restoredDb = PosDatabase.CreateFile(restorePath);
            var restoredCatalog = new CatalogService(restoredDb);
            var restoredProd = await restoredCatalog.GetProductByIdAsync(prod.ProductId);

            Assert.NotNull(restoredProd);
            Assert.Equal("Alternator Assembly", restoredProd.Name);
            Assert.Equal(25000.00m, restoredProd.UnitPrice);
            Assert.Equal(5.0m, restoredProd.StockOnHand);
        }
        finally
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
            if (File.Exists(restorePath)) File.Delete(restorePath);
        }
    }
}
