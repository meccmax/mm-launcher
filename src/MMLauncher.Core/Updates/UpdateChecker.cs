using System.Security.Cryptography;
using System.Text.Json;

namespace MMLauncher.Core.Updates;

/// <summary>A published release on GitHub.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Notes,
    string PageUrl,
    IReadOnlyDictionary<string, (string Url, long Size)> Assets);

/// <summary>
/// Looks up the newest release of the launcher/agent on GitHub and downloads release files,
/// checking each against the SHA256SUMS.txt published with the release so a broken or
/// tampered download is never used.
/// </summary>
public sealed class UpdateChecker(HttpClient http)
{
    public const string Repository = "meccmax/mm-launcher";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    public const string ChecksumsAsset = "SHA256SUMS.txt";

    /// <summary>MMLAUNCHER_UPDATE_URL overrides the API address (testing).</summary>
    private static string LatestUrl =>
        Environment.GetEnvironmentVariable("MMLAUNCHER_UPDATE_URL") is { Length: > 0 } u
            ? u
            : $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>The latest stable release, or null if there is none / GitHub can't be reached.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        req.Headers.UserAgent.ParseAdd("MMLauncher-Updater");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null; // 404 = no releases yet
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version)) return null;

        var assets = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("assets", out var list))
            foreach (var a in list.EnumerateArray())
                assets[a.GetProperty("name").GetString() ?? ""] =
                    (a.GetProperty("browser_download_url").GetString() ?? "", a.GetProperty("size").GetInt64());

        return new ReleaseInfo(
            version, tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage,
            assets);
    }

    /// <summary>
    /// Release notes of every stable release newer than <paramref name="current"/> (up to 10),
    /// so someone skipping versions sees everything they're getting. Null if GitHub can't be reached.
    /// </summary>
    public async Task<List<(Version Version, string Notes)>?> GetNotesSinceAsync(Version current, CancellationToken ct = default) =>
        (await GetRecentNotesAsync(ct))?.Where(r => IsNewer(r.Version, current)).ToList();

    /// <summary>Release notes of the newest stable releases (up to 15), newest first. Null if GitHub can't be reached.</summary>
    public async Task<List<(Version Version, string Notes)>?> GetRecentNotesAsync(CancellationToken ct = default)
    {
        if (Environment.GetEnvironmentVariable("MMLAUNCHER_UPDATE_URL") is { Length: > 0 }) return null; // test server: latest only
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=15");
            req.Headers.UserAgent.ParseAdd("MMLauncher-Updater");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var list = new List<(Version, string)>();
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                if (r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;
                if (!TryParseVersion(r.GetProperty("tag_name").GetString() ?? "", out var v)) continue;
                var body = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                list.Add((v, body.Length > 20_000 ? body[..20_000] : body));
            }
            return list.OrderByDescending(x => x.Item1).Take(15).ToList();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>"v0.5.0" / "0.5.0" / "0.5" -> 0.5.0. Anything after a '-' (e.g. "-beta") is ignored.</summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        var t = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (Version.TryParse(t, out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Compares major.minor.patch only.</summary>
    public static bool IsNewer(Version candidate, Version current) =>
        new Version(candidate.Major, candidate.Minor, Math.Max(0, candidate.Build)) >
        new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>
    /// Downloads one asset of a release to <paramref name="destination"/> and verifies its SHA-256
    /// against the release's SHA256SUMS.txt. Throws with a clear message if anything is off;
    /// on failure the partial file is deleted.
    /// </summary>
    public async Task DownloadVerifiedAsync(ReleaseInfo release, string assetName, string destination,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!release.Assets.TryGetValue(assetName, out var asset))
            throw new InvalidOperationException($"Release {release.Tag} has no {assetName}.");
        if (!IsTrustedDownload(asset.Url) || !IsTrustedDownload(release.Assets.GetValueOrDefault(ChecksumsAsset).Url))
            throw new InvalidOperationException("The release points somewhere other than GitHub, so it wasn't downloaded.");
        if (!release.Assets.TryGetValue(ChecksumsAsset, out var sums))
            throw new InvalidOperationException($"Release {release.Tag} has no {ChecksumsAsset}, so the download can't be checked.");

        var expected = ParseChecksums(await GetStringAsync(sums.Url, ct)).GetValueOrDefault(assetName)
                       ?? throw new InvalidOperationException($"{ChecksumsAsset} doesn't list {assetName}.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".part";
        try
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, asset.Url))
            {
                req.Headers.UserAgent.ParseAdd("MMLauncher-Updater");
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                long total = res.Content.Headers.ContentLength ?? asset.Size;
                await using var input = await res.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(temp);
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            string actual;
            await using (var fs = File.OpenRead(temp))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The downloaded {assetName} doesn't match its published checksum (download damaged?). Nothing was changed.");

            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>
    /// Update files must come from GitHub over HTTPS (release downloads redirect to
    /// *.githubusercontent.com, which HttpClient follows only over HTTPS).
    /// </summary>
    public static bool IsTrustedDownload(string? url)
    {
        if (Environment.GetEnvironmentVariable("MMLAUNCHER_UPDATE_URL") is { Length: > 0 }) return url != null; // test mode
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
               (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Links the launcher opens in the browser: HTTPS on GitHub or Steam only.</summary>
    public static bool IsSafeLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses "hash  filename" lines (sha256sum format, optional '*' before the name).</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 66) continue;
            var hash = line[..64];
            if (!hash.All(Uri.IsHexDigit)) continue;
            var name = line[64..].Trim().TrimStart('*');
            if (name.Length > 0) map[name] = hash;
        }
        return map;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("MMLauncher-Updater");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }
}
