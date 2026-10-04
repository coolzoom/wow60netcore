using Silk.NET.OpenGL;

namespace Engine.Rendering;

public sealed class Texture : IDisposable
{
    private readonly GL _gl;

    public uint Handle { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Approximate GPU memory, including the mip chain.</summary>
    public long Bytes { get; }

    /// <param name="storage">GPU-side format; the RGBA8 source is converted on upload (e.g. Rgba4 halves memory).</param>
    public unsafe Texture(GL gl, int width, int height, ReadOnlySpan<byte> rgba, bool repeat = true, bool mipmaps = true,
        InternalFormat storage = InternalFormat.Rgba8)
    {
        _gl = gl;
        Width = width;
        Height = height;
        Bytes = (long)width * height * (storage == InternalFormat.Rgba4 ? 2 : 4) * (mipmaps ? 4 : 3) / 3;
        Handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* pixels = rgba)
            gl.TexImage2D(TextureTarget.Texture2D, 0, storage, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        var wrap = (int)(repeat ? GLEnum.Repeat : GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, wrap);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, wrap);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(mipmaps ? GLEnum.LinearMipmapLinear : GLEnum.Linear));
        if (mipmaps)
            gl.GenerateMipmap(TextureTarget.Texture2D);
    }

    public void Bind(int unit)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
    }

    public void Dispose() => _gl.DeleteTexture(Handle);
}
