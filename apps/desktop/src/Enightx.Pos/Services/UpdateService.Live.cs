using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace Enightx.Pos.Services;

public partial class UpdateService
{
    public void ApplyUpdateAndRestart(string stagedDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Install on Windows only.");
        if (_prepared != stagedDirectory) throw new InvalidOperationException("Prepare and verify release first.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(Path.Combine(stagedDirectory, "release.json")), JsonOptions)!;
        Validate(manifest);
        lock (_verifiedManifests)
        {
            if (!_verifiedManifests.Contains(JsonSerializer.Serialize(manifest, JsonOptions))) throw new InvalidDataException("Staged manifest has changed.");
        }
        // Copy the current trusted installer outside the application before replacing its files.
        var installer = Path.Combine(_cache, "ApplyUpdate.ps1");
        File.Copy(Path.Combine(_baseDirectory, "ApplyUpdate.ps1"), installer, true);
        var config = Path.Combine(_cache, "install.json");
        var ready = Path.Combine(_cache, "installer-ready-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(config, JsonSerializer.Serialize(new { pid = Environment.ProcessId, target = _baseDirectory, stage = stagedDirectory,
            backup = Path.Combine(_cache, "backup", Guid.NewGuid().ToString("N")), result = Path.Combine(_cache, "install-result.json"), ready }, JsonOptions));
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-File", installer, "-ConfigPath", config }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start installer.");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(ready) && !process.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(100);
        if (!File.Exists(ready))
        {
            if (!process.HasExited) process.Kill();
            throw new InvalidOperationException("Installer could not initialize. Check Windows script policy and installation permissions.");
        }
        File.Delete(ready);
    }
    public void StartListeningForLiveUpdates(string? wsUrl = null, CancellationToken ct = default)
    {
        lock (_listenerLock)
        {
            if (_listener != null) return;
            _listener = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var uri = wsUrl == null ? new UriBuilder(_manifestUri) { Scheme = "wss", Path = "/ws/updates", Query = "" }.Uri : new Uri(wsUrl);
            _ = ListenAsync(uri, _listener.Token);
        }
    }
    public void StopListeningForLiveUpdates() { lock (_listenerLock) { _listener?.Cancel(); _listener = null; } }
    private async Task ListenAsync(Uri uri, CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                // HTTP check still works when a proxy blocks WebSockets.
                await NotifyFromServerAsync(ct);
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(uri, connectTimeout.Token);
                byte[] buffer = new byte[8192];
                using var message = new MemoryStream();
                while (socket.State == WebSocketState.Open)
                {
                    // Never abandon a pending receive and issue another on the same socket.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(75));
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Expected text message.");
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 65536) throw new InvalidDataException("Oversized notification.");
                    if (!result.EndOfMessage) continue;
                    using var doc = JsonDocument.Parse(message.ToArray());
                    message.SetLength(0);
                    failures = 0;
                    if (doc.RootElement.TryGetProperty("event", out var ev) && ev.GetString() == "update_available") await NotifyFromServerAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Debug.WriteLine($"Update notifications unavailable: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(failures++, 5)) + Random.Shared.NextDouble()), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
    public async Task NotifyFromServerAsync(CancellationToken ct = default)
    {
        var check = await CheckForUpdatesAsync(ct);
        if (!check.UpdateAvailable || check.Manifest == null) return;
        lock (_notified) { if (!_notified.Add(check.Manifest.ReleaseId)) return; }
        LiveUpdateReceived?.Invoke(check.Manifest);
    }
}
