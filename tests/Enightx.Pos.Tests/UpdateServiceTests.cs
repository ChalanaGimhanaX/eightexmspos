using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Enightx.Pos.Services;
using Xunit;

namespace Enightx.Pos.Tests;

public class UpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enightx-update-test-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(3072);
    private string Installed => Path.Combine(_root, "installed");
    private string Cache => Path.Combine(_root, "cache");
    private static readonly byte[] Exe = Encoding.UTF8.GetBytes("target executable");
    private static readonly byte[] Dll = Encoding.UTF8.GetBytes("target assembly");
    public UpdateServiceTests() { Directory.CreateDirectory(Installed); }
    private static ReleaseFile FileEntry(string name, byte[] data) => new() { Path = name, Size = data.Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(data)), Url = "https://mock/" + name };
    private static UpdateManifest Manifest() => new() { ProtocolVersion = 2, ReleaseId = "1.0.4", Version = "1.0.4",
        SupportedFrom = ["1.0.1"], Files = [FileEntry("Enightx.Pos.Wpf.exe", Exe), FileEntry("Enightx.Pos.Wpf.dll", Dll)] };
    private byte[] Envelope(UpdateManifest manifest)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateService.JsonOptions);
        return JsonSerializer.SerializeToUtf8Bytes(new { payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) });
    }
    private UpdateService Service(Handler handler) => new(new HttpClient(handler), "https://mock/version-v2.json", "1.0.1", Installed, Cache, _key.ExportSubjectPublicKeyInfoPem());
    private Handler Transport(UpdateManifest? manifest = null) => new(Envelope(manifest ?? Manifest()));

    [Theory]
    [InlineData("1.0.10", "1.0.9", true)]
    [InlineData("1.0.1.0", "1.0.1", false)]
    [InlineData("v1.0.2", "1.0.1", true)]
    [InlineData("broken", "1.0.1", false)]
    [InlineData("1.0.1", "1.0.4", false)]
    public void VersionsAreNumericAndNormalized(string next, string current, bool expected) => Assert.Equal(expected, UpdateService.IsVersionNewer(next, current));

    [Fact]
    public async Task RepeatedAndConcurrentHintsNotifyOnceWithoutDownloading()
    {
        var handler = Transport(); var service = Service(handler); int notifications = 0;
        service.LiveUpdateReceived += _ => Interlocked.Increment(ref notifications);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.NotifyFromServerAsync()));
        Assert.Equal(1, notifications); Assert.Equal(0, handler.FileRequests);
    }
    [Fact]
    public async Task AToDReusesUnchangedFilesAndConcurrentPreparationDownloadsOnlyOnce()
    {
        await File.WriteAllBytesAsync(Path.Combine(Installed, "Enightx.Pos.Wpf.exe"), Exe);
        var handler = Transport(); var service = Service(handler);
        var check = await service.CheckForUpdatesAsync(); Assert.True(check.UpdateAvailable);
        var results = await Task.WhenAll(service.PrepareUpdateAsync(check.Manifest!), service.PrepareUpdateAsync(check.Manifest!));
        Assert.Equal(results[0], results[1]); Assert.Equal(1, handler.FileRequests);
        Assert.Equal(Dll, await File.ReadAllBytesAsync(Path.Combine(results[0], "Enightx.Pos.Wpf.dll")));
        // Persisted stage/cache survives a new application process/service instance.
        var restarted = Service(handler); var again = await restarted.CheckForUpdatesAsync();
        await restarted.PrepareUpdateAsync(again.Manifest!);
        Assert.Equal(1, handler.FileRequests);
    }
    [Fact]
    public async Task ResumePartialFileUsesRangeAndRetainsWholeRelease()
    {
        var manifest = Manifest(); var handler = Transport(manifest); var service = Service(handler);
        Directory.CreateDirectory(Path.Combine(Cache, "objects"));
        await File.WriteAllBytesAsync(Path.Combine(Cache, "objects", manifest.Files[0].Sha256.ToLowerInvariant() + ".part"), Exe[..5]);
        var check = await service.CheckForUpdatesAsync();
        var stage = await service.PrepareUpdateAsync(check.Manifest!);
        Assert.Contains(5L, handler.Offsets); Assert.Equal(Exe, await File.ReadAllBytesAsync(Path.Combine(stage, "Enightx.Pos.Wpf.exe")));
    }
    [Fact]
    public async Task ServerIgnoringRangeRestartsOnlyThatFile()
    {
        var manifest = Manifest(); var handler = Transport(manifest); handler.IgnoreRange = true;
        Directory.CreateDirectory(Path.Combine(Cache, "objects"));
        await File.WriteAllBytesAsync(Path.Combine(Cache, "objects", manifest.Files[0].Sha256.ToLowerInvariant() + ".part"), Exe[..5]);
        var service = Service(handler); var check = await service.CheckForUpdatesAsync();
        var stage = await service.PrepareUpdateAsync(check.Manifest!);
        Assert.Equal(Exe, await File.ReadAllBytesAsync(Path.Combine(stage, "Enightx.Pos.Wpf.exe")));
    }
    [Fact]
    public async Task CorruptPayloadDoesNotStageOrRetryAutomatically()
    {
        var handler = Transport(); handler.Corrupt = true;
        var service = Service(handler); var check = await service.CheckForUpdatesAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareUpdateAsync(check.Manifest!));
        Assert.Equal(1, handler.FileRequests);
        Assert.False(File.Exists(Path.Combine(Cache, "staged", "1.0.4", "release.json")));
    }
    [Fact]
    public async Task NewDependencyIsIncludedEvenWhenNameDoesNotMatchApplication()
    {
        var manifest = Manifest(); manifest.Files.Add(FileEntry("ThirdParty.dll", Dll));
        var handler = Transport(manifest); var service = Service(handler); var check = await service.CheckForUpdatesAsync();
        var stage = await service.PrepareUpdateAsync(check.Manifest!);
        Assert.True(File.Exists(Path.Combine(stage, "ThirdParty.dll")));
    }
    [Fact]
    public async Task UnsupportedJumpAndSchemaChangeAreBlocked()
    {
        var manifest = Manifest(); manifest.SupportedFrom = ["1.0.3"];
        Assert.False((await Service(Transport(manifest)).CheckForUpdatesAsync()).UpdateAvailable);
        manifest.SupportedFrom = ["1.0.1"]; manifest.DatabaseSchema = 2;
        Assert.False((await Service(Transport(manifest)).CheckForUpdatesAsync()).UpdateAvailable);
    }
    [Fact]
    public async Task WrongSigningKeyAndMutatedManifestAreBlocked()
    {
        var handler = Transport(); using var wrongKey = RSA.Create(3072);
        var wrong = new UpdateService(new HttpClient(handler), "https://mock/version-v2.json", "1.0.1", Installed, Cache, wrongKey.ExportSubjectPublicKeyInfoPem());
        Assert.False((await wrong.CheckForUpdatesAsync()).UpdateAvailable);
        var service = Service(handler); var check = await service.CheckForUpdatesAsync();
        check.Manifest!.Files[0].Url = "https://mock/injected";
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareUpdateAsync(check.Manifest));
    }
    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/absolute.dll")]
    [InlineData("C:/absolute.dll")]
    [InlineData("aux.dll")]
    [InlineData("dir/../file.dll")]
    [InlineData("data.db")]
    public void RejectUnsafePaths(string path) => Assert.Throws<InvalidDataException>(() => UpdateService.SafePath(Installed, path));

    private sealed class Handler(byte[] envelope) : HttpMessageHandler
    {
        public int FileRequests;
        public bool Corrupt, IgnoreRange;
        public List<long> Offsets = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("version-v2.json")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(envelope) });
            Interlocked.Increment(ref FileRequests);
            byte[] data = request.RequestUri.AbsolutePath.EndsWith(".exe") ? Exe : Dll;
            long offset = request.Headers.Range?.Ranges.First().From ?? 0; Offsets.Add(offset);
            var content = new ByteArrayContent(Corrupt ? new byte[data.Length] : IgnoreRange ? data : data[(int)offset..]);
            var status = offset > 0 && !IgnoreRange ? HttpStatusCode.PartialContent : HttpStatusCode.OK;
            if (status == HttpStatusCode.PartialContent) content.Headers.ContentRange = new ContentRangeHeaderValue(offset, data.Length - 1, data.Length);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }
    public void Dispose() { _key.Dispose(); Directory.Delete(_root, true); }
}
