using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FrameXml.Objects;

/// <summary>3D model frames. The model itself isn't rendered yet; scripts can still drive their state.</summary>
public class Model(UiScreen ui) : Frame(ui)
{
    public string? ModelFile { get; set; }
    public Color4? FogColor { get; set; }
    public float FogNear { get; set; }
    public float FogFar { get; set; } = 1f;
    public float ModelScale { get; set; } = 1f;
    public int Sequence { get; set; }
    public int Camera { get; set; }
    public float Facing { get; set; }

    public override string ObjectType => "Model";
    protected override IEnumerable<string> ScriptNames => base.ScriptNames.Concat(["OnUpdateModel", "OnAnimFinished"]);

    internal override void LoadXml(XElement node, UiLoader loader)
    {
        base.LoadXml(node, loader);
        if (node.Attr("file") is { Length: > 0 } file) ModelFile = file;
        if (node.Attr("fogNear") is { Length: > 0 } near) FogNear = UiEnums.Float(near);
        if (node.Attr("fogFar") is { Length: > 0 } far) FogFar = UiEnums.Float(far);
        if (node.Attr("scale") is { Length: > 0 } scale) ModelScale = UiEnums.Float(scale, 1f);
        if (node.Child("FogColor") is { } fog) FogColor = loader.Color(fog);
    }
}

/// <summary>Glue-only model frame with the login screen's fog and FFX effects (registered by FUN_0046a400).</summary>
public sealed class ModelFfx(UiScreen ui) : Model(ui)
{
    public override string ObjectType => "ModelFFX";
}

public class PlayerModel(UiScreen ui) : Model(ui)
{
    public override string ObjectType => "PlayerModel";
}

public sealed class DressUpModel(UiScreen ui) : PlayerModel(ui)
{
    public override string ObjectType => "DressUpModel";
}

public sealed class TabardModel(UiScreen ui) : PlayerModel(ui)
{
    public override string ObjectType => "TabardModel";
}

public sealed record MessageLine(string Text, Color4 Color, double Time);

public class MessageFrame(UiScreen ui) : Frame(ui)
{
    public FontString FontString { get; } = new(ui);
    public List<MessageLine> Messages { get; } = [];
    public int MaxLines { get; set; } = 120;
    public bool Fading { get; set; } = true;
    /// <summary>Seconds a message stays fully visible, then seconds it takes to fade out.</summary>
    public float TimeVisible { get; set; } = 10f;
    public float FadeDuration { get; set; } = 3f;
    /// <summary>Lines scrolled up from the newest message (scrolling message frames).</summary>
    public int ScrollOffset { get; set; }

    public override string ObjectType => "MessageFrame";

    public void AddMessage(string text, Color4 color)
    {
        Messages.Add(new MessageLine(text, color, Ui.Time));
        if (Messages.Count > MaxLines)
            Messages.RemoveAt(0);
    }

    /// <summary>Opacity of a message now: 1 while fresh, fading to 0 once it is older than <see cref="TimeVisible"/>.</summary>
    public float MessageAlpha(MessageLine line)
    {
        if (!Fading || ScrollOffset > 0)
            return 1f;
        var age = (float)(Ui.Time - line.Time) - TimeVisible;
        return age <= 0 ? 1f : FadeDuration <= 0 ? 0f : Math.Clamp(1f - age / FadeDuration, 0f, 1f);
    }

    internal override void LoadXml(XElement node, UiLoader loader)
    {
        base.LoadXml(node, loader);
        FontString.Parent = this;
        if (node.Child("FontString") is { } font)
            loader.LoadFontString(FontString, font);
        if (node.Attr("maxLines") is { Length: > 0 } lines && int.TryParse(lines, out var max))
            MaxLines = max;
        if (node.Attr("fade") is { Length: > 0 } fade)
            Fading = UiEnums.Bool(fade);
        if (node.Attr("displayDuration") is { Length: > 0 } duration)
            TimeVisible = UiEnums.Float(duration, TimeVisible);
        if (node.Attr("fadeDuration") is { Length: > 0 } fadeDuration)
            FadeDuration = UiEnums.Float(fadeDuration, FadeDuration);
    }
}

public sealed class ScrollingMessageFrame(UiScreen ui) : MessageFrame(ui)
{
    public override string ObjectType => "ScrollingMessageFrame";
    protected override IEnumerable<string> ScriptNames =>
        base.ScriptNames.Concat(["OnMessageScrollChanged", "OnHyperlinkClick", "OnHyperlinkEnter", "OnHyperlinkLeave"]);
}

public sealed partial class SimpleHtml(UiScreen ui) : Frame(ui)
{
    public FontString FontString { get; } = new(ui);
    public string Html { get; private set; } = "";

    public override string ObjectType => "SimpleHTML";
    protected override IEnumerable<string> ScriptNames =>
        base.ScriptNames.Concat(["OnHyperlinkClick", "OnHyperlinkEnter", "OnHyperlinkLeave"]);

    /// <summary>Rendered as plain text: paragraphs become line breaks and other tags are dropped.</summary>
    public string PlainText => System.Net.WebUtility.HtmlDecode(Tags().Replace(LineBreaks().Replace(Html, "\n"), ""));

    public void SetText(string text) => Html = text;

    internal override void LoadXml(XElement node, UiLoader loader)
    {
        base.LoadXml(node, loader);
        FontString.Parent = this;
        FontString.Font.JustifyH = "LEFT";
        FontString.Font.JustifyV = "TOP";
        if (node.Attr("font") is { Length: > 0 } font && Ui.FindFont(font) is { } fontObject)
            FontString.SetFontObject(fontObject);
        if (node.Child("FontString") is { } fontString)
        {
            loader.LoadFontString(FontString, fontString);
            if (fontString.Attr("justifyH") is null)
                FontString.Font.JustifyH = "LEFT";
        }
        if (node.Attr("file") is { Length: > 0 } file)
        {
            // FUN_0078a130 hands the name to the file loader; the HTML pages ship as Data\*.html in base.MPQ.
            if ((Ui.Files.Read(file) ?? Ui.Files.Read(@"Data\" + file)) is { } data)
                SetText(System.Text.Encoding.UTF8.GetString(data).TrimStart('\uFEFF'));
            else
                loader.Log.Warning($"Frame {Name ?? "<unnamed>"}: Couldn't open HTML file {file}");
        }
    }

    [GeneratedRegex(@"<\s*(br|/p|/h\d)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();
}

public sealed class ColorSelect(UiScreen ui) : Frame(ui)
{
    public override string ObjectType => "ColorSelect";
    protected override IEnumerable<string> ScriptNames => base.ScriptNames.Append("OnColorSelect");
}

public sealed class MovieFrame(UiScreen ui) : Frame(ui)
{
    public override string ObjectType => "MovieFrame";
    protected override IEnumerable<string> ScriptNames =>
        base.ScriptNames.Concat(["OnMovieFinished", "OnMovieShowSubtitle", "OnMovieHideSubtitle"]);
}

public sealed record TooltipLine(string Left, string? Right, Color4 LeftColor, Color4 RightColor, bool Wrap);

/// <summary>
/// GameTooltip: lines go into the template's $parentTextLeftN / $parentTextRightN font strings (more are created as
/// needed), the frame sizes itself around them and anchors to its owner as SetOwner asked.
/// </summary>
public sealed class GameTooltip(UiScreen ui) : Frame(ui)
{
    private const float Padding = 10f, LineGap = 2f, ColumnGap = 20f, MaxWrapWidth = 260f;

    public List<TooltipLine> Lines { get; } = [];
    public Frame? Owner { get; set; }
    public string Anchor { get; set; } = "ANCHOR_NONE";
    public Vector2 AnchorOffset { get; set; }
    public float MinimumWidth { get; set; }

    public override string ObjectType => "GameTooltip";
    protected override IEnumerable<string> ScriptNames =>
        base.ScriptNames.Concat(["OnTooltipSetDefaultAnchor", "OnTooltipCleared", "OnTooltipAddMoney"]);

    public void SetOwner(Frame? owner, string? anchor, float x, float y)
    {
        Owner = owner;
        Anchor = (anchor ?? "ANCHOR_LEFT").ToUpperInvariant();
        AnchorOffset = new Vector2(x, y);
        ClearLines();
    }

    public void ClearLines()
    {
        Lines.Clear();
        MinimumWidth = 0;
        Layout();
    }

    public void AddLine(string left, string? right, Color4 leftColor, Color4 rightColor, bool wrap = false)
    {
        Lines.Add(new TooltipLine(left, right, leftColor, rightColor, wrap));
        Layout();
    }

    /// <summary>Fills the font strings, stacks them from the top left and sizes the frame to the widest line.</summary>
    public void Layout()
    {
        var y = -Padding;
        float width = MinimumWidth;
        for (var i = 1; ; i++)
        {
            var left = Line(i, "Left", create: i <= Lines.Count);
            var right = Line(i, "Right", create: i <= Lines.Count);
            if (left is null)
                break;
            if (i > Lines.Count)
            {
                left.Hide();
                right?.Hide();
                continue;
            }
            var line = Lines[i - 1];
            left.Text = line.Left;
            left.VertexColor = line.LeftColor;
            left.Font.JustifyH = "LEFT";
            left.ClearAllPoints();
            left.SetPoint(FramePoint.TopLeft, this, FramePoint.TopLeft, Padding, y);
            var leftWidth = Ui.MeasureText(left.Font, line.Left);
            left.Width = line.Wrap && leftWidth > MaxWrapWidth ? MaxWrapWidth : 0;
            left.Show();
            var height = left.Font.Height > 0 ? left.Font.Height : 12;
            if (line.Wrap && leftWidth > MaxWrapWidth)
            {
                var rows = (int)MathF.Ceiling(leftWidth / MaxWrapWidth);
                left.Height = rows * height;
                height *= rows;
                leftWidth = MaxWrapWidth;
            }
            else
                left.Height = 0;
            var lineWidth = leftWidth;
            if (right is not null)
            {
                if (line.Right is { Length: > 0 } rightText)
                {
                    right.Text = rightText;
                    right.VertexColor = line.RightColor;
                    right.Font.JustifyH = "RIGHT";
                    right.ClearAllPoints();
                    right.SetPoint(FramePoint.TopRight, this, FramePoint.TopRight, -Padding, y);
                    right.Show();
                    lineWidth += ColumnGap + Ui.MeasureText(right.Font, rightText);
                }
                else
                    right.Hide();
            }
            width = Math.Max(width, lineWidth);
            y -= height + LineGap;
        }
        Width = MathF.Ceiling(width + Padding * 2);
        Height = MathF.Ceiling(-y - LineGap + Padding);
        PlaceAtOwner();
        Ui.InvalidateLayout();
    }

    private FontString? Line(int index, string side, bool create)
    {
        var name = $"{Name}Text{side}{index}";
        if (Ui.FindRegion(name) is FontString existing)
            return existing;
        if (!create || Name is null)
            return null;
        var template = Ui.FindRegion($"{Name}Text{side}{Math.Min(index - 1, 2)}") as FontString ?? Ui.FindRegion($"{Name}Text{side}1") as FontString;
        var created = AddRegion(new FontString(Ui), DrawLayer.Artwork);
        created.Name = name;
        if (template is not null)
            created.Font = template.Font.Clone();
        Ui.Register(created);
        return created;
    }

    private void PlaceAtOwner()
    {
        if (Owner is null || Anchor is "ANCHOR_NONE" or "ANCHOR_PRESERVE")
            return;
        ClearAllPoints();
        var (x, y) = (AnchorOffset.X, AnchorOffset.Y);
        switch (Anchor)
        {
            case "ANCHOR_RIGHT": SetPoint(FramePoint.BottomLeft, Owner, FramePoint.TopRight, x, y); break;
            case "ANCHOR_LEFT": SetPoint(FramePoint.BottomRight, Owner, FramePoint.TopLeft, x, y); break;
            case "ANCHOR_TOPLEFT": SetPoint(FramePoint.BottomLeft, Owner, FramePoint.TopLeft, x, y); break;
            case "ANCHOR_TOPRIGHT": SetPoint(FramePoint.BottomRight, Owner, FramePoint.TopRight, x, y); break;
            case "ANCHOR_BOTTOMLEFT": SetPoint(FramePoint.TopRight, Owner, FramePoint.BottomLeft, x, y); break;
            case "ANCHOR_BOTTOMRIGHT": SetPoint(FramePoint.TopLeft, Owner, FramePoint.BottomRight, x, y); break;
            case "ANCHOR_TOP": SetPoint(FramePoint.Bottom, Owner, FramePoint.Top, x, y); break;
            case "ANCHOR_BOTTOM": SetPoint(FramePoint.Top, Owner, FramePoint.Bottom, x, y); break;
            case "ANCHOR_CURSOR":
                SetPoint(FramePoint.BottomLeft, null, FramePoint.BottomLeft, Ui.MousePosition.X + 12 + x, Ui.MousePosition.Y + 12 + y);
                break;
            default: SetPoint(FramePoint.BottomRight, Owner, FramePoint.TopLeft, x, y); break;
        }
    }
}

public sealed class WorldFrame(UiScreen ui) : Frame(ui)
{
    public override string ObjectType => "WorldFrame";
}

public sealed class Minimap(UiScreen ui) : Frame(ui)
{
    public override string ObjectType => "Minimap";
}

public sealed class TaxiRouteFrame(UiScreen ui) : Frame(ui)
{
    public override string ObjectType => "TaxiRouteFrame";
}

public sealed class LootButton(UiScreen ui) : Button(ui)
{
    public override string ObjectType => "LootButton";
}
