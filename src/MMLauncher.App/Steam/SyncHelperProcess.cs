using System.Diagnostics;
using System.Text.Json;
using MMLauncher.Core.Launch;
using MMLauncher.Core.Logging;

namespace MMLauncher.App.Steam;

/// <summary>What the main window sends the helper: the mods to sync.</summary>
public sealed class SyncRequest
{
    public List<RequiredMod> All { get; set; } = [];
    public List<RequiredMod> ToDownload { get; set; } = [];
    public Dictionary<ulong, long> Sizes { get; set; } = [];
    public RepairSpec? Repair { get; set; }
    /// <summary>True = unsubscribe from All instead of downloading.</summary>
    public bool Remove { get; set; }
}

/// <summary>
/// Runs the Steam download in a separate, short-lived copy of MMLauncher.exe
/// ("MMLauncher.exe --steam-sync").
///
/// Why: once a process connects to Steam as DayZ, Steam treats DayZ as running until that
/// process EXITS, even after it disconnects. Steam then silently ignores the request to start
/// the real game. Running the download in a helper that exits when it's done clears that,
/// and the "Playing DayZ" status disappears the moment downloading ends.
///
/// Protocol: the request goes in on stdin as one JSON line; the helper writes lines to stdout:
///   P {SyncProgress json}   progress
///   R {SyncResult json}     final result (always the last line)
/// </summary>
public sealed class SyncHelperProcess : IModDownloader
{
    public const string Argument = "--steam-sync";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public Task<SyncResult> SyncAsync(
        IReadOnlyList<RequiredMod> all,
        IReadOnlyList<RequiredMod> toDownload,
        IReadOnlyDictionary<ulong, long> sizes,
        RepairSpec? repair,
        IProgress<SyncProgress> progress,
        CancellationToken ct) =>
        RunHelperAsync(new SyncRequest { All = all.ToList(), ToDownload = toDownload.ToList(), Sizes = sizes.ToDictionary(), Repair = repair },
                       toDownload, progress, ct);

    public Task<SyncResult> RemoveAsync(IReadOnlyList<RequiredMod> mods, IProgress<SyncProgress> progress, CancellationToken ct) =>
        RunHelperAsync(new SyncRequest { All = mods.ToList(), Remove = true }, mods, progress, ct);

    private async Task<SyncResult> RunHelperAsync(SyncRequest request, IReadOnlyList<RequiredMod> toDownload, IProgress<SyncProgress> progress, CancellationToken ct)
    {
        var exe = Environment.ProcessPath;
        if (exe == null)
            return new SyncResult(false, "Couldn't find the launcher's own exe to start the Steam helper.", toDownload);

        // Testing hook: MMLAUNCHER_HELPER_DLL=<path to MMLauncher.dll> runs "dotnet <dll> --steam-sync".
        var helperDll = Environment.GetEnvironmentVariable("MMLAUNCHER_HELPER_DLL");
        if (!string.IsNullOrEmpty(helperDll)) exe = "dotnet";

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Running via "dotnet MMLauncher.dll" (developer builds): pass the dll along.
        if (!string.IsNullOrEmpty(helperDll))
            psi.ArgumentList.Add(helperDll);
        else if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(SyncHelperProcess).Assembly.Location);
        psi.ArgumentList.Add(Argument);

        Process helper;
        try
        {
            helper = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't start Steam helper: " + ex.Message);
            return new SyncResult(false, "Couldn't start the Steam helper: " + ex.Message, toDownload);
        }

        using (helper)
        {
            await helper.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            helper.StandardInput.Close();
            var stderr = helper.StandardError.ReadToEndAsync(CancellationToken.None);

            SyncResult? result = null;
            try
            {
                string? line;
                while ((line = await helper.StandardOutput.ReadLineAsync(ct)) != null)
                {
                    if (line.StartsWith("P "))
                    {
                        var p = JsonSerializer.Deserialize<SyncProgress>(line[2..], Json);
                        if (p != null) progress.Report(p);
                    }
                    else if (line.StartsWith("R "))
                    {
                        result = JsonSerializer.Deserialize<SyncResult>(line[2..], Json);
                    }
                }
                await helper.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { helper.Kill(); } catch { }
                Log.Info("Sync cancelled; Steam helper stopped.");
                return new SyncResult(false, "Cancelled. Steam may finish any download already in progress.", toDownload);
            }

            if (result != null) return result;
            var err = (await stderr).Trim();
            Log.Error($"Steam helper exited with code {helper.ExitCode} and no result. {err}");
            return new SyncResult(false, $"The Steam helper stopped unexpectedly (code {helper.ExitCode}). Open the log for details.", toDownload);
        }
    }

    /// <summary>Entry point inside the helper process. Returns the exit code.</summary>
    public static async Task<int> RunAsync()
    {
        var stdout = Console.Out;
        void Send(string kind, object payload)
        {
            lock (stdout) { stdout.WriteLine($"{kind} {JsonSerializer.Serialize(payload)}"); stdout.Flush(); }
        }

        try
        {
            var line = await Console.In.ReadLineAsync();
            var request = line == null ? null : JsonSerializer.Deserialize<SyncRequest>(line, Json);
            if (request == null)
            {
                Send("R", new SyncResult(false, "Steam helper got no request.", []));
                return 2;
            }

            // Progress is relayed straight to stdout (not through a sync context).
            var progress = new DirectProgress<SyncProgress>(p => Send("P", p));
            var steam = new SteamworksDownloader();
            var result = request.Remove
                ? await steam.RemoveAsync(request.All, progress, CancellationToken.None)
                : await steam.SyncAsync(request.All, request.ToDownload, request.Sizes, request.Repair, progress, CancellationToken.None);
            Send("R", result);
            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error("Steam helper crashed: " + ex);
            Send("R", new SyncResult(false, "Steam helper crashed: " + ex.Message, []));
            return 3;
        }
    }

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
