using System.Net;
using System.Text.Json;
using Xunit;
using Enightx.Pos.Services;

namespace Enightx.Pos.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("v1.0.2", "1.0.1", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("0.9.9", "1.0.0", false)]
    public void IsVersionNewer_CalculatesCorrectly(string serverVer, string currentVer, bool expected)
    {
        var result = UpdateService.IsVersionNewer(serverVer, currentVer);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task CheckForUpdates_NewerVersionAvailable_ReturnsTrue()
    {
        var manifest = new UpdateManifest
        {
            Version = "1.0.2",
            ReleaseNotes = "Auto update feature added",
            DownloadUrl = "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe",
            Mandatory = false
        };

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, JsonSerializer.Serialize(manifest));
        var client = new HttpClient(handler);
        var service = new UpdateService(client, "https://mock/version.json", "1.0.1");

        var check = await service.CheckForUpdatesAsync();

        Assert.True(check.UpdateAvailable);
        Assert.NotNull(check.Manifest);
        Assert.Equal("1.0.2", check.Manifest.Version);
        Assert.Equal("Auto update feature added", check.Manifest.ReleaseNotes);
    }

    [Fact]
    public async Task CheckForUpdates_SameOrOlderVersion_ReturnsFalse()
    {
        var manifest = new UpdateManifest
        {
            Version = "1.0.0",
            ReleaseNotes = "Old release",
            DownloadUrl = "https://mock/app.exe"
        };

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, JsonSerializer.Serialize(manifest));
        var client = new HttpClient(handler);
        var service = new UpdateService(client, "https://mock/version.json", "1.0.0");

        var check = await service.CheckForUpdatesAsync();

        Assert.False(check.UpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdates_NetworkOffline_FailsSilently()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "");
        var client = new HttpClient(handler);
        var service = new UpdateService(client, "https://mock/version.json", "1.0.0");

        var check = await service.CheckForUpdatesAsync();

        Assert.False(check.UpdateAvailable);
        Assert.Equal("1.0.0", check.CurrentVersion);
    }

    [Fact]
    public void ProcessWebSocketMessage_NewerVersion_FiresLiveUpdateReceived()
    {
        var service = new UpdateService(currentVersion: "1.0.1");
        UpdateManifest? received = null;
        service.LiveUpdateReceived += m => received = m;

        var json = """
        {
            "event": "update_available",
            "manifest": {
                "version": "1.0.2",
                "releaseNotes": "Real-time push works!",
                "downloadUrl": "https://mock/app.exe"
            }
        }
        """;

        service.ProcessWebSocketMessage(json);

        Assert.NotNull(received);
        Assert.Equal("1.0.2", received.Version);
        Assert.Equal("Real-time push works!", received.ReleaseNotes);
    }

    [Fact]
    public void ProcessWebSocketMessage_SameOrOlderVersion_DoesNotFire()
    {
        var service = new UpdateService(currentVersion: "1.0.2");
        UpdateManifest? received = null;
        service.LiveUpdateReceived += m => received = m;

        var json = """
        {
            "event": "update_available",
            "manifest": {
                "version": "1.0.1",
                "releaseNotes": "Older version"
            }
        }
        """;

        service.ProcessWebSocketMessage(json);

        Assert.Null(received);
    }

    [Fact]
    public void IsModularInstallation_DetectsBasedOnCoreClr()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test_modular_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var nonModularService = new UpdateService(baseDirectory: tempDir);
            Assert.False(nonModularService.IsModularInstallation());

            File.WriteAllText(Path.Combine(tempDir, "coreclr.dll"), "dummy");
            var modularService = new UpdateService(baseDirectory: tempDir);
            Assert.True(modularService.IsModularInstallation());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetBestDownloadUrl_AlwaysPrefersUpdateZipWhenAvailable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test_best_url_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifest = new UpdateManifest
            {
                Version = "1.0.3",
                DownloadUrl = "https://mock/Enightx.Pos.Wpf.exe",
                UpdateZipUrl = "https://mock/EnightxPos-Update.zip",
                SetupZipUrl = "https://mock/EnightxPos-Setup.zip",
                ZipSizeBytes = 1200000
            };

            // Non-modular install should now also get the fast update zip (~1.2 MB)
            var nonModular = new UpdateService(baseDirectory: tempDir);
            Assert.Equal("https://mock/EnightxPos-Update.zip", nonModular.GetBestDownloadUrl(manifest));

            // Modular install also gets the update zip
            File.WriteAllText(Path.Combine(tempDir, "coreclr.dll"), "dummy");
            var modular = new UpdateService(baseDirectory: tempDir);
            Assert.Equal("https://mock/EnightxPos-Update.zip", modular.GetBestDownloadUrl(manifest));

            // Without UpdateZipUrl, falls back to setup zip
            var manifestNoZip = new UpdateManifest
            {
                Version = "1.0.3",
                DownloadUrl = "https://mock/Enightx.Pos.Wpf.exe",
                SetupZipUrl = "https://mock/EnightxPos-Setup.zip"
            };
            Assert.Equal("https://mock/EnightxPos-Setup.zip", modular.GetBestDownloadUrl(manifestNoZip));

            // Without either zip, falls back to standalone exe
            var manifestExeOnly = new UpdateManifest
            {
                Version = "1.0.3",
                DownloadUrl = "https://mock/Enightx.Pos.Wpf.exe"
            };
            Assert.Equal("https://mock/Enightx.Pos.Wpf.exe", modular.GetBestDownloadUrl(manifestExeOnly));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Deserialize_VpsVersionV2Json_DoesNotThrow()
    {
        var json = """
        {
          "version": "1.0.14",
          "Version": "1.0.14",
          "releaseNotes": "Enightx POS v1.0.14",
          "ReleaseNotes": "Enightx POS v1.0.14",
          "release_notes": "Enightx POS v1.0.14",
          "downloadUrl": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe",
          "DownloadUrl": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe",
          "download_url": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe",
          "updateZipUrl": "https://posapi.eightexms.site/downloads/EnightxPos-Update.zip",
          "UpdateZipUrl": "https://posapi.eightexms.site/downloads/EnightxPos-Update.zip",
          "update_zip_url": "https://posapi.eightexms.site/downloads/EnightxPos-Update.zip",
          "setupZipUrl": "https://posapi.eightexms.site/downloads/EnightxPos-Setup.zip",
          "SetupZipUrl": "https://posapi.eightexms.site/downloads/EnightxPos-Setup.zip",
          "setup_zip_url": "https://posapi.eightexms.site/downloads/EnightxPos-Setup.zip",
          "sha256": "4b88979e861b28f2e3997fc3f9ab5845fd8b3422271627e86dc6132eded0ca1d",
          "Sha256": "4b88979e861b28f2e3997fc3f9ab5845fd8b3422271627e86dc6132eded0ca1d",
          "zipSha256": "4b88979e861b28f2e3997fc3f9ab5845fd8b3422271627e86dc6132eded0ca1d",
          "ZipSha256": "4b88979e861b28f2e3997fc3f9ab5845fd8b3422271627e86dc6132eded0ca1d",
          "zipSizeBytes": 117121,
          "ZipSizeBytes": 117121,
          "publishedAtUtc": "2026-09-22T02:04:24Z",
          "PublishedAtUtc": "2026-09-22T02:04:24Z",
          "mandatory": true,
          "Mandatory": true
        }
        """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, options);
        Assert.NotNull(manifest);
        Assert.Equal("1.0.14", manifest.Version);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _content;

        public MockHttpMessageHandler(HttpStatusCode code, string content)
        {
            _code = code;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_code)
            {
                Content = new StringContent(_content, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}

