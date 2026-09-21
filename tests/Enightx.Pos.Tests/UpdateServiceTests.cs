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
