using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Engine;

public static class ViewExtensions
{
    /// <summary>
    /// <see cref="IView.FramebufferSize"/>, retried once: Silk reports any error SDL left pending from an earlier,
    /// unrelated call (e.g. "Unknown touch device id -1, cannot reset" during macOS text input) as if this call failed.
    /// </summary>
    public static Vector2D<int> SafeFramebufferSize(this IView view)
    {
        try
        {
            return view.FramebufferSize;
        }
        catch (Silk.NET.SDL.SdlException)
        {
            Silk.NET.SDL.Sdl.GetApi().ClearError();
            return view.FramebufferSize;
        }
    }
}
