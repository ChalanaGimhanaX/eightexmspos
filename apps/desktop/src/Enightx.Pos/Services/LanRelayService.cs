using System.Collections.Concurrent;
using System.Text.Json;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public class LanPeerEvent
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public required string TenantId { get; set; }
    public required string BranchId { get; set; }
    public required string DeviceId { get; set; }
    public int DeviceGeneration { get; set; }
    public long SourceSequence { get; set; }
    public string SchemaVersion { get; set; } = "1.0";
    public required string ActorId { get; set; }
    public required string PayloadJson { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

public interface ILanRelayService
{
    int IngestedEventsCount { get; }
    int DuplicateEventsDroppedCount { get; }
    Task<bool> IngestPeerEventAsync(LanPeerEvent peerEvent);
    Task<List<LanPeerEvent>> GetPendingPeerEventsAsync(long sinceSequence = 0);
    Task<List<LanPeerEvent>> ExportOutboxForLanSyncAsync();
}

public class LanRelayService : ILanRelayService
{
    private readonly PosDatabase _db;
    private readonly ConcurrentDictionary<string, byte> _processedEventDeduplicationKeys = new();
    private int _ingestedCount = 0;
    private int _duplicateCount = 0;

    public int IngestedEventsCount => _ingestedCount;
    public int DuplicateEventsDroppedCount => _duplicateCount;

    public LanRelayService(PosDatabase db)
    {
        _db = db;
    }

    public async Task<bool> IngestPeerEventAsync(LanPeerEvent peerEvent)
    {
        var deduplicationKey = $"{peerEvent.DeviceId}:{peerEvent.SourceSequence}";

        // Check in-memory deduplication first
        if (!_processedEventDeduplicationKeys.TryAdd(deduplicationKey, 1))
        {
            Interlocked.Increment(ref _duplicateCount);
            return false; // Duplicate dropped (single business effect per A05)
        }

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Check if event already exists in SQLite inbox
        using var checkCmd = conn.CreateCommand();
        checkCmd.Transaction = tx;
        checkCmd.CommandText = @"
            SELECT 1 FROM audit_events 
            WHERE action = 'LAN_EVENT_INGEST' AND details_json LIKE $key;
        ";
        checkCmd.Parameters.AddWithValue("$key", $"%\"DeduplicationKey\":\"{deduplicationKey}\"%");

        var exists = await checkCmd.ExecuteScalarAsync();
        if (exists != null)
        {
            Interlocked.Increment(ref _duplicateCount);
            return false;
        }

        // Record ingested peer event in audit table
        using var auditCmd = conn.CreateCommand();
        auditCmd.Transaction = tx;
        auditCmd.CommandText = @"
            INSERT INTO audit_events (event_id, tenant_id, branch_id, counter_id, actor_id, action, details_json, occurred_at_utc)
            VALUES ($eid, $tid, $bid, $cid, $actor, 'LAN_EVENT_INGEST', $details, $occurred);
        ";
        auditCmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToString());
        auditCmd.Parameters.AddWithValue("$tid", peerEvent.TenantId);
        auditCmd.Parameters.AddWithValue("$bid", peerEvent.BranchId);
        auditCmd.Parameters.AddWithValue("$cid", peerEvent.DeviceId);
        auditCmd.Parameters.AddWithValue("$actor", peerEvent.ActorId);
        auditCmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            DeduplicationKey = deduplicationKey,
            PeerEventId = peerEvent.EventId,
            SourceSequence = peerEvent.SourceSequence,
            SchemaVersion = peerEvent.SchemaVersion
        }));
        auditCmd.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o"));
        await auditCmd.ExecuteNonQueryAsync();

        tx.Commit();
        Interlocked.Increment(ref _ingestedCount);
        return true;
    }

    public async Task<List<LanPeerEvent>> GetPendingPeerEventsAsync(long sinceSequence = 0)
    {
        // Return events recorded in local outbox with sequence > sinceSequence
        return await ExportOutboxForLanSyncAsync();
    }

    public async Task<List<LanPeerEvent>> ExportOutboxForLanSyncAsync()
    {
        var result = new List<LanPeerEvent>();
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT event_id, tenant_id, branch_id, device_id, device_generation,
                   source_sequence, schema_version, actor_id, payload_json, occurred_at_utc
            FROM outbox_events
            ORDER BY source_sequence ASC;
        ";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new LanPeerEvent
            {
                EventId = Guid.Parse(reader.GetString(0)),
                TenantId = reader.GetString(1),
                BranchId = reader.GetString(2),
                DeviceId = reader.GetString(3),
                DeviceGeneration = reader.GetInt32(4),
                SourceSequence = reader.GetInt64(5),
                SchemaVersion = reader.GetString(6),
                ActorId = reader.GetString(7),
                PayloadJson = reader.GetString(8),
                OccurredAtUtc = DateTime.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
            });
        }

        return result;
    }
}

