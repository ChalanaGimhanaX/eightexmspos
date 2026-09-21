using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enightx.Pos.Services;

public class UpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("releaseNotes")]
    public string ReleaseNotes { get; set; } = "";

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("updateZipUrl")]
    public string? UpdateZipUrl { get; set; }

    [JsonPropertyName("setupZipUrl")]
    public string? SetupZipUrl { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("zipSha256")]
    public string? ZipSha256 { get; set; }

    [JsonPropertyName("zipSizeBytes")]
    public long? ZipSizeBytes { get; set; }

    [JsonPropertyName("publishedAtUtc")]
    public DateTime? PublishedAtUtc { get; set; }

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; }
}

public class UpdateCheckResult
{
    public bool UpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = "";
    public UpdateManifest? Manifest { get; set; }
}

public interface IUpdateService
{
    string CurrentVersion { get; }
    event Action<UpdateManifest>? LiveUpdateReceived;
    bool IsModularInstallation();
    string GetBestDownloadUrl(UpdateManifest manifest);
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default);
    Task<string> DownloadUpdateAsync(string downloadUrl, IProgress<double>? progress = null, CancellationToken ct = default);
    void ApplyUpdateAndRestart(string downloadedPath);
    void StartListeningForLiveUpdates(string? wsUrl = null, CancellationToken ct = default);
}

public class UpdateService : IUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly string _manifestUrl;
    private readonly string _currentVersion;
    private readonly string? _baseDirectory;

    public string CurrentVersion => _currentVersion;

    public UpdateService(
        HttpClient? httpClient = null,
        string manifestUrl = "https://posapi.eightexms.site/downloads/version.json",
        string? currentVersion = null,
        string? baseDirectory = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _manifestUrl = manifestUrl;
        _baseDirectory = baseDirectory;

        // Priority 1 – version stamp file written by the modular update zip.
        // This file (enightx_version.txt) is included in every update zip so that
        // xcopy/robocopy places it in the app directory after the update is applied.
        // Reading from here is the only reliable way to know the current version when
        // the original WPF exe (Enightx.Pos.Wpf.exe) is never replaced by a modular update.
        var appDir = baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        var stampFile = Path.Combine(appDir, "enightx_version.txt");
        if (File.Exists(stampFile))
        {
            var stamped = File.ReadAllText(stampFile).Trim();
            if (!string.IsNullOrWhiteSpace(stamped))
            {
                _currentVersion = stamped;
                return;
            }
        }

        // Priority 2 – explicit override (used by unit tests and in App.xaml.cs for
        // installs that have never received a modular update, so the stamp file doesn't
        // exist yet and the passed-in assembly version is the best we have).
        if (!string.IsNullOrEmpty(currentVersion))
        {
            _currentVersion = currentVersion;
            return;
        }

        // Priority 3 – core assembly version (Enightx.Pos.dll). Correct after any
        // modular update that side-loads the updated DLL next to the exe.
        var asm = typeof(UpdateService).Assembly;
        var ver = asm.GetName().Version;
        _currentVersion = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
    }

    public virtual bool IsModularInstallation()
    {
        var appDir = _baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        return File.Exists(Path.Combine(appDir, "coreclr.dll")) || File.Exists(Path.Combine(appDir, "hostfxr.dll"));
    }

    public string GetBestDownloadUrl(UpdateManifest manifest)
    {
        // Always prefer the lightweight modular zip (~1.2 MB) if available.
        // Works for both modular and standalone installations — the bat script
        // copies DLLs into the app directory in both cases.
        if (!string.IsNullOrWhiteSpace(manifest.UpdateZipUrl))
        {
            return manifest.UpdateZipUrl;
        }
        if (!string.IsNullOrWhiteSpace(manifest.SetupZipUrl))
        {
            return manifest.SetupZipUrl;
        }
        return manifest.DownloadUrl;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _manifestUrl);
            // Bypass any intermediate proxy caching
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult { UpdateAvailable = false, CurrentVersion = _currentVersion };
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var manifest = await resp.Content.ReadFromJsonAsync<UpdateManifest>(options, ct);

            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                return new UpdateCheckResult { UpdateAvailable = false, CurrentVersion = _currentVersion };
            }

            bool isNewer = IsVersionNewer(manifest.Version, _currentVersion);
            return new UpdateCheckResult
            {
                UpdateAvailable = isNewer,
                CurrentVersion = _currentVersion,
                Manifest = manifest
            };
        }
        catch (Exception ex)
        {
            // Offline resilience: fail quietly so normal POS checkout is never interrupted
            Debug.WriteLine($"[UpdateService] Check failed (offline or unreachable): {ex.Message}");
            return new UpdateCheckResult { UpdateAvailable = false, CurrentVersion = _currentVersion };
        }
    }

    public static bool IsVersionNewer(string serverVer, string currentVer)
    {
        var cleanServer = serverVer.Trim().TrimStart('v', 'V');
        var cleanCurrent = currentVer.Trim().TrimStart('v', 'V');

        if (Version.TryParse(cleanServer, out var sv) && Version.TryParse(cleanCurrent, out var cv))
        {
            return sv > cv;
        }

        return string.Compare(cleanServer, cleanCurrent, StringComparison.OrdinalIgnoreCase) > 0;
    }

    public async Task<string> DownloadUpdateAsync(string downloadUrl, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "EnightxPosUpdate");
        Directory.CreateDirectory(tempDir);
        
        bool isZip = downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var targetFile = Path.Combine(tempDir, isZip ? "update.zip" : "Enightx.Pos.Wpf.new.exe");

        if (File.Exists(targetFile))
        {
            try { File.Delete(targetFile); } catch { }
        }

        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using (var fileStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    var pct = (double)totalRead / totalBytes;
                    progress.Report(pct);
                }
            }
        }

        progress?.Report(1.0);

        if (isZip)
        {
            var extractedDir = Path.Combine(tempDir, "extracted");
            if (Directory.Exists(extractedDir))
            {
                try { Directory.Delete(extractedDir, true); } catch { }
            }
            Directory.CreateDirectory(extractedDir);
            ZipFile.ExtractToDirectory(targetFile, extractedDir, overwriteFiles: true);
            return extractedDir;
        }

        return targetFile;
    }

    public void ApplyUpdateAndRestart(string downloadedPath)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe))
        {
            using var proc = Process.GetCurrentProcess();
            currentExe = proc.MainModule?.FileName;
        }

        bool isDirectory = Directory.Exists(downloadedPath);
        bool isFile = File.Exists(downloadedPath);

        if (string.IsNullOrEmpty(currentExe) || (!isDirectory && !isFile))
        {
            throw new InvalidOperationException("Could not resolve current executable path or downloaded update package.");
        }

        var currentDir = Path.GetDirectoryName(currentExe) ?? AppDomain.CurrentDomain.BaseDirectory;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pid = Process.GetCurrentProcess().Id;
            var tempDir = Path.Combine(Path.GetTempPath(), "EnightxPosUpdate");
            var batPath = Path.Combine(tempDir, "apply_update.bat");

            string script;
            if (isDirectory)
            {
                script = $@"@echo off
setlocal
set PID={pid}
set TARGET_DIR=""{currentDir}""
set TARGET_EXE=""{currentExe}""
set EXTRACTED=""{downloadedPath}""

:wait_loop
tasklist /fi ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul
if %ERRORLEVEL% == 0 (
    timeout /t 1 /nobreak >nul
    goto wait_loop
)

echo Applying modular update files...
xcopy /y /e /q %EXTRACTED%\* %TARGET_DIR%\ >nul
if %ERRORLEVEL% neq 0 (
    robocopy %EXTRACTED% %TARGET_DIR% /E /IS /IT /NP >nul
)

rd /s /q %EXTRACTED% 2>nul

start """" %TARGET_EXE%

(goto) 2>nul & del ""%~f0""
";
            }
            else
            {
                script = $@"@echo off
setlocal
set PID={pid}
set TARGET=""{currentExe}""
set SOURCE=""{downloadedPath}""

:wait_loop
tasklist /fi ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul
if %ERRORLEVEL% == 0 (
    timeout /t 1 /nobreak >nul
    goto wait_loop
)

copy /y %SOURCE% %TARGET% >nul
if %ERRORLEVEL% neq 0 (
    move /y %SOURCE% %TARGET% >nul
)

if exist %SOURCE% del /f /q %SOURCE% >nul

start """" %TARGET%

(goto) 2>nul & del ""%~f0""
";
            }

            File.WriteAllText(batPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);
            Environment.Exit(0);
        }
        else
        {
            // Non-Windows fallback (Linux/macOS development)
            try
            {
                if (isDirectory)
                {
                    foreach (var file in Directory.GetFiles(downloadedPath, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(downloadedPath, file);
                        var dest = Path.Combine(currentDir, rel);
                        var destDir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                        File.Copy(file, dest, true);
                    }
                }
                else
                {
                    File.Copy(downloadedPath, currentExe, true);
                }
                Process.Start(currentExe);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] Non-Windows restart simulated: {ex.Message}");
            }
        }
    }

    public event Action<UpdateManifest>? LiveUpdateReceived;
    private CancellationTokenSource? _wsCts;

    public void StartListeningForLiveUpdates(string? wsUrl = null, CancellationToken ct = default)
    {
        var targetWsUrl = wsUrl ?? "wss://posapi.eightexms.site/ws/updates";
        _wsCts?.Cancel();
        _wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _wsCts.Token;

        Task.Run(async () =>
        {
            var buffer = new byte[8192];
            while (!token.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                try
                {
                    Debug.WriteLine($"[UpdateService] Connecting to WebSocket: {targetWsUrl}");
                    await ws.ConnectAsync(new Uri(targetWsUrl), token);
                    Debug.WriteLine("[UpdateService] WebSocket connected. Listening for real-time updates...");

                    while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        var segment = new ArraySegment<byte>(buffer);
                        var receiveTask = ws.ReceiveAsync(segment, token);
                        var completed = await Task.WhenAny(receiveTask, Task.Delay(25000, token));

                        if (completed == receiveTask)
                        {
                            var result = await receiveTask;
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", token);
                                break;
                            }

                            if (result.MessageType == WebSocketMessageType.Text)
                            {
                                var jsonText = Encoding.UTF8.GetString(buffer, 0, result.Count);
                                ProcessWebSocketMessage(jsonText);
                            }
                        }
                        else
                        {
                            if (ws.State == WebSocketState.Open)
                            {
                                var pingBytes = Encoding.UTF8.GetBytes("ping");
                                await ws.SendAsync(new ArraySegment<byte>(pingBytes), WebSocketMessageType.Text, true, token);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UpdateService] WebSocket disconnect/error: {ex.Message}. Reconnecting in 15s...");
                }

                try
                {
                    await Task.Delay(15000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    public void ProcessWebSocketMessage(string jsonText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            if (root.TryGetProperty("event", out var evProp) && evProp.GetString() == "update_available")
            {
                if (root.TryGetProperty("manifest", out var manifestProp))
                {
                    var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestProp.GetRawText(), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (manifest != null && IsVersionNewer(manifest.Version, _currentVersion))
                    {
                        Debug.WriteLine($"[UpdateService] Real-time update event received: v{manifest.Version}");
                        LiveUpdateReceived?.Invoke(manifest);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] Error parsing WebSocket message: {ex.Message}");
        }
    }
}

