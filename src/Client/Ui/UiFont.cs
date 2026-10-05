using Formats.Mpq;
using ImGuiNET;

namespace Client.Ui;

public static class UiFont
{
    private static readonly string[] Candidates = ["Fonts\\FZXHJW.TTF", "Fonts\\FZXHLJW.ttf", "Fonts\\FRIZQT__.TTF"];

    /// <summary>Extracts the client's own CJK UI font from fonts.MPQ so ImGui can render Chinese map and file names.</summary>
    /// <param name="allChinese">All CJK ideographs (chat and names can use any), not just the ~2500 common ones.</param>
    public static ImGuiFontConfig? Create(MpqFileSystem files, bool allChinese = false)
    {
        foreach (var name in Candidates)
        {
            if (files.TryRead(name) is not { } data)
                continue;
            var path = Path.Combine(Path.GetTempPath(), "NetCoreClient-" + Path.GetFileName(name));
            File.WriteAllBytes(path, data);
            return new ImGuiFontConfig(path, 16, io => allChinese ? io.Fonts.GetGlyphRangesChineseFull() : io.Fonts.GetGlyphRangesChineseSimplifiedCommon());
        }
        return null;
    }
}
