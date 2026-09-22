using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

[Collection("SequentialTests")]
public class SyncMechanicsChallengerTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly CatalogService _catalog;
    private readonly string? _originalEnv;

    public SyncMechanicsChallengerTests()
    {
        _db = PosDatabase.CreateInMemory();
        _catalog = new CatalogService(_db);
        _originalEnv = Environment.GetEnvironmentVariable("ENIGHTX_DEVICE_TOKEN");
        Environment.SetEnvironmentVariable("ENIGHTX_DEVICE_TOKEN", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ENIGHTX_DEVICE_TOKEN", _originalEnv);
        _db.Dispose();
    }

    private async Task SetSqliteTokenAsync(string token)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO sync_state (key, value, updated_at_utc) VALUES ('device_token', $val, $now);";
        cmd.Parameters.AddWithValue("$val", token);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedPendingOutboxEventAsync()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO outbox_events (
                event_id, tenant_id, branch_id, device_id, device_generation,
                source_sequence, schema_version, payload_json, actor_id,
                occurred_at_utc, causal_reference, status, created_at_utc
            ) VALUES (
                $id, 'TENANT_LK_01', 'B01', 'C01', 1,
                1, '1.0', '{}', 'usr_cashier_01',
                $now, NULL, 'PENDING', $now
            );";
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static HttpResponseMessage CreateOkCatalogResponse()
    {
        var fake = new CatalogSyncResponseDto
        {
            ServerTime = DateTime.UtcNow,
            Categories = new List<CategoryDto>(),
            Products = new List<CatalogProductDto>(),
            DeletedItemIds = new List<string>(),
            HasMore = false
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(fake), System.Text.Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage CreateOkPushResponse(long seq = 1)
    {
        var fake = new
        {
            batch_id = Guid.NewGuid(),
            acknowledged_sequence = seq,
            status = "acknowledged"
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(fake), System.Text.Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task Fallback_1_ExplicitToken_TakesPrecedenceOverEnvVarAndSqlite()
    {
        Environment.SetEnvironmentVariable("ENIGHTX_DEVICE_TOKEN", "tok_env_secondary");
        await SetSqliteTokenAsync("tok_sqlite_tertiary");

        HttpRequestMessage? captured = null;
        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            captured = req;
            return CreateOkCatalogResponse();
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.site", deviceToken: "tok_explicit_primary");

        var result = await sync.PullCatalogUpdatesAsync();
        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.True(captured.Headers.Contains("X-Device-Token"));
        Assert.Equal("tok_explicit_primary", captured.Headers.GetValues("X-Device-Token").Single());
    }

    [Fact]
    public async Task Fallback_2_EnvVar_TakesPrecedenceOverSqlite_WhenExplicitNull()
    {
        Environment.SetEnvironmentVariable("ENIGHTX_DEVICE_TOKEN", "tok_env_secondary");
        await SetSqliteTokenAsync("tok_sqlite_tertiary");

        HttpRequestMessage? captured = null;
        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            captured = req;
            return CreateOkCatalogResponse();
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.site", deviceToken: null);

        var result = await sync.PullCatalogUpdatesAsync();
        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.True(captured.Headers.Contains("X-Device-Token"));
        Assert.Equal("tok_env_secondary", captured.Headers.GetValues("X-Device-Token").Single());
    }

    [Fact]
    public async Task Fallback_3_Sqlite_Used_WhenExplicitAndEnvVarNull()
    {
        Environment.SetEnvironmentVariable("ENIGHTX_DEVICE_TOKEN", null);
        await SetSqliteTokenAsync("tok_sqlite_tertiary");

        HttpRequestMessage? captured = null;
        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            captured = req;
            return CreateOkCatalogResponse();
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.site", deviceToken: null);

        var result = await sync.PullCatalogUpdatesAsync();
        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.True(captured.Headers.Contains("X-Device-Token"));
        Assert.Equal("tok_sqlite_tertiary", captured.Headers.GetValues("X-Device-Token").Single());
    }

    [Fact]
    public async Task Fallback_4_Null_WhenAllSourcesAbsent_NoHeaderAttached()
    {
        Environment.SetEnvironmentVariable("ENIGHTX_DEVICE_TOKEN", null);

        HttpRequestMessage? captured = null;
        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            captured = req;
            return CreateOkCatalogResponse();
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.site", deviceToken: null);

        var result = await sync.PullCatalogUpdatesAsync();
        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.False(captured.Headers.Contains("X-Device-Token"), "Header should be omitted when no token resolved");
    }

    [Fact]
    public async Task EdgeCase_WhitespaceExplicitToken_FallsBackToEnvVar()
    {
        Environment.SetEnvironmentVariable("ENIGHTX_DEVICE_TOKEN", "tok_env_fallback");
        await SetSqliteTokenAsync("tok_sqlite_ignore");

        HttpRequestMessage? captured = null;
        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            captured = req;
            return CreateOkCatalogResponse();
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.site", deviceToken: "   \t\n  ");

        var result = await sync.PullCatalogUpdatesAsync();
        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.Equal("tok_env_fallback", captured.Headers.GetValues("X-Device-Token").Single());
    }

    [Fact]
    public async Task EdgeCase_WhitespaceEnvVar_FallsBackToSqlite()
    {
        Environment.SetEnvironmentVariable("ENIGHTX_DEVICE_TOKEN", "   ");
        await SetSqliteTokenAsync("tok_sqlite_from_db");

        HttpRequestMessage? captured = null;
        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            captured = req;
            return CreateOkCatalogResponse();
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.site", deviceToken: null);

        var result = await sync.PullCatalogUpdatesAsync();
        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.Equal("tok_sqlite_from_db", captured.Headers.GetValues("X-Device-Token").Single());
    }

    [Fact]
    public async Task EdgeCase_WhitespaceSqlite_OmittedHeader()
    {
        Environment.SetEnvironmentVariable("ENIGHTX_DEVICE_TOKEN", null);
        await SetSqliteTokenAsync("    ");

        HttpRequestMessage? captured = null;
        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            captured = req;
            return CreateOkCatalogResponse();
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.site", deviceToken: null);

        var result = await sync.PullCatalogUpdatesAsync();
        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.False(captured.Headers.Contains("X-Device-Token"));
    }

    [Fact]
    public async Task EdgeCase_MissingSyncStateTable_GracefulFallbackToNull()
    {
        Environment.SetEnvironmentVariable("ENIGHTX_DEVICE_TOKEN", null);

        // PosDatabase without sync_state table
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS sync_state;";
        await cmd.ExecuteNonQueryAsync();

        var sync = new SyncService(_db, _catalog, null, "https://mockapi.site", deviceToken: null);

        // Invoke private GetDeviceTokenAsync via reflection
        var method = typeof(SyncService).GetMethod("GetDeviceTokenAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<string?>)method.Invoke(sync, null)!;
        var token = await task;

        Assert.Null(token);
    }

    [Fact]
    public async Task HeaderAttachment_PushPendingBatchesAsync_AttachesHeaderProperly()
    {
        await SeedPendingOutboxEventAsync();

        HttpRequestMessage? captured = null;
        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            captured = req;
            return CreateOkPushResponse(1);
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.site", deviceToken: "tok_push_verify_001");

        var result = await sync.PushPendingBatchesAsync();
        Assert.True(result.Success);
        Assert.Equal(1, result.PushedCount);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.True(captured.Headers.Contains("X-Device-Token"));
        Assert.Equal("tok_push_verify_001", captured.Headers.GetValues("X-Device-Token").Single());
    }

    [Fact]
    public async Task HeaderAttachment_PushPendingBatchesAsync_NoHeaderWhenNoToken()
    {
        await SeedPendingOutboxEventAsync();
        Environment.SetEnvironmentVariable("ENIGHTX_DEVICE_TOKEN", null);

        HttpRequestMessage? captured = null;
        var handler = MockHttpMessageHandler.FromSync(req =>
        {
            captured = req;
            return CreateOkPushResponse(1);
        });

        using var http = new HttpClient(handler);
        var sync = new SyncService(_db, _catalog, http, "https://mockapi.site", deviceToken: null);

        var result = await sync.PushPendingBatchesAsync();
        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.False(captured.Headers.Contains("X-Device-Token"));
    }

    [Fact]
    public void BackwardCompatibility_ExistingConstructorCallersRemainValid()
    {
        // 1. (db)
        var s1 = new SyncService(_db);
        Assert.NotNull(s1);

        // 2. (db, catalog)
        var s2 = new SyncService(_db, _catalog);
        Assert.NotNull(s2);

        // 3. (db, catalog, http)
        using var client = new HttpClient();
        var s3 = new SyncService(_db, _catalog, client);
        Assert.NotNull(s3);

        // 4. (db, catalog, http, apiBaseUrl)
        var s4 = new SyncService(_db, _catalog, client, "https://custom.site");
        Assert.NotNull(s4);

        // 5. (db, catalog, http, apiBaseUrl, deviceToken)
        var s5 = new SyncService(_db, _catalog, client, "https://custom.site", "tok_explicit");
        Assert.NotNull(s5);
    }
}
