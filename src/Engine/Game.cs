using Silk.NET.Input;
using Silk.NET.Input.Sdl;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;

namespace Engine;

/// <param name="ScreenshotPath">If set together with ExitAfterFrames, the last frame is saved there as a BMP.</param>
/// <param name="UiScale">Scale for debug UI fonts and widgets (display density on phones).</param>
/// <param name="TouchControls">Show on-screen movement controls (no keyboard).</param>
public sealed record GameOptions(
    string Title, int Width = 1280, int Height = 720, int? ExitAfterFrames = null, string? ScreenshotPath = null,
    float UiScale = 1f, bool TouchControls = false);

/// <summary>
/// Game loop on an SDL view: a desktop window with an OpenGL 3.3 core context, or (on Android) the activity's
/// surface with OpenGL ES 3.0. Shaders are written in GLSL ES 3.00 and adapted to the desktop context by <see cref="Rendering.Shader"/>.
/// </summary>
public abstract class Game : IDisposable
{
    /// <summary>macOS only exposes core profiles through a forward-compatible context.</summary>
    public static readonly GraphicsAPI DesktopApi = new(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3));
    public static readonly GraphicsAPI MobileApi = new(ContextAPI.OpenGLES, ContextProfile.Compatability, ContextFlags.Default, new APIVersion(3, 0));

    private IView? _view;
    private IInputContext? _inputContext;
    private int _frames;

    static Game()
    {
        SdlWindowing.Use();
        SdlInput.Use();
    }

    protected GL Gl { get; private set; } = null!;
    protected InputState Input { get; private set; } = null!;
    protected IView Window => _view!;
    protected IInputContext InputContext => _inputContext!;
    protected Vector2D<int> FramebufferSize => _view!.FramebufferSize;
    public GameOptions Options { get; private set; } = new("Game");

    /// <summary>Opens a desktop window and runs until it is closed.</summary>
    public void Run(GameOptions options)
    {
        var window = Silk.NET.Windowing.Window.Create(WindowOptions.Default with
        {
            Title = options.Title,
            Size = new Vector2D<int>(options.Width, options.Height),
            VSync = true,
            API = DesktopApi,
        });
        Run(window, options);
    }

    /// <summary>Runs on an existing view, e.g. <c>Window.GetView</c> inside an Android <c>SilkActivity</c>.</summary>
    public void Run(IView view, GameOptions options)
    {
        Options = options;
        _view = view;
        view.Load += HandleLoad;
        view.Update += dt => OnUpdate((float)dt);
        view.Render += HandleRender;
        view.FramebufferResize += size => Gl.Viewport(size);
        view.Closing += OnUnload;
        view.Run();
    }

    protected void Exit() => _view?.Close();

    private void HandleLoad()
    {
        var view = _view!;
        Gl = GL.GetApi(view);
        Console.WriteLine($"OpenGL: {Gl.GetStringS(StringName.Version)} / {Gl.GetStringS(StringName.Renderer)}");
        _inputContext = view.CreateInput();
        Input = new InputState(_inputContext);
        Gl.Viewport(view.FramebufferSize);
        OnLoad();
    }

    private void HandleRender(double dt)
    {
        OnRender((float)dt);
        Input.EndFrame();

        _frames++;
        if (Options.ExitAfterFrames is { } limit && _frames >= limit)
        {
            if (Options.ScreenshotPath is { } path)
                SaveScreenshot(path);
            Console.WriteLine($"Rendered {_frames} frames, exiting.");
            Exit();
        }
    }

    private unsafe void SaveScreenshot(string path)
    {
        var size = FramebufferSize;
        // RGBA/UNSIGNED_BYTE is the one read-back format OpenGL ES guarantees.
        var rgba = new byte[size.X * size.Y * 4];
        Gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = rgba)
            Gl.ReadPixels(0, 0, (uint)size.X, (uint)size.Y, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        var bgr = new byte[size.X * size.Y * 3];
        for (int i = 0, o = 0; i < rgba.Length; i += 4, o += 3)
        {
            bgr[o] = rgba[i + 2];
            bgr[o + 1] = rgba[i + 1];
            bgr[o + 2] = rgba[i];
        }
        Bitmap.WriteBgr24(path, size.X, size.Y, bgr);
        Console.WriteLine($"Saved screenshot to {path}");
    }

    protected abstract void OnLoad();
    protected abstract void OnUpdate(float dt);
    protected abstract void OnRender(float dt);
    protected abstract void OnUnload();

    public void Dispose()
    {
        _inputContext?.Dispose();
        _view?.Dispose();
        GC.SuppressFinalize(this);
    }
}
