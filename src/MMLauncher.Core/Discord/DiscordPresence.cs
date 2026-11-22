using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MMLauncher.Core.Logging;

namespace MMLauncher.Core.Discord;

/// <summary>What the player is doing, as shown on their Discord profile ("Playing MM Launcher").</summary>
public sealed record PresenceStatus(string Details, string? State = null, DateTimeOffset? Since = null);

/// <summary>
/// Shows "Playing MM Launcher" on the player's Discord profile (Discord's Rich Presence), with what
/// they're doing: browsing servers, or which server they're on. Talks only to the Discord app
/// already running on this PC, through its local pipe (\\.\pipe\discord-ipc-0); nothing is sent
/// over the internet by the launcher itself. Discord not running, or the setting off: nothing happens.
///
/// Protocol: each message is a 4-byte little-endian opcode, a 4-byte little-endian length, then
/// UTF-8 JSON. Opcode 0 = handshake ({"v":1,"client_id":…}), 1 = command, 2 = close, 3/4 = ping/pong.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    /// <summary>The "MM Launcher" application on Discord's developer portal (public, not a secret).</summary>
    public const string ApplicationId = "1574150677830303905";
    /// <summary>Art asset uploaded to that application (Rich Presence > Art Assets).</summary>
    public const string LogoAsset = "mm_logo";
    public const string DownloadUrl = "https://github.com/meccmax/mm-launcher/releases/latest";
    public const string DiscordUrl = "https://www.dsc.gg/mmservers";

    /// <summary>Pipe name prefix; MMLAUNCHER_DISCORD_PIPE overrides it (testing).</summary>
    private static string PipePrefix =>
        Environment.GetEnvironmentVariable("MMLAUNCHER_DISCORD_PIPE") is { Length: > 0 } p ? p : "discord-ipc-";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private PresenceStatus? _wanted;
    private PresenceStatus? _sent;
    private DateTime _nextConnectTry = DateTime.MinValue;
    private bool _enabled;
    private bool _disposed;

    public bool Connected => _pipe is { IsConnected: true };

    /// <summary>Turns the status on or off. Off clears it from the profile right away.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        _enabled = enabled;
        if (!enabled) await ClearAsync();
        else await PushAsync();
    }

    /// <summary>Sets what to show. Sent now if Discord is running, or as soon as it starts (see <see cref="TickAsync"/>).</summary>
    public async Task SetAsync(PresenceStatus status)
    {
        _wanted = status;
        await PushAsync();
    }

    /// <summary>Call every few seconds: reconnects when Discord was started or restarted, and resends.</summary>
    public Task TickAsync() => PushAsync();

    private async Task PushAsync()
    {
        if (!_enabled || _disposed || _wanted == null) return;
        await _gate.WaitAsync();
        try
        {
            if (!Connected)
            {
                _sent = null;
                if (DateTime.UtcNow < _nextConnectTry) return;
                _nextConnectTry = DateTime.UtcNow.AddSeconds(30);
                if (!await ConnectAsync()) return;
            }
            if (_sent == _wanted) return;
            await SendAsync(1, Activity(_wanted));
            await ReadReplyAsync();
            _sent = _wanted;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            Drop();
        }
        finally { _gate.Release(); }
    }

    private async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Connected)
            {
                await SendAsync(1, Command(null));
                await ReadReplyAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException or OperationCanceledException) { }
        finally
        {
            _sent = null;
            Drop();
            _gate.Release();
        }
    }

    private async Task<bool> ConnectAsync()
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", PipePrefix + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(200);
                _pipe = pipe;
                await SendAsync(0, new JsonObject { ["v"] = 1, ["client_id"] = ApplicationId });
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var (op, _) = await ReadAsync(cts.Token); // READY (or an error, after which Discord closes the pipe)
                if (op != 1) { Drop(); return false; }
                Log.Info("Discord: connected, showing \"Playing MM Launcher\".");
                return true;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
            {
                pipe.Dispose();
                if (ReferenceEquals(_pipe, pipe)) _pipe = null;
            }
        }
        return false;
    }

    /// <summary>The SET_ACTIVITY command for a status (exposed for the self-checks).</summary>
    public static JsonObject Activity(PresenceStatus s) => Command(new JsonObject
    {
        ["details"] = Clip(s.Details),
        ["state"] = s.State is { Length: > 0 } st ? Clip(st) : null,
        ["timestamps"] = s.Since is { } since ? new JsonObject { ["start"] = since.ToUnixTimeSeconds() } : null,
        ["assets"] = new JsonObject { ["large_image"] = LogoAsset, ["large_text"] = "MM Launcher · Misfit Mercenaries" },
        ["buttons"] = new JsonArray
        {
            new JsonObject { ["label"] = "Get MM Launcher", ["url"] = DownloadUrl },
            new JsonObject { ["label"] = "Join Misfit Mercenaries", ["url"] = DiscordUrl },
        },
    });

    private static JsonObject Command(JsonObject? activity)
    {
        if (activity != null)
            foreach (var key in activity.Where(p => p.Value == null).Select(p => p.Key).ToList()) activity.Remove(key);
        return new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity },
            ["nonce"] = Guid.NewGuid().ToString("N"),
        };
    }

    /// <summary>Discord allows 2-128 characters per line.</summary>
    private static string Clip(string text)
    {
        var t = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (t.Length > 128) t = t[..127] + "…";
        return t.Length < 2 ? t.PadRight(2) : t;
    }

    private async Task SendAsync(int opcode, JsonObject body)
    {
        var pipe = _pipe ?? throw new IOException("Not connected.");
        var json = Encoding.UTF8.GetBytes(body.ToJsonString());
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), json.Length);
        json.CopyTo(frame, 8);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.WriteAsync(frame, cts.Token);
        await pipe.FlushAsync(cts.Token);
    }

    /// <summary>Reads Discord's answer to a command (answering pings); drops the connection if Discord closed it.</summary>
    private async Task ReadReplyAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var (op, body) = await ReadAsync(cts.Token);
            if (op == 3) { await SendAsync(4, body as JsonObject ?? new JsonObject()); continue; } // ping -> pong
            if (op == 2) throw new IOException("Discord closed the connection.");
            if (body?["evt"]?.GetValue<string>() == "ERROR")
                Log.Warn("Discord didn't accept the status: " + (body["data"]?["message"]?.GetValue<string>() ?? "unknown error"));
            return;
        }
    }

    private async Task<(int Opcode, JsonNode? Body)> ReadAsync(CancellationToken ct)
    {
        var pipe = _pipe ?? throw new IOException("Not connected.");
        var head = new byte[8];
        await pipe.ReadExactlyAsync(head, ct);
        int op = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0, 4));
        int len = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4, 4));
        if (len is < 0 or > 64 * 1024) throw new IOException("Unexpected reply from Discord.");
        var body = new byte[len];
        await pipe.ReadExactlyAsync(body, ct);
        try { return (op, JsonNode.Parse(body)); }
        catch (JsonException) { return (op, null); }
    }

    private void Drop()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Drop();
    }
}
