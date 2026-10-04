using System.Numerics;
using Silk.NET.Input;

namespace Engine;

public sealed class InputState
{
    private readonly IKeyboard? _keyboard;
    private readonly IMouse? _mouse;
    private Vector2? _lastMousePosition;

    public Vector2 MouseDelta { get; private set; }
    public float ScrollDelta { get; private set; }

    public InputState(IInputContext context)
    {
        _keyboard = context.Keyboards.FirstOrDefault();
        _mouse = context.Mice.FirstOrDefault();

        if (_mouse is not null)
        {
            _mouse.MouseMove += (_, position) =>
            {
                if (_lastMousePosition is { } last)
                    MouseDelta += position - last;
                _lastMousePosition = position;
            };
            _mouse.Scroll += (_, wheel) => ScrollDelta += wheel.Y;
            // A new touch starts wherever the finger lands; don't turn that jump into a drag.
            _mouse.MouseDown += (mouse, _) =>
            {
                MouseDelta = Vector2.Zero;
                _lastMousePosition = mouse.Position;
            };
        }
    }

    public bool IsKeyDown(Key key) => _keyboard?.IsKeyPressed(key) ?? false;

    public bool IsMouseDown(MouseButton button) => _mouse?.IsButtonPressed(button) ?? false;

    internal void EndFrame()
    {
        MouseDelta = Vector2.Zero;
        ScrollDelta = 0;
    }
}
