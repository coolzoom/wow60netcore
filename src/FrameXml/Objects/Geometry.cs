using System.Globalization;
using System.Numerics;

namespace FrameXml.Objects;

public enum FramePoint { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

/// <summary>Draw layers in the order the client renders them; HIGHLIGHT only shows while the mouse is over the frame.</summary>
public enum DrawLayer { Background, Border, Artwork, Overlay, Highlight }

/// <summary>Strata from the client's table at 0x8119f8 (BACKGROUND = 1 ... TOOLTIP = 8).</summary>
public enum FrameStrata { Parent, Background, Low, Medium, High, Dialog, Fullscreen, FullscreenDialog, Tooltip }

public enum BlendMode { Disable, Blend, AlphaKey, Add, Mod }

/// <summary>Rectangle in UI units: origin bottom-left, y up, screen height 768.</summary>
public readonly record struct UiRect(float Left, float Bottom, float Right, float Top)
{
    public float Width => Right - Left;
    public float Height => Top - Bottom;

    public Vector2 Point(FramePoint point) => point switch
    {
        FramePoint.TopLeft => new(Left, Top),
        FramePoint.Top => new((Left + Right) / 2, Top),
        FramePoint.TopRight => new(Right, Top),
        FramePoint.Left => new(Left, (Top + Bottom) / 2),
        FramePoint.Center => new((Left + Right) / 2, (Top + Bottom) / 2),
        FramePoint.Right => new(Right, (Top + Bottom) / 2),
        FramePoint.BottomLeft => new(Left, Bottom),
        FramePoint.Bottom => new((Left + Right) / 2, Bottom),
        _ => new(Right, Bottom),
    };

    public bool Contains(Vector2 p) => p.X >= Left && p.X <= Right && p.Y >= Bottom && p.Y <= Top;
}

public record struct Color4(float R, float G, float B, float A = 1f)
{
    public static readonly Color4 White = new(1, 1, 1);
    public static readonly Color4 Black = new(0, 0, 0);

    public Color4 WithAlpha(float alpha) => this with { A = A * alpha };
}

public static class UiEnums
{
    private static readonly Dictionary<string, FramePoint> Points = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TOPLEFT"] = FramePoint.TopLeft, ["TOP"] = FramePoint.Top, ["TOPRIGHT"] = FramePoint.TopRight,
        ["LEFT"] = FramePoint.Left, ["CENTER"] = FramePoint.Center, ["RIGHT"] = FramePoint.Right,
        ["BOTTOMLEFT"] = FramePoint.BottomLeft, ["BOTTOM"] = FramePoint.Bottom, ["BOTTOMRIGHT"] = FramePoint.BottomRight,
    };

    public static bool TryPoint(string? text, out FramePoint point) => Points.TryGetValue(text ?? "", out point);

    public static string Name(FramePoint point) => Points.First(p => p.Value == point).Key;

    public static bool TryLayer(string? text, out DrawLayer layer) =>
        Enum.TryParse(text, true, out layer) && Enum.IsDefined(layer);

    public static string Name(DrawLayer layer) => layer.ToString().ToUpperInvariant();

    public static bool TryStrata(string? text, out FrameStrata strata)
    {
        strata = (text ?? "").ToUpperInvariant() switch
        {
            "PARENT" => FrameStrata.Parent,
            "BACKGROUND" => FrameStrata.Background,
            "LOW" => FrameStrata.Low,
            "MEDIUM" => FrameStrata.Medium,
            "HIGH" => FrameStrata.High,
            "DIALOG" => FrameStrata.Dialog,
            "FULLSCREEN" => FrameStrata.Fullscreen,
            "FULLSCREEN_DIALOG" => FrameStrata.FullscreenDialog,
            "TOOLTIP" => FrameStrata.Tooltip,
            _ => (FrameStrata)(-1),
        };
        return strata >= 0;
    }

    public static string Name(FrameStrata strata) => strata switch
    {
        FrameStrata.FullscreenDialog => "FULLSCREEN_DIALOG",
        _ => strata.ToString().ToUpperInvariant(),
    };

    public static bool TryBlend(string? text, out BlendMode mode) =>
        Enum.TryParse(text?.Replace("_", ""), true, out mode) && Enum.IsDefined(mode);

    /// <summary>Boolean attribute parsing from FUN_006f1b30: 1-9/T/Y are true, 0/F/N false, then "true"/"enabled".</summary>
    public static bool Bool(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        return char.ToUpperInvariant(text[0]) switch
        {
            '0' or 'F' or 'N' => false,
            >= '1' and <= '9' or 'T' or 'Y' => true,
            _ => text.Equals("enabled", StringComparison.OrdinalIgnoreCase),
        };
    }

    public static float Float(string? text, float fallback = 0f) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
