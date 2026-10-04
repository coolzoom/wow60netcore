using System.Numerics;
using MoonSharp.Interpreter;

namespace FrameXml.Objects;

public abstract class UiObject(UiScreen ui)
{
    private Table? _table;

    public UiScreen Ui { get; } = ui;
    public string? Name { get; internal set; }
    /// <summary>Type name reported by GetObjectType and used to pick the Lua method table.</summary>
    public abstract string ObjectType { get; }
    /// <summary>The Lua table scripts see for this object (created on first use).</summary>
    public Table Table => _table ??= Ui.Bindings.Bind(this);

    public override string ToString() => $"{ObjectType} {Name ?? "<unnamed>"}";
}

/// <param name="RelativeTo">Null anchors to the parent, or the screen for parentless regions.</param>
public sealed record Anchor(FramePoint Point, Region? RelativeTo, FramePoint RelativePoint, float X, float Y);

/// <summary>A positioned rectangle (CLayoutFrame). Rects are derived lazily from anchors and size.</summary>
public abstract class Region(UiScreen ui) : UiObject(ui)
{
    private readonly Anchor?[] _points = new Anchor?[9];
    private float _width, _height;
    private UiRect? _rect;
    private int _rectVersion = -1;
    private bool _resolving;

    public Frame? Parent { get; internal set; }
    public bool Shown { get; protected set; } = true;
    public IEnumerable<Anchor> Points => _points.OfType<Anchor>();
    public bool HasPoints => _points.Any(p => p is not null);

    public float Width
    {
        get => _width;
        set { _width = value; Ui.InvalidateLayout(); }
    }

    public float Height
    {
        get => _height;
        set { _height = value; Ui.InvalidateLayout(); }
    }

    public virtual bool IsVisible => Shown && (Parent?.IsVisible ?? true);

    /// <summary>Effective alpha including all ancestors.</summary>
    public virtual float EffectiveAlpha => Parent?.EffectiveAlpha ?? 1f;

    /// <summary>Size used along an axis that has only one anchor and no explicit size (e.g. a font string's text).</summary>
    protected virtual Vector2 IntrinsicSize => Vector2.Zero;

    public void SetPoint(FramePoint point, Region? relativeTo, FramePoint relativePoint, float x, float y)
    {
        _points[(int)point] = new Anchor(point, relativeTo == Parent ? null : relativeTo, relativePoint, x, y);
        Ui.InvalidateLayout();
    }

    public void ClearAllPoints()
    {
        Array.Clear(_points);
        Ui.InvalidateLayout();
    }

    public void SetAllPoints(Region? target)
    {
        ClearAllPoints();
        SetPoint(FramePoint.TopLeft, target, FramePoint.TopLeft, 0, 0);
        SetPoint(FramePoint.BottomRight, target, FramePoint.BottomRight, 0, 0);
    }

    public float GetWidth() => Rect?.Width ?? Width;
    public float GetHeight() => Rect?.Height ?? Height;

    /// <summary>Screen rectangle, or null when the anchors don't determine one.</summary>
    public UiRect? Rect
    {
        get
        {
            if (_rectVersion == Ui.LayoutVersion)
                return _rect;
            if (_resolving)
                return null; // anchor cycle
            _resolving = true;
            try
            {
                _rect = Resolve();
                _rectVersion = Ui.LayoutVersion;
                return _rect;
            }
            finally
            {
                _resolving = false;
            }
        }
    }

    private UiRect? Resolve()
    {
        float? left = null, right = null, top = null, bottom = null, centerX = null, centerY = null;
        foreach (var anchor in Points)
        {
            var target = anchor.RelativeTo ?? (Region?)Parent;
            var targetRect = target is null ? Ui.ScreenRect : target.Rect;
            if (targetRect is not { } r)
                continue;
            var p = r.Point(anchor.RelativePoint) + new Vector2(anchor.X, anchor.Y);
            switch (anchor.Point)
            {
                case FramePoint.TopLeft: left = p.X; top = p.Y; break;
                case FramePoint.Top: centerX = p.X; top = p.Y; break;
                case FramePoint.TopRight: right = p.X; top = p.Y; break;
                case FramePoint.Left: left = p.X; centerY = p.Y; break;
                case FramePoint.Center: centerX = p.X; centerY = p.Y; break;
                case FramePoint.Right: right = p.X; centerY = p.Y; break;
                case FramePoint.BottomLeft: left = p.X; bottom = p.Y; break;
                case FramePoint.Bottom: centerX = p.X; bottom = p.Y; break;
                case FramePoint.BottomRight: right = p.X; bottom = p.Y; break;
            }
        }

        var intrinsic = (left is null || right is null) && Width <= 0 || (top is null || bottom is null) && Height <= 0
            ? IntrinsicSize
            : Vector2.Zero;
        if (!Span(ref left, ref right, centerX, Width > 0 ? Width : intrinsic.X) ||
            !Span(ref bottom, ref top, centerY, Height > 0 ? Height : intrinsic.Y))
            return null;
        return new UiRect(left!.Value, bottom!.Value, right!.Value, top!.Value);
    }

    private static bool Span(ref float? min, ref float? max, float? center, float size)
    {
        if (min is not null && max is not null)
            return true;
        if (min is not null && center is not null) max = 2 * center - min;
        else if (max is not null && center is not null) min = 2 * center - max;
        else if (min is not null) max = min + size;
        else if (max is not null) min = max - size;
        else if (center is not null) (min, max) = (center - size / 2, center + size / 2);
        else return false;
        return true;
    }

    /// <summary>Applies Size, Anchors and setAllPoints (FUN_00767800).</summary>
    internal void LoadLayout(System.Xml.Linq.XElement node, UiLoader loader)
    {
        if (node.Child("Size") is { } size && loader.Dimension(size) is { } dimension)
        {
            Width = dimension.X;
            Height = dimension.Y;
        }

        var setAllPoints = UiEnums.Bool(node.Attr("setAllPoints"));
        var anchors = node.Child("Anchors");
        if (anchors is null)
        {
            if (setAllPoints)
                SetAllPoints(null);
            return;
        }
        if (setAllPoints)
            loader.Log.Warning("SETALLPOINTS set to true in frame with anchors (ignored)");

        foreach (var anchor in anchors.Elements())
        {
            if (!UiEnums.TryPoint(anchor.Attr("point"), out var point))
            {
                loader.Log.Warning($"Invalid anchor point in frame: {anchor.Attr("point")}");
                continue;
            }
            var relativePoint = point;
            if (anchor.Attr("relativePoint") is { Length: > 0 } rp && !UiEnums.TryPoint(rp, out relativePoint))
            {
                loader.Log.Warning($"Invalid anchor point in frame: {rp}");
                continue;
            }
            Region? relative = null;
            if (anchor.Attr("relativeTo") is { Length: > 0 } relativeName)
            {
                relative = Ui.FindRegion(Ui.ExpandName(relativeName, Parent));
                if (relative is null)
                {
                    loader.Log.Warning($"Couldn't find relative frame: {relativeName}");
                    continue;
                }
                if (relative == this)
                {
                    loader.Log.Warning($"Frame anchored to itself: {relativeName}");
                    continue;
                }
            }
            var offset = anchor.Child("Offset") is { } o ? loader.Dimension(o) ?? Vector2.Zero : Vector2.Zero;
            SetPoint(point, relative, relativePoint, offset.X, offset.Y);
        }
    }
}

/// <summary>Texture or font string drawn in one of its frame's layers.</summary>
public abstract class LayeredRegion(UiScreen ui) : Region(ui)
{
    public DrawLayer Layer { get; internal set; } = DrawLayer.Artwork;
    public Color4 VertexColor { get; set; } = Color4.White;
    public float Alpha { get; set; } = 1f;

    public override float EffectiveAlpha => Alpha * base.EffectiveAlpha;

    public void Show() => Shown = true;
    public void Hide() => Shown = false;

    /// <summary>Regions without any anchor fill their frame (FUN_007701c0).</summary>
    internal void DefaultAnchors()
    {
        if (!HasPoints && Parent is not null)
            SetAllPoints(null);
    }
}

public sealed class Texture(UiScreen ui) : LayeredRegion(ui)
{
    /// <summary>Corner UVs in client order: UL, LL, UR, LR.</summary>
    public Vector2[] TexCoords { get; } = [new(0, 0), new(0, 1), new(1, 0), new(1, 1)];
    public string? File { get; private set; }
    /// <summary>Solid fill instead of an image (SetTexture(r, g, b, a) or a missing file).</summary>
    public Color4? SolidColor { get; private set; }
    public BlendMode Blend { get; set; } = BlendMode.Blend;
    public bool Desaturated { get; set; }

    public override string ObjectType => "Texture";

    /// <summary>Returns false when the file doesn't exist; the client then fills the region green.</summary>
    public bool SetTexture(string? file)
    {
        SolidColor = null;
        if (string.IsNullOrEmpty(file))
        {
            File = null;
            return true;
        }
        File = Ui.ResolveTexture(file);
        if (File is not null)
            return true;
        SolidColor = new Color4(0, 1, 0);
        return false;
    }

    public void SetColorTexture(Color4 color)
    {
        File = null;
        SolidColor = color;
    }

    public void SetTexCoord(float left, float right, float top, float bottom)
    {
        TexCoords[0] = new(left, top);
        TexCoords[1] = new(left, bottom);
        TexCoords[2] = new(right, top);
        TexCoords[3] = new(right, bottom);
    }

    public void SetTexCoord(ReadOnlySpan<float> corners)
    {
        for (var i = 0; i < 4; i++)
            TexCoords[i] = new(corners[i * 2], corners[i * 2 + 1]);
    }
}

/// <summary>Font face, size and style shared by Font objects, font strings, buttons and edit boxes.</summary>
public sealed class FontInfo
{
    public string? File { get; set; }
    public float Height { get; set; }
    public string Flags { get; set; } = "";
    public Color4 Color { get; set; } = Color4.White;
    public Color4? ShadowColor { get; set; }
    public Vector2 ShadowOffset { get; set; }
    public string JustifyH { get; set; } = "CENTER";
    public string JustifyV { get; set; } = "MIDDLE";
    public float Spacing { get; set; }

    public FontInfo Clone() => (FontInfo)MemberwiseClone();
}

/// <summary>Named &lt;Font&gt; definition, also visible to Lua as a global.</summary>
public sealed class FontObject(UiScreen ui) : UiObject(ui)
{
    public FontInfo Font { get; set; } = new();
    public override string ObjectType => "Font";
}

public sealed class FontString(UiScreen ui) : LayeredRegion(ui)
{
    private string _text = "";

    public FontInfo Font { get; set; } = new();
    public FontObject? FontObject { get; set; }
    public bool NonSpaceWrap { get; set; }
    public int MaxLines { get; set; }
    public override string ObjectType => "FontString";

    public string Text
    {
        get => _text;
        set { _text = value; Ui.InvalidateLayout(); }
    }

    public float StringWidth => Ui.MeasureText(Font, Text);

    protected override Vector2 IntrinsicSize => new(StringWidth, Font.Height);

    public void SetFontObject(FontObject? font)
    {
        FontObject = font;
        if (font is not null)
            Font = font.Font.Clone();
        Ui.InvalidateLayout();
    }
}
