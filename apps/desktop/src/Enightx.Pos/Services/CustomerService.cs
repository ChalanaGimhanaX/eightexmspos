using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface ICustomerService
{
    Task<Customer> CreateCustomerAsync(string name, string phone, string? address, decimal creditLimit, string actorId, decimal initialBalance = 0m);
    Task<Customer> UpdateCustomerAsync(string customerId, string name, string phone, string? address, decimal creditLimit, bool isActive, string actorId);
    Task<Customer?> GetCustomerByIdAsync(string customerId);
    Task<List<Customer>> SearchCustomersAsync(string query, bool activeOnly = true);
    Task<List<Customer>> GetAllCustomersAsync(bool activeOnly = false);
    Task<decimal> GetCustomerBalanceAsync(string customerId);
    Task<List<CustomerLedgerEntry>> GetCustomerLedgerAsync(string customerId, int limit = 50);
    Task<CustomerLedgerEntry> RecordPaymentAsync(
        string customerId,
        decimal amount,
        string paymentMethod,
        string actorId,
        Guid? shiftId = null,
        string? tenantId = null,
        string? branchId = null,
        string? counterId = null,
        string? notes = null,
        string? reference = null
    );
}

public class CustomerService : ICustomerService
{
    private readonly PosDatabase _db;
    private readonly IShiftService? _shiftService;

    public CustomerService(PosDatabase db, IShiftService? shiftService = null)
    {
        _db = db;
        _shiftService = shiftService;
    }

    public async Task<Customer> CreateCustomerAsync(string name, string phone, string? address, decimal creditLimit, string actorId, decimal initialBalance = 0m)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new ArgumentException("Actor ID cannot be empty.", nameof(actorId));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Customer name cannot be empty.", nameof(name));
        }
        if (string.IsNullOrWhiteSpace(phone))
        {
            throw new ArgumentException("Customer phone cannot be empty.", nameof(phone));
        }

        var limit = MoneyCalculator.Round(creditLimit);
        if (limit < 0)
        {
            throw new ArgumentException("Credit limit cannot be negative.", nameof(creditLimit));
        }

        var balance = MoneyCalculator.Round(initialBalance);
        if (balance < 0)
        {
            throw new ArgumentException("Initial balance cannot be negative.", nameof(initialBalance));
        }

        var customer = new Customer
        {
            CustomerId = $"cust_{Guid.NewGuid():N}",
            Name = name.Trim(),
            Phone = phone.Trim(),
            Address = string.IsNullOrWhiteSpace(address) ? null : address.Trim(),
            CreditLimit = limit,
            OutstandingBalance = balance,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO customers (customer_id, name, phone, address, credit_limit, outstanding_balance, is_active, created_at_utc, updated_at_utc)
            VALUES ($id, $name, $phone, $addr, $limit, $bal, 1, $created, $updated);
        ";
        cmd.Parameters.AddWithValue("$id", customer.CustomerId);
        cmd.Parameters.AddWithValue("$name", customer.Name);
        cmd.Parameters.AddWithValue("$phone", customer.Phone);
        cmd.Parameters.AddWithValue("$addr", (object?)customer.Address ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", customer.CreditLimit);
        cmd.Parameters.AddWithValue("$bal", customer.OutstandingBalance);
        cmd.Parameters.AddWithValue("$created", customer.CreatedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$updated", customer.UpdatedAtUtc.ToString("o"));
        await cmd.ExecuteNonQueryAsync();

        if (balance > 0)
        {
            using var ledgerCmd = conn.CreateCommand();
            ledgerCmd.Transaction = tx;
            ledgerCmd.CommandText = @"
                INSERT INTO customer_ledger_entries (entry_id, customer_id, entry_type, amount, balance_after, reference_id, shift_id, payment_method, notes, actor_id, occurred_at_utc)
                VALUES ($eid, $cid, 'INITIAL_BALANCE', $amt, $bal, null, null, null, $notes, $actor, $occurred);
            ";
            ledgerCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
            ledgerCmd.Parameters.AddWithValue("$cid", customer.CustomerId);
            ledgerCmd.Parameters.AddWithValue("$amt", balance);
            ledgerCmd.Parameters.AddWithValue("$bal", balance);
            ledgerCmd.Parameters.AddWithValue("$notes", "Initial balance recorded on creation");
            ledgerCmd.Parameters.AddWithValue("$actor", actorId);
            ledgerCmd.Parameters.AddWithValue("$occurred", customer.CreatedAtUtc.ToString("o"));
            await ledgerCmd.ExecuteNonQueryAsync();
        }

        // Audit create customer
        using var auditCmd = conn.CreateCommand();
        auditCmd.Transaction = tx;
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, 'TENANT_LK_01', 'B01', 'C01', $actor, 'CREATE_CUSTOMER', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$actor", actorId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            CustomerId = customer.CustomerId,
            Name = customer.Name,
            Phone = customer.Phone,
            CreditLimit = customer.CreditLimit,
            InitialBalance = customer.OutstandingBalance
        }));
        auditCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();

        tx.Commit();
        return customer;
    }

    public async Task<Customer> UpdateCustomerAsync(string customerId, string name, string phone, string? address, decimal creditLimit, bool isActive, string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new ArgumentException("Actor ID cannot be empty.", nameof(actorId));
        }
        if (string.IsNullOrWhiteSpace(customerId))
        {
            throw new ArgumentException("Customer ID cannot be empty.", nameof(customerId));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Customer name cannot be empty.", nameof(name));
        }
        if (string.IsNullOrWhiteSpace(phone))
        {
            throw new ArgumentException("Customer phone cannot be empty.", nameof(phone));
        }

        var limit = MoneyCalculator.Round(creditLimit);
        if (limit < 0)
        {
            throw new ArgumentException("Credit limit cannot be negative.", nameof(creditLimit));
        }

        var existing = await GetCustomerByIdAsync(customerId);
        if (existing == null)
        {
            throw new PosException($"Customer '{customerId}' not found.");
        }

        var nowUtc = DateTime.UtcNow;
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            UPDATE customers SET
                name = $name,
                phone = $phone,
                address = $addr,
                credit_limit = $limit,
                is_active = $active,
                updated_at_utc = $updated
            WHERE customer_id = $id;
        ";
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$phone", phone.Trim());
        cmd.Parameters.AddWithValue("$addr", (object?)address?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$active", isActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated", nowUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$id", customerId);
        await cmd.ExecuteNonQueryAsync();

        // Audit update customer
        using var auditCmd = conn.CreateCommand();
        auditCmd.Transaction = tx;
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, 'TENANT_LK_01', 'B01', 'C01', $actor, 'UPDATE_CUSTOMER', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$actor", actorId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            CustomerId = customerId,
            OldName = existing.Name,
            NewName = name.Trim(),
            OldCreditLimit = existing.CreditLimit,
            NewCreditLimit = limit,
            OldIsActive = existing.IsActive,
            NewIsActive = isActive
        }));
        auditCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();

        tx.Commit();

        existing.Name = name.Trim();
        existing.Phone = phone.Trim();
        existing.Address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        existing.CreditLimit = limit;
        existing.IsActive = isActive;
        existing.UpdatedAtUtc = nowUtc;

        return existing;
    }

    public async Task<Customer?> GetCustomerByIdAsync(string customerId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT customer_id, name, phone, address, credit_limit, outstanding_balance, is_active, created_at_utc, updated_at_utc
            FROM customers WHERE customer_id = $id;
        ";
        cmd.Parameters.AddWithValue("$id", customerId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return MapCustomer(reader);
    }

    public async Task<List<Customer>> SearchCustomersAsync(string query, bool activeOnly = true)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        var filter = "%" + (query ?? "").Trim() + "%";

        cmd.CommandText = $@"
            SELECT customer_id, name, phone, address, credit_limit, outstanding_balance, is_active, created_at_utc, updated_at_utc
            FROM customers
            WHERE (name LIKE $query OR phone LIKE $query)
            {(activeOnly ? "AND is_active = 1" : "")}
            ORDER BY name ASC;
        ";
        cmd.Parameters.AddWithValue("$query", filter);

        var list = new List<Customer>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(MapCustomer(reader));
        }
        return list;
    }

    public async Task<List<Customer>> GetAllCustomersAsync(bool activeOnly = false)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT customer_id, name, phone, address, credit_limit, outstanding_balance, is_active, created_at_utc, updated_at_utc
            FROM customers
            {(activeOnly ? "WHERE is_active = 1" : "")}
            ORDER BY name ASC;
        ";

        var list = new List<Customer>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(MapCustomer(reader));
        }
        return list;
    }

    public async Task<decimal> GetCustomerBalanceAsync(string customerId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT outstanding_balance FROM customers WHERE customer_id = $id;";
        cmd.Parameters.AddWithValue("$id", customerId);

        var obj = await cmd.ExecuteScalarAsync();
        if (obj == null || obj == DBNull.Value)
        {
            throw new PosException($"Customer '{customerId}' not found.");
        }
        return Convert.ToDecimal(obj);
    }

    public async Task<List<CustomerLedgerEntry>> GetCustomerLedgerAsync(string customerId, int limit = 50)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT entry_id, customer_id, entry_type, amount, balance_after, reference_id, shift_id, payment_method, notes, actor_id, occurred_at_utc
            FROM customer_ledger_entries
            WHERE customer_id = $cid
            ORDER BY occurred_at_utc DESC
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$cid", customerId);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<CustomerLedgerEntry>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new CustomerLedgerEntry
            {
                EntryId = Guid.Parse(reader.GetString(0)),
                CustomerId = reader.GetString(1),
                EntryType = reader.GetString(2),
                Amount = reader.GetDecimal(3),
                BalanceAfter = reader.GetDecimal(4),
                ReferenceId = reader.IsDBNull(5) ? null : reader.GetString(5),
                ShiftId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                PaymentMethod = reader.IsDBNull(7) ? null : reader.GetString(7),
                Notes = reader.IsDBNull(8) ? null : reader.GetString(8),
                ActorId = reader.GetString(9),
                OccurredAtUtc = DateTime.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
            });
        }
        return list;
    }

    public async Task<CustomerLedgerEntry> RecordPaymentAsync(
        string customerId,
        decimal amount,
        string paymentMethod,
        string actorId,
        Guid? shiftId = null,
        string? tenantId = null,
        string? branchId = null,
        string? counterId = null,
        string? notes = null,
        string? reference = null)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new ArgumentException("Actor ID cannot be empty.", nameof(actorId));
        }

        var payAmt = MoneyCalculator.Round(amount);
        if (payAmt <= 0)
        {
            throw new ArgumentException("Payment amount must be greater than zero.", nameof(amount));
        }

        var customer = await GetCustomerByIdAsync(customerId);
        if (customer == null)
        {
            throw new PosException($"Customer '{customerId}' not found.");
        }
        if (!customer.IsActive)
        {
            throw new PosException($"Cannot accept payment: Customer '{customer.Name}' is inactive.");
        }

        var effectiveMethod = paymentMethod.ToUpperInvariant().Trim();
        var nowUtc = DateTime.UtcNow;
        var newBalance = MoneyCalculator.Round(customer.OutstandingBalance - payAmt);

        var entry = new CustomerLedgerEntry
        {
            EntryId = Guid.NewGuid(),
            CustomerId = customerId,
            EntryType = "DEBT_PAYMENT",
            Amount = payAmt,
            BalanceAfter = newBalance,
            ReferenceId = reference ?? $"PAY_{nowUtc:yyyyMMddHHmmss}",
            ShiftId = shiftId,
            PaymentMethod = effectiveMethod,
            Notes = notes ?? $"Debt payment received via {effectiveMethod}",
            ActorId = actorId,
            OccurredAtUtc = nowUtc
        };

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // 1. If payment is in CASH with an active shift, verify shift is open and update drawer atomically
        if (effectiveMethod == "CASH" && shiftId.HasValue)
        {
            using var chkShiftCmd = conn.CreateCommand();
            chkShiftCmd.Transaction = tx;
            chkShiftCmd.CommandText = "SELECT status FROM shifts WHERE shift_id = $sid;";
            chkShiftCmd.Parameters.AddWithValue("$sid", shiftId.Value.ToString());
            var shiftStatus = await chkShiftCmd.ExecuteScalarAsync();
            if (shiftStatus == null || Convert.ToInt32(shiftStatus) != 1)
            {
                throw new ShiftClosedException("Cannot record cash payment: Shift is not open or not found.");
            }

            using var shiftUpdCmd = conn.CreateCommand();
            shiftUpdCmd.Transaction = tx;
            shiftUpdCmd.CommandText = "UPDATE shifts SET cash_in = cash_in + $amt WHERE shift_id = $sid;";
            shiftUpdCmd.Parameters.AddWithValue("$amt", payAmt);
            shiftUpdCmd.Parameters.AddWithValue("$sid", shiftId.Value.ToString());
            await shiftUpdCmd.ExecuteNonQueryAsync();

            using var moveCmd = conn.CreateCommand();
            moveCmd.Transaction = tx;
            moveCmd.CommandText = @"
                INSERT INTO shift_cash_movements (movement_id, shift_id, movement_type, amount, reason, actor_id, occurred_at_utc)
                VALUES ($mid, $sid, 'CASH_IN', $amt, $reason, $actor, $occurred);
            ";
            moveCmd.Parameters.AddWithValue("$mid", Guid.NewGuid().ToString());
            moveCmd.Parameters.AddWithValue("$sid", shiftId.Value.ToString());
            moveCmd.Parameters.AddWithValue("$amt", payAmt);
            moveCmd.Parameters.AddWithValue("$reason", $"Customer debt payment: {customer.Name} ({customer.CustomerId})");
            moveCmd.Parameters.AddWithValue("$actor", actorId);
            moveCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
            await moveCmd.ExecuteNonQueryAsync();
        }

        // 2. Update customer outstanding balance
        using var updCmd = conn.CreateCommand();
        updCmd.Transaction = tx;
        updCmd.CommandText = @"
            UPDATE customers SET
                outstanding_balance = $bal,
                updated_at_utc = $updated
            WHERE customer_id = $cid;
        ";
        updCmd.Parameters.AddWithValue("$bal", newBalance);
        updCmd.Parameters.AddWithValue("$updated", nowUtc.ToString("o"));
        updCmd.Parameters.AddWithValue("$cid", customerId);
        await updCmd.ExecuteNonQueryAsync();

        // 3. Insert into customer_ledger_entries
        using var ledgerCmd = conn.CreateCommand();
        ledgerCmd.Transaction = tx;
        ledgerCmd.CommandText = @"
            INSERT INTO customer_ledger_entries (
                entry_id, customer_id, entry_type, amount, balance_after,
                reference_id, shift_id, payment_method, notes, actor_id, occurred_at_utc
            ) VALUES (
                $eid, $cid, $etype, $amt, $bal,
                $ref, $sid, $method, $notes, $actor, $occurred
            );
        ";
        ledgerCmd.Parameters.AddWithValue("$eid", entry.EntryId.ToString());
        ledgerCmd.Parameters.AddWithValue("$cid", entry.CustomerId);
        ledgerCmd.Parameters.AddWithValue("$etype", entry.EntryType);
        ledgerCmd.Parameters.AddWithValue("$amt", entry.Amount);
        ledgerCmd.Parameters.AddWithValue("$bal", entry.BalanceAfter);
        ledgerCmd.Parameters.AddWithValue("$ref", (object?)entry.ReferenceId ?? DBNull.Value);
        ledgerCmd.Parameters.AddWithValue("$sid", (object?)entry.ShiftId?.ToString() ?? DBNull.Value);
        ledgerCmd.Parameters.AddWithValue("$method", (object?)entry.PaymentMethod ?? DBNull.Value);
        ledgerCmd.Parameters.AddWithValue("$notes", (object?)entry.Notes ?? DBNull.Value);
        ledgerCmd.Parameters.AddWithValue("$actor", entry.ActorId);
        ledgerCmd.Parameters.AddWithValue("$occurred", entry.OccurredAtUtc.ToString("o"));
        await ledgerCmd.ExecuteNonQueryAsync();

        // 4. Log audit event
        using var auditCmd = conn.CreateCommand();
        auditCmd.Transaction = tx;
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, 'CUSTOMER_DEBT_PAYMENT', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", tenantId ?? "TENANT_LK_01");
        auditCmd.Parameters.AddWithValue("$bid", branchId ?? "B01");
        auditCmd.Parameters.AddWithValue("$cid", counterId ?? "C01");
        auditCmd.Parameters.AddWithValue("$actor", actorId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            CustomerId = customerId,
            CustomerName = customer.Name,
            AmountPaid = payAmt,
            PaymentMethod = effectiveMethod,
            BalanceBefore = customer.OutstandingBalance,
            BalanceAfter = newBalance,
            Reference = entry.ReferenceId
        }));
        auditCmd.Parameters.AddWithValue("$occurred", nowUtc.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();

        tx.Commit();

        return entry;
    }

    private static Customer MapCustomer(SqliteDataReader reader)
    {
        return new Customer
        {
            CustomerId = reader.GetString(0),
            Name = reader.GetString(1),
            Phone = reader.GetString(2),
            Address = reader.IsDBNull(3) ? null : reader.GetString(3),
            CreditLimit = reader.GetDecimal(4),
            OutstandingBalance = reader.GetDecimal(5),
            IsActive = reader.GetInt32(6) == 1,
            CreatedAtUtc = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            UpdatedAtUtc = DateTime.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
        };
    }
}

