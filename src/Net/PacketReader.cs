using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Net;

/// <summary>Little-endian reader over a packet body.</summary>
public sealed class PacketReader(byte[] data, int offset = 0)
{
    private int _position = offset;

    public byte[] Data => data;
    public int Position
    {
        get => _position;
        set => _position = value;
    }
    public int Remaining => data.Length - _position;

    public byte U8() => data[_position++];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public Vector3 Vector3() => new(F32(), F32(), F32());
    public ReadOnlySpan<byte> Bytes(int count) => Take(count);

    public string CString()
    {
        var end = Array.IndexOf(data, (byte)0, _position);
        if (end < 0)
            end = data.Length;
        var text = Encoding.UTF8.GetString(data, _position, end - _position);
        _position = Math.Min(end + 1, data.Length);
        return text;
    }

    /// <summary>A GUID sent as a byte mask followed by its non-zero bytes.</summary>
    public ulong PackedGuid()
    {
        var mask = U8();
        ulong guid = 0;
        for (var i = 0; i < 8; i++)
            if ((mask & (1 << i)) != 0)
                guid |= (ulong)U8() << (i * 8);
        return guid;
    }

    public void Skip(int count) => Take(count);

    private ReadOnlySpan<byte> Take(int count)
    {
        if (_position + count > data.Length)
            throw new EndOfStreamException($"Packet ended at {data.Length}, needed {count} bytes at {_position}.");
        var span = data.AsSpan(_position, count);
        _position += count;
        return span;
    }
}
