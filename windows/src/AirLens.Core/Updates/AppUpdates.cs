using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AirLens.Core.Updates;

/// <summary>The Windows setup zip attached to a release.</summary>
public sealed record UpdateAsset(string Name, long Size, Uri Url, string? Sha256);

/// <summary>
/// The newest release, as GitHub's "latest release" API describes it (drafts and pre-releases are
/// never "latest"). <see cref="Windows"/> is the setup zip, or null when the release has none.
/// </summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Title, string PageUrl, DateTimeOffset? Published, UpdateAsset? Windows)
{
    private static readonly Regex WindowsZip = new(@"^AirLens-[0-9][0-9A-Za-z.\-]*-windows-x64\.zip$", RegexOptions.IgnoreCase);
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.IgnoreCase);

    /// <summary>Reads a GitHub release JSON object; null if it is not a usable release.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
            string tag = Str(root, "tag_name") ?? "";
            if (UpdateVersion.Parse(tag) is not { } version) return null;
            string notes = Str(root, "body") ?? "";

            UpdateAsset? windows = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = Str(a, "name") ?? "";
                    if (!WindowsZip.IsMatch(name)) continue;
                    if (!Uri.TryCreate(Str(a, "browser_download_url"), UriKind.Absolute, out var url)) continue;
                    long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                    windows = new UpdateAsset(name, size, url, DigestOf(a) ?? ChecksumInNotes(notes, name));
                    break;
                }
            }
            DateTimeOffset? published = DateTimeOffset.TryParse(Str(root, "published_at"), out var p) ? p : null;
            return new ReleaseInfo(version, tag, Str(root, "name") is { Length: > 0 } title ? title : tag,
                Str(root, "html_url") ?? "", published, windows);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>GitHub's own checksum of the uploaded file: <c>"digest": "sha256:&lt;hex&gt;"</c>.</summary>
    private static string? DigestOf(JsonElement asset)
    {
        string? digest = Str(asset, "digest");
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        string hex = digest[7..];
        return Sha256Hex.IsMatch(hex) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>Fallback: a <c>&lt;sha256&gt;  &lt;file name&gt;</c> line in the release notes.</summary>
    private static string? ChecksumInNotes(string notes, string name)
    {
        foreach (var line in notes.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1] == name && Sha256Hex.IsMatch(parts[0])) return parts[0].ToLowerInvariant();
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Release versions: tags like <c>v0.1.1</c>, compared with all four parts filled in.</summary>
public static class UpdateVersion
{
    /// <summary>"v0.1.1" → 0.1.1.0, "0.2" → 0.2.0.0, "1.0.0-beta" → 1.0.0.0; anything else → null.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        int cut = t.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) t = t[..cut];
        if (!t.Contains('.')) return null;
        return Version.TryParse(t, out var v) ? Normalize(v) : null;
    }

    /// <summary>Unset parts (-1) become 0, so 0.1.0 equals 0.1.0.0 instead of sorting below it.</summary>
    public static Version Normalize(Version v) =>
        new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    /// <summary>"0.1.1" for 0.1.1.0, "0.1.0.1" when the fourth part is set.</summary>
    public static string Display(Version v)
    {
        var n = Normalize(v);
        return n.Revision > 0 ? n.ToString(4) : n.ToString(3);
    }
}

/// <summary>
/// Finds, downloads and unpacks AirLens updates. Every file is checked against the SHA-256 the
/// release publishes before anything runs; a file that does not match is deleted.
/// </summary>
public static class UpdateFeed
{
    /// <summary>The public repository's latest release.</summary>
    public static readonly Uri GitHubLatest = new("https://api.github.com/repos/RizwanNaasir/air-lens/releases/latest");

    /// <summary>HTTPS only, or plain HTTP to this PC (a local test feed).</summary>
    public static bool IsAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    /// <summary>The latest release, or null when the repository has none yet.</summary>
    public static async Task<ReleaseInfo?> FetchLatestAsync(HttpClient http, Uri feed, CancellationToken ct = default)
    {
        if (!IsAllowed(feed)) throw new InvalidOperationException($"refusing an update feed that is not HTTPS: {feed}");
        using var request = new HttpRequestMessage(HttpMethod.Get, feed);
        request.Headers.UserAgent.ParseAdd("AirLens-Updater"); // GitHub's API rejects requests without one
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ReleaseInfo.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> into <paramref name="folder"/> and returns its path, only
    /// once its size and SHA-256 match. Progress is reported from 0 to 1.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateAsset asset, string folder,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (asset.Sha256 is null) throw new InvalidDataException("the release publishes no SHA-256 for this file, so it can't be verified");
        if (!IsAllowed(asset.Url)) throw new InvalidOperationException($"refusing a download that is not HTTPS: {asset.Url}");
        Directory.CreateDirectory(folder);
        string final = Path.Combine(folder, Path.GetFileName(asset.Name));
        string partial = final + ".part";
        try
        {
            string hash;
            long received = 0;
            using (var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? asset.Size;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = File.Create(partial);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                int n;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    sha.AppendData(buffer, 0, n);
                    received += n;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)received / total));
                }
                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            if (asset.Size > 0 && received != asset.Size)
                throw new InvalidDataException($"the download is {received:N0} bytes, the release says {asset.Size:N0}");
            if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the download does not match the release's SHA-256");
            File.Move(partial, final, overwrite: true);
            return final;
        }
        catch
        {
            try { File.Delete(partial); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Unpacks a setup zip into <paramref name="folder"/> (replacing it) and returns its Setup.cmd.</summary>
    public static string ExtractSetup(string zipPath, string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, folder); // rejects entries that would land outside the folder
        string? setup = Directory.EnumerateFiles(folder, "Setup.cmd", SearchOption.AllDirectories)
            .Where(p => File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "install.ps1")))
            .OrderBy(p => p.Length)
            .FirstOrDefault();
        return setup ?? throw new InvalidDataException("the update has no Setup.cmd and install.ps1");
    }
}
