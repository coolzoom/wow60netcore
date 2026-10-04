using System.Numerics;
using Client.Ui;
using Engine;
using Formats.Mpq;
using FrameXml;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;

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

    public UiScreen Ui => _ui;
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
        api.LoginRequested = (account, _) =>
        {
            Console.WriteLine($"Login as '{account}' to {api.GetCVar("realmList")} (no network client yet)");
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
        _imgui = new ImGuiController(Gl, Window, InputContext, null, _renderer.ConfigureFonts);
        _ui.TextMeasurer = _renderer.Measure;
        _ui.InvalidateLayout();

        if (InputContext.Mice.FirstOrDefault() is { } mouse)
        {
            mouse.MouseMove += (_, position) => _ui.MouseMove(ToUi(position));
            mouse.MouseDown += (_, button) => _ui.MouseDown(ButtonName(button));
            mouse.MouseUp += (_, button) => _ui.MouseUp(ButtonName(button));
            mouse.Scroll += (_, wheel) => _ui.MouseWheel(wheel.Y);
        }
        if (InputContext.Keyboards.FirstOrDefault() is { } keyboard)
        {
            keyboard.KeyDown += (_, key, _) => _ui.KeyDown(KeyName(key));
            keyboard.KeyChar += (_, c) =>
            {
                if (!char.IsControl(c))
                    _ui.Char(c.ToString());
            };
        }
        Window.Resize += size => _ui.SetScreenSize(size.X, size.Y);
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

    protected override void OnUpdate(float dt) => _ui.Update(dt);

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
        _renderer.Dispose();
        _imgui.Dispose();
        _files.Dispose();
    }
}
