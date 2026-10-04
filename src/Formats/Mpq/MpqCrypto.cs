namespace Formats.Mpq;

/// <summary>Storm hashing and block cipher used by MPQ archives.</summary>
public static class MpqCrypto
{
    public const uint HashTableOffset = 0;
    public const uint HashNameA = 1;
    public const uint HashNameB = 2;
    public const uint HashFileKey = 3;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[0x500];
        uint seed = 0x00100001;
        for (var index1 = 0; index1 < 0x100; index1++)
        {
            var index2 = index1;
            for (var i = 0; i < 5; i++, index2 += 0x100)
            {
                seed = (seed * 125 + 3) % 0x2AAAAB;
                var high = (seed & 0xFFFF) << 16;
                seed = (seed * 125 + 3) % 0x2AAAAB;
                table[index2] = high | (seed & 0xFFFF);
            }
        }
        return table;
    }

    public static uint Hash(string text, uint hashType)
    {
        uint seed1 = 0x7FED7FED, seed2 = 0xEEEEEEEE;
        foreach (var c in text)
        {
            uint ch = char.ToUpperInvariant(c == '/' ? '\\' : c);
            seed1 = Table[hashType * 0x100 + ch] ^ (seed1 + seed2);
            seed2 = ch + seed1 + seed2 + (seed2 << 5) + 3;
        }
        return seed1;
    }

    public static void Decrypt(Span<uint> data, uint key)
    {
        uint seed = 0xEEEEEEEE;
        for (var i = 0; i < data.Length; i++)
        {
            seed += Table[0x400 + (key & 0xFF)];
            var value = data[i] ^ (key + seed);
            key = ((~key << 21) + 0x11111111) | (key >> 11);
            seed = value + seed + (seed << 5) + 3;
            data[i] = value;
        }
    }

    public static void Decrypt(Span<byte> data, uint key) =>
        Decrypt(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(data[..(data.Length & ~3)]), key);
}
