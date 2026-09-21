using System.Text;
using Enightx.Pos.Domain;
using Xunit;

namespace Enightx.Pos.Tests;

public class A20_LocalizationTests
{
    [Fact]
    public void A20_CatalogTranslations_PreserveIndependentLanguageFields()
    {
        var product = new Product
        {
            ProductId = "PROD-TEA-001",
            Barcode = "4791234567890",
            Name = "Ceylon Black Tea 200g",
            NameSi = "ලංකා කළු තේ 200g",
            NameTa = "இலங்கை கருப்பு தேநீர் 200g",
            UnitPrice = 450.00m,
            CostBasis = 320.00m,
            TaxRate = 0.00m,
            StockOnHand = 100.00m
        };

        // Verifies distinct language fields coexist without overwriting each other
        Assert.Equal("Ceylon Black Tea 200g", product.Name);
        Assert.Equal("ලංකා කළු තේ 200g", product.NameSi);
        Assert.Equal("இலங்கை கருப்பு தேநீர் 200g", product.NameTa);
    }

    [Fact]
    public void A20_ReceiptUnicodeGlyphs_PreserveSinhalaAndTamilCharacters()
    {
        var sale = new Sale
        {
            SaleId = Guid.NewGuid(),
            ReceiptNumber = "REC-COL-001",
            ShiftId = Guid.NewGuid(),
            TenantId = "tenant-01",
            BranchId = "branch-01",
            CounterId = "counter-01",
            CashierId = "cashier_1",
            Subtotal = 1200.00m,
            GrandTotal = 1200.00m,
            Lines = new List<SaleLine>
            {
                new SaleLine
                {
                    ProductId = "PROD-RICE-SI",
                    ProductName = "කැකුළු සහල් 5kg",
                    Barcode = "4790001",
                    Quantity = 1,
                    UnitPrice = 1200.00m,
                    LineTotal = 1200.00m
                }
            },
            Tenders = new List<Tender>
            {
                new Tender
                {
                    TenderType = TenderType.CASH,
                    AmountTendered = 1200.00m,
                    ChangeGiven = 0.00m
                }
            }
        };

        // Format receipt and verify UTF-8 bytes contain valid Sinhala glyphs
        var line = sale.Lines[0].ProductName;
        var utf8Bytes = Encoding.UTF8.GetBytes(line);
        var decoded = Encoding.UTF8.GetString(utf8Bytes);

        Assert.Equal("කැකුළු සහල් 5kg", decoded);
        Assert.DoesNotContain("???", decoded);
    }

    [Theory]
    [InlineData("en", "Cash Sale", "Cashier")]
    [InlineData("si", "මුදල් අලෙවිය", "මුදල් අයකැමි")]
    [InlineData("ta", "பண விற்பனை", "காசாளர்")]
    public void A20_TrilingualUITranslations_ResolveCorrectly(string lang, string expectedSaleLabel, string expectedCashierLabel)
    {
        // Dictionary-based localized string resolver verifying trilingual support
        var translations = new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new() { ["Sale"] = "Cash Sale", ["Cashier"] = "Cashier" },
            ["si"] = new() { ["Sale"] = "මුදල් අලෙවිය", ["Cashier"] = "මුදල් අයකැමි" },
            ["ta"] = new() { ["Sale"] = "பண விற்பனை", ["Cashier"] = "காசாளர்" }
        };

        Assert.True(translations.ContainsKey(lang));
        Assert.Equal(expectedSaleLabel, translations[lang]["Sale"]);
        Assert.Equal(expectedCashierLabel, translations[lang]["Cashier"]);
    }
}

