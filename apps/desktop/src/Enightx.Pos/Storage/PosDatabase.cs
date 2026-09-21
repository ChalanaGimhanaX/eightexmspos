using Microsoft.Data.Sqlite;
using System.Data;

namespace Enightx.Pos.Storage;

public class PosDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly bool _isInMemory;
    private SqliteConnection? _keepAliveConnection; // For in-memory testing

    public PosDatabase(string connectionString, bool isInMemory = false)
    {
        _connectionString = connectionString;
        _isInMemory = isInMemory;
    }

    public static PosDatabase CreateInMemory()
    {
        var dbName = $"mem_{Guid.NewGuid():N}";
        var db = new PosDatabase($"Data Source={dbName};Mode=Memory;Cache=Shared", isInMemory: true);
        db.Initialize();
        return db;
    }

    public static PosDatabase CreateFile(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var db = new PosDatabase($"Data Source={filePath};Mode=ReadWriteCreate;Cache=Shared", isInMemory: false);
        db.Initialize();
        return db;
    }

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();

        if (!_isInMemory)
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            cmd.ExecuteNonQuery();
        }

        return conn;
    }

    public void Initialize()
    {
        if (_isInMemory && _keepAliveConnection == null)
        {
            _keepAliveConnection = new SqliteConnection(_connectionString);
            _keepAliveConnection.Open();
        }

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaDdl;
        cmd.ExecuteNonQuery();

        try
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE products ADD COLUMN category_id TEXT;";
            alterCmd.ExecuteNonQuery();
        }
        catch
        {
            // Column already exists or table freshly created
        }

        try
        {
            using var alterLineCmd = conn.CreateCommand();
            alterLineCmd.CommandText = "ALTER TABLE sale_lines ADD COLUMN cost_basis NUMERIC NOT NULL DEFAULT 0;";
            alterLineCmd.ExecuteNonQuery();
        }
        catch
        {
            // Column already exists or table freshly created
        }

        try
        {
            using var alterLineCmd2 = conn.CreateCommand();
            alterLineCmd2.CommandText = "ALTER TABLE sale_lines ADD COLUMN parent_line_id TEXT;";
            alterLineCmd2.ExecuteNonQuery();
        }
        catch
        {
            // Column already exists or table freshly created
        }

        try
        {
            using var alterHeldCmd = conn.CreateCommand();
            alterHeldCmd.CommandText = "ALTER TABLE held_carts ADD COLUMN tenant_id TEXT NOT NULL DEFAULT 'TENANT_LK_01';";
            alterHeldCmd.ExecuteNonQuery();
        }
        catch
        {
            // Column already exists or table freshly created
        }

        try
        {
            using var alterShiftCmd = conn.CreateCommand();
            alterShiftCmd.CommandText = "ALTER TABLE shifts ADD COLUMN tenant_id TEXT NOT NULL DEFAULT 'TENANT_LK_01';";
            alterShiftCmd.ExecuteNonQuery();
        }
        catch
        {
            // Column already exists or table freshly created
        }

        try
        {
            using var alterPinCmd = conn.CreateCommand();
            alterPinCmd.CommandText = "ALTER TABLE users ADD COLUMN pin_hash TEXT;";
            alterPinCmd.ExecuteNonQuery();
        }
        catch { }

        try
        {
            using var alterPinSaltCmd = conn.CreateCommand();
            alterPinSaltCmd.CommandText = "ALTER TABLE users ADD COLUMN pin_salt TEXT;";
            alterPinSaltCmd.ExecuteNonQuery();
        }
        catch { }

        try
        {
            using var alterSalesCustCmd = conn.CreateCommand();
            alterSalesCustCmd.CommandText = "ALTER TABLE sales ADD COLUMN customer_id TEXT;";
            alterSalesCustCmd.ExecuteNonQuery();
        }
        catch { }
    }

    private const string SchemaDdl = @"
        CREATE TABLE IF NOT EXISTS users (
            user_id TEXT PRIMARY KEY,
            username TEXT UNIQUE NOT NULL,
            display_name TEXT NOT NULL,
            role INTEGER NOT NULL,
            password_hash TEXT NOT NULL,
            password_salt TEXT NOT NULL,
            pin_hash TEXT,
            pin_salt TEXT,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS categories (
            category_id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            description TEXT,
            is_active INTEGER NOT NULL DEFAULT 1,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS products (
            product_id TEXT PRIMARY KEY,
            category_id TEXT,
            barcode TEXT UNIQUE NOT NULL,
            name TEXT NOT NULL,
            name_si TEXT,
            name_ta TEXT,
            unit_price NUMERIC NOT NULL,
            cost_basis NUMERIC NOT NULL,
            tax_rate NUMERIC NOT NULL DEFAULT 0.0,
            stock_on_hand NUMERIC NOT NULL DEFAULT 0.0,
            is_active INTEGER NOT NULL DEFAULT 1,
            FOREIGN KEY (category_id) REFERENCES categories(category_id)
        );

        CREATE TABLE IF NOT EXISTS sync_state (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS receipt_sequences (
            branch_id TEXT NOT NULL,
            counter_id TEXT NOT NULL,
            last_sequence INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (branch_id, counter_id)
        );

        CREATE TABLE IF NOT EXISTS shifts (
            shift_id TEXT PRIMARY KEY,
            tenant_id TEXT NOT NULL DEFAULT 'TENANT_LK_01',
            branch_id TEXT NOT NULL,
            counter_id TEXT NOT NULL,
            cashier_id TEXT NOT NULL,
            opened_at_utc TEXT NOT NULL,
            closed_at_utc TEXT,
            opening_float NUMERIC NOT NULL,
            cash_received NUMERIC NOT NULL DEFAULT 0,
            change_given NUMERIC NOT NULL DEFAULT 0,
            cash_refunds NUMERIC NOT NULL DEFAULT 0,
            cash_in NUMERIC NOT NULL DEFAULT 0,
            cash_out NUMERIC NOT NULL DEFAULT 0,
            expected_cash NUMERIC NOT NULL,
            actual_counted_cash NUMERIC,
            variance NUMERIC,
            status INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS sales (
            sale_id TEXT PRIMARY KEY,
            receipt_number TEXT UNIQUE NOT NULL,
            shift_id TEXT NOT NULL,
            tenant_id TEXT NOT NULL,
            branch_id TEXT NOT NULL,
            counter_id TEXT NOT NULL,
            cashier_id TEXT NOT NULL,
            customer_id TEXT,
            parent_sale_id TEXT,
            subtotal NUMERIC NOT NULL,
            discount_total NUMERIC NOT NULL,
            tax_total NUMERIC NOT NULL,
            grand_total NUMERIC NOT NULL,
            status INTEGER NOT NULL DEFAULT 1,
            reprint_count INTEGER NOT NULL DEFAULT 0,
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY (shift_id) REFERENCES shifts(shift_id),
            FOREIGN KEY (cashier_id) REFERENCES users(user_id)
        );

        CREATE TABLE IF NOT EXISTS sale_lines (
            line_id TEXT PRIMARY KEY,
            sale_id TEXT NOT NULL,
            product_id TEXT NOT NULL,
            product_name TEXT NOT NULL,
            barcode TEXT NOT NULL,
            quantity NUMERIC NOT NULL,
            unit_price NUMERIC NOT NULL,
            cost_basis NUMERIC NOT NULL DEFAULT 0,
            discount_rate NUMERIC NOT NULL DEFAULT 0,
            discount_fixed NUMERIC NOT NULL DEFAULT 0,
            discount_amount NUMERIC NOT NULL DEFAULT 0,
            tax_rate NUMERIC NOT NULL DEFAULT 0,
            tax_amount NUMERIC NOT NULL DEFAULT 0,
            line_total NUMERIC NOT NULL,
            parent_line_id TEXT,
            FOREIGN KEY (sale_id) REFERENCES sales(sale_id) ON DELETE CASCADE,
            FOREIGN KEY (product_id) REFERENCES products(product_id)
        );

        CREATE TABLE IF NOT EXISTS tenders (
            tender_id TEXT PRIMARY KEY,
            sale_id TEXT NOT NULL,
            tender_type TEXT NOT NULL,
            amount_tendered NUMERIC NOT NULL,
            change_given NUMERIC NOT NULL DEFAULT 0,
            payment_reference TEXT,
            FOREIGN KEY (sale_id) REFERENCES sales(sale_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS stock_movements (
            movement_id TEXT PRIMARY KEY,
            product_id TEXT NOT NULL,
            movement_type TEXT NOT NULL,
            quantity_change NUMERIC NOT NULL,
            reference_id TEXT NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            FOREIGN KEY (product_id) REFERENCES products(product_id)
        );

        CREATE TABLE IF NOT EXISTS audit_events (
            event_id TEXT PRIMARY KEY,
            tenant_id TEXT NOT NULL,
            branch_id TEXT NOT NULL,
            counter_id TEXT NOT NULL,
            actor_id TEXT NOT NULL,
            action TEXT NOT NULL,
            details_json TEXT NOT NULL,
            occurred_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS outbox_events (
            event_id TEXT PRIMARY KEY,
            tenant_id TEXT NOT NULL,
            branch_id TEXT NOT NULL,
            device_id TEXT NOT NULL,
            device_generation INTEGER NOT NULL,
            source_sequence INTEGER NOT NULL,
            schema_version TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            actor_id TEXT NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            causal_reference TEXT,
            status TEXT NOT NULL,
            created_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS held_carts (
            held_cart_id TEXT PRIMARY KEY,
            tenant_id TEXT NOT NULL DEFAULT 'TENANT_LK_01',
            branch_id TEXT NOT NULL,
            counter_id TEXT NOT NULL,
            cashier_id TEXT NOT NULL,
            customer_reference TEXT,
            subtotal NUMERIC NOT NULL,
            discount_total NUMERIC NOT NULL,
            tax_total NUMERIC NOT NULL,
            grand_total NUMERIC NOT NULL,
            held_at_utc TEXT NOT NULL,
            cart_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS shift_cash_movements (
            movement_id TEXT PRIMARY KEY,
            shift_id TEXT NOT NULL,
            movement_type TEXT NOT NULL,
            amount NUMERIC NOT NULL,
            reason TEXT NOT NULL,
            actor_id TEXT NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            FOREIGN KEY (shift_id) REFERENCES shifts(shift_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS goods_receipts (
            receipt_id TEXT PRIMARY KEY,
            tenant_id TEXT NOT NULL,
            branch_id TEXT NOT NULL,
            supplier_name TEXT NOT NULL,
            invoice_reference TEXT NOT NULL,
            received_by_user_id TEXT NOT NULL,
            received_at_utc TEXT NOT NULL,
            total_cost NUMERIC NOT NULL,
            notes TEXT
        );

        CREATE TABLE IF NOT EXISTS goods_receipt_lines (
            line_id TEXT PRIMARY KEY,
            receipt_id TEXT NOT NULL,
            product_id TEXT NOT NULL,
            quantity NUMERIC NOT NULL,
            unit_cost NUMERIC NOT NULL,
            line_total_cost NUMERIC NOT NULL,
            FOREIGN KEY (receipt_id) REFERENCES goods_receipts(receipt_id) ON DELETE CASCADE,
            FOREIGN KEY (product_id) REFERENCES products(product_id)
        );

        CREATE TABLE IF NOT EXISTS customers (
            customer_id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            phone TEXT NOT NULL,
            address TEXT,
            credit_limit NUMERIC NOT NULL DEFAULT 0.0,
            outstanding_balance NUMERIC NOT NULL DEFAULT 0.0,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS customer_ledger_entries (
            entry_id TEXT PRIMARY KEY,
            customer_id TEXT NOT NULL,
            entry_type TEXT NOT NULL,
            amount NUMERIC NOT NULL,
            balance_after NUMERIC NOT NULL,
            reference_id TEXT,
            shift_id TEXT,
            payment_method TEXT,
            notes TEXT,
            actor_id TEXT NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            FOREIGN KEY (customer_id) REFERENCES customers(customer_id)
        );

        CREATE INDEX IF NOT EXISTS idx_customer_ledger_cust ON customer_ledger_entries(customer_id);
    ";

    public void Dispose()
    {
        _keepAliveConnection?.Dispose();
        _keepAliveConnection = null;
    }
}
