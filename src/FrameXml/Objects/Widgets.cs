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

public class MessageFrame(UiScreen ui) : Frame(ui)
{
    public FontString FontString { get; } = new(ui);
    public List<(string Text, Color4 Color)> Messages { get; } = [];
    public int MaxLines { get; set; } = 120;

    public override string ObjectType => "MessageFrame";

    public void AddMessage(string text, Color4 color)
    {
        Messages.Add((text, color));
        if (Messages.Count > MaxLines)
            Messages.RemoveAt(0);
    }

    internal override void LoadXml(XElement node, UiLoader loader)
    {
        base.LoadXml(node, loader);
        FontString.Parent = this;
        if (node.Child("FontString") is { } font)
            loader.LoadFontString(FontString, font);
        if (node.Attr("maxLines") is { Length: > 0 } lines && int.TryParse(lines, out var max))
            MaxLines = max;
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

public sealed class GameTooltip(UiScreen ui) : Frame(ui)
{
    public List<(string Left, string? Right, Color4 Color)> Lines { get; } = [];
    public Frame? Owner { get; set; }

    public override string ObjectType => "GameTooltip";
    protected override IEnumerable<string> ScriptNames =>
        base.ScriptNames.Concat(["OnTooltipSetDefaultAnchor", "OnTooltipCleared", "OnTooltipAddMoney"]);
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
