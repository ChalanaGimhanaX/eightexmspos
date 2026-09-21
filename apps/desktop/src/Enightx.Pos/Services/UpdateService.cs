using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

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
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default);
    Task<string> DownloadUpdateAsync(string downloadUrl, IProgress<double>? progress = null, CancellationToken ct = default);
    void ApplyUpdateAndRestart(string downloadedFilePath);
}

public class UpdateService : IUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly string _manifestUrl;
    private readonly string _currentVersion;

    public string CurrentVersion => _currentVersion;

    public UpdateService(
        HttpClient? httpClient = null,
        string manifestUrl = "https://posapi.eightexms.site/downloads/version.json",
        string? currentVersion = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _manifestUrl = manifestUrl;
        
        if (!string.IsNullOrEmpty(currentVersion))
        {
            _currentVersion = currentVersion;
        }
        else
        {
            var asm = typeof(UpdateService).Assembly;
            var ver = asm.GetName().Version;
            _currentVersion = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
        }
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
        var targetFile = Path.Combine(tempDir, "Enightx.Pos.Wpf.new.exe");

        if (File.Exists(targetFile))
        {
            try { File.Delete(targetFile); } catch { }
        }

        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

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

        progress?.Report(1.0);
        return targetFile;
    }

    public void ApplyUpdateAndRestart(string downloadedFilePath)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe))
        {
            using var proc = Process.GetCurrentProcess();
            currentExe = proc.MainModule?.FileName;
        }

        if (string.IsNullOrEmpty(currentExe) || !File.Exists(downloadedFilePath))
        {
            throw new InvalidOperationException("Could not resolve current executable path or downloaded update file.");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pid = Process.GetCurrentProcess().Id;
            var tempDir = Path.GetDirectoryName(downloadedFilePath) ?? Path.GetTempPath();
            var batPath = Path.Combine(tempDir, "apply_update.bat");

            var script = $@"@echo off
setlocal
set PID={pid}
set TARGET=""{currentExe}""
set SOURCE=""{downloadedFilePath}""

:wait_loop
tasklist /fi ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul
if %ERRORLEVEL% == 0 (
    timeout /t 1 /nobreak >nul
    goto wait_loop
)

timeout /t 1 /nobreak >nul

copy /y %SOURCE% %TARGET% >nul
if %ERRORLEVEL% neq 0 (
    move /y %SOURCE% %TARGET% >nul
)

if exist %SOURCE% del /f /q %SOURCE% >nul

start """" %TARGET%

(goto) 2>nul & del ""%~f0""
";

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
                File.Copy(downloadedFilePath, currentExe, true);
                Process.Start(currentExe);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] Non-Windows restart simulated: {ex.Message}");
            }
        }
    }
}
