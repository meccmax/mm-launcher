using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;

namespace MMLauncher.Core.Browser;

/// <summary>
/// Looks up which country an IPv4 address is in, from DB-IP's free "IP to Country Lite"
/// database (db-ip.com, CC BY 4.0). The MM Server Agent downloads it once a month and uses it
/// to add a country to every server in the browser list; lookups happen on the agent itself,
/// so no address is ever sent anywhere to be looked up.
///
/// File format (gzip CSV), one range per line: <c>1.0.0.0,1.0.0.255,AU</c>. IPv6 lines are skipped.
/// </summary>
public sealed class GeoIpDatabase
{
    public const string Attribution = "IP Geolocation by DB-IP (db-ip.com)";

    private readonly uint[] _starts;
    private readonly uint[] _ends;
    private readonly ushort[] _codes; // two ASCII letters packed

    private GeoIpDatabase(uint[] starts, uint[] ends, ushort[] codes)
    {
        _starts = starts;
        _ends = ends;
        _codes = codes;
    }

    public int Count => _starts.Length;

    /// <summary>Reads a gzip-compressed DB-IP country CSV. Throws InvalidDataException if it holds no usable ranges.</summary>
    public static GeoIpDatabase LoadGzip(Stream gzip)
    {
        using var gz = new GZipStream(gzip, CompressionMode.Decompress);
        return LoadCsv(gz);
    }

    public static GeoIpDatabase LoadCsv(Stream csv)
    {
        var starts = new List<uint>(400_000);
        var ends = new List<uint>(400_000);
        var codes = new List<ushort>(400_000);
        using var reader = new StreamReader(csv);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            var cc = parts[2].Trim().Trim('"');
            if (cc.Length != 2 || cc == "ZZ" || !char.IsAsciiLetter(cc[0]) || !char.IsAsciiLetter(cc[1])) continue;
            if (!TryV4(parts[0].Trim('"'), out var a) || !TryV4(parts[1].Trim('"'), out var b) || b < a) continue;
            starts.Add(a);
            ends.Add(b);
            codes.Add(Pack(cc));
        }
        if (starts.Count == 0) throw new InvalidDataException("No IPv4 country ranges in the file.");

        // DB-IP's file is already sorted; sort anyway so a different source can't break lookups.
        var order = Enumerable.Range(0, starts.Count).OrderBy(i => starts[i]).ToArray();
        return new GeoIpDatabase(order.Select(i => starts[i]).ToArray(), order.Select(i => ends[i]).ToArray(), order.Select(i => codes[i]).ToArray());
    }

    /// <summary>"US", "DE", … or "" when the address isn't covered (or isn't IPv4).</summary>
    public string Lookup(string ip) => IPAddress.TryParse(ip, out var a) ? Lookup(a) : "";

    public string Lookup(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return "";
        uint v = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        int i = Array.BinarySearch(_starts, v);
        if (i < 0) i = ~i - 1; // the last range starting at or before v
        if (i < 0 || v > _ends[i]) return "";
        return Unpack(_codes[i]);
    }

    private static bool TryV4(string s, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(s, out var a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        value = BinaryPrimitives.ReadUInt32BigEndian(a.GetAddressBytes());
        return true;
    }

    private static ushort Pack(string cc) => (ushort)((char.ToUpperInvariant(cc[0]) << 8) | char.ToUpperInvariant(cc[1]));
    private static string Unpack(ushort v) => new([(char)(v >> 8), (char)(v & 0xFF)]);
}
