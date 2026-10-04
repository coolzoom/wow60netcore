using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Formats.Mpq;

/// <summary>Read-only MPQ archive (format v0/v1). Safe for concurrent reads.</summary>
public sealed class MpqArchive : IDisposable
{
    private const uint Magic = 0x1A51504D; // "MPQ\x1A"
    private const uint HashEmpty = 0xFFFFFFFF;
    private const uint HashDeleted = 0xFFFFFFFE;

    private const uint FileImplode = 0x00000100;
    private const uint FileCompress = 0x00000200;
    private const uint FileEncrypted = 0x00010000;
    private const uint FileFixKey = 0x00020000;
    private const uint FileSingleUnit = 0x01000000;
    private const uint FileDeleteMarker = 0x02000000;
    private const uint FileSectorCrc = 0x04000000;
    private const uint FileExists = 0x80000000;

    private const byte CompressionZlib = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct HashEntry
    {
        public uint NameA;
        public uint NameB;
        public ushort Locale;
        public ushort Platform;
        public uint BlockIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockEntry
    {
        public uint FilePosition;
        public uint CompressedSize;
        public uint FileSize;
        public uint Flags;
    }

    private readonly SafeFileHandle _handle;
    private readonly long _archiveOffset;
    private readonly int _sectorSize;
    private readonly HashEntry[] _hashTable;
    private readonly BlockEntry[] _blockTable;

    public string Path { get; }

    public MpqArchive(string path)
    {
        Path = path;
        _handle = File.OpenHandle(path);

        Span<byte> header = stackalloc byte[32];
        _archiveOffset = FindHeader(header);
        _sectorSize = 512 << BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        var hashTablePos = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var blockTablePos = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        var hashTableSize = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        var blockTableSize = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);

        _hashTable = ReadTable<HashEntry>(hashTablePos, hashTableSize, MpqCrypto.Hash("(hash table)", MpqCrypto.HashFileKey));
        _blockTable = ReadTable<BlockEntry>(blockTablePos, blockTableSize, MpqCrypto.Hash("(block table)", MpqCrypto.HashFileKey));
    }

    public bool Contains(string name) => Lookup(name) == MpqLookup.Found;

    /// <summary>Distinguishes "absent" from "deleted by this archive", which hides lower-priority copies.</summary>
    public MpqLookup Lookup(string name) => FindBlock(name, out var deleted) is not null
        ? MpqLookup.Found
        : deleted ? MpqLookup.Deleted : MpqLookup.Missing;

    /// <summary>Names from the archive's internal (listfile), if present.</summary>
    public IReadOnlyList<string> ReadListFile()
    {
        var data = TryReadFile("(listfile)");
        if (data is null)
            return [];
        return Encoding.UTF8.GetString(data)
            .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public byte[]? TryReadFile(string name)
    {
        if (FindBlock(name, out _) is not { } block)
            return null;
        return ReadBlock(block, name);
    }

    private BlockEntry? FindBlock(string name, out bool deleted)
    {
        deleted = false;
        var mask = (uint)_hashTable.Length - 1;
        var start = MpqCrypto.Hash(name, MpqCrypto.HashTableOffset) & mask;
        var nameA = MpqCrypto.Hash(name, MpqCrypto.HashNameA);
        var nameB = MpqCrypto.Hash(name, MpqCrypto.HashNameB);

        BlockEntry? fallback = null;
        for (var i = start; ; i = (i + 1) & mask)
        {
            var entry = _hashTable[i];
            if (entry.BlockIndex == HashEmpty)
                break;
            if (entry.BlockIndex != HashDeleted && entry.NameA == nameA && entry.NameB == nameB
                && entry.BlockIndex < _blockTable.Length)
            {
                var block = _blockTable[entry.BlockIndex];
                if ((block.Flags & FileExists) == 0 || (block.Flags & FileDeleteMarker) != 0)
                {
                    deleted = (block.Flags & FileDeleteMarker) != 0;
                    return null;
                }
                if (entry.Locale == 0)
                    return block;
                fallback ??= block;
            }
            if (((i + 1) & mask) == start)
                break;
        }
        return fallback;
    }

    private byte[] ReadBlock(BlockEntry block, string name)
    {
        var key = 0u;
        if ((block.Flags & FileEncrypted) != 0)
        {
            var slash = name.LastIndexOfAny(['\\', '/']);
            key = MpqCrypto.Hash(name[(slash + 1)..], MpqCrypto.HashFileKey);
            if ((block.Flags & FileFixKey) != 0)
                key = (key + block.FilePosition) ^ block.FileSize;
        }

        var position = _archiveOffset + block.FilePosition;
        var output = new byte[block.FileSize];
        if (block.FileSize == 0)
            return output;

        if ((block.Flags & FileSingleUnit) != 0)
        {
            var raw = ReadRaw(position, (int)block.CompressedSize);
            if ((block.Flags & FileEncrypted) != 0)
                MpqCrypto.Decrypt(raw, key);
            DecompressSector(raw, output, block.Flags);
            return output;
        }

        if ((block.Flags & (FileCompress | FileImplode)) == 0)
        {
            var raw = ReadRaw(position, (int)block.FileSize);
            if ((block.Flags & FileEncrypted) != 0)
                for (var s = 0; s * _sectorSize < raw.Length; s++)
                    MpqCrypto.Decrypt(raw.AsSpan(s * _sectorSize, Math.Min(_sectorSize, raw.Length - s * _sectorSize)), key + (uint)s);
            return raw;
        }

        var sectorCount = (int)((block.FileSize + _sectorSize - 1) / _sectorSize);
        var offsetCount = sectorCount + 1 + ((block.Flags & FileSectorCrc) != 0 ? 1 : 0);
        var offsetBytes = ReadRaw(position, offsetCount * 4);
        var offsets = MemoryMarshal.Cast<byte, uint>(offsetBytes.AsSpan());
        if ((block.Flags & FileEncrypted) != 0)
            MpqCrypto.Decrypt(offsets, key - 1);

        var data = ReadRaw(position, (int)offsets[sectorCount]);
        for (var s = 0; s < sectorCount; s++)
        {
            var sector = data.AsSpan((int)offsets[s], (int)(offsets[s + 1] - offsets[s]));
            if ((block.Flags & FileEncrypted) != 0)
                MpqCrypto.Decrypt(sector, key + (uint)s);
            var start = s * _sectorSize;
            DecompressSector(sector, output.AsSpan(start, Math.Min(_sectorSize, output.Length - start)), block.Flags);
        }
        return output;
    }

    private static void DecompressSector(ReadOnlySpan<byte> input, Span<byte> output, uint flags)
    {
        if (input.Length >= output.Length)
        {
            input[..output.Length].CopyTo(output);
            return;
        }

        // Every archive shipped with 1.12 uses zlib only; implode, bzip2 and audio codecs are not implemented.
        if ((flags & FileImplode) != 0 || input[0] != CompressionZlib)
            throw new NotSupportedException($"MPQ compression 0x{input[0]:X2} (flags 0x{flags:X8}) is not supported.");

        using var zlib = new ZLibStream(new MemoryStream(input[1..].ToArray()), CompressionMode.Decompress);
        zlib.ReadExactly(output);
    }

    private long FindHeader(Span<byte> header)
    {
        var length = RandomAccess.GetLength(_handle);
        for (long offset = 0; offset + header.Length <= length; offset += 512)
        {
            RandomAccess.Read(_handle, header, offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) == Magic)
                return offset;
        }
        throw new InvalidDataException($"{Path} is not an MPQ archive.");
    }

    private T[] ReadTable<T>(uint position, int count, uint key) where T : struct
    {
        var raw = ReadRaw(_archiveOffset + position, count * Marshal.SizeOf<T>());
        MpqCrypto.Decrypt(raw, key);
        return MemoryMarshal.Cast<byte, T>(raw).ToArray();
    }

    private byte[] ReadRaw(long position, int count)
    {
        var buffer = new byte[count];
        var read = RandomAccess.Read(_handle, buffer, position);
        if (read != count)
            throw new EndOfStreamException($"Unexpected end of {Path} at {position}.");
        return buffer;
    }

    public void Dispose() => _handle.Dispose();
}
