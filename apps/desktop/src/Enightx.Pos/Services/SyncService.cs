using Microsoft.Data.Sqlite;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface ISyncService
{
    Task<List<OutboxEvent>> GetPendingEventsAsync(int limit = 50);
    Task MarkEventsAcknowledgedAsync(IEnumerable<Guid> eventIds);
    Task<long> GetLastAcknowledgedSequenceAsync(string branchId, string deviceId);
}

public class SyncService : ISyncService
{
    private readonly PosDatabase _db;

    public SyncService(PosDatabase db)
    {
        _db = db;
    }

    public async Task<List<OutboxEvent>> GetPendingEventsAsync(int limit = 50)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT event_id, tenant_id, branch_id, device_id, device_generation,
                   source_sequence, schema_version, payload_json, actor_id,
                   occurred_at_utc, causal_reference, status, created_at_utc
            FROM outbox_events
            WHERE status = 'PENDING'
            ORDER BY source_sequence ASC
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<OutboxEvent>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new OutboxEvent
            {
                EventId = Guid.Parse(reader.GetString(0)),
                TenantId = reader.GetString(1),
                BranchId = reader.GetString(2),
                DeviceId = reader.GetString(3),
                DeviceGeneration = reader.GetInt32(4),
                SourceSequence = reader.GetInt64(5),
                SchemaVersion = reader.GetString(6),
                PayloadJson = reader.GetString(7),
                ActorId = reader.GetString(8),
                OccurredAtUtc = DateTime.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
                CausalReference = reader.IsDBNull(10) ? null : reader.GetString(10),
                Status = reader.GetString(11),
                CreatedAtUtc = DateTime.Parse(reader.GetString(12), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
            });
        }
        return list;
    }

    public async Task MarkEventsAcknowledgedAsync(IEnumerable<Guid> eventIds)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        foreach (var id in eventIds)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE outbox_events SET status = 'ACKNOWLEDGED' WHERE event_id = $id;";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        tx.Commit();
    }

    public async Task<long> GetLastAcknowledgedSequenceAsync(string branchId, string deviceId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(MAX(source_sequence), 0)
            FROM outbox_events
            WHERE branch_id = $bid AND device_id = $did AND status = 'ACKNOWLEDGED';
        ";
        cmd.Parameters.AddWithValue("$bid", branchId);
        cmd.Parameters.AddWithValue("$did", deviceId);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
}

