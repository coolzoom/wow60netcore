using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FrameXml.Objects;

namespace FrameXml;

/// <summary>
/// Loads table-of-contents, XML and Lua files the way the client's FrameXML.cpp does. Each method names the
/// WoW.exe (1.12.1) function it mirrors.
/// </summary>
public sealed class UiLoader(UiScreen ui)
{
    public UiScreen Ui { get; } = ui;
    public UiLog Log => Ui.Log;
    /// <summary>Extra "-- Creating ..." / "-- Added virtual frame ..." lines, like the FrameXML_Debug CVar.</summary>
    public bool Verbose { get; set; }
    public int FilesLoaded { get; private set; }
    /// <summary>Called after every TOC entry with (done, total), like the loading bar callback (FUN_006ef160).</summary>
    public Action<int, int>? Progress { get; set; }

    /// <summary>FUN_006edb90: every non-comment line is a file relative to the TOC's directory.</summary>
    public bool LoadToc(string tocPath)
    {
        tocPath = UiPath.Normalize(tocPath);
        if (Ui.Files.Read(tocPath) is not { } data)
        {
            Log.Error($"Couldn't open {tocPath}");
            return false;
        }
        var toc = TocFile.Parse(data);
        var directory = UiPath.Directory(tocPath);
        for (var i = 0; i < toc.Files.Count; i++)
        {
            LoadFile(directory + toc.Files[i]);
            Progress?.Invoke(i + 1, toc.Files.Count);
        }
        Log.Info($"** Loading table of contents {tocPath}");
        return true;
    }

    /// <summary>FUN_006ede10: .lua files run directly; XML top-level elements are Include, Script, Font, virtual templates or frames.</summary>
    public bool LoadFile(string path)
    {
        path = UiPath.Normalize(path);
        if (UiPath.HasExtension(path, ".lua"))
            return RunLuaFile(path);

        if (ParseXml(path) is not { } root)
            return false;
        FilesLoaded++;
        foreach (var node in root.Elements())
        {
            if (node.Is("Include"))
            {
                if (node.Attr("file") is { Length: > 0 } file)
                    LoadFile(UiPath.Directory(path) + file);
                else
                    Log.Warning("Element 'Include' without file attribute");
            }
            else if (node.Is("Script"))
            {
                if (node.Attr("file") is { Length: > 0 } file)
                    RunLuaFile(file.Contains('\\') || file.Contains('/') ? file : UiPath.Directory(path) + file);
                if (!string.IsNullOrWhiteSpace(node.Value))
                    Ui.Lua.Execute(node.Value, $"{path}:<Scripts>");
            }
            else if (node.Is("Font"))
            {
                if (node.Attr("name") is { Length: > 0 } name)
                    LoadFontObject(name, node);
                else
                    Log.Warning("Unnamed font node at top level");
            }
            else if (UiEnums.Bool(node.Attr("virtual")))
            {
                if (node.Attr("name") is not { Length: > 0 } name)
                    Log.Warning("Unnamed virtual node at top level");
                else if (!Ui.RegisterVirtual(name, node))
                    Log.Warning($"Virtual object named {name} already exists");
                else if (Verbose)
                    Log.Info($"-- Added virtual frame {name}");
            }
            else
            {
                CreateFrame(node, null);
            }
        }
        Log.Info($"** Loading file {path}");
        return true;
    }

    /// <summary>FUN_00704bc0.</summary>
    public bool RunLuaFile(string path)
    {
        path = UiPath.Normalize(path);
        if (Ui.Files.Read(path) is not { } data)
        {
            Log.Error($"Error loading {path}");
            return false;
        }
        FilesLoaded++;
        return Ui.Lua.Execute(Text(data), path);
    }

    /// <summary>FUN_006edaa0.</summary>
    private XElement? ParseXml(string path)
    {
        if (Ui.Files.Read(path) is not { } data)
        {
            Log.Error($"Couldn't open {path}");
            return null;
        }
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            });
            return XDocument.Load(reader).Root;
        }
        catch (XmlException e)
        {
            Log.Error($"Couldn't parse XML in {path}: {e.Message}");
            return null;
        }
    }

    private static string Text(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>
    /// FUN_006ee280: look up the frame type, take the parent from the node (or its template), create it, then
    /// LoadXML and PostLoadXML. Frames nested in &lt;Frames&gt; default to the enclosing frame as parent.
    /// </summary>
    public Frame? CreateFrame(XElement node, Frame? parent)
    {
        var type = node.Tag();
        if (!Ui.Factories.TryGetValue(type, out var factory))
        {
            Log.Warning($"Unknown frame type: {type}");
            return null;
        }

        var parentName = node.Attr("parent");
        if (string.IsNullOrEmpty(parentName) && node.Attr("inherits") is { Length: > 0 } inherits)
            parentName = Ui.FindVirtual(inherits)?.Attr("parent");
        if (!string.IsNullOrEmpty(parentName))
        {
            if (Ui.FindRegion(Ui.ExpandName(parentName, parent)) is Frame explicitParent)
                parent = explicitParent;
            else
                Log.Warning($"Couldn't find frame parent: {parentName}");
        }

        var frame = factory(Ui);
        var name = node.Attr("name");
        if (Verbose)
            Log.Info(string.IsNullOrEmpty(name) ? $"-- Creating unnamed {type}" : $"-- Creating {type} named {name}");
        Ui.InitFrame(frame, name, parent);
        frame.LoadXml(node, this);
        frame.PostLoadXml(node, this);
        return frame;
    }

    /// <summary>FUN_006f26f0 + FUN_0076fe20 + FUN_007701c0.</summary>
    public Texture CreateTexture(XElement node, Frame frame, DrawLayer layer, bool defaultAnchors = true)
    {
        var texture = new Texture(Ui);
        Ui.InitRegion(texture, node.Attr("name"), frame);
        frame.AddRegion(texture, layer);
        LoadTexture(texture, node);
        if (defaultAnchors)
            texture.DefaultAnchors();
        return texture;
    }

    public void LoadTexture(Texture texture, XElement node)
    {
        if (node.Attr("inherits") is { Length: > 0 } inherits)
        {
            if (Ui.FindVirtual(inherits) is { } template)
                LoadTexture(texture, template);
            else
                Log.Warning($"Couldn't find inherited node: {inherits}");
        }
        texture.LoadLayout(node, this);
        if (node.Attr("hidden") is { Length: > 0 } hidden)
        {
            if (UiEnums.Bool(hidden)) texture.Hide();
            else texture.Show();
        }
        foreach (var child in node.Elements())
        {
            if (child.Is("TexCoords"))
                texture.SetTexCoord(
                    UiEnums.Float(child.Attr("left"), 0f), UiEnums.Float(child.Attr("right"), 1f),
                    UiEnums.Float(child.Attr("top"), 0f), UiEnums.Float(child.Attr("bottom"), 1f));
            else if (child.Is("Color"))
                texture.SetColorTexture(Color(child));
        }
        if (node.Attr("file") is { Length: > 0 } file && !texture.SetTexture(file))
            Log.Warning($"Texture {texture.Name ?? "<unnamed>"}: Unable to load texture file {file}");
        if (node.Attr("alphaMode") is { Length: > 0 } mode && UiEnums.TryBlend(mode, out var blend))
            texture.Blend = blend;
    }

    public FontString CreateFontString(XElement node, Frame frame, DrawLayer layer, bool defaultAnchors = true)
    {
        var fontString = new FontString(Ui);
        Ui.InitRegion(fontString, node.Attr("name"), frame);
        frame.AddRegion(fontString, layer);
        LoadFontString(fontString, node);
        if (defaultAnchors)
            fontString.DefaultAnchors();
        return fontString;
    }

    /// <summary>FUN_00770f40: inherits names a Font object first, then a virtual node.</summary>
    public void LoadFontString(FontString fontString, XElement node)
    {
        if (node.Attr("inherits") is { Length: > 0 } inherits)
        {
            if (Ui.FindFont(inherits) is { } font)
                fontString.SetFontObject(font);
            else if (Ui.FindVirtual(inherits) is { } template)
                LoadFontString(fontString, template);
            else
                Log.Warning($"Couldn't find inherited node: {inherits}");
        }
        fontString.LoadLayout(node, this);
        if (node.Attr("hidden") is { Length: > 0 } hidden)
        {
            if (UiEnums.Bool(hidden)) fontString.Hide();
            else fontString.Show();
        }
        if (node.Attr("text") is { Length: > 0 } text)
            fontString.Text = Ui.LocalizedText(text);
        if (node.Attr("nonspacewrap") is { Length: > 0 } wrap)
            fontString.NonSpaceWrap = UiEnums.Bool(wrap);
        if (node.Attr("maxLines") is { Length: > 0 } lines && int.TryParse(lines, out var maxLines))
            fontString.MaxLines = maxLines;
        if (node.Attr("font") is { Length: > 0 } fontName && Ui.FindFont(fontName) is { } named)
        {
            fontString.SetFontObject(named);
            LoadFontStyle(fontString.Font, node, fontString.Name);
        }
        else
        {
            var style = fontString.Font.Clone();
            if (LoadFontFace(style, node, fontString.Name, "FontString"))
                fontString.Font = style;
            LoadFontStyle(fontString.Font, node, fontString.Name);
        }
    }

    /// <summary>FUN_00783870 + FUN_00783c30.</summary>
    public FontObject LoadFontObject(string name, XElement node)
    {
        var font = Ui.GetOrCreateFont(name);
        if (node.Attr("inherits") is { Length: > 0 } inherits)
        {
            if (Ui.FindFont(inherits) is { } parent && parent != font)
                font.Font = parent.Font.Clone();
            else
                Log.Warning($"Couldn't find inherited font: {inherits}");
        }
        LoadFontFace(font.Font, node, name, "Font");
        LoadFontStyle(font.Font, node, name);
        return font;
    }

    /// <summary>NormalFont / HighlightFont / DisabledFont elements of a button.</summary>
    public FontInfo? FontReference(XElement node)
    {
        if (node.Attr("inherits") is not { Length: > 0 } inherits)
            return null;
        if (Ui.FindFont(inherits) is { } font)
            return font.Font.Clone();
        Log.Warning($"Couldn't find inherited font: {inherits}");
        return null;
    }

    /// <summary>font file + FontHeight + outline/monochrome; a font file without a height is rejected.</summary>
    private bool LoadFontFace(FontInfo font, XElement node, string? owner, string kind)
    {
        if (node.Attr("font") is not { Length: > 0 } file)
        {
            if (node.Child("FontHeight") is { } heightOnly && Value(heightOnly) is { } h)
                font.Height = h;
            return false;
        }
        var height = node.Child("FontHeight") is { } fontHeight ? Value(fontHeight) ?? 0 : font.Height;
        if (height <= 0)
        {
            Log.Warning($"{kind} {owner ?? "<unnamed>"}: Missing font height in {node.Tag()} element");
            return false;
        }
        font.File = UiPath.Normalize(file);
        font.Height = height;
        var flags = new List<string>();
        switch (node.Attr("outline")?.ToUpperInvariant())
        {
            case "NORMAL": flags.Add("OUTLINE"); break;
            case "THICK": flags.Add("THICKOUTLINE"); break;
        }
        if (UiEnums.Bool(node.Attr("monochrome")))
            flags.Add("MONOCHROME");
        if (flags.Count > 0 || node.Attr("outline") is not null)
            font.Flags = string.Join(",", flags);
        return true;
    }

    private void LoadFontStyle(FontInfo font, XElement node, string? owner)
    {
        if (node.Attr("spacing") is { Length: > 0 } spacing) font.Spacing = UiEnums.Float(spacing);
        if (node.Attr("justifyV") is { Length: > 0 } justifyV) font.JustifyV = justifyV.ToUpperInvariant();
        if (node.Attr("justifyH") is { Length: > 0 } justifyH) font.JustifyH = justifyH.ToUpperInvariant();
        if (node.Child("Color") is { } color) font.Color = Color(color);
        if (node.Child("Shadow") is { } shadow)
        {
            font.ShadowColor = shadow.Child("Color") is { } shadowColor ? Color(shadowColor) : Color4.Black;
            font.ShadowOffset = shadow.Child("Offset") is { } offset ? Dimension(offset) ?? Vector2.Zero : Vector2.Zero;
        }
    }

    public Backdrop LoadBackdrop(XElement node)
    {
        var backdrop = new Backdrop
        {
            BgFile = Ui.ResolveTexture(node.Attr("bgFile")),
            EdgeFile = Ui.ResolveTexture(node.Attr("edgeFile")),
            Tile = UiEnums.Bool(node.Attr("tile")),
        };
        if (node.Child("TileSize") is { } tileSize) backdrop.TileSize = Value(tileSize) ?? 0;
        if (node.Child("EdgeSize") is { } edgeSize) backdrop.EdgeSize = Value(edgeSize) ?? 0;
        if (node.Child("BackgroundInsets") is { } insets && Inset(insets) is { } value) backdrop.Insets = value;
        if (node.Child("Color") is { } color) backdrop.Color = Color(color);
        if (node.Child("BorderColor") is { } border) backdrop.BorderColor = Color(border);
        return backdrop;
    }

    /// <summary>FUN_006f1eb0: x/y attributes or an AbsDimension / RelDimension child (relative values are fractions of the screen height).</summary>
    public Vector2? Dimension(XElement node)
    {
        var x = node.Attr("x");
        var y = node.Attr("y");
        var result = new Vector2(UiEnums.Float(x), UiEnums.Float(y));
        if (node.Elements().FirstOrDefault() is not { } child)
        {
            if (x is null && y is null)
            {
                Log.Warning($"No \"x\" or \"y\" attributes in element {node.Tag()}");
                return null;
            }
            return result;
        }
        if (child.Is("AbsDimension"))
            return new Vector2(UiEnums.Float(child.Attr("x")), UiEnums.Float(child.Attr("y")));
        if (child.Is("RelDimension"))
            return new Vector2(UiEnums.Float(child.Attr("x")), UiEnums.Float(child.Attr("y"))) * UiScreen.ScreenHeight;
        Log.Warning($"Unknown child node in {node.Tag()} element: {child.Tag()}");
        return null;
    }

    public float? Value(XElement node)
    {
        if (node.Attr("val") is { Length: > 0 } val)
            return UiEnums.Float(val);
        if (node.Child("AbsValue") is { } abs)
            return UiEnums.Float(abs.Attr("val"));
        if (node.Child("RelValue") is { } rel)
            return UiEnums.Float(rel.Attr("val")) * UiScreen.ScreenHeight;
        return null;
    }

    /// <summary>Left, right, top, bottom from attributes or an AbsInset / RelInset child.</summary>
    public Vector4? Inset(XElement node)
    {
        var source = node.Child("AbsInset") ?? node.Child("RelInset") ?? node;
        var scale = source.Is("RelInset") ? UiScreen.ScreenHeight : 1f;
        if (source == node && !node.Attributes().Any())
            return null;
        return new Vector4(
            UiEnums.Float(source.Attr("left")), UiEnums.Float(source.Attr("right")),
            UiEnums.Float(source.Attr("top")), UiEnums.Float(source.Attr("bottom"))) * scale;
    }

    public Color4 Color(XElement node) => new(
        UiEnums.Float(node.Attr("r")), UiEnums.Float(node.Attr("g")),
        UiEnums.Float(node.Attr("b")), UiEnums.Float(node.Attr("a"), 1f));
}
