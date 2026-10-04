using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Formats;

/// <summary>A chunk in an IFF-style file whose 4-character id is stored byte-reversed (e.g. "REVM" for MVER).</summary>
public readonly record struct Chunk(string Id, int Offset, int Size);

public static class ChunkReader
{
    public static IEnumerable<Chunk> Read(byte[] data, int start = 0, int end = -1)
    {
        if (end < 0)
            end = data.Length;
        var position = start;
        while (position + 8 <= end)
        {
            var id = new string([(char)data[position + 3], (char)data[position + 2], (char)data[position + 1], (char)data[position]]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position + 4));
            if (size < 0 || position + 8 + size > end)
                yield break;
            yield return new Chunk(id, position + 8, size);
            position += 8 + size;
        }
    }

    /// <summary>Splits a block of NUL-terminated strings, keyed by each string's offset within the block.</summary>
    public static Dictionary<int, string> ReadStringTable(ReadOnlySpan<byte> block)
    {
        var result = new Dictionary<int, string>();
        var start = 0;
        for (var i = 0; i < block.Length; i++)
        {
            if (block[i] != 0)
                continue;
            if (i > start)
                result[start] = Encoding.UTF8.GetString(block[start..i]);
            start = i + 1;
        }
        return result;
    }
}

/// <summary>Little-endian struct reads from a byte buffer.</summary>
internal static class Bytes
{
    public static int I32(this byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
    public static uint U32(this byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    public static ushort U16(this byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    public static short I16(this byte[] data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset));
    public static float F32(this byte[] data, int offset) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset));
    public static Vector3 Vec3(this byte[] data, int offset) => new(data.F32(offset), data.F32(offset + 4), data.F32(offset + 8));
    public static Vector2 Vec2(this byte[] data, int offset) => new(data.F32(offset), data.F32(offset + 4));

    public static T[] Structs<T>(this byte[] data, int offset, int count) where T : struct =>
        MemoryMarshal.Cast<byte, T>(data.AsSpan(offset, count * Marshal.SizeOf<T>())).ToArray();

    public static string CString(this byte[] data, int offset, int maxLength = int.MaxValue)
    {
        var span = data.AsSpan(offset, Math.Min(maxLength, data.Length - offset));
        var end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }
}
