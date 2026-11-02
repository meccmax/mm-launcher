using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MMLauncher.Core.Query;

/// <summary>Thrown when a server doesn't answer in time or answers with something unusable.</summary>
public sealed class ServerQueryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Talks to one game server's Steam query port directly from this PC.
/// No middleman service: if the server is up and the port is open, we get an answer.
/// </summary>
public static class A2sClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2.5);
    private const int Attempts = 2;

    /// <summary>Asks for server info. Returns the info plus round-trip time in milliseconds.</summary>
    public static async Task<(A2sInfo Info, int PingMs)> QueryInfoAsync(
        IPEndPoint endpoint, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        return await WithRetries(endpoint, async udp =>
        {
            var t = timeout ?? DefaultTimeout;
            var sw = Stopwatch.StartNew();
            var reply = await ExchangeAsync(udp, A2sPackets.InfoRequest(), t, ct);

            // Newer servers answer the first request with a challenge we must echo back.
            if (A2sPackets.IsChallenge(reply))
            {
                var challenge = A2sPackets.ChallengeOf(reply);
                sw.Restart();
                reply = await ExchangeAsync(udp, A2sPackets.InfoRequest(challenge), t, ct);
            }
            sw.Stop();

            if (A2sPackets.TypeOf(reply) != A2sPackets.InfoReply)
                throw new ServerQueryException($"Unexpected reply type 0x{A2sPackets.TypeOf(reply):X2} to info request.");

            return (A2sInfo.Parse(reply), (int)Math.Max(1, sw.ElapsedMilliseconds));
        }, ct);
    }

    /// <summary>
    /// Asks for the server's rules list. Keys/values are returned as raw bytes because
    /// DayZ hides its binary mod list inside rule entries with 2-byte binary keys.
    /// </summary>
    public static async Task<List<KeyValuePair<byte[], byte[]>>> QueryRulesAsync(
        IPEndPoint endpoint, TimeSpan? timeout = null, CancellationToken ct = default, int attempts = Attempts)
    {
        return await WithRetries(endpoint, attempts, async udp =>
        {
            var t = timeout ?? DefaultTimeout;
            var reply = await ExchangeAsync(udp, A2sPackets.RulesRequest(), t, ct);
            if (A2sPackets.IsChallenge(reply))
                reply = await ExchangeAsync(udp, A2sPackets.RulesRequest(A2sPackets.ChallengeOf(reply)), t, ct);

            if (A2sPackets.TypeOf(reply) != A2sPackets.RulesReply)
                throw new ServerQueryException($"Unexpected reply type 0x{A2sPackets.TypeOf(reply):X2} to rules request.");

            var r = new ByteReader(reply, 5);
            int count = r.ReadUInt16();
            var rules = new List<KeyValuePair<byte[], byte[]>>(count);
            for (int i = 0; i < count && !r.AtEnd; i++)
            {
                var key = r.ReadCStringBytes();
                var value = r.AtEnd ? [] : r.ReadCStringBytes();
                rules.Add(new(key, value));
            }
            return rules;
        }, ct);
    }

    private static Task<T> WithRetries<T>(IPEndPoint endpoint, Func<UdpClient, Task<T>> action, CancellationToken ct) =>
        WithRetries(endpoint, Attempts, action, ct);

    private static async Task<T> WithRetries<T>(IPEndPoint endpoint, int attempts, Func<UdpClient, Task<T>> action, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var udp = new UdpClient(endpoint.AddressFamily);
            A2sPackets.DisableConnectionReset(udp.Client);
            udp.Connect(endpoint);
            try
            {
                return await action(udp);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Port actively refused: no point retrying.
                throw new ServerQueryException("Server refused the query (port closed).", ex);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw last as ServerQueryException ?? new ServerQueryException(last?.Message ?? "No reply.", last);
    }

    /// <summary>Sends one request and waits for one complete (possibly multi-packet) reply.</summary>
    private static async Task<byte[]> ExchangeAsync(UdpClient udp, byte[] request, TimeSpan timeout, CancellationToken ct)
    {
        await udp.SendAsync(request, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await ReceiveCompleteAsync(udp, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ServerQueryException("No reply (timed out).");
        }
    }

    /// <summary>
    /// Receives a reply, reassembling Source-style split packets when a reply is too big
    /// for one UDP datagram (DayZ rules with lots of mods usually are).
    /// </summary>
    internal static async Task<byte[]> ReceiveCompleteAsync(UdpClient udp, CancellationToken ct)
    {
        Dictionary<int, byte[]>? parts = null;
        int expected = 0;
        int splitId = 0;

        while (true)
        {
            var result = await udp.ReceiveAsync(ct);
            var packet = result.Buffer;

            if (A2sPackets.IsSingle(packet))
                return packet;

            if (!A2sPackets.IsSplit(packet))
                continue; // garbage; keep waiting until timeout

            var r = new ByteReader(packet, 4);
            int id = r.ReadInt32();
            int total = r.ReadByte();
            int number = r.ReadByte();
            r.ReadUInt16(); // max packet size (Source engine format)

            if ((id & unchecked((int)0x80000000)) != 0)
                throw new ServerQueryException("Server sent a compressed reply, which isn't supported.");

            if (parts == null || id != splitId)
            {
                parts = new Dictionary<int, byte[]>();
                splitId = id;
                expected = total;
            }

            parts[number] = packet[r.Position..];

            if (parts.Count == expected)
            {
                using var ms = new MemoryStream();
                for (int i = 0; i < expected; i++)
                {
                    if (!parts.TryGetValue(i, out var chunk))
                        throw new ServerQueryException("Split reply was missing a piece.");
                    ms.Write(chunk);
                }
                var whole = ms.ToArray();
                if (!A2sPackets.IsSingle(whole))
                    throw new ServerQueryException("Reassembled reply had a bad header.");
                return whole;
            }
        }
    }
}
