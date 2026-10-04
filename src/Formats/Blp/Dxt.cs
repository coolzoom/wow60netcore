using System.Buffers.Binary;

namespace Formats.Blp;

/// <summary>Software S3TC (DXT1/3/5) block decoders producing RGBA8.</summary>
public static class Dxt
{
    public static byte[] DecodeDxt1(ReadOnlySpan<byte> data, int width, int height, bool punchThroughAlpha) =>
        Decode(data, width, height, 8, (block, rgba) => DecodeColorBlock(block, rgba, allowThreeColor: true, punchThroughAlpha));

    public static byte[] DecodeDxt3(ReadOnlySpan<byte> data, int width, int height) =>
        Decode(data, width, height, 16, (block, rgba) =>
        {
            DecodeColorBlock(block[8..], rgba, allowThreeColor: false, punchThroughAlpha: false);
            for (var i = 0; i < 16; i++)
                rgba[i * 4 + 3] = (byte)(((block[i >> 1] >> ((i & 1) * 4)) & 0xF) * 17);
        });

    public static byte[] DecodeDxt5(ReadOnlySpan<byte> data, int width, int height) =>
        Decode(data, width, height, 16, (block, rgba) =>
        {
            DecodeColorBlock(block[8..], rgba, allowThreeColor: false, punchThroughAlpha: false);
            int a0 = block[0], a1 = block[1];
            Span<byte> alphas = stackalloc byte[8];
            alphas[0] = (byte)a0;
            alphas[1] = (byte)a1;
            for (var i = 2; i < 8; i++)
                alphas[i] = a0 > a1
                    ? (byte)(((8 - i) * a0 + (i - 1) * a1) / 7)
                    : i < 6 ? (byte)(((6 - i) * a0 + (i - 1) * a1) / 5) : i == 6 ? (byte)0 : (byte)255;

            ulong bits = 0;
            for (var i = 0; i < 6; i++)
                bits |= (ulong)block[2 + i] << (8 * i);
            for (var i = 0; i < 16; i++)
                rgba[i * 4 + 3] = alphas[(int)((bits >> (3 * i)) & 7)];
        });

    private delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<byte> rgba);

    private static byte[] Decode(ReadOnlySpan<byte> data, int width, int height, int blockSize, BlockDecoder decodeBlock)
    {
        var pixels = new byte[width * height * 4];
        Span<byte> block = stackalloc byte[64];
        var blocksX = (width + 3) / 4;
        var blocksY = (height + 3) / 4;

        for (var by = 0; by < blocksY; by++)
        for (var bx = 0; bx < blocksX; bx++)
        {
            var offset = (by * blocksX + bx) * blockSize;
            if (offset + blockSize > data.Length)
                return pixels;
            decodeBlock(data.Slice(offset, blockSize), block);

            for (var py = 0; py < 4; py++)
            for (var px = 0; px < 4; px++)
            {
                int x = bx * 4 + px, y = by * 4 + py;
                if (x < width && y < height)
                    block.Slice((py * 4 + px) * 4, 4).CopyTo(pixels.AsSpan((y * width + x) * 4));
            }
        }
        return pixels;
    }

    private static void DecodeColorBlock(ReadOnlySpan<byte> block, Span<byte> rgba, bool allowThreeColor, bool punchThroughAlpha)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);

        Span<int> palette = stackalloc int[16];
        Expand565(c0, palette[..4]);
        Expand565(c1, palette[4..8]);
        var fourColor = c0 > c1 || !allowThreeColor;
        for (var channel = 0; channel < 3; channel++)
        {
            int a = palette[channel], b = palette[4 + channel];
            palette[8 + channel] = fourColor ? (2 * a + b) / 3 : (a + b) / 2;
            palette[12 + channel] = fourColor ? (a + 2 * b) / 3 : 0;
        }
        palette[3] = palette[7] = palette[11] = 255;
        palette[15] = fourColor || !punchThroughAlpha ? 255 : 0;

        for (var i = 0; i < 16; i++)
        {
            var index = (int)((indices >> (2 * i)) & 3) * 4;
            for (var channel = 0; channel < 4; channel++)
                rgba[i * 4 + channel] = (byte)palette[index + channel];
        }
    }

    private static void Expand565(ushort color, Span<int> rgb)
    {
        int r = (color >> 11) & 31, g = (color >> 5) & 63, b = color & 31;
        rgb[0] = (r << 3) | (r >> 2);
        rgb[1] = (g << 2) | (g >> 4);
        rgb[2] = (b << 3) | (b >> 2);
    }
}
