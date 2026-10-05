using System.Numerics;
using Client.Online;
using Client.Ui;
using Engine;
using Formats.Mpq;
using FrameXml;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;

namespace Client;

/// <summary>
/// The glue screens (login, realm list, character select) loaded from Interface\GlueXML the way WoW.exe does,
/// rendered 2D. The 3D login scene behind them (ModelFFX) is not drawn.
/// </summary>
public sealed class GlueGame(string dataDirectory, bool looseFiles, bool acceptAgreements) : Game
{
    private static readonly Dictionary<Key, string> KeyNames = new()
    {
        [Key.Enter] = "ENTER", [Key.KeypadEnter] = "ENTER", [Key.Escape] = "ESCAPE", [Key.Tab] = "TAB",
        [Key.Backspace] = "BACKSPACE", [Key.Left] = "LEFT", [Key.Right] = "RIGHT", [Key.Up] = "UP", [Key.Down] = "DOWN",
        [Key.Home] = "HOME", [Key.End] = "END", [Key.Delete] = "DELETE", [Key.Space] = "SPACE", [Key.PrintScreen] = "PRINTSCREEN",
    };

    private MpqFileSystem _files = null!;
    private ImGuiController _imgui = null!;
    private GlueRenderer _renderer = null!;
    private UiScreen _ui = null!;
    private GlueNetwork? _network;

    public UiScreen Ui => _ui;
    /// <summary>Set to log in to a server; without it the screens run offline.</summary>
    public OnlineClient? Online { get; init; }
    public string StartScreen { get; init; } = "login";
    public string GameDirectory => Path.GetDirectoryName(Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar))!;

    protected override void OnLoad()
    {
        _files = new MpqFileSystem(dataDirectory);
        var source = new MpqUiFileSource(_files, looseFiles ? GameDirectory : null);
        _ui = new UiScreen(source, new UiLog { Echo = true });
        _ui.SetScreenSize(Window.Size.X, Window.Size.Y);

        var api = new GlueApi();
        api.LoadConfig(GameDirectory);
        if (acceptAgreements)
            api.AcceptAgreements();
        api.QuitRequested = Exit;
        api.UrlRequested = url => Console.WriteLine($"LaunchURL: {url}");
        if (Online is { } online)
        {
            if (online.RealmList is { } realmList)
                api.SetCVar("realmList", realmList);
            _network = new GlueNetwork(api, online.Session, online.Data(_files)) { AutoEnterWorld = online.AutoEnterWorld && StartScreen == "login" };
            _network.EnteredWorld += OnEnteredWorld;
        }
        else
            api.LoginRequested = (account, _) =>
            {
                Console.WriteLine($"Login as '{account}' to {api.GetCVar("realmList")} (offline: pass --realmlist or use realmlist.wtf with --glue)");
                _ui.FireEvent("OPEN_STATUS_DIALOG", "CANCEL", _ui.LocalizedText("LOGIN_STATE_CONNECTING"));
            };

        _renderer = new GlueRenderer(Gl, _ui, source);
        var started = DateTime.UtcNow;
        var loaded = _ui.LoadGlue(api);
        var log = Path.Combine(Path.GetTempPath(), "NetCoreClient-GlueXML.log");
        File.WriteAllText(log, _ui.Log.ToString());
        Console.WriteLine($"GlueXML {(loaded ? "loaded" : "failed")}: {_ui.Loader.FilesLoaded} files, {_ui.Frames.Count} frames, " +
                          $"{_ui.Log.Problems.Count(p => p.Level == UiLogLevel.Error)} errors, " +
                          $"{_ui.Log.Problems.Count(p => p.Level == UiLogLevel.Warning)} warnings in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms; log: {log}");

        _renderer.CollectFonts();
        _imgui = new ImGuiController(Gl, Window, InputContext, null, _renderer.ConfigureFonts, softKeyboard: Options.TouchControls);
        _ui.TextMeasurer = _renderer.Measure;
        _ui.InvalidateLayout();

        Hook(true);
        switch (StartScreen)
        {
            case "charselect":
                _ui.SetGlueScreen("charselect");
                break;
            case "disconnected":
                _ui.FireEvent("DISCONNECTED_FROM_SERVER");
                break;
            case "login" when Online?.AutoLogin?.Split(':', 2) is [var account, var password]:
                _network!.Login(account, password);
                break;
        }
    }

    private void Hook(bool attach)
    {
        if (InputContext.Mice.FirstOrDefault() is { } mouse)
        {
            if (attach)
            {
                mouse.MouseMove += OnMouseMove;
                mouse.MouseDown += OnMouseDown;
                mouse.MouseUp += OnMouseUp;
                mouse.Scroll += OnScroll;
            }
            else
            {
                mouse.MouseMove -= OnMouseMove;
                mouse.MouseDown -= OnMouseDown;
                mouse.MouseUp -= OnMouseUp;
                mouse.Scroll -= OnScroll;
            }
        }
        if (InputContext.Keyboards.FirstOrDefault() is { } keyboard)
        {
            if (attach)
            {
                keyboard.KeyDown += OnKeyDown;
                keyboard.KeyChar += OnKeyChar;
            }
            else
            {
                keyboard.KeyDown -= OnKeyDown;
                keyboard.KeyChar -= OnKeyChar;
            }
        }
        if (attach)
            Window.Resize += OnResize;
        else
            Window.Resize -= OnResize;
    }

    private void OnMouseMove(IMouse _, Vector2 position) => _ui.MouseMove(ToUi(position));
    private void OnMouseDown(IMouse _, MouseButton button) => _ui.MouseDown(ButtonName(button));
    private void OnMouseUp(IMouse _, MouseButton button) => _ui.MouseUp(ButtonName(button));
    private void OnScroll(IMouse _, ScrollWheel wheel) => _ui.MouseWheel(wheel.Y);
    private void OnKeyDown(IKeyboard _, Key key, int __) => _ui.KeyDown(KeyName(key));
    private void OnResize(Silk.NET.Maths.Vector2D<int> size) => _ui.SetScreenSize(size.X, size.Y);

    private void OnKeyChar(IKeyboard _, char c)
    {
        if (!char.IsControl(c))
            _ui.Char(c.ToString());
    }

    private void OnEnteredWorld(Net.WorldEntry entry)
    {
        var online = Online!;
        if (online.Data(_files).Map((int)entry.Map) is { } map)
            SwitchTo(online.World(map.Directory));
        else
        {
            _ui.FireEvent("OPEN_STATUS_DIALOG", "OKAY", $"Map {entry.Map} is not in Map.dbc");
            online.Session.Logout();
        }
    }

    private Vector2 ToUi(Vector2 window)
    {
        var scale = Window.Size.Y / UiScreen.ScreenHeight;
        return new Vector2(window.X / scale, (Window.Size.Y - window.Y) / scale);
    }

    private static string ButtonName(MouseButton button) => button switch
    {
        MouseButton.Right => "RightButton",
        MouseButton.Middle => "MiddleButton",
        _ => "LeftButton",
    };

    private static string KeyName(Key key) =>
        KeyNames.TryGetValue(key, out var name) ? name : key.ToString().ToUpperInvariant();

    protected override void OnUpdate(float dt)
    {
        Online?.Session.Poll();
        _ui.Update(dt);
    }

    protected override void OnRender(float dt)
    {
        Gl.ClearColor(0, 0, 0, 1);
        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _imgui.Update(dt);
        _renderer.Draw(new Vector2(Window.Size.X, Window.Size.Y), dt);
        _imgui.Render();
    }

    protected override void OnUnload()
    {
        Hook(false);
        if (_network is { } network)
        {
            network.EnteredWorld -= OnEnteredWorld;
            network.Dispose();
        }
        _renderer.Dispose();
        _imgui.Dispose();
        _files.Dispose();
    }
}
