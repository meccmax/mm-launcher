using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MMLauncher.Core.Query;

namespace MMLauncher.Core.Discovery;

/// <summary>A server found by scanning: which host/port answered, and what it said.</summary>
public sealed record DiscoveredServer(string Host, IPAddress Address, int QueryPort, A2sInfo Info, int PingMs);

/// <summary>
/// Finds MM servers by sending an A2S_INFO "who are you?" packet to every port in the
/// configured range on each MM host. Whatever answers is a server's query port.
/// This means new servers show up without anyone editing a list.
/// </summary>
public static class ServerScanner
{
    /// <summary>How long to keep listening after the last packet goes out.</summary>
    public static TimeSpan ListenWindow { get; set; } = TimeSpan.FromSeconds(3);

    public static async Task<List<DiscoveredServer>> ScanAsync(
        IEnumerable<string> hosts, int portStart, int portEnd,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Resolve every host; if two names point at the same machine, scan it once.
        var targets = new List<(string Host, IPAddress Address)>();
        foreach (var host in hosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()))
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, ct);
                var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (v4 == null)
                {
                    progress?.Report($"{host}: no IPv4 address");
                    continue;
                }
                if (targets.Any(t => t.Address.Equals(v4)))
                {
                    progress?.Report($"{host} is the same machine as {targets.First(t => t.Address.Equals(v4)).Host}");
                    continue;
                }
                targets.Add((host, v4));
            }
            catch (SocketException)
            {
                progress?.Report($"{host}: name not found (DNS)");
            }
        }

        if (targets.Count == 0)
            return [];

        var found = new ConcurrentDictionary<IPEndPoint, DiscoveredServer>();
        var sentAt = new ConcurrentDictionary<IPEndPoint, long>();
        var hostByAddress = targets.ToDictionary(t => t.Address, t => t.Host);
        long lastActivity = Stopwatch.GetTimestamp();

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)); // bound now so we can listen before sending
        A2sPackets.DisableConnectionReset(udp.Client);
        udp.Client.ReceiveBufferSize = 1024 * 1024;

        using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var receiver = Task.Run(async () =>
        {
            while (!listenCts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(listenCts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; } // ICMP noise from closed ports
                catch (ObjectDisposedException) { break; }

                var packet = result.Buffer;
                var from = result.RemoteEndPoint;
                if (!hostByAddress.ContainsKey(from.Address)) continue;

                try
                {
                    if (A2sPackets.IsChallenge(packet))
                    {
                        sentAt[from] = Stopwatch.GetTimestamp();
                        await udp.SendAsync(A2sPackets.InfoRequest(A2sPackets.ChallengeOf(packet)), from, listenCts.Token);
                        Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
                    }
                    else if (A2sPackets.IsSingle(packet) && A2sPackets.TypeOf(packet) == A2sPackets.InfoReply)
                    {
                        var info = A2sInfo.Parse(packet);
                        int ping = sentAt.TryGetValue(from, out var t0)
                            ? (int)Math.Max(1, Stopwatch.GetElapsedTime(t0).TotalMilliseconds) : 0;
                        found[from] = new DiscoveredServer(hostByAddress[from.Address], from.Address, from.Port, info, ping);
                        Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
                    }
                }
                catch (Exception) { /* malformed reply from something that isn't a game server */ }
            }
        }, CancellationToken.None);

        // Send the probes, gently: small batches with short pauses so we don't trip
        // flood protection on the server's firewall.
        var request = A2sPackets.InfoRequest();
        int total = targets.Count * (portEnd - portStart + 1);
        int sent = 0;
        foreach (var (host, address) in targets)
        {
            progress?.Report($"Scanning {host} ports {portStart}-{portEnd}...");
            for (int port = portStart; port <= portEnd; port++)
            {
                ct.ThrowIfCancellationRequested();
                var ep = new IPEndPoint(address, port);
                sentAt[ep] = Stopwatch.GetTimestamp();
                try { await udp.SendAsync(request, ep, ct); } catch (SocketException) { }
                if (++sent % 50 == 0) await Task.Delay(10, ct);
            }
        }
        Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
        progress?.Report($"Sent {total} probes, waiting for replies...");

        // Listen until things go quiet.
        while (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastActivity)) < ListenWindow)
            await Task.Delay(100, ct);

        listenCts.Cancel();
        await receiver;

        // A DayZ server might answer on more than one port; keep one per game port.
        return found.Values
            .GroupBy(s => (s.Address, Port: s.Info.GamePort ?? -s.QueryPort))
            .Select(g => g.OrderBy(s => s.QueryPort).First())
            .OrderBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
