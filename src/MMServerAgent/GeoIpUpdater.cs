using MMLauncher.Core.Browser;

namespace MMServerAgent;

/// <summary>
/// Keeps DB-IP's free country database (db-ip.com, CC BY 4.0, about 5 MB) in the "geoip" folder
/// next to the agent, downloading the new edition once a month, so every server in the browser
/// list can show which country it's in. Lookups happen here on the agent; no address is sent
/// anywhere. Turned off with "CountryLookup": false in agent.jsonc.
/// </summary>
public sealed class GeoIpUpdater(AgentConfig config, Action<string> log)
{
    public const string FileName = "dbip-country-lite.csv.gz";
    public const string FolderName = "geoip";
    private static string FolderPath => Path.Combine(AppContext.BaseDirectory, FolderName);
    private static string FilePath => Path.Combine(FolderPath, FileName);

    /// <summary>A new edition comes out at the start of each month.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(32);

    private GeoIpDatabase? _db;
    private DateTime _nextTry = DateTime.MinValue;
    private DateTime _loadedFileTime;

    public bool Enabled => config.CountryLookup;

    /// <summary>"US", "DE", … or "" (unknown, or lookups are off / not loaded yet).</summary>
    public string Lookup(string ip) => _db?.Lookup(ip) ?? "";

    /// <summary>Loads the file on disk, and downloads a newer one when it's over a month old (at most one try every 6 hours).</summary>
    public async Task EnsureFreshAsync(HttpClient http, CancellationToken ct)
    {
        if (!Enabled) return;
        var info = new FileInfo(FilePath);
        if (info.Exists && (_db == null || info.LastWriteTimeUtc != _loadedFileTime)) Load(info);

        bool stale = !info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > MaxAge;
        if (!stale || DateTime.UtcNow < _nextTry) return;
        _nextTry = DateTime.UtcNow.AddHours(6);
        try
        {
            Directory.CreateDirectory(FolderPath);
            var now = DateTime.UtcNow;
            // Early in the month the new edition may not be out yet, so fall back to last month's.
            foreach (var month in new[] { now, now.AddMonths(-1) })
            {
                var url = $"https://download.db-ip.com/free/dbip-country-lite-{month:yyyy-MM}.csv.gz";
                using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!res.IsSuccessStatusCode) continue;
                var temp = FilePath + ".download";
                await using (var file = File.Create(temp))
                await using (var body = await res.Content.ReadAsStreamAsync(ct))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await body.ReadAsync(buffer, ct)) > 0)
                    {
                        total += n;
                        if (total > 64L * 1024 * 1024) throw new InvalidDataException("The download is far bigger than the database should be.");
                        await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    }
                }
                // Check it reads before replacing the working copy.
                await using (var check = File.OpenRead(temp)) GeoIpDatabase.LoadGzip(check);
                File.Move(temp, FilePath, overwrite: true);
                Load(new FileInfo(FilePath));
                log($"Server countries: downloaded DB-IP's {month:MMMM yyyy} country database ({_db?.Count:N0} ranges)");
                return;
            }
            log("Server countries: couldn't download DB-IP's country database (will try again later)" + (info.Exists ? "; using the copy already here" : ""));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log("Server countries: couldn't update the country database: " + ex.Message);
            try { File.Delete(FilePath + ".download"); } catch { }
        }
    }

    private void Load(FileInfo info)
    {
        try
        {
            using var stream = info.OpenRead();
            _db = GeoIpDatabase.LoadGzip(stream);
            _loadedFileTime = info.LastWriteTimeUtc;
        }
        catch (Exception ex)
        {
            log($"Server countries: {FileName} couldn't be read ({ex.Message}); it will be downloaded again");
            _db = null;
            try { info.Delete(); } catch { }
        }
    }
}
