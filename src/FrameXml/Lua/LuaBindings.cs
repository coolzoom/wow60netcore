using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using FrameXml.Objects;
using MoonSharp.Interpreter;

namespace FrameXml.Lua;

/// <summary>Arguments after "self" of a method call.</summary>
public readonly struct LuaArgs(CallbackArguments args, int offset)
{
    public int Count => Math.Max(0, args.Count - offset);
    public DynValue this[int i] => i + offset < args.Count ? args[i + offset] : DynValue.Nil;

    public bool Has(int i) => !this[i].IsNil();
    public bool IsNumber(int i) => this[i].Type == DataType.Number;

    public string? Str(int i) => this[i] switch
    {
        { Type: DataType.String } v => v.String,
        { Type: DataType.Number } v => v.Number.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    public double Num(int i, double fallback = 0) => this[i] switch
    {
        { Type: DataType.Number } v => v.Number,
        { Type: DataType.String } v when double.TryParse(v.String, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
        _ => fallback,
    };

    public float F(int i, float fallback = 0) => (float)Num(i, fallback);
    public int Int(int i, int fallback = 0) => (int)Num(i, fallback);
    public bool Bool(int i) => this[i].CastToBool();
}

/// <summary>
/// Lua side of UI objects: each object is a table whose metatable indexes the method table of its type, like the
/// client's per-class method tables (CSimpleFrame::RegisterScriptMethods and friends). Booleans are 1 / nil as in 1.12.
/// </summary>
public sealed class LuaBindings
{
    private readonly UiScreen _ui;
    private readonly Dictionary<Table, UiObject> _objects = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Type, Dictionary<string, Func<UiObject, LuaArgs, DynValue>>> _methods = [];
    private readonly Dictionary<Type, Table> _metatables = [];

    private static readonly string[] ModelNoOps =
    [
        "ClearModel", "SetSequence", "SetSequenceTime", "SetCamera", "SetPosition", "SetLight", "SetFogNear", "SetFogFar",
        "ClearFog", "ReplaceIconTexture", "AdvanceTime", "SetUnit", "RefreshUnit", "SetRotation", "SetCreature",
        "Dress", "Undress", "TryOn", "InitializeTabardColors", "Save", "CycleVariation", "AddCharacterLight",
        "AddLight", "AddPetLight", "ResetLights", "SetModelScale", "SetFacing",
    ];

    public LuaBindings(UiScreen ui)
    {
        _ui = ui;
        RegisterObject();
        RegisterRegion();
        RegisterTexture();
        RegisterFont();
        RegisterFrame();
        RegisterButton();
        RegisterEditBox();
        RegisterBars();
        RegisterScrollFrame();
        RegisterOthers();
    }

    private Script Script => _ui.Lua.Script;

    public Table Bind(UiObject obj)
    {
        var table = new Table(Script) { MetaTable = Metatable(obj.GetType()) };
        _objects[table] = obj;
        return table;
    }

    public UiObject? ObjectOf(DynValue value) => value switch
    {
        { Type: DataType.Table } t => _objects.GetValueOrDefault(t.Table),
        { Type: DataType.String } s => _ui.FindObject(s.String),
        _ => null,
    };

    public T? ObjectOf<T>(DynValue value) where T : UiObject => ObjectOf(value) as T;

    public static DynValue N(double value) => DynValue.NewNumber(value);
    public static DynValue S(string? value) => value is null ? DynValue.Nil : DynValue.NewString(value);
    public static DynValue B(bool value) => value ? DynValue.NewNumber(1) : DynValue.Nil;
    public static DynValue O(UiObject? obj) => obj?.Table is { } t ? DynValue.NewTable(t) : DynValue.Nil;
    public static DynValue Tuple(params DynValue[] values) => DynValue.NewTuple(values);
    public static DynValue Color(Color4 c) => Tuple(N(c.R), N(c.G), N(c.B), N(c.A));

    private void Def<T>(string name, Func<T, LuaArgs, DynValue> method) where T : UiObject
    {
        if (!_methods.TryGetValue(typeof(T), out var table))
            _methods[typeof(T)] = table = new Dictionary<string, Func<UiObject, LuaArgs, DynValue>>();
        table[name] = (obj, args) => method((T)obj, args);
    }

    private void Def<T>(string name, Action<T, LuaArgs> method) where T : UiObject =>
        Def<T>(name, (obj, args) => { method(obj, args); return DynValue.Nil; });

    private Table Metatable(Type type)
    {
        if (_metatables.TryGetValue(type, out var meta))
            return meta;
        var methods = new Table(Script);
        var chain = new List<Type>();
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            chain.Insert(0, t);
        foreach (var t in chain)
        {
            if (!_methods.TryGetValue(t, out var defs))
                continue;
            foreach (var (name, method) in defs)
            {
                var methodName = name;
                methods[name] = DynValue.NewCallback((_, args) =>
                {
                    if (args.Count == 0 || ObjectOf(args[0]) is not { } self || !type.IsInstanceOfType(self))
                        throw new ScriptRuntimeException($"Usage: obj:{methodName}(...) called without a valid object");
                    return method(self, new LuaArgs(args, 1));
                }, name);
            }
        }
        meta = new Table(Script) { ["__index"] = methods };
        _metatables[type] = meta;
        return meta;
    }

    private static FramePoint Point(LuaArgs args, int i)
    {
        if (UiEnums.TryPoint(args.Str(i), out var point))
            return point;
        throw new ScriptRuntimeException($"Invalid anchor point: {args.Str(i)}");
    }

    private static Color4 ColorArgs(LuaArgs args, int first, float alpha = 1f) =>
        new(args.F(first), args.F(first + 1), args.F(first + 2), args.F(first + 3, alpha));

    private void RegisterObject()
    {
        Def<UiObject>("GetName", (o, _) => S(o.Name));
        Def<UiObject>("GetObjectType", (o, _) => S(o.ObjectType));
        Def<UiObject>("IsObjectType", (o, a) =>
        {
            var name = a.Str(0) ?? "";
            for (var t = o.GetType(); t is not null && t != typeof(object); t = t.BaseType)
            {
                var typeName = t == o.GetType() ? o.ObjectType : t == typeof(Frame) ? "Frame" : t.Name;
                if (typeName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return B(true);
            }
            return B(name.Equals("UIObject", StringComparison.OrdinalIgnoreCase) || name.Equals("Region", StringComparison.OrdinalIgnoreCase) && o is Region);
        });
    }

    private void RegisterRegion()
    {
        Def<Region>("GetParent", (r, _) => O(r.Parent));
        Def<Region>("SetPoint", (r, a) =>
        {
            var point = Point(a, 0);
            Region? relative = r.Parent;
            var relativePoint = point;
            var next = 1;
            if (a.Count > 1 && !a.IsNumber(1))
            {
                if (a.Has(1))
                    relative = ObjectOf<Region>(a[1]) ?? throw new ScriptRuntimeException($"{r.Name}:SetPoint(): Couldn't find region named '{a.Str(1)}'");
                next = 2;
                if (a.Str(2) is { } rp && !a.IsNumber(2))
                {
                    relativePoint = Point(a, 2);
                    next = 3;
                }
            }
            r.SetPoint(point, relative, relativePoint, a.F(next), a.F(next + 1));
        });
        Def<Region>("SetAllPoints", (r, a) => r.SetAllPoints(a.Has(0) ? ObjectOf<Region>(a[0]) : r.Parent));
        Def<Region>("ClearAllPoints", (r, _) => r.ClearAllPoints());
        Def<Region>("GetNumPoints", (r, _) => N(r.Points.Count()));
        Def<Region>("GetPoint", (r, a) =>
        {
            var anchor = r.Points.ElementAtOrDefault(a.Int(0, 1) - 1);
            return anchor is null
                ? DynValue.Nil
                : Tuple(S(UiEnums.Name(anchor.Point)), O(anchor.RelativeTo ?? r.Parent), S(UiEnums.Name(anchor.RelativePoint)), N(anchor.X), N(anchor.Y));
        });
        Def<Region>("GetWidth", (r, _) => N(r.GetWidth()));
        Def<Region>("GetHeight", (r, _) => N(r.GetHeight()));
        Def<Region>("SetWidth", (r, a) => r.Width = a.F(0));
        Def<Region>("SetHeight", (r, a) => r.Height = a.F(0));
        Def<Region>("GetLeft", (r, _) => r.Rect is { } x ? N(x.Left) : DynValue.Nil);
        Def<Region>("GetRight", (r, _) => r.Rect is { } x ? N(x.Right) : DynValue.Nil);
        Def<Region>("GetTop", (r, _) => r.Rect is { } x ? N(x.Top) : DynValue.Nil);
        Def<Region>("GetBottom", (r, _) => r.Rect is { } x ? N(x.Bottom) : DynValue.Nil);
        Def<Region>("GetCenter", (r, _) => r.Rect is { } x ? Tuple(N((x.Left + x.Right) / 2), N((x.Top + x.Bottom) / 2)) : DynValue.Nil);
        Def<Region>("IsShown", (r, _) => B(r.Shown));
        Def<Region>("IsVisible", (r, _) => B(r.IsVisible));
        Def<Region>("Show", (r, _) => { if (r is Frame f) f.Show(); else ((LayeredRegion)r).Show(); });
        Def<Region>("Hide", (r, _) => { if (r is Frame f) f.Hide(); else ((LayeredRegion)r).Hide(); });

        Def<LayeredRegion>("SetVertexColor", (r, a) => r.VertexColor = ColorArgs(a, 0));
        Def<LayeredRegion>("GetVertexColor", (r, _) => Color(r.VertexColor));
        Def<LayeredRegion>("SetAlpha", (r, a) => r.Alpha = Math.Clamp(a.F(0, 1), 0, 1));
        Def<LayeredRegion>("GetAlpha", (r, _) => N(r.Alpha));
        Def<LayeredRegion>("GetDrawLayer", (r, _) => S(UiEnums.Name(r.Layer)));
        Def<LayeredRegion>("SetDrawLayer", (r, a) => { if (UiEnums.TryLayer(a.Str(0), out var layer)) r.Layer = layer; });
    }

    private void RegisterTexture()
    {
        Def<Texture>("SetTexture", (t, a) =>
        {
            if (a.IsNumber(0))
            {
                t.SetColorTexture(ColorArgs(a, 0));
                return B(true);
            }
            return B(t.SetTexture(a.Str(0)));
        });
        Def<Texture>("GetTexture", (t, _) => S(t.File));
        Def<Texture>("SetTexCoord", (t, a) =>
        {
            if (a.Count >= 8)
                t.SetTexCoord([a.F(0), a.F(1), a.F(2), a.F(3), a.F(4), a.F(5), a.F(6), a.F(7)]);
            else
                t.SetTexCoord(a.F(0), a.F(1), a.F(2), a.F(3));
        });
        Def<Texture>("GetTexCoord", (t, _) => Tuple(t.TexCoords.SelectMany(c => new[] { N(c.X), N(c.Y) }).ToArray()));
        Def<Texture>("SetBlendMode", (t, a) => { if (UiEnums.TryBlend(a.Str(0), out var mode)) t.Blend = mode; });
        Def<Texture>("GetBlendMode", (t, _) => S(t.Blend.ToString().ToUpperInvariant()));
        Def<Texture>("SetDesaturated", (t, a) => { t.Desaturated = a.Bool(0); return B(true); });
        Def<Texture>("IsDesaturated", (t, _) => B(t.Desaturated));
        Def<Texture>("SetGradient", (_, _) => { });
        Def<Texture>("SetGradientAlpha", (_, _) => { });
    }

    private void RegisterFont()
    {
        Def<FontString>("SetText", (f, a) => f.Text = a.Str(0) ?? "");
        Def<FontString>("GetText", (f, _) => f.Text.Length == 0 ? DynValue.Nil : S(f.Text));
        Def<FontString>("GetStringWidth", (f, _) => N(f.StringWidth));
        Def<FontString>("SetFontObject", (f, a) => f.SetFontObject(ObjectOf<FontObject>(a[0])));
        Def<FontString>("GetFontObject", (f, _) => O(f.FontObject));
        Def<FontString>("SetNonSpaceWrap", (f, a) => f.NonSpaceWrap = a.Bool(0));
        Def<FontString>("SetTextHeight", (f, a) => { f.Font.Height = a.F(0); _ui.InvalidateLayout(); });
        DefFontMethods<FontString>(f => f.Font);
        DefFontMethods<FontObject>(f => f.Font);
        Def<FontObject>("SetFontObject", (f, a) => { if (ObjectOf<FontObject>(a[0]) is { } other) f.Font = other.Font.Clone(); });
        Def<FontObject>("CopyFontObject", (f, a) => { if (ObjectOf<FontObject>(a[0]) is { } other) f.Font = other.Font.Clone(); });
    }

    private void DefFontMethods<T>(Func<T, FontInfo> font) where T : UiObject
    {
        Def<T>("SetFont", (o, a) =>
        {
            var info = font(o);
            info.File = a.Str(0) is { } file ? UiPath.Normalize(file) : info.File;
            info.Height = a.F(1, info.Height);
            info.Flags = a.Str(2) ?? "";
            _ui.InvalidateLayout();
            return B(true);
        });
        Def<T>("GetFont", (o, _) => Tuple(S(font(o).File), N(font(o).Height), S(font(o).Flags)));
        Def<T>("SetTextColor", (o, a) => font(o).Color = ColorArgs(a, 0));
        Def<T>("GetTextColor", (o, _) => Color(font(o).Color));
        Def<T>("SetShadowColor", (o, a) => font(o).ShadowColor = ColorArgs(a, 0));
        Def<T>("GetShadowColor", (o, _) => Color(font(o).ShadowColor ?? new Color4(0, 0, 0, 0)));
        Def<T>("SetShadowOffset", (o, a) => font(o).ShadowOffset = new Vector2(a.F(0), a.F(1)));
        Def<T>("GetShadowOffset", (o, _) => Tuple(N(font(o).ShadowOffset.X), N(font(o).ShadowOffset.Y)));
        Def<T>("SetJustifyH", (o, a) => font(o).JustifyH = (a.Str(0) ?? "CENTER").ToUpperInvariant());
        Def<T>("GetJustifyH", (o, _) => S(font(o).JustifyH));
        Def<T>("SetJustifyV", (o, a) => font(o).JustifyV = (a.Str(0) ?? "MIDDLE").ToUpperInvariant());
        Def<T>("GetJustifyV", (o, _) => S(font(o).JustifyV));
        Def<T>("SetSpacing", (o, a) => font(o).Spacing = a.F(0));
        Def<T>("GetSpacing", (o, _) => N(font(o).Spacing));
    }

    private void RegisterFrame()
    {
        Def<Frame>("RegisterEvent", (f, a) => f.RegisterEvent(a.Str(0) ?? ""));
        Def<Frame>("UnregisterEvent", (f, a) => f.UnregisterEvent(a.Str(0) ?? ""));
        Def<Frame>("UnregisterAllEvents", (f, _) => { foreach (var e in f.Events.ToList()) f.UnregisterEvent(e); });
        Def<Frame>("IsEventRegistered", (f, a) => B(f.Events.Contains(a.Str(0) ?? "")));
        Def<Frame>("SetScript", (f, a) =>
        {
            var name = a.Str(0) ?? "";
            if (!f.SupportsScript(name))
                throw new ScriptRuntimeException($"{f.ObjectType} doesn't have a \"{name}\" script");
            f.SetScript(name, a[1].Type == DataType.Function ? a[1] : null);
        });
        Def<Frame>("GetScript", (f, a) => f.GetScript(a.Str(0) ?? "") ?? DynValue.Nil);
        Def<Frame>("HasScript", (f, a) => B(f.SupportsScript(a.Str(0) ?? "")));
        Def<Frame>("SetParent", (f, a) => f.SetParent(ObjectOf<Frame>(a[0])));
        Def<Frame>("GetID", (f, _) => N(f.Id));
        Def<Frame>("SetID", (f, a) => f.Id = a.Int(0));
        Def<Frame>("SetAlpha", (f, a) => f.Alpha = Math.Clamp(a.F(0, 1), 0, 1));
        Def<Frame>("GetAlpha", (f, _) => N(f.Alpha));
        Def<Frame>("GetEffectiveAlpha", (f, _) => N(f.EffectiveAlpha));
        Def<Frame>("SetScale", (_, _) => { });
        Def<Frame>("GetScale", (_, _) => N(1));
        Def<Frame>("GetEffectiveScale", (_, _) => N(1));
        Def<Frame>("SetFrameLevel", (f, a) => f.Level = a.Int(0));
        Def<Frame>("GetFrameLevel", (f, _) => N(f.Level));
        Def<Frame>("SetFrameStrata", (f, a) => { if (UiEnums.TryStrata(a.Str(0), out var s)) f.Strata = s; });
        Def<Frame>("GetFrameStrata", (f, _) => S(UiEnums.Name(f.Strata)));
        Def<Frame>("SetToplevel", (f, a) => f.Toplevel = a.Bool(0));
        Def<Frame>("IsToplevel", (f, _) => B(f.Toplevel));
        Def<Frame>("Raise", (_, _) => { });
        Def<Frame>("Lower", (_, _) => { });
        Def<Frame>("EnableMouse", (f, a) => f.MouseEnabled = a.Bool(0));
        Def<Frame>("IsMouseEnabled", (f, _) => B(f.MouseEnabled));
        Def<Frame>("EnableKeyboard", (f, a) => f.KeyboardEnabled = a.Bool(0));
        Def<Frame>("IsKeyboardEnabled", (f, _) => B(f.KeyboardEnabled));
        Def<Frame>("EnableMouseWheel", (f, a) => f.MouseWheelEnabled = a.Bool(0));
        Def<Frame>("IsMouseWheelEnabled", (f, _) => B(f.MouseWheelEnabled));
        Def<Frame>("SetMovable", (f, a) => f.Movable = a.Bool(0));
        Def<Frame>("IsMovable", (f, _) => B(f.Movable));
        Def<Frame>("SetResizable", (f, a) => f.Resizable = a.Bool(0));
        Def<Frame>("IsResizable", (f, _) => B(f.Resizable));
        Def<Frame>("SetClampedToScreen", (f, a) => f.ClampedToScreen = a.Bool(0));
        Def<Frame>("RegisterForDrag", (_, _) => { });
        Def<Frame>("StartMoving", (_, _) => { });
        Def<Frame>("StartSizing", (_, _) => { });
        Def<Frame>("StopMovingOrSizing", (_, _) => { });
        Def<Frame>("SetUserPlaced", (_, _) => { });
        Def<Frame>("SetMinResize", (_, _) => { });
        Def<Frame>("SetMaxResize", (_, _) => { });
        Def<Frame>("SetHitRectInsets", (f, a) => f.HitRectInsets = new Vector4(a.F(0), a.F(1), a.F(2), a.F(3)));
        Def<Frame>("GetHitRectInsets", (f, _) => Tuple(N(f.HitRectInsets.X), N(f.HitRectInsets.Y), N(f.HitRectInsets.Z), N(f.HitRectInsets.W)));
        Def<Frame>("GetChildren", (f, _) => Tuple(f.Children.Select(O).ToArray()));
        Def<Frame>("GetNumChildren", (f, _) => N(f.Children.Count));
        Def<Frame>("GetRegions", (f, _) => Tuple(f.Regions.Select(O).ToArray()));
        Def<Frame>("GetNumRegions", (f, _) => N(f.Regions.Count));
        Def<Frame>("CreateTexture", (f, a) =>
        {
            var node = new XElement("Texture");
            if (a.Str(0) is { } name) node.SetAttributeValue("name", name);
            if (a.Str(2) is { } inherits) node.SetAttributeValue("inherits", inherits);
            return O(_ui.Loader.CreateTexture(node, f, UiEnums.TryLayer(a.Str(1), out var layer) ? layer : DrawLayer.Artwork, defaultAnchors: false));
        });
        Def<Frame>("CreateFontString", (f, a) =>
        {
            var node = new XElement("FontString");
            if (a.Str(0) is { } name) node.SetAttributeValue("name", name);
            if (a.Str(2) is { } inherits) node.SetAttributeValue("inherits", inherits);
            return O(_ui.Loader.CreateFontString(node, f, UiEnums.TryLayer(a.Str(1), out var layer) ? layer : DrawLayer.Artwork, defaultAnchors: false));
        });
        Def<Frame>("SetBackdrop", (f, a) =>
        {
            if (a[0].Type != DataType.Table)
            {
                f.Backdrop = null;
                return;
            }
            var t = a[0].Table;
            var insets = t.Get("insets");
            f.Backdrop = new Backdrop
            {
                BgFile = _ui.ResolveTexture(t.Get("bgFile").CastToString()),
                EdgeFile = _ui.ResolveTexture(t.Get("edgeFile").CastToString()),
                Tile = t.Get("tile").CastToBool(),
                TileSize = (float)(t.Get("tileSize").CastToNumber() ?? 0),
                EdgeSize = (float)(t.Get("edgeSize").CastToNumber() ?? 0),
                Insets = insets.Type == DataType.Table
                    ? new Vector4(Num(insets.Table, "left"), Num(insets.Table, "right"), Num(insets.Table, "top"), Num(insets.Table, "bottom"))
                    : Vector4.Zero,
            };
        });
        Def<Frame>("SetBackdropColor", (f, a) => { if (f.Backdrop is { } b) b.Color = ColorArgs(a, 0); });
        Def<Frame>("SetBackdropBorderColor", (f, a) => { if (f.Backdrop is { } b) b.BorderColor = ColorArgs(a, 0); });
        Def<Frame>("GetBackdropColor", (f, _) => Color(f.Backdrop?.Color ?? Color4.White));
        Def<Frame>("GetBackdropBorderColor", (f, _) => Color(f.Backdrop?.BorderColor ?? Color4.White));
        Def<Frame>("IsFrameType", (f, a) => B(f.ObjectType.Equals(a.Str(0), StringComparison.OrdinalIgnoreCase)));
    }

    private static float Num(Table t, string key) => (float)(t.Get(key).CastToNumber() ?? 0);

    private Texture? TextureArg(Button button, LuaArgs a, Texture? existing, DrawLayer layer)
    {
        if (ObjectOf<Texture>(a[0]) is { } texture)
        {
            if (existing is not null && existing != texture)
                button.Regions.Remove(existing);
            if (texture.Parent != button)
                button.AddRegion(texture, layer);
            return texture;
        }
        if (a.Str(0) is not { } file)
            return existing;
        if (existing is null)
        {
            existing = button.AddRegion(new Texture(_ui), layer);
            existing.SetAllPoints(null);
        }
        existing.SetTexture(file);
        return existing;
    }

    private void RegisterButton()
    {
        Def<Button>("SetText", (b, a) => b.SetText(a.Str(0)));
        Def<Button>("GetText", (b, _) => b.Text.Length == 0 ? DynValue.Nil : S(b.Text));
        Def<Button>("GetTextWidth", (b, _) => N(b.TextString?.StringWidth ?? 0));
        Def<Button>("GetTextHeight", (b, _) => N(b.TextString?.Font.Height ?? 0));
        Def<Button>("GetFontString", (b, _) => O(b.TextString));
        Def<Button>("GetTextFontString", (b, _) => O(b.TextString));
        Def<Button>("SetFontString", (b, a) => { if (ObjectOf<FontString>(a[0]) is { } fs) b.TextString = fs; });
        Def<Button>("SetTextColor", (b, a) => b.NormalColor = ColorArgs(a, 0));
        Def<Button>("SetDisabledTextColor", (b, a) => b.DisabledColor = ColorArgs(a, 0));
        Def<Button>("SetHighlightTextColor", (b, a) => b.HighlightColor = ColorArgs(a, 0));
        Def<Button>("GetTextColor", (b, _) => Color(b.NormalColor ?? b.TextString?.Font.Color ?? Color4.White));
        Def<Button>("SetTextFontObject", (b, a) => { if (ObjectOf<FontObject>(a[0]) is { } f) { b.NormalFont = f.Font.Clone(); b.TextString?.SetFontObject(f); } });
        Def<Button>("SetDisabledFontObject", (b, a) => b.DisabledFont = ObjectOf<FontObject>(a[0])?.Font.Clone());
        Def<Button>("SetHighlightFontObject", (b, a) => b.HighlightFont = ObjectOf<FontObject>(a[0])?.Font.Clone());
        Def<Button>("Enable", (b, _) => b.Enabled = true);
        Def<Button>("Disable", (b, _) => b.Enabled = false);
        Def<Button>("IsEnabled", (b, _) => B(b.Enabled));
        Def<Button>("Click", (b, a) => b.Click(a.Str(0) ?? "LeftButton"));
        Def<Button>("LockHighlight", (b, _) => b.HighlightLocked = true);
        Def<Button>("UnlockHighlight", (b, _) => b.HighlightLocked = false);
        Def<Button>("SetButtonState", (b, a) => b.Pushed = (a.Str(0) ?? "").Equals("PUSHED", StringComparison.OrdinalIgnoreCase));
        Def<Button>("GetButtonState", (b, _) => S(b.ButtonState));
        Def<Button>("RegisterForClicks", (_, _) => { });
        Def<Button>("SetNormalTexture", (b, a) => b.NormalTexture = TextureArg(b, a, b.NormalTexture, DrawLayer.Artwork));
        Def<Button>("SetPushedTexture", (b, a) => b.PushedTexture = TextureArg(b, a, b.PushedTexture, DrawLayer.Artwork));
        Def<Button>("SetDisabledTexture", (b, a) => b.DisabledTexture = TextureArg(b, a, b.DisabledTexture, DrawLayer.Artwork));
        Def<Button>("SetHighlightTexture", (b, a) =>
        {
            b.HighlightTexture = TextureArg(b, a, b.HighlightTexture, DrawLayer.Highlight);
            if (b.HighlightTexture is { } t && UiEnums.TryBlend(a.Str(1), out var mode))
                t.Blend = mode;
        });
        Def<Button>("GetNormalTexture", (b, _) => O(b.NormalTexture));
        Def<Button>("GetPushedTexture", (b, _) => O(b.PushedTexture));
        Def<Button>("GetDisabledTexture", (b, _) => O(b.DisabledTexture));
        Def<Button>("GetHighlightTexture", (b, _) => O(b.HighlightTexture));
        Def<Button>("SetPushedTextOffset", (b, a) => b.PushedTextOffset = new Vector2(a.F(0), a.F(1)));

        Def<CheckButton>("SetChecked", (c, a) => c.Checked = a.Bool(0));
        Def<CheckButton>("GetChecked", (c, _) => B(c.Checked));
        Def<CheckButton>("SetCheckedTexture", (c, a) => c.CheckedTexture = TextureArg(c, a, c.CheckedTexture, DrawLayer.Overlay));
        Def<CheckButton>("SetDisabledCheckedTexture", (c, a) => c.DisabledCheckedTexture = TextureArg(c, a, c.DisabledCheckedTexture, DrawLayer.Overlay));
        Def<CheckButton>("GetCheckedTexture", (c, _) => O(c.CheckedTexture));
        Def<CheckButton>("GetDisabledCheckedTexture", (c, _) => O(c.DisabledCheckedTexture));
    }

    private void RegisterEditBox()
    {
        Def<EditBox>("SetText", (e, a) => e.SetText(a.Str(0)));
        Def<EditBox>("GetText", (e, _) => S(e.Text));
        Def<EditBox>("GetNumber", (e, _) => N(double.TryParse(e.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0));
        Def<EditBox>("SetNumber", (e, a) => e.SetText(a.Num(0).ToString(CultureInfo.InvariantCulture)));
        Def<EditBox>("GetNumLetters", (e, _) => N(e.Text.Length));
        Def<EditBox>("Insert", (e, a) => e.Insert(a.Str(0) ?? ""));
        Def<EditBox>("SetFocus", (e, _) => _ui.SetKeyboardFocus(e));
        Def<EditBox>("ClearFocus", (e, _) => { if (e.HasFocus) _ui.SetKeyboardFocus(null); });
        Def<EditBox>("HighlightText", (e, a) => e.Highlight = (a.Int(0), a.Int(1, e.Text.Length)));
        Def<EditBox>("SetMaxLetters", (e, a) => e.MaxLetters = a.Int(0));
        Def<EditBox>("GetMaxLetters", (e, _) => N(e.MaxLetters));
        Def<EditBox>("SetNumeric", (e, a) => e.Numeric = a.Bool(0));
        Def<EditBox>("IsNumeric", (e, _) => B(e.Numeric));
        Def<EditBox>("SetPassword", (e, a) => e.Password = a.Bool(0));
        Def<EditBox>("IsPassword", (e, _) => B(e.Password));
        Def<EditBox>("SetMultiLine", (e, a) => e.MultiLine = a.Bool(0));
        Def<EditBox>("IsMultiLine", (e, _) => B(e.MultiLine));
        Def<EditBox>("SetAutoFocus", (e, a) => e.AutoFocus = a.Bool(0));
        Def<EditBox>("IsAutoFocus", (e, _) => B(e.AutoFocus));
        Def<EditBox>("AddHistoryLine", (_, _) => { });
        Def<EditBox>("SetHistoryLines", (e, a) => e.HistoryLines = a.Int(0));
        Def<EditBox>("SetTextInsets", (e, a) => e.TextInsets = new Vector4(a.F(0), a.F(1), a.F(2), a.F(3)));
        Def<EditBox>("SetFontObject", (e, a) => e.FontString.SetFontObject(ObjectOf<FontObject>(a[0])));
        Def<EditBox>("SetTextColor", (e, a) => e.FontString.Font.Color = ColorArgs(a, 0));
        Def<EditBox>("SetFont", (e, a) =>
        {
            e.FontString.Font.File = a.Str(0) is { } file ? UiPath.Normalize(file) : e.FontString.Font.File;
            e.FontString.Font.Height = a.F(1, e.FontString.Font.Height);
            e.FontString.Font.Flags = a.Str(2) ?? "";
        });
        Def<EditBox>("SetJustifyH", (e, a) => e.FontString.Font.JustifyH = (a.Str(0) ?? "LEFT").ToUpperInvariant());
        Def<EditBox>("GetInputLanguage", (_, _) => S("ROMAN"));
        Def<EditBox>("ToggleInputLanguage", (_, _) => { });
    }

    private void RegisterBars()
    {
        Def<Slider>("SetMinMaxValues", (s, a) => s.SetMinMax(a.F(0), a.F(1)));
        Def<Slider>("GetMinMaxValues", (s, _) => Tuple(N(s.MinValue), N(s.MaxValue)));
        Def<Slider>("SetValue", (s, a) => s.SetValue(a.F(0)));
        Def<Slider>("GetValue", (s, _) => N(s.Value));
        Def<Slider>("SetValueStep", (s, a) => s.ValueStep = a.F(0));
        Def<Slider>("GetValueStep", (s, _) => N(s.ValueStep));
        Def<Slider>("SetOrientation", (s, a) => s.Orientation = a.Str(0) ?? "VERTICAL");
        Def<Slider>("GetOrientation", (s, _) => S(s.Orientation));
        Def<Slider>("GetThumbTexture", (s, _) => O(s.ThumbTexture));
        Def<Slider>("SetThumbTexture", (s, a) =>
        {
            if (ObjectOf<Texture>(a[0]) is { } t) s.ThumbTexture = t;
            else if (a.Str(0) is { } file)
            {
                s.ThumbTexture ??= s.AddRegion(new Texture(_ui), DrawLayer.Overlay);
                s.ThumbTexture.SetTexture(file);
            }
            s.PlaceThumb();
        });
        Def<Slider>("Enable", (s, _) => s.Enabled = true);
        Def<Slider>("Disable", (s, _) => s.Enabled = false);

        Def<StatusBar>("SetMinMaxValues", (s, a) => s.SetMinMax(a.F(0), a.F(1)));
        Def<StatusBar>("GetMinMaxValues", (s, _) => Tuple(N(s.MinValue), N(s.MaxValue)));
        Def<StatusBar>("SetValue", (s, a) => s.SetValue(a.F(0)));
        Def<StatusBar>("GetValue", (s, _) => N(s.Value));
        Def<StatusBar>("SetOrientation", (s, a) => { s.Orientation = a.Str(0) ?? "HORIZONTAL"; s.PlaceBar(); });
        Def<StatusBar>("SetStatusBarColor", (s, a) => { if (s.BarTexture is { } t) t.VertexColor = ColorArgs(a, 0); });
        Def<StatusBar>("GetStatusBarTexture", (s, _) => O(s.BarTexture));
        Def<StatusBar>("SetStatusBarTexture", (s, a) =>
        {
            if (ObjectOf<Texture>(a[0]) is { } t) s.SetBarTexture(t);
            else if (a.Str(0) is { } file)
            {
                var texture = s.BarTexture ?? new Texture(_ui);
                texture.SetTexture(file);
                s.SetBarTexture(texture);
            }
        });
    }

    private void RegisterScrollFrame()
    {
        Def<ScrollFrame>("SetScrollChild", (s, a) => { if (ObjectOf<Frame>(a[0]) is { } child) s.SetScrollChild(child); });
        Def<ScrollFrame>("GetScrollChild", (s, _) => O(s.ScrollChild));
        Def<ScrollFrame>("SetVerticalScroll", (s, a) => s.SetVerticalScroll(a.F(0)));
        Def<ScrollFrame>("GetVerticalScroll", (s, _) => N(s.VerticalScroll));
        Def<ScrollFrame>("SetHorizontalScroll", (s, a) => s.SetHorizontalScroll(a.F(0)));
        Def<ScrollFrame>("GetHorizontalScroll", (s, _) => N(s.HorizontalScroll));
        Def<ScrollFrame>("GetVerticalScrollRange", (s, _) => N(s.VerticalRange));
        Def<ScrollFrame>("GetHorizontalScrollRange", (s, _) => N(s.HorizontalRange));
        Def<ScrollFrame>("UpdateScrollChildRect", (s, _) => s.UpdateScrollChildRect());
    }

    private void RegisterOthers()
    {
        Def<Model>("SetModel", (m, a) => m.ModelFile = a.Str(0));
        Def<Model>("GetModel", (m, _) => S(m.ModelFile));
        Def<Model>("SetFogColor", (m, a) => m.FogColor = ColorArgs(a, 0));
        Def<Model>("GetFogColor", (m, _) => Color(m.FogColor ?? Color4.Black));
        Def<Model>("GetFacing", (m, _) => N(m.Facing));
        Def<Model>("GetModelScale", (m, _) => N(m.ModelScale));
        Def<Model>("GetPosition", (_, _) => Tuple(N(0), N(0), N(0)));
        foreach (var name in ModelNoOps)
            Def<Model>(name, (_, _) => { });

        Def<MessageFrame>("AddMessage", (m, a) => m.AddMessage(a.Str(0) ?? "", new Color4(a.F(1, 1), a.F(2, 1), a.F(3, 1))));
        Def<MessageFrame>("Clear", (m, _) => m.Messages.Clear());
        Def<MessageFrame>("GetNumMessages", (m, _) => N(m.Messages.Count));
        Def<MessageFrame>("SetMaxLines", (m, a) => m.MaxLines = a.Int(0, 120));
        Def<MessageFrame>("SetFontObject", (m, a) => m.FontString.SetFontObject(ObjectOf<FontObject>(a[0])));
        Def<MessageFrame>("SetJustifyH", (m, a) => m.FontString.Font.JustifyH = (a.Str(0) ?? "CENTER").ToUpperInvariant());
        Def<MessageFrame>("AtBottom", (_, _) => B(true));
        Def<MessageFrame>("AtTop", (_, _) => B(true));
        foreach (var name in new[] { "SetFading", "SetTimeVisible", "SetFadeDuration", "ScrollUp", "ScrollDown", "ScrollToTop", "ScrollToBottom", "PageUp", "PageDown", "SetInsertMode", "UpdateColorByID" })
            Def<MessageFrame>(name, (_, _) => { });

        Def<SimpleHtml>("SetText", (h, a) => h.SetText(a.Str(0) ?? ""));
        Def<SimpleHtml>("SetFontObject", (h, a) => h.FontString.SetFontObject(ObjectOf<FontObject>(a[a.Count > 1 ? 1 : 0])));
        Def<SimpleHtml>("SetTextColor", (h, a) => h.FontString.Font.Color = ColorArgs(a, a.IsNumber(0) ? 0 : 1));
        Def<SimpleHtml>("SetHyperlinkFormat", (_, _) => { });
        Def<SimpleHtml>("SetSpacing", (_, _) => { });

        Def<GameTooltip>("SetOwner", (t, a) => { t.Owner = ObjectOf<Frame>(a[0]); t.Lines.Clear(); });
        Def<GameTooltip>("IsOwned", (t, a) => B(t.Owner == ObjectOf<Frame>(a[0])));
        Def<GameTooltip>("GetOwner", (t, _) => O(t.Owner));
        Def<GameTooltip>("ClearLines", (t, _) => t.Lines.Clear());
        Def<GameTooltip>("NumLines", (t, _) => N(t.Lines.Count));
        Def<GameTooltip>("SetText", (t, a) => { t.Lines.Clear(); t.Lines.Add((a.Str(0) ?? "", null, new Color4(a.F(1, 1), a.F(2, 0.82f), a.F(3, 0)))); });
        Def<GameTooltip>("AddLine", (t, a) => t.Lines.Add((a.Str(0) ?? "", null, new Color4(a.F(1, 1), a.F(2, 0.82f), a.F(3, 0)))));
        Def<GameTooltip>("AddDoubleLine", (t, a) => t.Lines.Add((a.Str(0) ?? "", a.Str(1), new Color4(a.F(2, 1), a.F(3, 0.82f), a.F(4, 0)))));

        Def<ColorSelect>("SetColorRGB", (_, _) => { });
        Def<ColorSelect>("GetColorRGB", (_, _) => Tuple(N(1), N(1), N(1)));
        Def<ColorSelect>("SetColorHSV", (_, _) => { });
        Def<ColorSelect>("GetColorHSV", (_, _) => Tuple(N(0), N(0), N(1)));
    }
}
