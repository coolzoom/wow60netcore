using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Engine;

/// <param name="ScreenshotPath">If set together with ExitAfterFrames, the last frame is saved there as a BMP.</param>
public sealed record GameOptions(string Title, int Width = 1280, int Height = 720, int? ExitAfterFrames = null, string? ScreenshotPath = null);

public abstract class Game : IDisposable
{
    private IWindow? _window;
    private IInputContext? _inputContext;
    private int _frames;
    private int? _exitAfterFrames;
    private string? _screenshotPath;

    protected GL Gl { get; private set; } = null!;
    protected InputState Input { get; private set; } = null!;
    protected IWindow Window => _window!;
    protected IInputContext InputContext => _inputContext!;
    protected Vector2D<int> FramebufferSize => _window!.FramebufferSize;

    public void Run(GameOptions options)
    {
        _exitAfterFrames = options.ExitAfterFrames;
        _screenshotPath = options.ScreenshotPath;

        var windowOptions = WindowOptions.Default with
        {
            Title = options.Title,
            Size = new Vector2D<int>(options.Width, options.Height),
            VSync = true,
            // macOS only exposes core profiles through a forward-compatible context.
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
        };

        _window = Silk.NET.Windowing.Window.Create(windowOptions);
        _window.Load += HandleLoad;
        _window.Update += dt => OnUpdate((float)dt);
        _window.Render += HandleRender;
        _window.FramebufferResize += size => Gl.Viewport(size);
        _window.Closing += OnUnload;
        _window.Run();
    }

    protected void Exit() => _window?.Close();

    private void HandleLoad()
    {
        var window = _window!;
        Gl = GL.GetApi(window);
        _inputContext = window.CreateInput();
        Input = new InputState(_inputContext);
        Gl.Viewport(window.FramebufferSize);
        OnLoad();
    }

    private void HandleRender(double dt)
    {
        OnRender((float)dt);
        Input.EndFrame();

        _frames++;
        if (_exitAfterFrames is { } limit && _frames >= limit)
        {
            if (_screenshotPath is not null)
                SaveScreenshot(_screenshotPath);
            Console.WriteLine($"Rendered {_frames} frames, exiting.");
            Exit();
        }
    }

    private unsafe void SaveScreenshot(string path)
    {
        var size = FramebufferSize;
        var pixels = new byte[size.X * size.Y * 3];
        Gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = pixels)
            Gl.ReadPixels(0, 0, (uint)size.X, (uint)size.Y, PixelFormat.Bgr, PixelType.UnsignedByte, p);
        Bitmap.WriteBgr24(path, size.X, size.Y, pixels);
        Console.WriteLine($"Saved screenshot to {path}");
    }

    protected abstract void OnLoad();
    protected abstract void OnUpdate(float dt);
    protected abstract void OnRender(float dt);
    protected abstract void OnUnload();

    public void Dispose()
    {
        _inputContext?.Dispose();
        _window?.Dispose();
        GC.SuppressFinalize(this);
    }
}
