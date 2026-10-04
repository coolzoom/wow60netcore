using System.Numerics;
using System.Xml.Linq;
using MoonSharp.Interpreter;

namespace FrameXml.Objects;

public class Button : Frame
{
    private static readonly string[] ButtonScripts = ["OnClick", "OnDoubleClick"];

    public Button(UiScreen ui) : base(ui) => MouseEnabled = true;

    public Texture? NormalTexture { get; set; }
    public Texture? PushedTexture { get; set; }
    public Texture? DisabledTexture { get; set; }
    public Texture? HighlightTexture { get; set; }
    public FontString? TextString { get; set; }
    public FontInfo? NormalFont { get; set; }
    public FontInfo? HighlightFont { get; set; }
    public FontInfo? DisabledFont { get; set; }
    public Color4? NormalColor { get; set; }
    public Color4? HighlightColor { get; set; }
    public Color4? DisabledColor { get; set; }
    public Vector2 PushedTextOffset { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Pushed { get; set; }
    public bool HighlightLocked { get; set; }

    public override string ObjectType => "Button";
    protected override IEnumerable<string> ScriptNames => base.ScriptNames.Concat(ButtonScripts);

    public bool Highlighted => Enabled && (HighlightLocked || Ui.MouseFocus == this);
    public string ButtonState => !Enabled ? "DISABLED" : Pushed ? "PUSHED" : "NORMAL";

    public string Text => TextString?.Text ?? "";

    public void SetText(string? text)
    {
        if (TextString is null)
        {
            if (string.IsNullOrEmpty(text))
                return;
            TextString = AddRegion(new FontString(Ui), DrawLayer.Overlay);
            TextString.SetPoint(FramePoint.Center, null, FramePoint.Center, 0, 0);
            if (NormalFont is not null)
                TextString.Font = NormalFont.Clone();
        }
        TextString.Text = text ?? "";
    }

    /// <summary>Font the text is drawn with in the current state (CSimpleButton swaps font objects on state change).</summary>
    public FontInfo? StateFont()
    {
        var font = !Enabled ? DisabledFont ?? NormalFont : Highlighted ? HighlightFont ?? NormalFont : NormalFont;
        var color = !Enabled ? DisabledColor : Highlighted ? HighlightColor ?? NormalColor : NormalColor;
        if (font is null && color is null)
            return null;
        var result = (font ?? TextString?.Font ?? new FontInfo()).Clone();
        if (color is { } c)
            result.Color = c;
        return result;
    }

    public virtual void Click(string button = "LeftButton")
    {
        if (!Enabled)
            return;
        RunScript("OnClick", DynValue.NewString(button));
    }

    public override bool IsRegionActive(LayeredRegion region)
    {
        if (!region.Shown)
            return false;
        if (region == HighlightTexture)
            return Highlighted;
        if (region == NormalTexture)
            return Enabled ? !Pushed || PushedTexture is null : DisabledTexture is null;
        if (region == PushedTexture)
            return Enabled && Pushed;
        if (region == DisabledTexture)
            return !Enabled;
        return true;
    }

    internal override void LoadXml(XElement node, UiLoader loader)
    {
        base.LoadXml(node, loader);
        foreach (var child in node.Elements())
        {
            switch (child.Tag().ToUpperInvariant())
            {
                case "NORMALTEXTURE": NormalTexture = ReplaceState(NormalTexture, child, loader, DrawLayer.Artwork); break;
                case "PUSHEDTEXTURE": PushedTexture = ReplaceState(PushedTexture, child, loader, DrawLayer.Artwork); break;
                case "DISABLEDTEXTURE": DisabledTexture = ReplaceState(DisabledTexture, child, loader, DrawLayer.Artwork); break;
                case "HIGHLIGHTTEXTURE": HighlightTexture = ReplaceState(HighlightTexture, child, loader, DrawLayer.Highlight); break;
                case "BUTTONTEXT":
                    if (TextString is null)
                        TextString = loader.CreateFontString(child, this, DrawLayer.Overlay);
                    else
                        loader.LoadFontString(TextString, child);
                    break;
                case "NORMALFONT": NormalFont = loader.FontReference(child) ?? NormalFont; break;
                case "HIGHLIGHTFONT": HighlightFont = loader.FontReference(child) ?? HighlightFont; break;
                case "DISABLEDFONT": DisabledFont = loader.FontReference(child) ?? DisabledFont; break;
                case "NORMALCOLOR": NormalColor = loader.Color(child); break;
                case "HIGHLIGHTCOLOR": HighlightColor = loader.Color(child); break;
                case "DISABLEDCOLOR": DisabledColor = loader.Color(child); break;
                case "PUSHEDTEXTOFFSET": PushedTextOffset = loader.Dimension(child) ?? Vector2.Zero; break;
            }
        }
        if (TextString is not null && NormalFont is not null && TextString.FontObject is null && TextString.Font.Height <= 0)
            TextString.Font = NormalFont.Clone();
        if (node.Attr("text") is { Length: > 0 } text)
            SetText(Ui.LocalizedText(text));
    }

    protected Texture ReplaceState(Texture? existing, XElement node, UiLoader loader, DrawLayer layer)
    {
        if (existing is not null)
            Regions.Remove(existing);
        return loader.CreateTexture(node, this, layer);
    }
}

public sealed class CheckButton(UiScreen ui) : Button(ui)
{
    public Texture? CheckedTexture { get; set; }
    public Texture? DisabledCheckedTexture { get; set; }
    public bool Checked { get; set; }

    public override string ObjectType => "CheckButton";

    public override void Click(string button = "LeftButton")
    {
        if (!Enabled)
            return;
        Checked = !Checked;
        base.Click(button);
    }

    public override bool IsRegionActive(LayeredRegion region)
    {
        if (region == CheckedTexture)
            return region.Shown && Checked && (Enabled || DisabledCheckedTexture is null);
        if (region == DisabledCheckedTexture)
            return region.Shown && Checked && !Enabled;
        return base.IsRegionActive(region);
    }

    internal override void LoadXml(XElement node, UiLoader loader)
    {
        base.LoadXml(node, loader);
        foreach (var child in node.Elements())
        {
            if (child.Is("CheckedTexture"))
                CheckedTexture = ReplaceState(CheckedTexture, child, loader, DrawLayer.Overlay);
            else if (child.Is("DisabledCheckedTexture"))
                DisabledCheckedTexture = ReplaceState(DisabledCheckedTexture, child, loader, DrawLayer.Overlay);
        }
        if (node.Attr("checked") is { Length: > 0 } isChecked)
            Checked = UiEnums.Bool(isChecked);
    }
}

public sealed class EditBox : Frame
{
    private static readonly string[] EditScripts =
    [
        "OnEnterPressed", "OnEscapePressed", "OnSpacePressed", "OnTabPressed", "OnTextChanged", "OnTextSet",
        "OnCursorChanged", "OnInputLanguageChanged", "OnEditFocusGained", "OnEditFocusLost",
    ];

    private string _text = "";

    public EditBox(UiScreen ui) : base(ui)
    {
        MouseEnabled = true;
        KeyboardEnabled = true;
        FontString = new FontString(ui) { Parent = this };
        FontString.Font.JustifyH = "LEFT";
    }

    /// <summary>Font and justification of the text, taken from the box's &lt;FontString&gt; child.</summary>
    public FontString FontString { get; }
    public int MaxLetters { get; set; }
    public bool Password { get; set; }
    public bool Numeric { get; set; }
    public bool MultiLine { get; set; }
    public bool AutoFocus { get; set; } = true;
    public int HistoryLines { get; set; }
    /// <summary>Left, right, top, bottom.</summary>
    public Vector4 TextInsets { get; set; }
    public int Cursor { get; set; }
    public (int Start, int End) Highlight { get; set; }

    public override string ObjectType => "EditBox";
    protected override IEnumerable<string> ScriptNames => base.ScriptNames.Concat(EditScripts);
    public bool HasFocus => Ui.KeyboardFocus == this;

    public string Text => _text;
    public string DisplayText => Password ? new string('*', _text.Length) : _text;

    public void SetText(string? text)
    {
        text ??= "";
        if (MaxLetters > 0 && text.Length > MaxLetters)
            text = text[..MaxLetters];
        if (Numeric)
            text = new string(text.Where(char.IsDigit).ToArray());
        var changed = text != _text;
        _text = text;
        Cursor = _text.Length;
        if (changed)
            RunScript("OnTextChanged");
    }

    public void Insert(string text)
    {
        if (Numeric && !text.All(char.IsDigit))
            return;
        var room = MaxLetters > 0 ? MaxLetters - _text.Length : int.MaxValue;
        if (room <= 0)
            return;
        if (text.Length > room)
            text = text[..room];
        Cursor = Math.Clamp(Cursor, 0, _text.Length);
        _text = _text.Insert(Cursor, text);
        Cursor += text.Length;
        RunScript("OnTextChanged");
    }

    public void Backspace()
    {
        Cursor = Math.Clamp(Cursor, 0, _text.Length);
        if (Cursor == 0)
            return;
        _text = _text.Remove(Cursor - 1, 1);
        Cursor--;
        RunScript("OnTextChanged");
    }

    protected override void OnVisible()
    {
        if (AutoFocus)
            Ui.SetKeyboardFocus(this);
    }

    protected override void OnHidden()
    {
        if (HasFocus)
            Ui.SetKeyboardFocus(null);
    }

    internal override void LoadXml(XElement node, UiLoader loader)
    {
        base.LoadXml(node, loader);
        if (node.Attr("letters") is { Length: > 0 } letters && int.TryParse(letters, out var max)) MaxLetters = max;
        if (node.Attr("password") is { Length: > 0 } password) Password = UiEnums.Bool(password);
        if (node.Attr("numeric") is { Length: > 0 } numeric) Numeric = UiEnums.Bool(numeric);
        if (node.Attr("multiLine") is { Length: > 0 } multiLine) MultiLine = UiEnums.Bool(multiLine);
        if (node.Attr("autoFocus") is { Length: > 0 } autoFocus) AutoFocus = UiEnums.Bool(autoFocus);
        if (node.Attr("historyLines") is { Length: > 0 } history && int.TryParse(history, out var lines)) HistoryLines = lines;
        if (node.Child("FontString") is { } font)
            loader.LoadFontString(FontString, font);
        if (node.Child("TextInsets") is { } insets && loader.Inset(insets) is { } value)
            TextInsets = value;
    }
}

public sealed class Slider(UiScreen ui) : Frame(ui)
{
    public Texture? ThumbTexture { get; set; }
    public string Orientation { get; set; } = "VERTICAL";
    public float MinValue { get; set; }
    public float MaxValue { get; set; }
    public float Value { get; private set; }
    public float ValueStep { get; set; }
    public bool Enabled { get; set; } = true;

    public override string ObjectType => "Slider";
    protected override IEnumerable<string> ScriptNames => base.ScriptNames.Append("OnValueChanged");

    public void SetValue(float value)
    {
        value = Math.Clamp(value, MinValue, Math.Max(MinValue, MaxValue));
        if (ValueStep > 0)
            value = MinValue + MathF.Round((value - MinValue) / ValueStep) * ValueStep;
        if (value == Value)
            return;
        Value = value;
        PlaceThumb();
        RunScript("OnValueChanged", DynValue.NewNumber(value));
    }

    public void SetMinMax(float min, float max)
    {
        MinValue = min;
        MaxValue = max;
        if (Value < min || Value > max)
            SetValue(Value);
        PlaceThumb();
    }

    public void PlaceThumb()
    {
        if (ThumbTexture is null)
            return;
        var fraction = MaxValue > MinValue ? (Value - MinValue) / (MaxValue - MinValue) : 0f;
        var vertical = Orientation.Equals("VERTICAL", StringComparison.OrdinalIgnoreCase);
        var length = vertical ? GetHeight() - ThumbTexture.Height : GetWidth() - ThumbTexture.Width;
        ThumbTexture.ClearAllPoints();
        if (vertical)
            ThumbTexture.SetPoint(FramePoint.Top, null, FramePoint.Top, 0, -fraction * Math.Max(length, 0));
        else
            ThumbTexture.SetPoint(FramePoint.Left, null, FramePoint.Left, fraction * Math.Max(length, 0), 0);
    }

    internal override void LoadXml(XElement node, UiLoader loader)
    {
        base.LoadXml(node, loader);
        if (node.Child("ThumbTexture") is { } thumb)
        {
            if (ThumbTexture is not null)
                Regions.Remove(ThumbTexture);
            ThumbTexture = loader.CreateTexture(thumb, this, DrawLayer.Overlay);
        }
        if (node.Attr("orientation") is { Length: > 0 } orientation) Orientation = orientation;
        if (node.Attr("minValue") is { Length: > 0 } min) MinValue = UiEnums.Float(min);
        if (node.Attr("maxValue") is { Length: > 0 } max) MaxValue = UiEnums.Float(max);
        if (node.Attr("valueStep") is { Length: > 0 } step) ValueStep = UiEnums.Float(step);
        if (node.Attr("defaultValue") is { Length: > 0 } value) Value = UiEnums.Float(value);
        PlaceThumb();
    }
}

public sealed class StatusBar(UiScreen ui) : Frame(ui)
{
    public Texture? BarTexture { get; set; }
    public float MinValue { get; set; }
    public float MaxValue { get; set; } = 1f;
    public float Value { get; private set; }
    public string Orientation { get; set; } = "HORIZONTAL";

    public override string ObjectType => "StatusBar";
    protected override IEnumerable<string> ScriptNames => base.ScriptNames.Append("OnValueChanged");

    public void SetValue(float value)
    {
        value = Math.Clamp(value, MinValue, Math.Max(MinValue, MaxValue));
        var changed = value != Value;
        Value = value;
        PlaceBar();
        if (changed)
            RunScript("OnValueChanged", DynValue.NewNumber(value));
    }

    public void SetMinMax(float min, float max)
    {
        MinValue = min;
        MaxValue = max;
        SetValue(Value);
        PlaceBar();
    }

    public void SetBarTexture(Texture texture)
    {
        if (BarTexture is not null)
            Regions.Remove(BarTexture);
        BarTexture = AddRegion(texture, DrawLayer.Artwork);
        PlaceBar();
    }

    public void PlaceBar()
    {
        if (BarTexture is null)
            return;
        var fraction = MaxValue > MinValue ? (Value - MinValue) / (MaxValue - MinValue) : 0f;
        BarTexture.ClearAllPoints();
        if (Orientation.Equals("VERTICAL", StringComparison.OrdinalIgnoreCase))
        {
            BarTexture.SetPoint(FramePoint.BottomLeft, null, FramePoint.BottomLeft, 0, 0);
            BarTexture.SetPoint(FramePoint.BottomRight, null, FramePoint.BottomRight, 0, 0);
            BarTexture.Height = Math.Max(GetHeight() * fraction, 0.01f);
            BarTexture.SetTexCoord(0, 1, 1 - fraction, 1);
        }
        else
        {
            BarTexture.SetPoint(FramePoint.TopLeft, null, FramePoint.TopLeft, 0, 0);
            BarTexture.SetPoint(FramePoint.BottomLeft, null, FramePoint.BottomLeft, 0, 0);
            BarTexture.Width = Math.Max(GetWidth() * fraction, 0.01f);
            BarTexture.SetTexCoord(0, fraction, 0, 1);
        }
    }

    internal override void LoadXml(XElement node, UiLoader loader)
    {
        base.LoadXml(node, loader);
        if (node.Child("BarTexture") is { } bar)
        {
            if (BarTexture is not null)
                Regions.Remove(BarTexture);
            BarTexture = loader.CreateTexture(bar, this, DrawLayer.Artwork);
        }
        if (node.Child("BarColor") is { } color && BarTexture is not null)
            BarTexture.VertexColor = loader.Color(color);
        if (node.Attr("orientation") is { Length: > 0 } orientation) Orientation = orientation;
        if (node.Attr("minValue") is { Length: > 0 } min) MinValue = UiEnums.Float(min);
        if (node.Attr("maxValue") is { Length: > 0 } max) MaxValue = UiEnums.Float(max);
        if (node.Attr("defaultValue") is { Length: > 0 } value) Value = UiEnums.Float(value);
        PlaceBar();
    }
}

public sealed class ScrollFrame(UiScreen ui) : Frame(ui)
{
    private static readonly string[] ScrollScripts = ["OnHorizontalScroll", "OnVerticalScroll", "OnScrollRangeChanged"];

    public Frame? ScrollChild { get; private set; }
    public float HorizontalScroll { get; private set; }
    public float VerticalScroll { get; private set; }

    public override string ObjectType => "ScrollFrame";
    protected override IEnumerable<string> ScriptNames => base.ScriptNames.Concat(ScrollScripts);

    public float VerticalRange => ScrollChild is null ? 0 : Math.Max(0, ScrollChild.GetHeight() - GetHeight());
    public float HorizontalRange => ScrollChild is null ? 0 : Math.Max(0, ScrollChild.GetWidth() - GetWidth());

    public void SetScrollChild(Frame child)
    {
        ScrollChild = child;
        if (child.Parent != this)
            child.SetParent(this);
        PlaceChild();
    }

    public void SetVerticalScroll(float offset)
    {
        VerticalScroll = offset;
        PlaceChild();
        RunScript("OnVerticalScroll", DynValue.NewNumber(offset));
    }

    public void SetHorizontalScroll(float offset)
    {
        HorizontalScroll = offset;
        PlaceChild();
        RunScript("OnHorizontalScroll", DynValue.NewNumber(offset));
    }

    public void UpdateScrollChildRect() =>
        RunScript("OnScrollRangeChanged", DynValue.NewNumber(HorizontalRange), DynValue.NewNumber(VerticalRange));

    private void PlaceChild()
    {
        if (ScrollChild is null)
            return;
        ScrollChild.ClearAllPoints();
        ScrollChild.SetPoint(FramePoint.TopLeft, this, FramePoint.TopLeft, -HorizontalScroll, VerticalScroll);
    }

    internal override void LoadXml(XElement node, UiLoader loader)
    {
        base.LoadXml(node, loader);
        if (node.Child("ScrollChild") is { } scrollChild)
            foreach (var child in scrollChild.Elements())
                if (loader.CreateFrame(child, this) is { } frame)
                    SetScrollChild(frame);
    }
}
