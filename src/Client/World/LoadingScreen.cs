using System.Numerics;
using Formats.Blp;
using Formats.Dbc;
using Formats.Mpq;
using ImGuiNET;

namespace Client.World;

/// <summary>The full-screen picture and progress bar shown while a map streams in (Map.dbc → LoadingScreens.dbc).</summary>
public sealed class LoadingScreen
{
    private const int MapLoadingScreenField = 38;
    private readonly Dictionary<int, string> _byMap = [];

    public LoadingScreen(MpqFileSystem files)
    {
        var screens = new DbcFile(files.Read("DBFilesClient\\LoadingScreens.dbc"));
        var paths = Enumerable.Range(0, screens.RecordCount).ToDictionary(r => screens.GetInt(r, 0), r => screens.GetString(r, 2));
        var maps = new DbcFile(files.Read("DBFilesClient\\Map.dbc"));
        for (var r = 0; r < maps.RecordCount; r++)
            if (paths.TryGetValue(maps.GetInt(r, MapLoadingScreenField), out var path) && path.Length > 0)
                _byMap[maps.GetInt(r, 0)] = path;
    }

    public string? ImageFor(int mapId) => _byMap.GetValueOrDefault(mapId);

    private const string Bar = "Interface\\Glues\\LoadingBar\\Loading-Bar";
    /// <summary>The border texture (512x64) and the hollow inside it the bar fills, in texture pixels.</summary>
    private static readonly Vector2 BorderSize = new(512, 64), InnerMin = new(36, 21), InnerMax = new(476, 42);
    /// <summary>The glow (512x128) is a comet whose bright head, near its right end, rides the fill's leading edge.</summary>
    private static readonly Vector2 GlowSize = new(512, 128);
    private const float GlowHead = 490;

    /// <summary>
    /// Draws the picture letterboxed to 4:3 and the client's loading bar near the bottom: background, blue fill
    /// with the additive glow at its edge, and the ornate border over them, sized as at 1024x768.
    /// </summary>
    public static void Draw(AssetCache assets, string? image, Vector2 screen, float progress)
    {
        var draw = ImGui.GetForegroundDrawList();
        draw.AddRectFilled(Vector2.Zero, screen, 0xFF000000);
        var height = screen.Y;
        var width = MathF.Min(screen.X, height * 4f / 3f);
        var origin = new Vector2((screen.X - width) / 2, 0);
        if (image is not null && assets.Texture(image) is { } picture)
            draw.AddImage((IntPtr)picture.Handle, origin, origin + new Vector2(width, height));

        var scale = width / 1024f;
        var border = new Vector2((screen.X - BorderSize.X * scale) / 2, height * 0.89f - BorderSize.Y * scale / 2);
        var inner = (Min: border + InnerMin * scale, Max: border + InnerMax * scale);
        var fillEnd = inner.Min.X + (inner.Max.X - inner.Min.X) * Math.Clamp(progress, 0, 1);

        if (assets.Texture(Bar + "Background.blp") is { } background)
            draw.AddImage((IntPtr)background.Handle, inner.Min, inner.Max);
        if (fillEnd > inner.Min.X && assets.Texture(Bar + "Fill.blp") is { } fill)
            draw.AddImage((IntPtr)fill.Handle, inner.Min, new Vector2(fillEnd, inner.Max.Y));
        if (fillEnd > inner.Min.X && Additive(assets, Bar + "Glow.blp") is { } glow)
        {
            var center = (inner.Min.Y + inner.Max.Y) / 2;
            var glowMin = new Vector2(fillEnd - GlowHead * scale, center - GlowSize.Y * scale / 2);
            draw.PushClipRect(new Vector2(inner.Min.X, 0), new Vector2(fillEnd + (GlowSize.X - GlowHead) * scale, screen.Y), true);
            draw.AddImage((IntPtr)glow.Handle, glowMin, glowMin + GlowSize * scale);
            draw.PopClipRect();
        }
        if (assets.Texture(Bar + "Border.blp") is { } frame)
            draw.AddImage((IntPtr)frame.Handle, border, border + BorderSize * scale);
    }

    /// <summary>An additively blended texture, converted for ImGui's alpha blending.</summary>
    private static Engine.Rendering.Texture? Additive(AssetCache assets, string name) =>
        assets.Texture("@add:" + name, () => assets.Image(name) is { } image
            ? new RgbaImage(image.Width, image.Height, Client.Ui.GlueRenderer.AdditiveToAlpha(image.Pixels))
            : null);
}
