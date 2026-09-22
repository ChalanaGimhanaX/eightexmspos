using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Enightx.Pos.Services;

public sealed class ReleaseFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Url { get; set; } = "";
}
public class UpdateManifest
{
    public int ProtocolVersion { get; set; }
    public string Version { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string[] SupportedFrom { get; set; } = [];
    public int DatabaseSchema { get; set; } = 1;
    public List<ReleaseFile> Files { get; set; } = [];
}
public class UpdateCheckResult
{
    public bool UpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = "";
    public UpdateManifest? Manifest { get; set; }
    public string? Error { get; set; }
}
public interface IUpdateService
{
    string CurrentVersion { get; }
    event Action<UpdateManifest>? LiveUpdateReceived;
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default);
    Task<string> PrepareUpdateAsync(UpdateManifest manifest, IProgress<double>? progress = null, CancellationToken ct = default);
    void ApplyUpdateAndRestart(string stagedDirectory);
    void StartListeningForLiveUpdates(string? wsUrl = null, CancellationToken ct = default);
    void StopListeningForLiveUpdates();
}
public partial class UpdateService : IUpdateService
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly Uri _manifestUri;
    private readonly string _baseDirectory;
    private readonly string _cache;
    private readonly string? _trustedPublicKey;
    private readonly HashSet<string> _verifiedManifests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _checkGate = new(1, 1), _prepareGate = new(1, 1);
    private CancellationTokenSource? _listener;
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    private readonly object _listenerLock = new();
    private string? _prepared;
    public string CurrentVersion { get; }
    public event Action<UpdateManifest>? LiveUpdateReceived;

    public const string EmbeddedReleasePublicKey = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA1z1WbDxF/89XZVqojy8W
/RyUXSfSfeHSqpq47Zo6bt0nI7JTRDXI8uquJ3xi+S/hx+k741FjJQ3PTYoTOgOv
aYOh1r/JZIPQ8UIJDYC/qb6Ev5kXl0IvsPlvFYgNmDuEWsSxdJf7x74L0PjMh03y
s27xuUp8JnxuccOQi1yu1XIMtC2f2qMHPyKLzfsKD3KxmYlnLOiusxsBkFaqzvMG
4K3eG2eETEjrHlbzjj1EF6M1v0YSOxefChNp656HWfZ9Yl5ojTYdW2p88Wnqde+O
63HqUzbddCrzZEBmAs+U5a8LyBLxJ5Fq+D9YJ9eQMplzujpO7m5//Yr9XGQruF7z
0ZskiWauV3xTckgUCxQaZPXaekaGni7X4p+wGfuJR+EwIXGWTUOjAaXEEAVtIBq3
8dXDZTaDjkejk5nWlgIZUfVhk8TRT4rOJVMZY6u0Rt8pwvyqN2uDFZ/VYSNlg8HR
NmYaZpk7c8tLh7fAh3BxeCQj+2Ypn2hVfON0KWLK1BiRAgMBAAE=
-----END PUBLIC KEY-----
""";

    public UpdateService(HttpClient? httpClient = null,
        string manifestUrl = "https://posapi.eightexms.site/downloads/version-v2.json",
        string? currentVersion = null, string? baseDirectory = null, string? cacheDirectory = null, string? trustedPublicKey = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _manifestUri = new Uri(manifestUrl);
        _baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var keyPath = Path.Combine(_baseDirectory, "release-public-key.pem");
        _trustedPublicKey = trustedPublicKey ?? (File.Exists(keyPath) ? File.ReadAllText(keyPath) : EmbeddedReleasePublicKey);
        _cache = Path.GetFullPath(cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnightxPOS", "updates-v2"));
        CurrentVersion = currentVersion ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }
    public static Version ParseVersion(string value)
    {
        if (!Version.TryParse(value.Trim().TrimStart('v', 'V'), out var v)) throw new InvalidDataException("Invalid release version.");
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }
    public static bool IsVersionNewer(string server, string current)
    {
        try { return ParseVersion(server) > ParseVersion(current); }
        catch (InvalidDataException) { return false; }
    }
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        await _checkGate.WaitAsync(ct);
        try
        {
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var req = new HttpRequestMessage(HttpMethod.Get, _manifestUri);
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await _http.SendAsync(req, checkTimeout.Token);
            response.EnsureSuccessStatusCode();
            using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(checkTimeout.Token));
            var payload = Convert.FromBase64String(envelope.RootElement.GetProperty("payload").GetString()!);
            var signature = Convert.FromBase64String(envelope.RootElement.GetProperty("signature").GetString()!);
            if (_trustedPublicKey == null) throw new InvalidDataException("Release trust key not provisioned. Contact the installer administrator.");
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_trustedPublicKey);
            if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new InvalidDataException("Release signature verification failed.");
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(payload, JsonOptions) ?? throw new InvalidDataException("Empty release manifest.");
            Validate(manifest);
            lock (_verifiedManifests) { _verifiedManifests.Add(JsonSerializer.Serialize(manifest, JsonOptions)); }
            return new() { CurrentVersion = CurrentVersion, Manifest = manifest, UpdateAvailable = IsVersionNewer(manifest.Version, CurrentVersion) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new() { CurrentVersion = CurrentVersion, Error = ex.Message }; }
        finally { _checkGate.Release(); }
    }
    public void Validate(UpdateManifest manifest)
    {
        ParseVersion(manifest.Version);
        if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Version, @"^\d+\.\d+\.\d+$")) throw new InvalidDataException("Release version must be major.minor.patch.");
        if (manifest.ProtocolVersion != 2 || manifest.ReleaseId != manifest.Version || manifest.DatabaseSchema != 1)
            throw new InvalidDataException("Unsupported protocol, identity or schema; manual migration required.");
        if (IsVersionNewer(manifest.Version, CurrentVersion) && !manifest.SupportedFrom.Any(v => ParseVersion(v) == ParseVersion(CurrentVersion)))
            throw new InvalidDataException($"Direct upgrade from {CurrentVersion} is not certified for this release.");
        if (manifest.Files.Count == 0 || manifest.Files.Count > 10000) throw new InvalidDataException("Invalid file list.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            SafePath(_baseDirectory, file.Path);
            if (file.Path.Equals("release-public-key.pem", StringComparison.OrdinalIgnoreCase) || file.Path.Equals("release.json", StringComparison.OrdinalIgnoreCase) || file.Path.StartsWith('.'))
                throw new InvalidDataException("Reserved installation path.");
            if (!paths.Add(file.Path) || file.Size < 0 || file.Size > 2L * 1024 * 1024 * 1024 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid or duplicate release file.");
            var uri = new Uri(file.Url, UriKind.Absolute);
            if (uri.Scheme != "https" || uri.Authority != _manifestUri.Authority || uri.UserInfo.Length != 0)
                throw new InvalidDataException("Release files must use the configured HTTPS host.");
        }
        if (!paths.Contains("Enightx.Pos.Wpf.exe") || !paths.Contains("Enightx.Pos.Wpf.dll")) throw new InvalidDataException("Complete folder installation required.");
    }
    public static string SafePath(string root, string relative)
    {
        var parts = relative.Split('/');
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
            p.Any(c => c < 32 || ":*?\"<>|".Contains(c)) || System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) ||
            relative.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe release path.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes directory.");
        return full;
    }
    private static async Task<bool> MatchesAsync(string path, ReleaseFile file, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    public async Task<string> PrepareUpdateAsync(UpdateManifest manifest, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Validate(manifest);
        lock (_verifiedManifests)
        {
            if (!_verifiedManifests.Contains(JsonSerializer.Serialize(manifest, JsonOptions))) throw new InvalidDataException("Fetch and verify the signed release before preparing it.");
        }
        if (!IsVersionNewer(manifest.Version, CurrentVersion)) throw new InvalidOperationException("Release already installed or older.");
        await _prepareGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_cache);
            await using var lease = new FileStream(Path.Combine(_cache, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var stage = Path.Combine(_cache, "staged", manifest.Version);
            Directory.CreateDirectory(stage);
            long total = manifest.Files.Sum(f => f.Size), completed = 0;
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var destination = SafePath(stage, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!await MatchesAsync(destination, file, ct))
                {
                    var installed = SafePath(_baseDirectory, file.Path);
                    if (await MatchesAsync(installed, file, ct)) File.Copy(installed, destination, true);
                    else File.Copy(await DownloadFileAsync(file, ct), destination, true);
                }
                completed += file.Size;
                progress?.Report(total == 0 ? 1 : (double)completed / total);
            }
            await File.WriteAllTextAsync(Path.Combine(stage, "release.json"), JsonSerializer.Serialize(manifest, JsonOptions), ct);
            _prepared = stage;
            return stage;
        }
        finally { _prepareGate.Release(); }
    }
    private async Task<string> DownloadFileAsync(ReleaseFile file, CancellationToken ct)
    {
        var objects = Path.Combine(_cache, "objects");
        Directory.CreateDirectory(objects);
        var target = Path.Combine(objects, file.Sha256.ToLowerInvariant());
        if (await MatchesAsync(target, file, ct)) return target;
        var part = target + ".part";
        long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset == file.Size && await MatchesAsync(part, file, ct)) { File.Move(part, target, true); return target; }
        if (offset >= file.Size) { File.Delete(part); offset = 0; }
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        bool append = response.StatusCode == HttpStatusCode.PartialContent;
        if (append && (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != file.Size)) throw new InvalidDataException("Invalid resumed download range.");
        if (!append) offset = 0;
        await using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        {
            byte[] buffer = new byte[81920];
            while (true)
            {
                using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                int count = await input.ReadAsync(buffer, idleTimeout.Token);
                if (count == 0) break;
                offset += count;
                if (offset > file.Size) throw new InvalidDataException("Download exceeds declared size.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
        }
        if (!await MatchesAsync(part, file, ct)) { File.Delete(part); throw new InvalidDataException("Checksum mismatch; retry explicitly after checking release."); }
        File.Move(part, target, true);
        return target;
    }
}
