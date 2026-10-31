using System.Text;
using MMLauncher.Core.Query;

namespace MMLauncher.Core.DayZ;

/// <summary>One client mod a server requires, as the server itself reports it.</summary>
public sealed record DayZServerMod(ulong WorkshopId, string Name, uint Hash);

/// <summary>
/// Everything decoded from a DayZ server's A2S_RULES reply.
/// </summary>
public sealed record DayZRules
{
    public int ProtocolVersion { get; init; }

    /// <summary>
    /// Raw overflow flags from the server. Non-zero does NOT reliably mean the mod list
    /// is short: a real MM server with a complete 47-mod list still set it (likely the
    /// signatures/description part overflowed). Kept for diagnostics only.
    /// </summary>
    public int OverflowFlags { get; init; }

    /// <summary>True only when the data ran out partway through the mod list itself.</summary>
    public bool ModListCutOff { get; init; }

    public int DlcFlags { get; init; }
    public IReadOnlyList<DayZServerMod> Mods { get; init; } = [];
    public IReadOnlyList<string> Signatures { get; init; } = [];
    public string Description { get; init; } = "";

    /// <summary>Plain text rules (island, requiredVersion, timeLeft, ...).</summary>
    public IReadOnlyDictionary<string, string> Plain { get; init; } = new Dictionary<string, string>();

    public bool MayBeTruncated => ModListCutOff;

    /// <summary>Known DLC bits. Bit 2 = Frostline (Sakhal), bit 4 = Badlands (Nasdara).</summary>
    public IReadOnlyList<string> RequiredDlcNames
    {
        get
        {
            var names = new List<string>();
            if ((DlcFlags & 2) != 0) names.Add("Frostline");
            if ((DlcFlags & 4) != 0) names.Add("Badlands");
            for (int bit = 0; bit < 16; bit++)
            {
                int mask = 1 << bit;
                if (mask is 2 or 4) continue;
                if ((DlcFlags & mask) != 0) names.Add($"DLC #{mask}");
            }
            return names;
        }
    }
}

/// <summary>
/// Decodes the binary mod list DayZ hides in its rules reply.
///
/// Format (same family as Bohemia's "Arma 3 ServerBrowserProtocol3", cross-checked
/// against the open-source dayzquery and node-gamedig decoders):
///  - Rules whose key is exactly 2 bytes are chunks of one binary blob, ordered by key.
///  - The joined blob is escaped: 01 01 -> 01, 01 02 -> 00, 01 03 -> FF.
///  - Blob layout (little-endian):
///      u8  protocol version
///      u8  overflow flags
///      u16 DLC flags, then one u32 hash per set bit
///      u8  mod count, then per mod:
///            u32 hash, u8 id-length (low 4 bits), id bytes, u8 name length, name
///      u8  signature count, then per signature: u8 length, text
///      [optional] u8 length, server description
/// </summary>
public static class DayZRulesParser
{
    public static DayZRules Parse(IReadOnlyList<KeyValuePair<byte[], byte[]>> rules)
    {
        var chunks = new List<(int Order, byte[] Data)>();
        var plain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in rules)
        {
            if (key.Length == 2)
                chunks.Add((key[0] | (key[1] << 8), value));
            else
                plain[Encoding.UTF8.GetString(key)] = Encoding.UTF8.GetString(value);
        }

        if (chunks.Count == 0)
            return new DayZRules { Plain = plain };

        chunks.Sort((a, b) => a.Order.CompareTo(b.Order));
        var joined = chunks.SelectMany(c => c.Data).ToArray();
        var blob = Unescape(joined);

        return ParseBlob(blob) with { Plain = plain };
    }

    public static byte[] Unescape(byte[] data)
    {
        var output = new List<byte>(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == 0x01 && i + 1 < data.Length)
            {
                byte next = data[i + 1];
                byte? mapped = next switch { 0x01 => 0x01, 0x02 => 0x00, 0x03 => 0xFF, _ => null };
                if (mapped.HasValue)
                {
                    output.Add(mapped.Value);
                    i++;
                    continue;
                }
            }
            output.Add(b);
        }
        return output.ToArray();
    }

    public static DayZRules ParseBlob(byte[] blob)
    {
        var r = new ByteReader(blob);
        int version = r.ReadByte();
        int overflow = r.ReadByte();
        int dlcFlags = r.ReadUInt16();
        for (int i = 0; i < System.Numerics.BitOperations.PopCount((uint)dlcFlags); i++)
            r.ReadUInt32(); // DLC hash, not needed

        var mods = new List<DayZServerMod>();
        var signatures = new List<string>();
        string description = "";
        bool modsComplete = false;

        try
        {
            int modCount = r.ReadByte();
            for (int i = 0; i < modCount; i++)
            {
                uint hash = r.ReadUInt32();
                int idLen = r.ReadByte() & 0x0F;
                var idBytes = r.ReadBytes(idLen);
                ulong id = 0;
                for (int b = idLen - 1; b >= 0; b--)
                    id = (id << 8) | idBytes[b];
                int nameLen = r.ReadByte();
                string name = Encoding.UTF8.GetString(r.ReadBytes(nameLen));
                mods.Add(new DayZServerMod(id, name, hash));
            }
            modsComplete = true;

            int sigCount = r.ReadByte();
            for (int i = 0; i < sigCount; i++)
                signatures.Add(Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte())));

            if (!r.AtEnd)
                description = Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
        }
        catch (EndOfStreamException)
        {
            // A cut-off blob still gives us every mod read so far. Only a cut inside
            // the mod list matters; signatures/description being cut off is harmless.
        }

        return new DayZRules
        {
            ProtocolVersion = version,
            OverflowFlags = overflow,
            DlcFlags = dlcFlags,
            Mods = mods,
            Signatures = signatures,
            Description = description,
            ModListCutOff = !modsComplete,
        };
    }
}
