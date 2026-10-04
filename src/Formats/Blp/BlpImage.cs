namespace Formats.Blp;

public sealed record RgbaImage(int Width, int Height, byte[] Pixels);

/// <summary>Decodes the top mip level of a BLP2 texture to RGBA.</summary>
public static class BlpImage
{
    private const uint Magic = 0x32504C42; // "BLP2"
    private const int PaletteOffset = 148;

    public static RgbaImage Decode(byte[] data)
    {
        if (data.Length < PaletteOffset + 1024 || data.U32(0) != Magic)
            throw new InvalidDataException("Not a BLP2 texture.");
        if (data.U32(4) != 1)
            throw new NotSupportedException("JPEG-compressed BLP textures are not supported.");

        var encoding = data[8];
        var alphaDepth = data[9];
        var alphaEncoding = data[10];
        var width = (int)data.U32(12);
        var height = (int)data.U32(16);
        var mipOffset = (int)data.U32(20);
        var mipSize = (int)data.U32(84);
        var mip = data.AsSpan(mipOffset, Math.Min(mipSize, data.Length - mipOffset));

        var pixels = encoding switch
        {
            1 => DecodePalette(data.AsSpan(PaletteOffset, 1024), mip, width, height, alphaDepth),
            2 => alphaEncoding switch
            {
                0 => Dxt.DecodeDxt1(mip, width, height, alphaDepth > 0),
                1 => Dxt.DecodeDxt3(mip, width, height),
                7 => Dxt.DecodeDxt5(mip, width, height),
                _ => throw new NotSupportedException($"BLP DXT alpha encoding {alphaEncoding} is not supported."),
            },
            3 => DecodeBgra(mip, width, height),
            _ => throw new NotSupportedException($"BLP encoding {encoding} is not supported."),
        };
        return new RgbaImage(width, height, pixels);
    }

    private static byte[] DecodePalette(ReadOnlySpan<byte> palette, ReadOnlySpan<byte> mip, int width, int height, int alphaDepth)
    {
        var count = width * height;
        var pixels = new byte[count * 4];
        var alpha = mip[count..];
        for (var i = 0; i < count; i++)
        {
            var entry = mip[i] * 4;
            pixels[i * 4] = palette[entry + 2];
            pixels[i * 4 + 1] = palette[entry + 1];
            pixels[i * 4 + 2] = palette[entry];
            pixels[i * 4 + 3] = alphaDepth switch
            {
                1 => (alpha[i >> 3] >> (i & 7) & 1) != 0 ? (byte)255 : (byte)0,
                4 => (byte)(((alpha[i >> 1] >> ((i & 1) * 4)) & 0xF) * 17),
                8 => alpha[i],
                _ => 255,
            };
        }
        return pixels;
    }

    private static byte[] DecodeBgra(ReadOnlySpan<byte> mip, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = mip[i * 4 + 2];
            pixels[i * 4 + 1] = mip[i * 4 + 1];
            pixels[i * 4 + 2] = mip[i * 4];
            pixels[i * 4 + 3] = mip[i * 4 + 3];
        }
        return pixels;
    }
}
