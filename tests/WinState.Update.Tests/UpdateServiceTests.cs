using System.Net;
using System.Text;
using WinState.Update;
using Xunit;

namespace WinState.Update.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("0.6.0-alpha.1", "0.5.0-alpha.9", 1)]
    [InlineData("0.6.0", "0.6.0-rc.9", 1)]
    [InlineData("0.6.0-alpha.2", "0.6.0-alpha.10", -1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    public void SemanticVersion_compares_prerelease_identifiers(
        string left,
        string right,
        int expectedSign)
    {
        var comparison = SemanticVersion.Parse(left)
            .CompareTo(SemanticVersion.Parse(right));

        Assert.Equal(expectedSign, Math.Sign(comparison));
    }

    [Fact]
    public async Task CheckAsync_selects_newest_prerelease_and_matches_assets()
    {
        const string json = """
            [
              {
                "tag_name": "v0.6.0-alpha.1",
                "name": "WinState 0.6",
                "draft": false,
                "prerelease": true,
                "published_at": "2026-08-01T10:00:00Z",
                "html_url": "https://github.com/Onmaynec/WinState/releases/tag/v0.6.0-alpha.1",
                "assets": [
                  {
                    "name": "WinState-0.6.0-alpha.1-win-x64.zip",
                    "size": 123,
                    "browser_download_url": "https://example.test/winstate.zip"
                  },
                  {
                    "name": "WinState-0.6.0-alpha.1-win-x64.zip.sha256",
                    "size": 64,
                    "browser_download_url": "https://example.test/winstate.zip.sha256"
                  }
                ]
              },
              {
                "tag_name": "v0.5.0-alpha.1",
                "name": "Old",
                "draft": false,
                "prerelease": true,
                "published_at": "2026-07-31T10:00:00Z",
                "html_url": "https://github.com/Onmaynec/WinState/releases/tag/v0.5.0-alpha.1",
                "assets": []
              }
            ]
            """;
        using var client = new HttpClient(new StaticHandler(json));
        using var service = new UpdateService(
            new UpdateSettings
            {
                Repository = "Onmaynec/WinState",
                Channel = UpdateChannel.Prerelease,
                Mode = AutomaticUpdateMode.Check,
                NetworkTimeout = TimeSpan.FromSeconds(2)
            },
            client);

        var result = await service.CheckAsync(
            "0.5.0-alpha.1",
            CancellationToken.None);

        Assert.True(result.IsUpdateAvailable);
        Assert.NotNull(result.Release);
        Assert.Equal("0.6.0-alpha.1", result.Release!.Version.ToString());
        Assert.Equal(2, result.Release.Assets.Count);
    }

    [Fact]
    public async Task CheckAsync_stable_channel_ignores_prerelease()
    {
        const string json = """
            [
              {
                "tag_name": "v2.0.0-alpha.1",
                "name": "Preview",
                "draft": false,
                "prerelease": true,
                "published_at": "2026-08-01T10:00:00Z",
                "html_url": "https://example.test/preview",
                "assets": []
              },
              {
                "tag_name": "v1.1.0",
                "name": "Stable",
                "draft": false,
                "prerelease": false,
                "published_at": "2026-07-30T10:00:00Z",
                "html_url": "https://example.test/stable",
                "assets": []
              }
            ]
            """;
        using var client = new HttpClient(new StaticHandler(json));
        using var service = new UpdateService(
            new UpdateSettings
            {
                Repository = "Onmaynec/WinState",
                Channel = UpdateChannel.Stable,
                Mode = AutomaticUpdateMode.Check,
                NetworkTimeout = TimeSpan.FromSeconds(2)
            },
            client);

        var result = await service.CheckAsync("1.0.0", CancellationToken.None);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.1.0", result.Release!.Version.ToString());
        Assert.False(result.Release.IsPrerelease);
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly string _content;

        public StaticHandler(string content)
        {
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            });
        }
    }
}
