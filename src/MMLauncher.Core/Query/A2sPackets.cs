using System.Net.Sockets;
using System.Text;

namespace MMLauncher.Core.Query;

/// <summary>
/// Builders and helpers for the raw A2S packets.
/// Reference: Valve Developer Community, "Server queries".
/// </summary>
public static class A2sPackets
{
    public const byte InfoReply = 0x49;      // 'I'
    public const byte ChallengeReply = 0x41; // 'A'
    public const byte RulesReply = 0x45;     // 'E'

    private static readonly byte[] Prefix = [0xFF, 0xFF, 0xFF, 0xFF];
    private static readonly byte[] InfoBody = Encoding.ASCII.GetBytes("TSource Engine Query\0");

    /// <summary>A2S_INFO request, optionally with the challenge the server handed back.</summary>
    public static byte[] InfoRequest(byte[]? challenge = null)
    {
        var size = Prefix.Length + InfoBody.Length + (challenge?.Length ?? 0);
        var buf = new byte[size];
        Prefix.CopyTo(buf, 0);
        InfoBody.CopyTo(buf, Prefix.Length);
        challenge?.CopyTo(buf, Prefix.Length + InfoBody.Length);
        return buf;
    }

    /// <summary>A2S_RULES request. With no challenge it asks the server for one (FF FF FF FF).</summary>
    public static byte[] RulesRequest(byte[]? challenge = null)
    {
        var buf = new byte[9];
        Prefix.CopyTo(buf, 0);
        buf[4] = 0x56; // 'V'
        (challenge ?? Prefix).CopyTo(buf, 5);
        return buf;
    }

    /// <summary>Header byte after the FF FF FF FF prefix (e.g. 'I', 'A', 'E').</summary>
    public static byte TypeOf(byte[] packet) => packet.Length > 4 ? packet[4] : (byte)0;

    public static bool IsChallenge(byte[] packet) => packet.Length >= 9 && TypeOf(packet) == ChallengeReply;

    public static byte[] ChallengeOf(byte[] packet) => packet[5..9];

    public static bool IsSingle(byte[] packet) =>
        packet.Length >= 5 && packet[0] == 0xFF && packet[1] == 0xFF && packet[2] == 0xFF && packet[3] == 0xFF;

    public static bool IsSplit(byte[] packet) =>
        packet.Length >= 12 && packet[0] == 0xFE && packet[1] == 0xFF && packet[2] == 0xFF && packet[3] == 0xFF;

    /// <summary>
    /// Windows quirk: when we send to a closed UDP port, Windows reports the ICMP
    /// "port unreachable" as an error on the *next receive* of that socket and
    /// breaks scanning. This switches that behaviour off. No-op elsewhere.
    /// </summary>
    public static void DisableConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SIO_UDP_CONNRESET = -1744830452;
        try { socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); }
        catch { /* not fatal; we also catch ConnectionReset while receiving */ }
    }
}
