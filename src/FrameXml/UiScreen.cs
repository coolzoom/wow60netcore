using System.Numerics;
using System.Xml.Linq;
using FrameXml.Lua;
using FrameXml.Objects;
using MoonSharp.Interpreter;

namespace FrameXml;

/// <summary>
/// One UI state (the glue screens or the in-game FrameXML): Lua state, named objects, virtual templates, fonts,
/// events and input focus. Coordinates are UI units, 768 high with the origin at the bottom left.
/// </summary>
public sealed class UiScreen
{
    public const float ScreenHeight = 768f;
    public const string GlueToc = @"Interface\GlueXML\GlueXML.toc";
    public const string FrameXmlToc = @"Interface\FrameXML\FrameXML.toc";

    /// <summary>Glue events in the client's order (table at 0xb41e70, filled by FUN_0046cc60).</summary>
    public static readonly IReadOnlyList<string> GlueEvents =
    [
        "SET_GLUE_SCREEN", "START_GLUE_MUSIC", "DISCONNECTED_FROM_SERVER", "OPEN_STATUS_DIALOG", "UPDATE_STATUS_DIALOG",
        "CLOSE_STATUS_DIALOG", "ADDON_LIST_UPDATE", "CHARACTER_LIST_UPDATE", "UPDATE_SELECTED_CHARACTER", "OPEN_REALM_LIST",
        "GET_PREFERRED_REALM_INFO", "UPDATE_SELECTED_RACE", "SELECT_LAST_CHARACTER", "SELECT_FIRST_CHARACTER",
        "GLUE_SCREENSHOT_SUCCEEDED", "GLUE_SCREENSHOT_FAILED", "PATCH_UPDATE_PROGRESS", "PATCH_DOWNLOADED", "SUGGEST_REALM",
        "SUGGEST_REALM_WRONG_PVP", "SUGGEST_REALM_WRONG_CATEGORY", "SHOW_SERVER_ALERT", "FRAMES_LOADED",
        "FORCE_RENAME_CHARACTER", "SHOW_SURVEY_NOTIFICATION", "PLAYER_ENTER_PIN",
    ];

    private readonly Dictionary<string, XElement> _virtuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FontObject> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UiObject> _named = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Frame>> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pressedButtons = [];
    private int _serial;
    private Frame? _pressed;

    public UiScreen(IUiFileSource files, UiLog? log = null)
    {
        Files = files;
        Log = log ?? new UiLog();
        Lua = new LuaEngine(Log);
        Bindings = new LuaBindings(this);
        Loader = new UiLoader(this);
        RegisterFactories();
        CoreApi.Register(this);
    }

    public IUiFileSource Files { get; }
    public UiLog Log { get; }
    public LuaEngine Lua { get; }
    public LuaBindings Bindings { get; }
    public UiLoader Loader { get; }
    public Dictionary<string, Func<UiScreen, Frame>> Factories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Frame> Frames { get; } = [];
    public IReadOnlyCollection<string> VirtualNames => _virtuals.Keys;
    public IReadOnlyCollection<FontObject> FontObjects => _fonts.Values;

    public float Width { get; private set; } = ScreenHeight * 4 / 3;
    public float Height => ScreenHeight;
    public UiRect ScreenRect => new(0, 0, Width, ScreenHeight);
    public int LayoutVersion { get; private set; }

    public Vector2 MousePosition { get; private set; }
    public Frame? MouseFocus { get; private set; }
    public EditBox? KeyboardFocus { get; private set; }

    /// <summary>Text width for layout; the renderer replaces the estimate with real font metrics.</summary>
    public Func<FontInfo, string, float> TextMeasurer { get; set; } = EstimateTextWidth;

    /// <summary>Current glue screen name ("login", "charselect", ...) as reported by SetCurrentScreen.</summary>
    public string? CurrentScreen { get; internal set; }

    public void SetScreenSize(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return;
        Width = ScreenHeight * pixelWidth / pixelHeight;
        InvalidateLayout();
    }

    public void InvalidateLayout() => LayoutVersion++;

    /// <summary>
    /// The glue load sequence of FUN_0046a7b0: frame types, glue API, events, GlueXML.toc, FRAMES_LOADED, then the
    /// glue manager's first screen. The signature check (GlueXML.toc.sig) is skipped.
    /// </summary>
    public bool LoadGlue(GlueApi? api = null, string? toc = null)
    {
        Factories["ModelFFX"] = ui => new ModelFfx(ui);
        (api ?? new GlueApi()).Register(this);
        Log.Info("Skipping signature check of GlueXML.toc");
        if (!Loader.LoadToc(toc ?? GlueToc))
            return false;
        FireEvent("FRAMES_LOADED");
        SetGlueScreen("login");
        return true;
    }

    /// <summary>
    /// The in-game load sequence of FUN_0048fbf0: game frame types, FrameXML.toc, Bindings.xml, then AddOn TOCs.
    /// </summary>
    public bool LoadFrameXml(bool loadAddOns = true)
    {
        Factories["WorldFrame"] = ui => new WorldFrame(ui);
        Factories["GameTooltip"] = ui => new GameTooltip(ui);
        Factories["Minimap"] = ui => new Minimap(ui);
        Factories["PlayerModel"] = ui => new PlayerModel(ui);
        Factories["DressUpModel"] = ui => new DressUpModel(ui);
        Factories["TabardModel"] = ui => new TabardModel(ui);
        Factories["LootButton"] = ui => new LootButton(ui);
        Factories["TaxiRouteFrame"] = ui => new TaxiRouteFrame(ui);
        if (!Loader.LoadToc(FrameXmlToc))
            return false;
        if (loadAddOns)
            foreach (var addOn in AddOns.Discover(Files).Where(a => a.Enabled && !a.LoadOnDemand))
                AddOns.Load(this, addOn);
        FireEvent("VARIABLES_LOADED");
        return true;
    }

    /// <summary>The glue manager changing screens: GlueParent handles SET_GLUE_SCREEN and shows the frame.</summary>
    public void SetGlueScreen(string name) => FireEvent("SET_GLUE_SCREEN", name);

    private void RegisterFactories()
    {
        Factories["Frame"] = ui => new Frame(ui);
        Factories["Button"] = ui => new Button(ui);
        Factories["CheckButton"] = ui => new CheckButton(ui);
        Factories["EditBox"] = ui => new EditBox(ui);
        Factories["MessageFrame"] = ui => new MessageFrame(ui);
        Factories["Model"] = ui => new Model(ui);
        Factories["ScrollFrame"] = ui => new ScrollFrame(ui);
        Factories["ScrollingMessageFrame"] = ui => new ScrollingMessageFrame(ui);
        Factories["Slider"] = ui => new Slider(ui);
        Factories["SimpleHTML"] = ui => new SimpleHtml(ui);
        Factories["StatusBar"] = ui => new StatusBar(ui);
        Factories["ColorSelect"] = ui => new ColorSelect(ui);
        Factories["MovieFrame"] = ui => new MovieFrame(ui);
    }

    public bool RegisterVirtual(string name, XElement node) => _virtuals.TryAdd(name, node);
    public XElement? FindVirtual(string name) => _virtuals.GetValueOrDefault(name);

    public FontObject? FindFont(string name) => _fonts.GetValueOrDefault(name);

    public FontObject GetOrCreateFont(string name)
    {
        if (!_fonts.TryGetValue(name, out var font))
        {
            font = new FontObject(this) { Name = name };
            _fonts[name] = font;
            Register(font);
        }
        return font;
    }

    public UiObject? FindObject(string? name) => name is null ? null : _named.GetValueOrDefault(name);
    public Region? FindRegion(string? name) => FindObject(name) as Region;
    public Frame? FindFrame(string? name) => FindObject(name) as Frame;
    public T? Find<T>(string name) where T : UiObject => FindObject(name) as T;

    /// <summary>Replaces "$parent" with the parent's name (an unnamed parent contributes nothing).</summary>
    public string? ExpandName(string? name, Frame? parent)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        var index = name.IndexOf("$parent", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? name : name[..index] + (parent?.Name ?? "") + name[(index + 7)..];
    }

    internal void InitFrame(Frame frame, string? name, Frame? parent)
    {
        frame.Name = string.IsNullOrEmpty(name) ? null : ExpandName(name, parent);
        frame.SetParent(parent);
        frame.Serial = ++_serial;
        Frames.Add(frame);
        Register(frame);
    }

    internal void InitRegion(LayeredRegion region, string? name, Frame parent)
    {
        region.Name = string.IsNullOrEmpty(name) ? null : ExpandName(name, parent);
        Register(region);
    }

    /// <summary>Named objects become Lua globals.</summary>
    public void Register(UiObject obj)
    {
        if (string.IsNullOrEmpty(obj.Name))
            return;
        _named[obj.Name] = obj;
        Lua.Globals[obj.Name] = obj.Table;
    }

    /// <summary>File name of a texture (".blp" is implied), or null if it doesn't exist.</summary>
    public string? ResolveTexture(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return null;
        var path = UiPath.Normalize(file.Trim());
        if (_textures.TryGetValue(path, out var cached))
            return cached;
        string? resolved = null;
        foreach (var candidate in Path.HasExtension(path) ? [path, Path.ChangeExtension(path, ".blp")] : new[] { path + ".blp" })
            if (Files.Exists(candidate))
            {
                resolved = candidate;
                break;
            }
        _textures[path] = resolved;
        return resolved;
    }

    /// <summary>XML text attributes name a global string when one exists (FUN_00703bf0).</summary>
    public string LocalizedText(string key) => Lua.Globals.Get(key) is { Type: DataType.String } value ? value.String : key;

    public float MeasureText(FontInfo font, string text) => TextMeasurer(font, text);

    public static float EstimateTextWidth(FontInfo font, string text)
    {
        var height = font.Height > 0 ? font.Height : 12;
        float width = 0;
        foreach (var c in TextMarkup.Strip(text))
            width += c > 0x2e80 ? height : height * 0.55f;
        return width;
    }

    public void RegisterEvent(Frame frame, string name)
    {
        if (!_events.TryGetValue(name, out var frames))
            _events[name] = frames = [];
        if (!frames.Contains(frame))
            frames.Add(frame);
        frame.Events.Add(name);
    }

    public void UnregisterEvent(Frame frame, string name)
    {
        if (_events.TryGetValue(name, out var frames))
            frames.Remove(frame);
        frame.Events.Remove(name);
    }

    public bool IsEventRegistered(string name) => _events.TryGetValue(name, out var frames) && frames.Count > 0;

    /// <summary>Sets the globals event and arg1..arg9, then runs OnEvent of every registered frame.</summary>
    public void FireEvent(string name, params object?[] args)
    {
        if (!_events.TryGetValue(name, out var frames) || frames.Count == 0)
            return;
        var values = args.Select(ToLua).ToArray();
        foreach (var frame in frames.ToList())
        {
            Lua.Globals["event"] = name;
            for (var i = 0; i < 9; i++)
                Lua.Globals.Set($"arg{i + 1}", i < values.Length ? values[i] : DynValue.Nil);
            frame.RunScript("OnEvent", values);
        }
    }

    private static DynValue ToLua(object? value) => value switch
    {
        null => DynValue.Nil,
        DynValue v => v,
        string s => DynValue.NewString(s),
        bool b => b ? DynValue.NewNumber(1) : DynValue.Nil,
        int i => DynValue.NewNumber(i),
        float f => DynValue.NewNumber(f),
        double d => DynValue.NewNumber(d),
        _ => DynValue.NewString(value.ToString() ?? ""),
    };

    /// <summary>FUN_00704d50: the handler runs with the global "this" set to the frame; argN globals carry the arguments.</summary>
    internal void RunScript(Frame frame, string name, DynValue handler, DynValue[] args)
    {
        var previous = Lua.Globals.Get("this");
        Lua.Globals["this"] = frame.Table;
        if (!name.Equals("OnEvent", StringComparison.OrdinalIgnoreCase))
            for (var i = 0; i < args.Length && i < 9; i++)
                Lua.Globals.Set($"arg{i + 1}", args[i]);
        Lua.Call(handler, args);
        Lua.Globals.Set("this", previous);
    }

    /// <summary>Runs OnUpdate of every visible frame; arg1 is the elapsed time in seconds.</summary>
    public void Update(float elapsed)
    {
        var argument = DynValue.NewNumber(elapsed);
        foreach (var frame in Frames.ToList())
            if (frame.HasHandler("OnUpdate") && frame.IsVisible)
                frame.RunScript("OnUpdate", argument);
    }

    /// <summary>Visible frames back to front: strata, then level, then creation order.</summary>
    public List<Frame> DrawOrder()
    {
        var visible = new List<Frame>();
        foreach (var frame in Frames)
            if (frame.Parent is null && frame.Shown)
                Collect(frame, visible);
        visible.Sort((a, b) =>
        {
            var c = a.Strata.CompareTo(b.Strata);
            if (c == 0) c = a.Level.CompareTo(b.Level);
            return c != 0 ? c : a.Serial.CompareTo(b.Serial);
        });
        return visible;

        static void Collect(Frame frame, List<Frame> output)
        {
            output.Add(frame);
            foreach (var child in frame.Children)
                if (child.Shown)
                    Collect(child, output);
        }
    }

    public void SetKeyboardFocus(EditBox? box)
    {
        if (KeyboardFocus == box)
            return;
        var previous = KeyboardFocus;
        KeyboardFocus = box;
        previous?.RunScript("OnEditFocusLost");
        box?.RunScript("OnEditFocusGained");
    }

    /// <summary>Topmost visible mouse-enabled frame under the point.</summary>
    public Frame? HitTest(Vector2 point) => HitTest(point, f => f.MouseEnabled);

    public Frame? HitTest(Vector2 point, Func<Frame, bool> accepts)
    {
        var frames = DrawOrder();
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            var frame = frames[i];
            if (!accepts(frame) || frame.Rect is not { } r)
                continue;
            var insets = frame.HitRectInsets;
            var hit = new UiRect(r.Left + insets.X, r.Bottom + insets.W, r.Right - insets.Y, r.Top - insets.Z);
            if (hit.Contains(point))
                return frame;
        }
        return null;
    }

    public void MouseMove(Vector2 point)
    {
        MousePosition = point;
        var focus = HitTest(point);
        if (focus == MouseFocus)
            return;
        var previous = MouseFocus;
        MouseFocus = focus;
        previous?.RunScript("OnLeave");
        focus?.RunScript("OnEnter");
    }

    public void MouseDown(string button = "LeftButton")
    {
        _pressedButtons.Add(button);
        if (MouseFocus is not { } frame)
            return;
        _pressed = frame;
        if (frame is Button { Enabled: true } pressedButton)
            pressedButton.Pushed = true;
        if (frame is EditBox box)
            SetKeyboardFocus(box);
        frame.RunScript("OnMouseDown", DynValue.NewString(button));
    }

    public void MouseUp(string button = "LeftButton")
    {
        _pressedButtons.Remove(button);
        var pressed = _pressed;
        _pressed = null;
        if (pressed is Button clicked)
        {
            clicked.Pushed = false;
            if (MouseFocus == clicked)
                clicked.Click(button);
        }
        pressed?.RunScript("OnMouseUp", DynValue.NewString(button));
    }

    /// <summary>Goes to the topmost wheel-enabled frame under the cursor, whether or not it takes mouse clicks.</summary>
    public void MouseWheel(float delta) =>
        HitTest(MousePosition, f => f.MouseWheelEnabled)?.RunScript("OnMouseWheel", DynValue.NewNumber(delta));

    /// <summary>Text typed while an edit box has focus.</summary>
    public void Char(string text)
    {
        if (KeyboardFocus is not { } box)
            return;
        if (text == " ")
            box.RunScript("OnSpacePressed");
        box.Insert(text);
        box.RunScript("OnChar", DynValue.NewString(text));
    }

    /// <summary>Key names as the client reports them: ENTER, ESCAPE, TAB, BACKSPACE, LEFT, RIGHT, A ...</summary>
    public void KeyDown(string key)
    {
        if (KeyboardFocus is { } box)
        {
            switch (key)
            {
                case "ENTER": box.RunScript("OnEnterPressed"); return;
                case "ESCAPE": box.RunScript("OnEscapePressed"); return;
                case "TAB": box.RunScript("OnTabPressed"); return;
                case "BACKSPACE": box.Backspace(); return;
                case "LEFT": box.Cursor = Math.Max(0, box.Cursor - 1); return;
                case "RIGHT": box.Cursor = Math.Min(box.Text.Length, box.Cursor + 1); return;
                case "HOME": box.Cursor = 0; return;
                case "END": box.Cursor = box.Text.Length; return;
            }
        }
        var frames = DrawOrder();
        for (var i = frames.Count - 1; i >= 0; i--)
            if (frames[i].KeyboardEnabled && frames[i] is not EditBox && frames[i].HasHandler("OnKeyDown"))
            {
                frames[i].RunScript("OnKeyDown", DynValue.NewString(key));
                return;
            }
    }
}
