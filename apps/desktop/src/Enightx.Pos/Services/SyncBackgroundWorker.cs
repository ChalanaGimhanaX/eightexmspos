using System.Diagnostics;
using Enightx.Pos.Domain;

namespace Enightx.Pos.Services;

public interface ISyncBackgroundWorker : IDisposable
{
    bool IsRunning { get; }
    bool IsOnline { get; }
    DateTime? LastPushUtc { get; }
    DateTime? LastPullUtc { get; }
    TimeSpan Interval { get; set; }

    event Action<SyncPushResult>? PushCompleted;
    event Action<CatalogSyncResult>? CatalogSyncCompleted;
    event Action<bool>? OnlineStatusChanged;

    void Start();
    void Stop();
    Task<SyncPushResult> PushOnceAsync(CancellationToken ct = default);
    Task<CatalogSyncResult> PullOnceAsync(CancellationToken ct = default);
    Task SyncCycleAsync(CancellationToken ct = default);
}

public class SyncBackgroundWorker : ISyncBackgroundWorker
{
    private readonly ISyncService _syncService;
    private readonly Func<Task<bool>>? _connectivityProbe;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private bool _isOnline = true;

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
    public bool IsOnline
    {
        get => _isOnline;
        private set
        {
            if (_isOnline != value)
            {
                _isOnline = value;
                OnlineStatusChanged?.Invoke(_isOnline);
            }
        }
    }

    public DateTime? LastPushUtc { get; private set; }
    public DateTime? LastPullUtc { get; private set; }
    public TimeSpan Interval { get; set; }

    public event Action<SyncPushResult>? PushCompleted;
    public event Action<CatalogSyncResult>? CatalogSyncCompleted;
    public event Action<bool>? OnlineStatusChanged;

    public SyncBackgroundWorker(
        ISyncService syncService,
        TimeSpan? interval = null,
        Func<Task<bool>>? connectivityProbe = null)
    {
        _syncService = syncService;
        Interval = interval ?? TimeSpan.FromSeconds(15);
        _connectivityProbe = connectivityProbe;
    }

    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _workerTask = Task.Run(async () =>
        {
            Debug.WriteLine("[SyncBackgroundWorker] Started.");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await SyncCycleAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Fail silently so POS never crashes on background network sync
                    Debug.WriteLine($"[SyncBackgroundWorker] Error during sync cycle: {ex.Message}");
                }

                try
                {
                    await Task.Delay(Interval, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            Debug.WriteLine("[SyncBackgroundWorker] Stopped.");
        }, token);
    }

    public void Stop()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }

    public async Task<SyncPushResult> PushOnceAsync(CancellationToken ct = default)
    {
        var result = await _syncService.PushPendingBatchesAsync(50, ct);
        if (result.NetworkOffline)
        {
            IsOnline = false;
        }
        else if (result.Success)
        {
            IsOnline = true;
            if (result.PushedCount > 0)
            {
                LastPushUtc = DateTime.UtcNow;
            }
        }

        PushCompleted?.Invoke(result);
        return result;
    }

    public async Task<CatalogSyncResult> PullOnceAsync(CancellationToken ct = default)
    {
        var result = await _syncService.PullCatalogUpdatesAsync(ct);
        if (result.NetworkOffline)
        {
            IsOnline = false;
        }
        else if (result.Success)
        {
            IsOnline = true;
            LastPullUtc = DateTime.UtcNow;
        }

        CatalogSyncCompleted?.Invoke(result);
        return result;
    }

    public async Task SyncCycleAsync(CancellationToken ct = default)
    {
        // 1. If explicit probe is supplied, verify connectivity first
        if (_connectivityProbe != null)
        {
            bool online;
            try
            {
                online = await _connectivityProbe();
            }
            catch
            {
                online = false;
            }

            IsOnline = online;
            if (!online)
            {
                Debug.WriteLine("[SyncBackgroundWorker] Device is offline. Skipping sync cycle.");
                return;
            }
        }

        // 2. Push pending offline batches
        var pushRes = await PushOnceAsync(ct);

        // 3. Pull catalog updates if connected
        if (IsOnline)
        {
            await PullOnceAsync(ct);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
