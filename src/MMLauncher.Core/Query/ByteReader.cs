using System.Text;

namespace MMLauncher.Core.Query;

/// <summary>
/// Reads little-endian values out of a raw UDP response.
/// Throws <see cref="EndOfStreamException"/> if a packet is shorter than expected,
/// which callers treat as "malformed reply".
/// </summary>
public sealed class ByteReader
{
    private readonly byte[] _data;

    public ByteReader(byte[] data, int start = 0)
    {
        _data = data;
        Position = start;
    }

    public int Position { get; private set; }
    public int Remaining => _data.Length - Position;
    public bool AtEnd => Position >= _data.Length;

    private void Need(int count)
    {
        if (Position + count > _data.Length)
            throw new EndOfStreamException($"Packet ended early (needed {count} bytes at offset {Position}, length {_data.Length}).");
    }

    public byte ReadByte()
    {
        Need(1);
        return _data[Position++];
    }

    public short ReadInt16()
    {
        Need(2);
        var v = BitConverter.ToInt16(_data, Position);
        Position += 2;
        return v;
    }

    public ushort ReadUInt16()
    {
        Need(2);
        var v = BitConverter.ToUInt16(_data, Position);
        Position += 2;
        return v;
    }

    public int ReadInt32()
    {
        Need(4);
        var v = BitConverter.ToInt32(_data, Position);
        Position += 4;
        return v;
    }

    public uint ReadUInt32()
    {
        Need(4);
        var v = BitConverter.ToUInt32(_data, Position);
        Position += 4;
        return v;
    }

    public ulong ReadUInt64()
    {
        Need(8);
        var v = BitConverter.ToUInt64(_data, Position);
        Position += 8;
        return v;
    }

    public byte[] ReadBytes(int count)
    {
        Need(count);
        var result = new byte[count];
        Buffer.BlockCopy(_data, Position, result, 0, count);
        Position += count;
        return result;
    }

    /// <summary>Reads a null-terminated string as raw bytes (terminator consumed, not returned).</summary>
    public byte[] ReadCStringBytes()
    {
        int end = Array.IndexOf(_data, (byte)0, Position);
        if (end < 0)
            throw new EndOfStreamException("Unterminated string in packet.");
        var result = new byte[end - Position];
        Buffer.BlockCopy(_data, Position, result, 0, result.Length);
        Position = end + 1;
        return result;
    }

    public string ReadCString() => Encoding.UTF8.GetString(ReadCStringBytes());
}
