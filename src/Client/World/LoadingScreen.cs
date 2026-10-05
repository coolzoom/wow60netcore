using System.Numerics;
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

    /// <summary>Draws the picture letterboxed to 4:3 with a progress bar near the bottom.</summary>
    public static void Draw(AssetCache assets, string? image, Vector2 screen, float progress, string caption)
    {
        var draw = ImGui.GetForegroundDrawList();
        draw.AddRectFilled(Vector2.Zero, screen, 0xFF000000);
        var height = screen.Y;
        var width = MathF.Min(screen.X, height * 4f / 3f);
        var origin = new Vector2((screen.X - width) / 2, 0);
        if (image is not null && assets.Texture(image) is { } texture)
            draw.AddImage((IntPtr)texture.Handle, origin, origin + new Vector2(width, height));

        var barSize = new Vector2(width * 0.6f, 18);
        var barOrigin = new Vector2((screen.X - barSize.X) / 2, height * 0.9f);
        draw.AddRectFilled(barOrigin - Vector2.One * 2, barOrigin + barSize + Vector2.One * 2, 0xC0000000, 4);
        draw.AddRectFilled(barOrigin, barOrigin + new Vector2(barSize.X * Math.Clamp(progress, 0, 1), barSize.Y), 0xFF20A0E0, 3);
        var text = ImGui.CalcTextSize(caption);
        draw.AddText(new Vector2((screen.X - text.X) / 2, barOrigin.Y - text.Y - 6), 0xFFFFFFFF, caption);
    }
}
