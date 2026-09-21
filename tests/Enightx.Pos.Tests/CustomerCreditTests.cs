using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class CustomerCreditTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;
    private readonly CatalogService _catalog;
    private readonly ShiftService _shift;
    private readonly SaleService _saleService;
    private readonly CustomerService _customerService;

    public CustomerCreditTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
        _catalog = new CatalogService(_db);
        _shift = new ShiftService(_db);
        _customerService = new CustomerService(_db, _shift);
        _saleService = new SaleService(_db, _catalog);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task CreateCustomer_WithInitialBalance_CreatesCustomerAndLedgerEntry()
    {
        var actor = await _auth.CreateUserAsync("manager1", "Store Manager", "pass123", Role.Manager, "1234");

        var customer = await _customerService.CreateCustomerAsync(
            name: "Sunil Shantha",
            phone: "0777123456",
            address: "123 Main St, Kandy",
            creditLimit: 50000.00m,
            actorId: actor.UserId,
            initialBalance: 12000.00m
        );

        Assert.NotNull(customer);
        Assert.StartsWith("cust_", customer.CustomerId);
        Assert.Equal("Sunil Shantha", customer.Name);
        Assert.Equal("0777123456", customer.Phone);
        Assert.Equal(50000.00m, customer.CreditLimit);
        Assert.Equal(12000.00m, customer.OutstandingBalance);
        Assert.True(customer.IsActive);

        var balance = await _customerService.GetCustomerBalanceAsync(customer.CustomerId);
        Assert.Equal(12000.00m, balance);

        var ledger = await _customerService.GetCustomerLedgerAsync(customer.CustomerId);
        Assert.Single(ledger);
        Assert.Equal("INITIAL_BALANCE", ledger[0].EntryType);
        Assert.Equal(12000.00m, ledger[0].Amount);
        Assert.Equal(12000.00m, ledger[0].BalanceAfter);
    }

    [Fact]
    public async Task SearchCustomers_ReturnsMatchingResults()
    {
        var actor = await _auth.CreateUserAsync("admin1", "Admin", "pass123", Role.Manager, "1234");
        await _customerService.CreateCustomerAsync("Nimal Motors", "0711112222", null, 20000m, actor.UserId);
        await _customerService.CreateCustomerAsync("Nimali Perera", "0723334444", null, 15000m, actor.UserId);
        await _customerService.CreateCustomerAsync("Gamini Silva", "0719998888", null, 10000m, actor.UserId);

        var searchByName = await _customerService.SearchCustomersAsync("nimal");
        Assert.Equal(2, searchByName.Count);

        var searchByPhone = await _customerService.SearchCustomersAsync("071999");
        Assert.Single(searchByPhone);
        Assert.Equal("Gamini Silva", searchByPhone[0].Name);
    }

    [Fact]
    public async Task CreditSale_WithinLimit_UpdatesCustomerBalanceAndRecordsLedger()
    {
        var cashier = await _auth.CreateUserAsync("cashier_credit", "Cashier Credit", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var product = new Product
        {
            ProductId = "prod_battery",
            Barcode = "5551112223334",
            Name = "Amaron Battery 12V 35Ah",
            UnitPrice = 18500.00m,
            CostBasis = 14000.00m,
            TaxRate = 0.0m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(product);

        var customer = await _customerService.CreateCustomerAsync(
            name: "Ruwan Wickramasinghe",
            phone: "0778899001",
            address: "Colombo 07",
            creditLimit: 30000.00m,
            actorId: cashier.UserId,
            initialBalance: 5000.00m
        );

        // Commit Credit Sale of 18,500 LKR
        var cmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CREDIT, 18500.00m) },
            CustomerId: customer.CustomerId
        );

        var sale = await _saleService.CommitSaleAsync(cmd);
        Assert.NotNull(sale);
        Assert.Equal(18500.00m, sale.GrandTotal);

        // Verify customer balance updated: 5,000 + 18,500 = 23,500 LKR
        var updatedBalance = await _customerService.GetCustomerBalanceAsync(customer.CustomerId);
        Assert.Equal(23500.00m, updatedBalance);

        // Verify customer ledger entries
        var ledger = await _customerService.GetCustomerLedgerAsync(customer.CustomerId);
        Assert.Equal(2, ledger.Count);
        var creditEntry = ledger.First(e => e.EntryType == "CREDIT_SALE");
        Assert.Equal(18500.00m, creditEntry.Amount);
        Assert.Equal(23500.00m, creditEntry.BalanceAfter);
        Assert.Equal(sale.ReceiptNumber, creditEntry.ReferenceId);

        // A10: Credit sales must NOT increase physical drawer cash!
        var shiftInDb = await _shift.GetShiftByIdAsync(shift.ShiftId);
        Assert.NotNull(shiftInDb);
        Assert.Equal(0m, shiftInDb.CashReceived);
        Assert.Equal(5000.00m, shiftInDb.ExpectedCash); // Only opening float
    }

    [Fact]
    public async Task CreditSale_ExceedingCreditLimit_ThrowsCreditLimitExceededException()
    {
        var cashier = await _auth.CreateUserAsync("cashier2", "Cashier 02", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var product = new Product
        {
            ProductId = "prod_tyre_set",
            Barcode = "9998887776665",
            Name = "Pirelli Tyre Set",
            UnitPrice = 45000.00m,
            CostBasis = 35000.00m,
            TaxRate = 0.0m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(product);

        // Customer has credit limit 30,000 and current balance 10,000. Max available credit = 20,000.
        var customer = await _customerService.CreateCustomerAsync(
            name: "Ananda Jayasinghe",
            phone: "0712233445",
            address: "Kurunegala",
            creditLimit: 30000.00m,
            actorId: cashier.UserId,
            initialBalance: 10000.00m
        );

        var cmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1.0m) }, // 45,000 LKR
            Tenders: new List<CreateTenderRequest> { new(TenderType.CREDIT, 45000.00m) },
            CustomerId: customer.CustomerId
        );

        // 10,000 + 45,000 = 55,000 > 30,000 -> Must throw CreditLimitExceededException
        var ex = await Assert.ThrowsAsync<CreditLimitExceededException>(() => _saleService.CommitSaleAsync(cmd));
        Assert.Contains("Credit limit", ex.Message);
        Assert.Contains("30,000", ex.Message);

        // Customer balance remains unchanged
        var balance = await _customerService.GetCustomerBalanceAsync(customer.CustomerId);
        Assert.Equal(10000.00m, balance);
    }

    [Fact]
    public async Task SplitTender_CashPlusCredit_UpdatesDrawerCashByCashPortionOnly()
    {
        var cashier = await _auth.CreateUserAsync("cashier3", "Cashier 03", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var product = new Product
        {
            ProductId = "prod_shock_abs",
            Barcode = "1112223334445",
            Name = "Shock Absorber Rear Pair",
            UnitPrice = 25000.00m,
            CostBasis = 18000.00m,
            TaxRate = 0.0m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(product);

        var customer = await _customerService.CreateCustomerAsync(
            name: "Rohan Dissanayake",
            phone: "0766667778",
            address: "Matara",
            creditLimit: 20000.00m,
            actorId: cashier.UserId,
            initialBalance: 0.00m
        );

        // Split Tender: 15,000 Credit + 10,000 Cash = 25,000 Grand Total
        var cmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType.CREDIT, 15000.00m),
                new(TenderType.CASH, 10000.00m)
            },
            CustomerId: customer.CustomerId
        );

        var sale = await _saleService.CommitSaleAsync(cmd);
        Assert.NotNull(sale);

        // Customer balance increased by Credit portion (15,000) only
        var custBalance = await _customerService.GetCustomerBalanceAsync(customer.CustomerId);
        Assert.Equal(15000.00m, custBalance);

        // Cash drawer expected cash increased by Cash portion (10,000) only!
        // Expected Cash = 5,000 (float) + 10,000 (cash tender) = 15,000 LKR
        var shiftInDb = await _shift.GetShiftByIdAsync(shift.ShiftId);
        Assert.NotNull(shiftInDb);
        Assert.Equal(10000.00m, shiftInDb.CashReceived);

        var expectedCash = MoneyCalculator.CalculateShiftExpectedCash(
            shiftInDb.OpeningFloat,
            shiftInDb.CashReceived,
            shiftInDb.ChangeGiven,
            shiftInDb.CashRefunds,
            shiftInDb.CashIn,
            shiftInDb.CashOut
        );
        Assert.Equal(15000.00m, expectedCash);
    }

    [Fact]
    public async Task DebtCollection_CashPayment_ReducesCustomerBalanceAndIncreasesDrawerCash()
    {
        var cashier = await _auth.CreateUserAsync("cashier4", "Cashier 04", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var customer = await _customerService.CreateCustomerAsync(
            name: "Upul Chandana",
            phone: "0788889999",
            address: "Galle",
            creditLimit: 30000.00m,
            actorId: cashier.UserId,
            initialBalance: 20000.00m
        );

        // Customer pays 8,000 LKR in Cash against their debt
        var entry = await _customerService.RecordPaymentAsync(
            customerId: customer.CustomerId,
            amount: 8000.00m,
            paymentMethod: "CASH",
            actorId: cashier.UserId,
            shiftId: shift.ShiftId,
            notes: "Partial debt repayment"
        );

        Assert.NotNull(entry);
        Assert.Equal(8000.00m, entry.Amount);
        Assert.Equal(12000.00m, entry.BalanceAfter);
        Assert.Equal("CASH", entry.PaymentMethod);

        // Verify customer balance
        var balance = await _customerService.GetCustomerBalanceAsync(customer.CustomerId);
        Assert.Equal(12000.00m, balance);

        // Verify shift cash drawer:
        // Cash payment in drawer records cash movement (ShiftService.RecordCashMovementAsync)
        // Expected Cash = OpeningFloat (5,000) + CashIn (8,000) = 13,000.00 LKR
        var shiftInDb = await _shift.GetShiftByIdAsync(shift.ShiftId);
        Assert.NotNull(shiftInDb);
        Assert.Equal(8000.00m, shiftInDb.CashIn);

        var expectedCash = MoneyCalculator.CalculateShiftExpectedCash(
            shiftInDb.OpeningFloat,
            shiftInDb.CashReceived,
            shiftInDb.ChangeGiven,
            shiftInDb.CashRefunds,
            shiftInDb.CashIn,
            shiftInDb.CashOut
        );
        Assert.Equal(13000.00m, expectedCash);

        // Verify cash movement was recorded in shift_cash_movements
        var movements = await _shift.GetCashMovementsForShiftAsync(shift.ShiftId);
        Assert.Single(movements);
        Assert.Equal("CASH_IN", movements[0].MovementType);
        Assert.Equal(8000.00m, movements[0].Amount);
    }

    [Fact]
    public async Task DebtCollection_CardPayment_ReducesCustomerBalance_DoesNotIncreaseDrawerCash()
    {
        var cashier = await _auth.CreateUserAsync("cashier5", "Cashier 05", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var customer = await _customerService.CreateCustomerAsync(
            name: "Chaminda Vaas",
            phone: "0711122334",
            address: "Colombo",
            creditLimit: 50000.00m,
            actorId: cashier.UserId,
            initialBalance: 25000.00m
        );

        // Customer pays 15,000 LKR via CARD
        var entry = await _customerService.RecordPaymentAsync(
            customerId: customer.CustomerId,
            amount: 15000.00m,
            paymentMethod: "CARD",
            actorId: cashier.UserId,
            shiftId: shift.ShiftId,
            notes: "Card payment of credit balance"
        );

        Assert.Equal(10000.00m, entry.BalanceAfter);
        var balance = await _customerService.GetCustomerBalanceAsync(customer.CustomerId);
        Assert.Equal(10000.00m, balance);

        // Physical cash drawer is NOT increased for CARD payments!
        var shiftInDb = await _shift.GetShiftByIdAsync(shift.ShiftId);
        Assert.NotNull(shiftInDb);
        Assert.Equal(0m, shiftInDb.CashIn);
        Assert.Equal(5000.00m, shiftInDb.ExpectedCash);
    }

    [Fact]
    public async Task CancelSale_WithCreditTender_ReversesCustomerBalanceAndRecordsLedgerEntry()
    {
        var cashier = await _auth.CreateUserAsync("cashier6", "Cashier 06", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var product = new Product
        {
            ProductId = "prod_oil_drum",
            Barcode = "3334445556667",
            Name = "Engine Oil 20L Drum",
            UnitPrice = 22000.00m,
            CostBasis = 16000.00m,
            TaxRate = 0.0m,
            StockOnHand = 5m
        };
        await _catalog.AddProductAsync(product);

        var customer = await _customerService.CreateCustomerAsync(
            name: "Lasantha Rodrigo",
            phone: "0755556677",
            address: "Kalutara",
            creditLimit: 40000.00m,
            actorId: cashier.UserId,
            initialBalance: 0.00m
        );

        var cmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CREDIT, 22000.00m) },
            CustomerId: customer.CustomerId
        );

        var sale = await _saleService.CommitSaleAsync(cmd);
        Assert.Equal(22000.00m, await _customerService.GetCustomerBalanceAsync(customer.CustomerId));

        // Now Cancel the sale
        var cancelCmd = new CancelSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            SaleId: sale.SaleId,
            Reason: "Customer requested cancellation before taking goods"
        );

        var cancelledSale = await _saleService.CancelSaleAsync(cancelCmd);
        Assert.Equal(SaleStatus.Cancelled, cancelledSale.Status);

        // Customer balance should be reversed back to 0.00 LKR
        var balanceAfterCancel = await _customerService.GetCustomerBalanceAsync(customer.CustomerId);
        Assert.Equal(0.00m, balanceAfterCancel);

        // Ledger should have SALE_CANCELLED entry
        var ledger = await _customerService.GetCustomerLedgerAsync(customer.CustomerId);
        var cancelEntry = ledger.FirstOrDefault(e => e.EntryType == "SALE_CANCELLED");
        Assert.NotNull(cancelEntry);
        Assert.Equal(22000.00m, cancelEntry.Amount);
        Assert.Equal(0.00m, cancelEntry.BalanceAfter);
    }

    [Fact]
    public async Task CreditSale_WithInactiveCustomer_ThrowsPosException()
    {
        var cashier = await _auth.CreateUserAsync("cashier_inactive", "Cashier Inactive", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var product = new Product
        {
            ProductId = "prod_spark",
            Barcode = "8887776665554",
            Name = "NGK Spark Plug",
            UnitPrice = 2500.00m,
            CostBasis = 1800.00m,
            TaxRate = 0.0m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(product);

        var customer = await _customerService.CreateCustomerAsync(
            name: "Deactivated Customer",
            phone: "0770001122",
            address: null,
            creditLimit: 20000.00m,
            actorId: cashier.UserId
        );

        // Deactivate customer
        await _customerService.UpdateCustomerAsync(
            customerId: customer.CustomerId,
            name: customer.Name,
            phone: customer.Phone,
            address: customer.Address,
            creditLimit: customer.CreditLimit,
            isActive: false,
            actorId: cashier.UserId
        );

        var cmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CREDIT, 2500.00m) },
            CustomerId: customer.CustomerId
        );

        var ex = await Assert.ThrowsAsync<PosException>(() => _saleService.CommitSaleAsync(cmd));
        Assert.Contains("inactive", ex.Message);
    }

    [Fact]
    public async Task CreateCustomer_NegativeCreditLimit_ThrowsArgumentException()
    {
        var actor = await _auth.CreateUserAsync("admin_neg", "Admin", "pass123", Role.Manager, "1234");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _customerService.CreateCustomerAsync("Invalid Cust", "0771122334", null, -500.00m, actor.UserId)
        );
    }

    [Fact]
    public async Task CreateCustomer_NegativeInitialBalance_ThrowsArgumentException()
    {
        var actor = await _auth.CreateUserAsync("admin_neg_bal", "Admin", "pass123", Role.Manager, "1234");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _customerService.CreateCustomerAsync("Invalid Cust", "0771122334", null, 10000.00m, actor.UserId, initialBalance: -100m)
        );
    }

    [Fact]
    public async Task RecordPayment_InvalidAmountOrInactiveCustomer_Throws()
    {
        var actor = await _auth.CreateUserAsync("admin_pay_val", "Admin", "pass123", Role.Manager, "1234");
        var cust = await _customerService.CreateCustomerAsync("Valid Cust", "0775556666", null, 20000m, actor.UserId, initialBalance: 5000m);

        // Zero or negative payment
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _customerService.RecordPaymentAsync(cust.CustomerId, 0m, "CASH", actor.UserId)
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _customerService.RecordPaymentAsync(cust.CustomerId, -500m, "CASH", actor.UserId)
        );

        // Deactivate customer
        await _customerService.UpdateCustomerAsync(cust.CustomerId, cust.Name, cust.Phone, cust.Address, cust.CreditLimit, false, actor.UserId);

        // Payment on inactive customer throws PosException
        await Assert.ThrowsAsync<PosException>(() =>
            _customerService.RecordPaymentAsync(cust.CustomerId, 1000m, "CASH", actor.UserId)
        );
    }

    [Fact]
    public async Task CustomerLedger_MultiplePayments_MaintainsStrictBalanceHistory()
    {
        var actor = await _auth.CreateUserAsync("admin_history", "Admin", "pass123", Role.Manager, "1234");
        var cust = await _customerService.CreateCustomerAsync("History Cust", "0779998888", null, 50000m, actor.UserId, initialBalance: 30000m);

        // Payment 1: 10,000 Cash -> Balance becomes 20,000
        var p1 = await _customerService.RecordPaymentAsync(cust.CustomerId, 10000m, "CASH", actor.UserId);
        Assert.Equal(20000m, p1.BalanceAfter);

        // Payment 2: 15,000 Card -> Balance becomes 5,000
        var p2 = await _customerService.RecordPaymentAsync(cust.CustomerId, 15000m, "CARD", actor.UserId);
        Assert.Equal(5000m, p2.BalanceAfter);

        // Payment 3: 5,000 Cash -> Balance becomes 0.00 (Fully cleared!)
        var p3 = await _customerService.RecordPaymentAsync(cust.CustomerId, 5000m, "CASH", actor.UserId);
        Assert.Equal(0m, p3.BalanceAfter);

        var finalBalance = await _customerService.GetCustomerBalanceAsync(cust.CustomerId);
        Assert.Equal(0.00m, finalBalance);

        var ledger = await _customerService.GetCustomerLedgerAsync(cust.CustomerId);
        Assert.Equal(4, ledger.Count); // Initial + 3 payments
    }

    [Fact]
    public async Task CreditSale_WithoutCustomerId_ThrowsPosException()
    {
        var cashier = await _auth.CreateUserAsync("cashier_anon", "Cashier Anonymous", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var product = new Product
        {
            ProductId = "prod_spark_anon",
            Barcode = "1122334455667",
            Name = "Spark Plug Anon",
            UnitPrice = 3000.00m,
            CostBasis = 2000.00m,
            TaxRate = 0.0m,
            StockOnHand = 10m
        };
        await _catalog.AddProductAsync(product);

        // Attempting to commit a credit sale WITHOUT specifying a CustomerId must fail
        var cmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CREDIT, 3000.00m) },
            CustomerId: null // Anonymous customer on credit is forbidden!
        );

        var ex = await Assert.ThrowsAsync<PosException>(() => _saleService.CommitSaleAsync(cmd));
        Assert.Contains("customer must be specified", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Blank string customer ID should also fail
        var cmdBlank = cmd with { CustomerId = "   " };
        var exBlank = await Assert.ThrowsAsync<PosException>(() => _saleService.CommitSaleAsync(cmdBlank));
        Assert.Contains("customer must be specified", exBlank.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefundCreditSale_CreditsCustomerOutstandingBalance_AndDoesNotDecrementDrawerCash()
    {
        var cashier = await _auth.CreateUserAsync("cashier_crd_ref", "Cashier Refund", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var product = new Product
        {
            ProductId = "prod_side_mirror",
            Barcode = "9988776655443",
            Name = "Side Mirror Set",
            UnitPrice = 8000.00m,
            CostBasis = 5500.00m,
            TaxRate = 0.0m,
            StockOnHand = 5m
        };
        await _catalog.AddProductAsync(product);

        var customer = await _customerService.CreateCustomerAsync(
            name: "Sarath Fonseka",
            phone: "0771239876",
            address: "Gampaha",
            creditLimit: 25000.00m,
            actorId: cashier.UserId,
            initialBalance: 0.00m
        );

        // 1. Customer buys side mirror set on 100% Credit (8,000 LKR)
        var saleCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest> { new(TenderType.CREDIT, 8000.00m) },
            CustomerId: customer.CustomerId
        );
        var sale = await _saleService.CommitSaleAsync(saleCmd);
        Assert.Equal(8000.00m, await _customerService.GetCustomerBalanceAsync(customer.CustomerId));

        // Drawer cash is still just opening float (5,000 LKR)
        var shiftAfterSale = await _shift.GetShiftByIdAsync(shift.ShiftId);
        Assert.NotNull(shiftAfterSale);
        Assert.Equal(0m, shiftAfterSale.CashReceived);
        Assert.Equal(5000.00m, shiftAfterSale.ExpectedCash);

        // 2. Customer returns the item for a refund
        var refundCmd = new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest>
            {
                new(LineId: sale.Lines[0].LineId, QuantityToRefund: 1.0m)
            },
            Reason: "Customer bought wrong model mirror",
            ReturnStockToInventory: true
        );

        var refundSale = await _saleService.RefundSaleAsync(refundCmd);
        Assert.NotNull(refundSale);
        Assert.Equal(8000.00m, refundSale.GrandTotal);
        Assert.Equal(customer.CustomerId, refundSale.CustomerId);

        // Tender on refund sale should be CREDIT, not CASH
        Assert.Single(refundSale.Tenders);
        Assert.Equal(TenderType.CREDIT, refundSale.Tenders[0].TenderType);
        Assert.Equal(8000.00m, refundSale.Tenders[0].AmountTendered);

        // Customer outstanding balance should be credited back to 0.00 LKR
        var balanceAfterRefund = await _customerService.GetCustomerBalanceAsync(customer.CustomerId);
        Assert.Equal(0.00m, balanceAfterRefund);

        // Ledger must contain SALE_REFUND entry
        var ledger = await _customerService.GetCustomerLedgerAsync(customer.CustomerId);
        var refEntry = ledger.FirstOrDefault(e => e.EntryType == "SALE_REFUND");
        Assert.NotNull(refEntry);
        Assert.Equal(8000.00m, refEntry.Amount);
        Assert.Equal(0.00m, refEntry.BalanceAfter);

        // Crucial: Drawer cash must NOT be decremented! CashRefunds remains 0!
        var shiftAfterRefund = await _shift.GetShiftByIdAsync(shift.ShiftId);
        Assert.NotNull(shiftAfterRefund);
        Assert.Equal(0m, shiftAfterRefund.CashRefunds);
        Assert.Equal(5000.00m, shiftAfterRefund.ExpectedCash);
    }

    [Fact]
    public async Task RefundSplitSale_ProratesCreditAndCashRefundsCorrectly()
    {
        var cashier = await _auth.CreateUserAsync("cashier_split_ref", "Cashier Split", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var product = new Product
        {
            ProductId = "prod_clutch_plate",
            Barcode = "5544332211009",
            Name = "Clutch Plate Assembly",
            UnitPrice = 20000.00m,
            CostBasis = 14000.00m,
            TaxRate = 0.0m,
            StockOnHand = 5m
        };
        await _catalog.AddProductAsync(product);

        var customer = await _customerService.CreateCustomerAsync(
            name: "Dhammika Prasad",
            phone: "0722233445",
            address: "Panadura",
            creditLimit: 30000.00m,
            actorId: cashier.UserId,
            initialBalance: 0.00m
        );

        // Split sale: 12,000 Credit + 8,000 Cash = 20,000 Grand Total (60% Credit, 40% Cash)
        var saleCmd = new CreateSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            Items: new List<CreateSaleLineRequest> { new(product.ProductId, 1.0m) },
            Tenders: new List<CreateTenderRequest>
            {
                new(TenderType.CREDIT, 12000.00m),
                new(TenderType.CASH, 8000.00m)
            },
            CustomerId: customer.CustomerId
        );
        var sale = await _saleService.CommitSaleAsync(saleCmd);
        Assert.Equal(12000.00m, await _customerService.GetCustomerBalanceAsync(customer.CustomerId));

        // Refund the entire 20,000 LKR sale
        var refundCmd = new RefundSaleCommand(
            TenantId: "TENANT_LK_01",
            BranchId: "B01",
            CounterId: "C01",
            CashierId: cashier.UserId,
            ShiftId: shift.ShiftId,
            OriginalSaleId: sale.SaleId,
            Items: new List<RefundLineRequest>
            {
                new(LineId: sale.Lines[0].LineId, QuantityToRefund: 1.0m)
            },
            Reason: "Defective clutch plate",
            ReturnStockToInventory: false
        );

        var refundSale = await _saleService.RefundSaleAsync(refundCmd);
        Assert.NotNull(refundSale);
        Assert.Equal(20000.00m, refundSale.GrandTotal);

        // Refund tenders should have: 8,000 Cash and 12,000 Credit
        var cashTender = refundSale.Tenders.FirstOrDefault(t => t.TenderType == TenderType.CASH);
        var creditTender = refundSale.Tenders.FirstOrDefault(t => t.TenderType == TenderType.CREDIT);
        Assert.NotNull(cashTender);
        Assert.NotNull(creditTender);
        Assert.Equal(8000.00m, cashTender.AmountTendered);
        Assert.Equal(12000.00m, creditTender.AmountTendered);

        // Customer balance credited back by 12,000 -> 0.00 LKR
        Assert.Equal(0.00m, await _customerService.GetCustomerBalanceAsync(customer.CustomerId));

        // Shift cash_refunds incremented by Cash portion (8,000) ONLY
        var shiftInDb = await _shift.GetShiftByIdAsync(shift.ShiftId);
        Assert.NotNull(shiftInDb);
        Assert.Equal(8000.00m, shiftInDb.CashRefunds);
        // Expected Cash = 5,000 (float) + 8,000 (cash received) - 8,000 (cash refunds) = 5,000
        var expectedCash = MoneyCalculator.CalculateShiftExpectedCash(
            shiftInDb.OpeningFloat,
            shiftInDb.CashReceived,
            shiftInDb.ChangeGiven,
            shiftInDb.CashRefunds,
            shiftInDb.CashIn,
            shiftInDb.CashOut
        );
        Assert.Equal(5000.00m, expectedCash);
    }

    [Fact]
    public async Task RecordPayment_ClosedShift_ThrowsShiftClosedException_AndDoesNotUpdateCustomerBalance()
    {
        var cashier = await _auth.CreateUserAsync("cashier_atom", "Cashier Atom", "pass123", Role.Cashier);
        var shift = await _shift.OpenShiftAsync("B01", "C01", cashier.UserId, 5000.00m, "TENANT_LK_01");

        var customer = await _customerService.CreateCustomerAsync(
            name: "Atomicity Test Cust",
            phone: "0770009999",
            address: null,
            creditLimit: 30000.00m,
            actorId: cashier.UserId,
            initialBalance: 15000.00m
        );

        // Close the shift
        await _shift.CloseShiftAsync(shift.ShiftId, 5000.00m, cashier.UserId, "TENANT_LK_01");

        // Attempting to record cash payment against CLOSED shift must throw ShiftClosedException
        await Assert.ThrowsAsync<ShiftClosedException>(() =>
            _customerService.RecordPaymentAsync(
                customerId: customer.CustomerId,
                amount: 5000.00m,
                paymentMethod: "CASH",
                actorId: cashier.UserId,
                shiftId: shift.ShiftId
            )
        );

        // Atomic guarantee: Customer balance must NOT have been changed!
        var balance = await _customerService.GetCustomerBalanceAsync(customer.CustomerId);
        Assert.Equal(15000.00m, balance);

        // Ledger must NOT have any payment entry
        var ledger = await _customerService.GetCustomerLedgerAsync(customer.CustomerId);
        Assert.DoesNotContain(ledger, e => e.EntryType == "DEBT_PAYMENT");
    }

    [Fact]
    public async Task CustomerService_EmptyActorId_ThrowsArgumentException()
    {
        var actor = await _auth.CreateUserAsync("mgr_actor_val", "Manager", "pass123", Role.Manager);
        var cust = await _customerService.CreateCustomerAsync("Valid Cust", "0771122334", null, 10000m, actor.UserId);

        // Empty actor in CreateCustomer
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _customerService.CreateCustomerAsync("Name", "0771111111", null, 5000m, "   ")
        );

        // Empty actor in UpdateCustomer
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _customerService.UpdateCustomerAsync(cust.CustomerId, "New Name", "0771122334", null, 10000m, true, "")
        );

        // Empty actor in RecordPayment
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _customerService.RecordPaymentAsync(cust.CustomerId, 1000m, "CASH", "")
        );
    }
}

