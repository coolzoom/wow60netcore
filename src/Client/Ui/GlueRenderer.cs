using System.Numerics;
using System.Text;
using Formats.Blp;
using FrameXml;
using FrameXml.Objects;
using ImGuiNET;
using Silk.NET.OpenGL;
using EngineTexture = Engine.Rendering.Texture;
using UiTexture = FrameXml.Objects.Texture;

namespace Client.Ui;

/// <summary>
/// Draws a <see cref="UiScreen"/> into ImGui's background draw list: frames back to front, each frame's backdrop
/// and then its layers. UI units are 768 high with y up; the draw list uses window points with y down.
/// </summary>
public sealed class GlueRenderer : IDisposable
{
    private const float RasterSize = 20f;

    private readonly GL _gl;
    private readonly UiScreen _ui;
    private readonly IUiFileSource _files;
    private readonly Dictionary<(string File, BlendMode Blend), EngineTexture?> _textures = [];
    private readonly Dictionary<string, ImFontPtr> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _fontFiles = [];
    private ImFontPtr _defaultFont;
    private float _scale = 1f;
    private float _height;
    private float _time;

    public GlueRenderer(GL gl, UiScreen ui, IUiFileSource files)
    {
        _gl = gl;
        _ui = ui;
        _files = files;
    }

    /// <summary>Also rasterize the ~2500 common Chinese characters (in-game chat and names can use any of them).</summary>
    public bool CommonChinese { get; init; }

    /// <summary>The font of a FontObject (e.g. "GameFontNormal"), for text the engine draws in the world.</summary>
    public ImFontPtr FontOf(string fontObject) => Font(_ui.FindFont(fontObject)?.Font ?? new FontInfo());

    /// <summary>Font files referenced by Font objects and font strings; call before the ImGui font atlas is built.</summary>
    public void CollectFonts()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var font in _ui.FontObjects)
            if (font.Font.File is { } file) files.Add(file);
        foreach (var frame in _ui.Frames)
        {
            foreach (var region in frame.Regions.OfType<FontString>())
                if (region.Font.File is { } file) files.Add(file);
            if (frame is EditBox { FontString.Font.File: { } edit }) files.Add(edit);
        }
        _fontFiles.Clear();
        _fontFiles.AddRange(files.Where(f => _files.Exists(f)).Order());
    }

    /// <summary>Adds every collected font with the glyphs the UI's strings use (onConfigureIO of the ImGui controller).</summary>
    public unsafe void ConfigureFonts()
    {
        var io = ImGui.GetIO();
        _defaultFont = io.Fonts.AddFontDefault();

        var builder = new ImFontGlyphRangesBuilderPtr(ImGuiNative.ImFontGlyphRangesBuilder_ImFontGlyphRangesBuilder());
        builder.AddRanges(io.Fonts.GetGlyphRangesDefault());
        if (CommonChinese)
            builder.AddRanges(io.Fonts.GetGlyphRangesChineseSimplifiedCommon());
        builder.AddText(UiText());
        builder.BuildRanges(out var ranges);

        foreach (var file in _fontFiles)
        {
            var path = Path.Combine(Path.GetTempPath(), "NetCoreClient-" + Path.GetFileName(file));
            File.WriteAllBytes(path, _files.Read(file)!);
            var font = io.Fonts.AddFontFromFileTTF(path, RasterSize, null, ranges.Data);
            if (font.NativePtr != null)
                _fonts[file] = font;
        }
    }

    /// <summary>All string globals (GlueStrings, GlobalStrings) plus the text currently set on the UI.</summary>
    private string UiText()
    {
        var text = new StringBuilder("…“”‘’·—–");
        foreach (var pair in _ui.Lua.Globals.Pairs)
            if (pair.Value.Type == MoonSharp.Interpreter.DataType.String)
                text.Append(pair.Value.String);
        foreach (var frame in _ui.Frames)
        {
            foreach (var region in frame.Regions.OfType<FontString>())
                text.Append(region.Text);
            if (frame is SimpleHtml html)
                text.Append(html.PlainText);
        }
        return text.ToString();
    }

    /// <summary>Text width in UI units from the font's glyph advances; used for layout of auto-sized font strings.</summary>
    public float Measure(FontInfo font, string text)
    {
        var imFont = Font(font);
        var height = font.Height > 0 ? font.Height : 12;
        float width = 0, line = 0;
        foreach (var c in TextMarkup.Strip(text))
        {
            if (c == '\n')
            {
                width = Math.Max(width, line);
                line = 0;
                continue;
            }
            line += Advance(imFont, c) * height / imFont.FontSize;
        }
        return Math.Max(width, line) + font.Spacing;
    }

    /// <summary>ImGui.NET's ImFontGlyph struct doesn't match the native bitfield layout, so ask the native side.</summary>
    private static float Advance(ImFontPtr font, char c) => font.GetCharAdvance(c);

    private ImFontPtr Font(FontInfo info) =>
        info.File is { } file && _fonts.TryGetValue(file, out var font) ? font : _fonts.Values.FirstOrDefault(_defaultFont);

    public void Draw(Vector2 displaySize, float dt)
    {
        _time += dt;
        _height = displaySize.Y;
        _scale = displaySize.Y / UiScreen.ScreenHeight;
        var list = ImGui.GetBackgroundDrawList();
        foreach (var frame in _ui.DrawOrder())
        {
            if (frame.EffectiveAlpha <= 0.001f || frame.Rect is not { } rect)
                continue;
            var clip = ClipRect(frame);
            if (clip is { } c)
                list.PushClipRect(c.Min, c.Max, true);
            DrawFrame(list, frame, rect);
            if (clip is not null)
                list.PopClipRect();
        }
    }

    /// <summary>An icon held on the cursor (a dragged item or spell), drawn above the interface.</summary>
    public void DrawCursorIcon(string file, Vector2 windowPosition)
    {
        if (_ui.ResolveTexture(file) is not { } path || Load(path, BlendMode.Blend) is not { } gpu)
            return;
        var size = new Vector2(32 * _scale);
        ImGui.GetForegroundDrawList().AddImage((IntPtr)gpu.Handle, windowPosition - size / 2, windowPosition + size / 2);
    }

    private (Vector2 Min, Vector2 Max)? ClipRect(Frame frame)
    {
        for (var child = frame; child.Parent is { } parent; child = parent)
            if (parent is ScrollFrame scroll && scroll.ScrollChild == child && scroll.Rect is { } r)
                return (ToScreen(r.Left, r.Top), ToScreen(r.Right, r.Bottom));
        return null;
    }

    private Vector2 ToScreen(float x, float y) => new(x * _scale, _height - y * _scale);

    private void DrawFrame(ImDrawListPtr list, Frame frame, UiRect rect)
    {
        if (frame.Backdrop is { } backdrop)
            DrawBackdrop(list, backdrop, rect, frame.EffectiveAlpha);

        foreach (var layer in Enum.GetValues<DrawLayer>())
            foreach (var region in frame.Regions)
            {
                if (region.Layer != layer || !frame.IsRegionActive(region))
                    continue;
                if (layer == DrawLayer.Highlight && frame is not Button && _ui.MouseFocus != frame)
                    continue;
                switch (region)
                {
                    case UiTexture texture:
                        DrawTexture(list, texture);
                        break;
                    case FontString text when frame is Button button && text == button.TextString:
                        DrawButtonText(list, button, text);
                        break;
                    case FontString text:
                        DrawFontString(list, text, text.Font, Vector2.Zero);
                        break;
                }
            }

        switch (frame)
        {
            case EditBox edit:
                DrawEditBox(list, edit, rect);
                break;
            case SimpleHtml html when html.Html.Length > 0:
                FitHtml(html, rect);
                DrawText(list, html.PlainText.Trim(), html.FontString.Font, rect, html.EffectiveAlpha, wrap: true);
                break;
            case MessageFrame messages when messages.Messages.Count > 0:
                DrawMessages(list, messages, rect);
                break;
            case Model { Sequence: 0 } model when model.Table.Get("duration").CastToNumber() is > 0 and var duration &&
                                                  model.Table.Get("start").CastToNumber() is { } start:
                DrawCooldown(list, rect, (float)((_ui.Time - start) / duration), model.EffectiveAlpha);
                break;
        }
    }

    /// <summary>
    /// Message lines in their own colours, fading with age. Scrolling message frames (chat) stack the newest line at
    /// the bottom and go up; plain message frames (UIErrorsFrame) put the newest line at the top.
    /// </summary>
    private void DrawMessages(ImDrawListPtr list, MessageFrame frame, UiRect rect)
    {
        var font = frame.FontString.Font;
        var height = (font.Height > 0 ? font.Height : 12) + font.Spacing;
        var fromBottom = frame is ScrollingMessageFrame;
        var y = fromBottom ? rect.Bottom : rect.Top;
        list.PushClipRect(ToScreen(rect.Left, rect.Top), ToScreen(rect.Right, rect.Bottom), true);
        for (var i = frame.Messages.Count - 1 - (fromBottom ? frame.ScrollOffset : 0); i >= 0; i--)
        {
            var message = frame.Messages[i];
            var alpha = frame.MessageAlpha(message) * frame.EffectiveAlpha;
            var lines = Layout(message.Text, font, rect.Width);
            var block = lines.Count * height;
            if (fromBottom ? y + block > rect.Top + height : y - block < rect.Bottom - height)
                break;
            if (alpha > 0.01f)
            {
                var style = font.Clone();
                style.Color = message.Color with { A = font.Color.A };
                style.JustifyV = "TOP";
                var top = fromBottom ? y + block : y;
                DrawText(list, string.Join("\n", lines), style, new UiRect(rect.Left, top - block, rect.Right, top), alpha, wrap: false);
            }
            y += fromBottom ? block : -block;
        }
        list.PopClipRect();
    }

    /// <summary>The cooldown sweep: the part of the button still cooling down is darkened, clockwise from 12 o'clock.</summary>
    private void DrawCooldown(ImDrawListPtr list, UiRect rect, float done, float alpha)
    {
        if (done is < 0 or >= 1)
            return;
        var min = ToScreen(rect.Left, rect.Top);
        var max = ToScreen(rect.Right, rect.Bottom);
        var center = (min + max) / 2;
        var radius = Vector2.Distance(min, max) / 2;
        var color = Pack(new Color4(0, 0, 0, 0.65f), alpha);
        list.PushClipRect(min, max, true);
        var from = -MathF.PI / 2 + done * MathF.Tau;
        var to = MathF.PI * 1.5f;
        for (var a = from; a < to; a += MathF.PI / 2)
        {
            list.PathLineTo(center);
            list.PathArcTo(center, radius, a, Math.Min(a + MathF.PI / 2, to), 12);
            list.PathFillConvex(color);
        }
        list.PopClipRect();
    }

    private void DrawTexture(ImDrawListPtr list, UiTexture texture)
    {
        if (texture.Rect is not { } r || r.Width <= 0 || r.Height <= 0)
            return;
        var alpha = texture.EffectiveAlpha;
        var color = texture.VertexColor;
        IntPtr handle;
        if (texture.SolidColor is { } solid)
        {
            handle = ImGui.GetIO().Fonts.TexID;
            color = new Color4(solid.R * color.R, solid.G * color.G, solid.B * color.B, solid.A * color.A);
            var white = ImGui.GetIO().Fonts.TexUvWhitePixel;
            Quad(list, handle, r, [white, white, white, white], color, alpha);
            return;
        }
        if (texture.File is null || Load(texture.File, texture.Blend) is not { } gpu)
            return;
        handle = (IntPtr)gpu.Handle;
        Quad(list, handle, r, texture.TexCoords, color, alpha);
    }

    /// <summary>Corners in client order: UL, LL, UR, LR.</summary>
    private void Quad(ImDrawListPtr list, IntPtr texture, UiRect r, IReadOnlyList<Vector2> uv, Color4 color, float alpha)
    {
        list.AddImageQuad(texture,
            ToScreen(r.Left, r.Top), ToScreen(r.Right, r.Top), ToScreen(r.Right, r.Bottom), ToScreen(r.Left, r.Bottom),
            uv[0], uv[2], uv[3], uv[1], Pack(color, alpha));
    }

    private static uint Pack(Color4 c, float alpha)
    {
        static uint B(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
        return B(c.R) | B(c.G) << 8 | B(c.B) << 16 | B(c.A * alpha) << 24;
    }

    /// <summary>
    /// Tiled background inside the insets, then the eight-piece edge strip (left, right, top, bottom, then the four
    /// corners) where the top and bottom pieces are stored rotated.
    /// </summary>
    private void DrawBackdrop(ImDrawListPtr list, Backdrop backdrop, UiRect rect, float alpha)
    {
        if (backdrop.BgFile is { } bgFile && Load(bgFile, BlendMode.Blend) is { } bg)
        {
            var inner = new UiRect(rect.Left + backdrop.Insets.X, rect.Bottom + backdrop.Insets.W, rect.Right - backdrop.Insets.Y, rect.Top - backdrop.Insets.Z);
            var tile = backdrop.Tile && backdrop.TileSize > 0 ? backdrop.TileSize : 0;
            var u = tile > 0 ? inner.Width / tile : 1;
            var v = tile > 0 ? inner.Height / tile : 1;
            Quad(list, (IntPtr)bg.Handle, inner, [new(0, 0), new(0, v), new(u, 0), new(u, v)], backdrop.Color, alpha);
        }
        if (backdrop.EdgeFile is not { } edgeFile || Load(edgeFile, BlendMode.Blend) is not { } edge)
            return;
        var e = backdrop.EdgeSize > 0 ? backdrop.EdgeSize : 16;
        var id = (IntPtr)edge.Handle;
        var color = backdrop.BorderColor;
        float Seg(int i) => i / 8f;
        var vertical = Math.Max(0, (rect.Height - 2 * e) / e);
        var horizontal = Math.Max(0, (rect.Width - 2 * e) / e);

        Quad(list, id, new UiRect(rect.Left, rect.Bottom + e, rect.Left + e, rect.Top - e),
            [new(Seg(0), 0), new(Seg(0), vertical), new(Seg(1), 0), new(Seg(1), vertical)], color, alpha);
        Quad(list, id, new UiRect(rect.Right - e, rect.Bottom + e, rect.Right, rect.Top - e),
            [new(Seg(1), 0), new(Seg(1), vertical), new(Seg(2), 0), new(Seg(2), vertical)], color, alpha);
        Quad(list, id, new UiRect(rect.Left + e, rect.Top - e, rect.Right - e, rect.Top),
            [new(Seg(2), horizontal), new(Seg(3), horizontal), new(Seg(2), 0), new(Seg(3), 0)], color, alpha);
        Quad(list, id, new UiRect(rect.Left + e, rect.Bottom, rect.Right - e, rect.Bottom + e),
            [new(Seg(3), horizontal), new(Seg(4), horizontal), new(Seg(3), 0), new(Seg(4), 0)], color, alpha);
        Corner(4, rect.Left, rect.Top);
        Corner(5, rect.Right - e, rect.Top);
        Corner(6, rect.Left, rect.Bottom + e);
        Corner(7, rect.Right - e, rect.Bottom + e);

        void Corner(int segment, float left, float top) => Quad(list, id, new UiRect(left, top - e, left + e, top),
            [new(Seg(segment), 0), new(Seg(segment), 1), new(Seg(segment + 1), 0), new(Seg(segment + 1), 1)], color, alpha);
    }

    /// <summary>The HTML frame grows to its text so a surrounding scroll frame can scroll through all of it.</summary>
    private void FitHtml(SimpleHtml html, UiRect rect)
    {
        var font = html.FontString.Font;
        var lines = Layout(html.PlainText.Trim(), font, rect.Width).Count;
        var height = lines * ((font.Height > 0 ? font.Height : 12) + font.Spacing);
        if (height <= html.Height + 0.5f)
            return;
        html.Height = MathF.Ceiling(height);
        if (html.Parent is ScrollFrame scroll && scroll.ScrollChild == html)
            scroll.UpdateScrollChildRect();
    }

    private void DrawButtonText(ImDrawListPtr list, Button button, FontString text)
    {
        var font = button.StateFont() ?? text.Font;
        DrawFontString(list, text, font, button.Pushed ? button.PushedTextOffset : Vector2.Zero);
    }

    private void DrawFontString(ImDrawListPtr list, FontString text, FontInfo font, Vector2 offset)
    {
        if (text.Text.Length == 0 || text.Rect is not { } r)
            return;
        var rect = new UiRect(r.Left + offset.X, r.Bottom + offset.Y, r.Right + offset.X, r.Top + offset.Y);
        var color = font.Color;
        color = new Color4(color.R * text.VertexColor.R, color.G * text.VertexColor.G, color.B * text.VertexColor.B, color.A * text.VertexColor.A);
        var style = font.Clone();
        style.Color = color;
        DrawText(list, text.Text, style, rect, text.EffectiveAlpha, wrap: text.Points.Count() > 1 || text.Width > 0);
    }

    private void DrawEditBox(ImDrawListPtr list, EditBox edit, UiRect rect)
    {
        var insets = edit.TextInsets;
        var inner = new UiRect(rect.Left + insets.X, rect.Bottom + insets.W, rect.Right - insets.Y, rect.Top - insets.Z);
        var font = edit.FontString.Font.Clone();
        font.JustifyV = edit.MultiLine ? "TOP" : "MIDDLE";
        if (!edit.MultiLine)
            font.JustifyH = "LEFT";
        var shown = edit.DisplayText;
        DrawText(list, shown, font, inner, edit.EffectiveAlpha, wrap: edit.MultiLine);
        if (!edit.HasFocus || (int)(_time * 2) % 2 == 1)
            return;
        var height = font.Height > 0 ? font.Height : 12;
        var x = inner.Left + Measure(font, shown[..Math.Min(edit.Cursor, shown.Length)]) - font.Spacing;
        var y = (inner.Top + inner.Bottom) / 2;
        list.AddLine(ToScreen(x + 1, y + height / 2), ToScreen(x + 1, y - height / 2), Pack(font.Color, edit.EffectiveAlpha), Math.Max(1, _scale));
    }

    /// <summary>Lines split at "\n" and, when wrapping, at spaces or between CJK characters; justified inside the rect.</summary>
    private void DrawText(ImDrawListPtr list, string text, FontInfo font, UiRect rect, float alpha, bool wrap)
    {
        var imFont = Font(font);
        var height = font.Height > 0 ? font.Height : 12;
        var lines = Layout(text, font, wrap ? rect.Width : float.PositiveInfinity);
        var lineHeight = height + font.Spacing;
        var total = lines.Count * lineHeight - font.Spacing;
        var y = font.JustifyV switch
        {
            "TOP" => rect.Top,
            "BOTTOM" => rect.Bottom + total,
            _ => (rect.Top + rect.Bottom) / 2 + total / 2,
        };
        var outline = font.Flags.Contains("OUTLINE", StringComparison.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var width = Measure(font, line) - font.Spacing;
            var x = font.JustifyH switch
            {
                "LEFT" => rect.Left,
                "RIGHT" => rect.Right - width,
                _ => (rect.Left + rect.Right - width) / 2,
            };
            if (font.ShadowColor is { } shadow)
                DrawRuns(list, imFont, height, line, x + font.ShadowOffset.X, y + font.ShadowOffset.Y, shadow, alpha, colorCodes: false);
            if (outline)
                foreach (var (dx, dy) in new[] { (-1f, 0f), (1f, 0f), (0f, -1f), (0f, 1f) })
                    DrawRuns(list, imFont, height, line, x + dx / _scale, y + dy / _scale, Color4.Black, alpha, colorCodes: false);
            DrawRuns(list, imFont, height, line, x, y, font.Color, alpha, colorCodes: true);
            y -= lineHeight;
        }
    }

    private void DrawRuns(ImDrawListPtr list, ImFontPtr font, float height, string line, float x, float top, Color4 color, float alpha, bool colorCodes)
    {
        var size = height * _scale;
        var position = ToScreen(x, top);
        foreach (var run in TextMarkup.Parse(line))
        {
            var runColor = colorCodes && run.Color is { } c ? c with { A = color.A } : color;
            list.AddText(font, size, position, Pack(runColor, alpha), run.Text);
            foreach (var ch in run.Text)
                position.X += Advance(font, ch) * size / font.FontSize;
        }
    }

    private List<string> Layout(string text, FontInfo font, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("|n", "\n").Split('\n'))
        {
            if (float.IsPositiveInfinity(maxWidth) || maxWidth <= 0 || Measure(font, paragraph) <= maxWidth + 0.5f)
            {
                lines.Add(paragraph);
                continue;
            }
            var start = 0;
            while (start < paragraph.Length)
            {
                var end = start + 1;
                var lastBreak = -1;
                while (end <= paragraph.Length && Measure(font, paragraph[start..end]) <= maxWidth + 0.5f)
                {
                    if (end < paragraph.Length && (paragraph[end] == ' ' || paragraph[end - 1] > 0x2e80))
                        lastBreak = end;
                    end++;
                }
                end--;
                if (end >= paragraph.Length)
                {
                    lines.Add(paragraph[start..]);
                    break;
                }
                var cut = lastBreak > start ? lastBreak : Math.Max(end, start + 1);
                lines.Add(paragraph[start..cut].TrimEnd());
                start = cut;
                while (start < paragraph.Length && paragraph[start] == ' ')
                    start++;
            }
        }
        return lines;
    }

    private EngineTexture? Load(string file, BlendMode blend)
    {
        if (_textures.TryGetValue((file, blend), out var cached))
            return cached;
        EngineTexture? texture = null;
        try
        {
            if (_files.Read(file) is { } data && Path.GetExtension(file).Equals(".blp", StringComparison.OrdinalIgnoreCase))
            {
                var image = BlpImage.Decode(data);
                var pixels = blend == BlendMode.Add ? AdditiveToAlpha(image.Pixels) : image.Pixels;
                texture = new EngineTexture(_gl, image.Width, image.Height, pixels, repeat: true, mipmaps: false);
            }
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or NotSupportedException)
        {
            Console.Error.WriteLine($"Texture {file}: {e.Message}");
        }
        _textures[(file, blend)] = texture;
        return texture;
    }

    /// <summary>ImGui only alpha-blends; additive textures become (rgb / a, a = max(rgb)), which looks the same over dark backgrounds.</summary>
    internal static byte[] AdditiveToAlpha(byte[] rgba)
    {
        var result = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var a = rgba[i + 3] / 255f;
            var r = rgba[i] * a;
            var g = rgba[i + 1] * a;
            var b = rgba[i + 2] * a;
            var max = Math.Max(r, Math.Max(g, b));
            if (max <= 0)
                continue;
            result[i] = (byte)(r * 255 / max);
            result[i + 1] = (byte)(g * 255 / max);
            result[i + 2] = (byte)(b * 255 / max);
            result[i + 3] = (byte)max;
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var texture in _textures.Values)
            texture?.Dispose();
        _textures.Clear();
    }
}
