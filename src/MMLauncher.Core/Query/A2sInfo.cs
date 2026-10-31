namespace MMLauncher.Core.Query;

/// <summary>
/// The A2S_INFO reply: the basic "what is this server" answer every Steam game server gives.
/// Field names follow Valve's Server Queries documentation.
/// </summary>
public sealed record A2sInfo
{
    public byte Protocol { get; init; }
    public string Name { get; init; } = "";
    public string Map { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Game { get; init; } = "";
    public short AppIdShort { get; init; }
    public int Players { get; init; }
    public int MaxPlayers { get; init; }
    public int Bots { get; init; }
    public char ServerType { get; init; }
    public char Environment { get; init; }

    /// <summary>True when the server needs a password to join.</summary>
    public bool Passworded { get; init; }

    public bool Vac { get; init; }
    public string Version { get; init; } = "";

    /// <summary>The port players actually connect to (DayZ "game port"). Null if the server didn't say.</summary>
    public int? GamePort { get; init; }

    public ulong? SteamId { get; init; }

    /// <summary>Comma-separated tags. DayZ packs a lot in here: time of day, queue, 1PP, etc.</summary>
    public string Keywords { get; init; } = "";

    public ulong? GameId { get; init; }

    /// <summary>
    /// Parses an A2S_INFO response. <paramref name="packet"/> must start with the
    /// FF FF FF FF header followed by 0x49 ('I').
    /// </summary>
    public static A2sInfo Parse(byte[] packet)
    {
        var r = new ByteReader(packet, 4);
        byte header = r.ReadByte();
        if (header != 0x49)
            throw new InvalidDataException($"Not an A2S_INFO reply (header 0x{header:X2}).");

        byte protocol = r.ReadByte();
        string name = r.ReadCString();
        string map = r.ReadCString();
        string folder = r.ReadCString();
        string game = r.ReadCString();
        short appId = r.ReadInt16();
        int players = r.ReadByte();
        int maxPlayers = r.ReadByte();
        int bots = r.ReadByte();
        char serverType = (char)r.ReadByte();
        char environment = (char)r.ReadByte();
        bool passworded = r.ReadByte() == 1;
        bool vac = r.ReadByte() == 1;
        string version = r.ReadCString();

        int? gamePort = null;
        ulong? steamId = null;
        string keywords = "";
        ulong? gameId = null;

        if (!r.AtEnd)
        {
            byte edf = r.ReadByte();
            if ((edf & 0x80) != 0) gamePort = r.ReadUInt16();
            if ((edf & 0x10) != 0) steamId = r.ReadUInt64();
            if ((edf & 0x40) != 0)
            {
                r.ReadUInt16();    // spectator port
                r.ReadCString();   // spectator name
            }
            if ((edf & 0x20) != 0) keywords = r.ReadCString();
            if ((edf & 0x01) != 0) gameId = r.ReadUInt64();
        }

        return new A2sInfo
        {
            Protocol = protocol,
            Name = name,
            Map = map,
            Folder = folder,
            Game = game,
            AppIdShort = appId,
            Players = players,
            MaxPlayers = maxPlayers,
            Bots = bots,
            ServerType = serverType,
            Environment = environment,
            Passworded = passworded,
            Vac = vac,
            Version = version,
            GamePort = gamePort,
            SteamId = steamId,
            Keywords = keywords,
            GameId = gameId,
        };
    }
}
