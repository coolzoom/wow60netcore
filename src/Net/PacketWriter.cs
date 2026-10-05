using System.Buffers.Binary;
using System.Text;

namespace Net;

/// <summary>Little-endian packet body builder.</summary>
public sealed class PacketWriter
{
    private readonly MemoryStream _stream = new();
    private readonly byte[] _scratch = new byte[8];

    public int Length => (int)_stream.Length;

    public PacketWriter U8(byte value)
    {
        _stream.WriteByte(value);
        return this;
    }

    public PacketWriter U16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_scratch, value);
        _stream.Write(_scratch, 0, 2);
        return this;
    }

    public PacketWriter U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_scratch, value);
        _stream.Write(_scratch, 0, 4);
        return this;
    }

    public PacketWriter U64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_scratch, value);
        _stream.Write(_scratch, 0, 8);
        return this;
    }

    public PacketWriter F32(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(_scratch, value);
        _stream.Write(_scratch, 0, 4);
        return this;
    }

    public PacketWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        _stream.Write(bytes);
        return this;
    }

    public PacketWriter CString(string text)
    {
        _stream.Write(Encoding.UTF8.GetBytes(text));
        _stream.WriteByte(0);
        return this;
    }

    public PacketWriter PackedGuid(ulong guid)
    {
        var mask = 0;
        Span<byte> bytes = stackalloc byte[8];
        var count = 0;
        for (var i = 0; i < 8; i++)
        {
            var b = (byte)(guid >> (i * 8));
            if (b == 0)
                continue;
            mask |= 1 << i;
            bytes[count++] = b;
        }
        _stream.WriteByte((byte)mask);
        _stream.Write(bytes[..count]);
        return this;
    }

    public byte[] ToArray() => _stream.ToArray();
}
