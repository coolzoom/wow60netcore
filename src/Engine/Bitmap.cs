namespace Engine;

public static class Bitmap
{
    /// <summary>Writes tightly packed bottom-up BGR rows (OpenGL read order) as a 24-bit BMP.</summary>
    public static void WriteBgr24(string path, int width, int height, ReadOnlySpan<byte> pixels)
    {
        var rowSize = width * 3;
        var paddedRow = (rowSize + 3) & ~3;
        var imageSize = paddedRow * height;

        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + imageSize);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(imageSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        Span<byte> padding = stackalloc byte[paddedRow - rowSize];
        for (var y = 0; y < height; y++)
        {
            writer.Write(pixels.Slice(y * rowSize, rowSize));
            writer.Write(padding);
        }
    }
}
