using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using AirLens.Core.Updates;
using Xunit;

namespace AirLens.Core.Tests;

public class UpdateTests
{
    private const string ZipSha = "0b5f6f58d111d9579b3c898eb6dae2234259063c266967db6982c57fe2587cb0";

    // The fields GitHub's "latest release" API returns that the updater reads.
    private static string ReleaseJson(string tag = "v0.1.1", string digest = "sha256:" + ZipSha, string extra = "") => $$"""
        {
          "tag_name": "{{tag}}", "name": "AirLens 0.1.1", "draft": false, "prerelease": false{{extra}},
          "html_url": "https://github.com/RizwanNaasir/air-lens/releases/tag/{{tag}}",
          "published_at": "2026-10-19T15:00:00Z",
          "body": "Notes\n```\n{{ZipSha}}  AirLens-0.1.1-windows-x64.zip\n```",
          "assets": [
            { "name": "AirLens-0.1.1-android.apk", "size": 63285695, "digest": "sha256:21de19a0370a47c5892399320e1f386a328e8edef628379057ae0b7de2c431ef",
              "browser_download_url": "https://github.com/RizwanNaasir/air-lens/releases/download/{{tag}}/AirLens-0.1.1-android.apk" },
            { "name": "AirLens-0.1.1-windows-x64.zip", "size": 127075333, "digest": "{{digest}}",
              "browser_download_url": "https://github.com/RizwanNaasir/air-lens/releases/download/{{tag}}/AirLens-0.1.1-windows-x64.zip" }
          ]
        }
        """;

    [Fact]
    public void ReadsTheReleaseAndItsWindowsZip()
    {
        var r = ReleaseInfo.Parse(ReleaseJson())!;
        Assert.Equal(new Version(0, 1, 1, 0), r.Version);
        Assert.Equal("AirLens 0.1.1", r.Title);
        Assert.Equal("https://github.com/RizwanNaasir/air-lens/releases/tag/v0.1.1", r.PageUrl);
        Assert.Equal("AirLens-0.1.1-windows-x64.zip", r.Windows!.Name); // not the APK
        Assert.Equal(127075333, r.Windows.Size);
        Assert.Equal(ZipSha, r.Windows.Sha256);
    }

    [Fact]
    public void FallsBackToTheChecksumInTheNotes()
    {
        Assert.Equal(ZipSha, ReleaseInfo.Parse(ReleaseJson(digest: ""))!.Windows!.Sha256);
        var noSum = ReleaseInfo.Parse(ReleaseJson(digest: "").Replace(ZipSha + "  ", ""))!;
        Assert.Null(noSum.Windows!.Sha256);
    }

    [Fact]
    public void IgnoresPreReleasesAndBadInput()
    {
        Assert.NotNull(ReleaseInfo.Parse(ReleaseJson()));
        Assert.Null(ReleaseInfo.Parse(ReleaseJson().Replace("\"draft\": false", "\"draft\": true")));
        Assert.Null(ReleaseInfo.Parse(ReleaseJson().Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.Null(ReleaseInfo.Parse(ReleaseJson(tag: "nightly")));
        Assert.Null(ReleaseInfo.Parse("not json"));
        Assert.Null(ReleaseInfo.Parse("[]"));
    }

    [Theory]
    [InlineData("v0.1.1", "0.1.1.0")]
    [InlineData("0.2", "0.2.0.0")]
    [InlineData("V1.0.0-beta.1", "1.0.0.0")]
    [InlineData("v0.1.0.1", "0.1.0.1")]
    public void ParsesVersionTags(string tag, string expected) => Assert.Equal(Version.Parse(expected), UpdateVersion.Parse(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1")]
    public void RejectsTagsThatAreNotVersions(string tag) => Assert.Null(UpdateVersion.Parse(tag));

    [Fact]
    public void ComparesVersionsWithAllPartsFilledIn()
    {
        // System.Version alone ranks 0.1.0 below 0.1.0.0 (an unset part is -1).
        Assert.False(UpdateVersion.IsNewer(new Version(0, 1, 0, 0), new Version(0, 1, 0)));
        Assert.True(UpdateVersion.IsNewer(new Version(0, 1, 0, 1), new Version(0, 1, 0)));
        Assert.True(UpdateVersion.IsNewer(new Version(0, 1, 1), new Version(0, 1, 0, 1)));
        Assert.False(UpdateVersion.IsNewer(new Version(0, 1, 0), new Version(0, 2, 0)));
        Assert.Equal("0.1.1", UpdateVersion.Display(new Version(0, 1, 1, 0)));
        Assert.Equal("0.1.0.1", UpdateVersion.Display(new Version(0, 1, 0, 1)));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/x/y/releases/latest", true)]
    [InlineData("http://127.0.0.1:4174/latest.json", true)]
    [InlineData("http://localhost/latest.json", true)]
    [InlineData("http://example.com/latest.json", false)]
    [InlineData("ftp://example.com/x.zip", false)]
    public void OnlyAllowsHttpsOrThisPc(string url, bool allowed) => Assert.Equal(allowed, UpdateFeed.IsAllowed(new Uri(url)));

    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "airlens-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task DownloadKeepsTheFileOnlyWhenItMatches()
    {
        var body = new byte[300_000];
        new Random(7).NextBytes(body);
        string sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        using var http = new HttpClient(new BytesHandler(body));
        var url = new Uri("https://example.com/AirLens-0.1.1-windows-x64.zip");
        string dir = TempDir();
        try
        {
            var reports = new List<double>();
            string path = await UpdateFeed.DownloadAsync(http, new UpdateAsset("AirLens-0.1.1-windows-x64.zip", body.Length, url, sha), dir,
                new SyncProgress(reports));
            Assert.Equal(body, File.ReadAllBytes(path));
            Assert.Equal(1.0, reports[^1], 3);

            File.Delete(path);
            string wrong = new string('0', 64);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                UpdateFeed.DownloadAsync(http, new UpdateAsset("AirLens-0.1.1-windows-x64.zip", body.Length, url, wrong), dir));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                UpdateFeed.DownloadAsync(http, new UpdateAsset("AirLens-0.1.1-windows-x64.zip", body.Length + 1, url, sha), dir));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                UpdateFeed.DownloadAsync(http, new UpdateAsset("AirLens-0.1.1-windows-x64.zip", body.Length, url, null), dir));
            Assert.Empty(Directory.GetFiles(dir)); // nothing unverified is left behind, not even a .part
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class SyncProgress(List<double> into) : IProgress<double>
    {
        public void Report(double value) => into.Add(value);
    }

    [Fact]
    public void FindsSetupInTheUnpackedZip()
    {
        string dir = TempDir();
        try
        {
            string zip = Path.Combine(dir, "AirLens-0.1.1-windows-x64.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var name in new[] { "AirLens-0.1.1/Setup.cmd", "AirLens-0.1.1/install.ps1", "AirLens-0.1.1/app/AirLens.App.exe" })
                {
                    using var w = new StreamWriter(archive.CreateEntry(name).Open());
                    w.Write("x");
                }
            }
            string setup = UpdateFeed.ExtractSetup(zip, Path.Combine(dir, "unpacked"));
            Assert.Equal(Path.Combine(dir, "unpacked", "AirLens-0.1.1", "Setup.cmd"), setup);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
