using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enightx.Pos.Domain;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface ISyncService
{
    Task<List<OutboxEvent>> GetPendingEventsAsync(int limit = 50);
    Task MarkEventsAcknowledgedAsync(IEnumerable<Guid> eventIds);
    Task<long> GetLastAcknowledgedSequenceAsync(string branchId, string deviceId);
    Task<SyncPushResult> PushPendingBatchesAsync(int batchSize = 50, CancellationToken ct = default);
    Task<CatalogSyncResult> PullCatalogUpdatesAsync(CancellationToken ct = default);
}

public class SyncService : ISyncService
{
    private readonly PosDatabase _db;
    private readonly ICatalogService _catalogService;
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;

    public SyncService(
        PosDatabase db,
        ICatalogService? catalogService = null,
        HttpClient? httpClient = null,
        string apiBaseUrl = "https://posapi.eightexms.site")
    {
        _db = db;
        _catalogService = catalogService ?? new CatalogService(db);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _apiBaseUrl = apiBaseUrl;
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

    public async Task<SyncPushResult> PushPendingBatchesAsync(int batchSize = 50, CancellationToken ct = default)
    {
        var pendingEvents = await GetPendingEventsAsync(batchSize);
        if (pendingEvents.Count == 0)
        {
            return new SyncPushResult { Success = true, PushedCount = 0 };
        }

        try
        {
            var first = pendingEvents[0];
            var batchId = Guid.NewGuid();
            var maxSeq = pendingEvents.Max(e => e.SourceSequence);

            var batchObj = new
            {
                batch_id = batchId,
                tenant_id = first.TenantId,
                source_device_id = first.DeviceId,
                source_generation = first.DeviceGeneration,
                batch_sequence = (int)maxSeq,
                sent_at = DateTime.UtcNow.ToString("o"),
                events = pendingEvents.Select(e => new
                {
                    event_id = e.EventId,
                    tenant_id = e.TenantId,
                    branch_id = e.BranchId,
                    device_id = e.DeviceId,
                    device_generation = e.DeviceGeneration,
                    source_sequence = (int)e.SourceSequence,
                    schema_version = e.SchemaVersion,
                    occurred_at = e.OccurredAtUtc.ToString("o"),
                    actor_id = e.ActorId,
                    causal_reference = e.CausalReference,
                    payload = JsonDocument.Parse(e.PayloadJson).RootElement
                }).ToList()
            };

            var url = $"{_apiBaseUrl.TrimEnd('/')}/api/v1/sync/push";
            using var resp = await _httpClient.PostAsJsonAsync(url, batchObj, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return new SyncPushResult
                {
                    Success = false,
                    PushedCount = 0,
                    ErrorMessage = $"Server returned {resp.StatusCode}: {err}"
                };
            }

            var res = await resp.Content.ReadFromJsonAsync<SyncPushApiResponse>(cancellationToken: ct);
            if (res != null && res.Status.Equals("acknowledged", StringComparison.OrdinalIgnoreCase))
            {
                await MarkEventsAcknowledgedAsync(pendingEvents.Select(e => e.EventId));
                return new SyncPushResult
                {
                    Success = true,
                    PushedCount = pendingEvents.Count,
                    AcknowledgedSequence = res.AcknowledgedSequence
                };
            }

            return new SyncPushResult
            {
                Success = false,
                PushedCount = 0,
                ErrorMessage = "Sync server did not acknowledge batch."
            };
        }
        catch (HttpRequestException ex)
        {
            return new SyncPushResult
            {
                Success = false,
                PushedCount = 0,
                NetworkOffline = true,
                ErrorMessage = ex.Message
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SyncPushResult
            {
                Success = false,
                PushedCount = 0,
                NetworkOffline = true,
                ErrorMessage = "Push request timed out."
            };
        }
        catch (Exception ex)
        {
            return new SyncPushResult
            {
                Success = false,
                PushedCount = 0,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<CatalogSyncResult> PullCatalogUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            var lastSync = await _catalogService.GetLastCatalogSyncTimeAsync();
            var query = new List<string>();
            if (lastSync.HasValue)
            {
                query.Add($"since={Uri.EscapeDataString(lastSync.Value.ToString("o"))}");
            }
            query.Add("limit=200");
            var queryString = string.Join("&", query);

            var url = $"{_apiBaseUrl.TrimEnd('/')}/api/v1/sync/catalog?{queryString}";
            using var resp = await _httpClient.GetAsync(url, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return new CatalogSyncResult
                {
                    Success = false,
                    ErrorMessage = $"Server returned {resp.StatusCode}: {err}"
                };
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var data = await resp.Content.ReadFromJsonAsync<CatalogSyncResponseDto>(options, ct);
            if (data == null)
            {
                return new CatalogSyncResult
                {
                    Success = false,
                    ErrorMessage = "Received empty response from catalog sync endpoint."
                };
            }

            await _catalogService.ApplyCatalogUpdatesAsync(data);

            return new CatalogSyncResult
            {
                Success = true,
                ProductsUpdated = data.Products.Count,
                CategoriesUpdated = data.Categories.Count,
                ItemsDeleted = data.DeletedItemIds.Count,
                ServerTimeUtc = data.ServerTime
            };
        }
        catch (HttpRequestException ex)
        {
            return new CatalogSyncResult
            {
                Success = false,
                NetworkOffline = true,
                ErrorMessage = ex.Message
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CatalogSyncResult
            {
                Success = false,
                NetworkOffline = true,
                ErrorMessage = "Catalog sync timed out."
            };
        }
        catch (Exception ex)
        {
            return new CatalogSyncResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
}

