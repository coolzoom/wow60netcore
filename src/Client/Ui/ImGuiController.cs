using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Shader = Engine.Rendering.Shader;
using Texture = Engine.Rendering.Texture;

namespace Client.Ui;

public sealed record ImGuiFontConfig(string FontPath, int FontSize, Func<ImGuiIOPtr, IntPtr>? GetGlyphRange = null);

/// <summary>
/// ImGui platform and renderer backend for Silk.NET input and OpenGL 3.3 core / OpenGL ES 3.0.
/// ES 3.0 has no base-vertex draws, so each command list is uploaded whole and drawn with plain index offsets.
/// </summary>
public sealed class ImGuiController : IDisposable
{
    private const string VertexSource = """
        #version 300 es
        precision highp float;
        layout (location = 0) in vec2 aPosition;
        layout (location = 1) in vec2 aUv;
        layout (location = 2) in vec4 aColor;
        uniform mat4 uProjection;
        out vec2 vUv;
        out vec4 vColor;
        void main()
        {
            vUv = aUv;
            vColor = aColor;
            gl_Position = uProjection * vec4(aPosition, 0.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 300 es
        precision mediump float;
        uniform sampler2D uTexture;
        in vec2 vUv;
        in vec4 vColor;
        out vec4 FragColor;
        void main()
        {
            FragColor = vColor * texture(uTexture, vUv);
        }
        """;

    private const float DefaultFontSize = 13f;

    private readonly GL _gl;
    private readonly IView _view;
    private readonly IKeyboard? _keyboard;
    private readonly bool _softKeyboard;
    private readonly Shader _shader;
    private readonly Texture _fontTexture;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private bool _frameBegun;
    private bool _textInputActive;

    /// <param name="scale">Font and widget scale, e.g. the display density on phones.</param>
    /// <param name="softKeyboard">Show the on-screen keyboard while a text field is active.</param>
    public unsafe ImGuiController(GL gl, IView view, IInputContext input, ImGuiFontConfig? font = null, Action? onConfigureIO = null,
        float scale = 1f, bool softKeyboard = false)
    {
        _gl = gl;
        _view = view;
        _softKeyboard = softKeyboard;
        ImGui.SetCurrentContext(ImGui.CreateContext());
        var io = ImGui.GetIO();

        if (font is not null)
            io.Fonts.AddFontFromFileTTF(font.FontPath, font.FontSize * scale, null, font.GetGlyphRange?.Invoke(io) ?? IntPtr.Zero);
        onConfigureIO?.Invoke();
        if (io.Fonts.Fonts.Size == 0)
        {
            var config = ImGuiNative.ImFontConfig_ImFontConfig();
            config->SizePixels = DefaultFontSize * scale;
            io.Fonts.AddFontDefault(config);
            ImGuiNative.ImFontConfig_destroy(config);
        }
        if (scale != 1f)
            ImGui.GetStyle().ScaleAllSizes(scale);

        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out var width, out var height, out _);
        _fontTexture = new Texture(gl, width, height, new ReadOnlySpan<byte>((void*)pixels, width * height * 4), repeat: false, mipmaps: false);
        io.Fonts.SetTexID((IntPtr)_fontTexture.Handle);
        io.Fonts.ClearTexData();

        _shader = new Shader(gl, VertexSource, FragmentSource);
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        _ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        var stride = (uint)sizeof(ImDrawVert);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)8);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, (void*)16);
        for (uint i = 0; i < 3; i++)
            gl.EnableVertexAttribArray(i);
        gl.BindVertexArray(0);

        foreach (var mouse in input.Mice)
        {
            mouse.MouseMove += (_, position) => ImGui.GetIO().AddMousePosEvent(position.X, position.Y);
            mouse.MouseDown += (_, button) => MouseButtonEvent(button, true);
            mouse.MouseUp += (_, button) => MouseButtonEvent(button, false);
            mouse.Scroll += (_, wheel) => ImGui.GetIO().AddMouseWheelEvent(wheel.X, wheel.Y);
        }
        _keyboard = input.Keyboards.FirstOrDefault();
        if (_keyboard is not null)
        {
            _keyboard.KeyDown += (_, key, _) => KeyEvent(key, true);
            _keyboard.KeyUp += (_, key, _) => KeyEvent(key, false);
            _keyboard.KeyChar += (_, c) => ImGui.GetIO().AddInputCharacter(c);
        }

        BeginFrame(1f / 60f);
    }

    /// <summary>Starts the next ImGui frame (closing the previous one if it was never rendered).</summary>
    public void Update(float dt)
    {
        if (_frameBegun)
            ImGui.Render();
        BeginFrame(dt);
    }

    public void Render()
    {
        if (!_frameBegun)
            return;
        _frameBegun = false;
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());
        UpdateSoftKeyboard();
    }

    private void BeginFrame(float dt)
    {
        var io = ImGui.GetIO();
        var size = _view.Size;
        var framebuffer = _view.FramebufferSize;
        io.DisplaySize = new Vector2(size.X, size.Y);
        if (size.X > 0 && size.Y > 0)
            io.DisplayFramebufferScale = new Vector2(framebuffer.X / (float)size.X, framebuffer.Y / (float)size.Y);
        io.DeltaTime = dt > 0 ? dt : 1f / 60f;
        _frameBegun = true;
        ImGui.NewFrame();
    }

    private void UpdateSoftKeyboard()
    {
        if (!_softKeyboard || _keyboard is null || ImGui.GetIO().WantTextInput == _textInputActive)
            return;
        _textInputActive = !_textInputActive;
        if (_textInputActive)
            _keyboard.BeginInput();
        else
            _keyboard.EndInput();
    }

    private unsafe void RenderDrawData(ImDrawDataPtr data)
    {
        var scale = data.FramebufferScale;
        var width = (int)(data.DisplaySize.X * scale.X);
        var height = (int)(data.DisplaySize.Y * scale.Y);
        if (width <= 0 || height <= 0 || data.CmdListsCount == 0)
            return;

        _gl.Enable(EnableCap.Blend);
        _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
        _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Viewport(0, 0, (uint)width, (uint)height);

        var left = data.DisplayPos.X;
        var right = left + data.DisplaySize.X;
        var top = data.DisplayPos.Y;
        var bottom = top + data.DisplaySize.Y;
        var projection = new Matrix4x4(
            2f / (right - left), 0, 0, 0,
            0, 2f / (top - bottom), 0, 0,
            0, 0, -1, 0,
            (right + left) / (left - right), (top + bottom) / (bottom - top), 0, 1);
        _shader.Use();
        _shader.Set("uProjection", projection);
        _shader.Set("uTexture", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);

        for (var n = 0; n < data.CmdListsCount; n++)
        {
            var list = data.CmdLists[n];
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(list.VtxBuffer.Size * sizeof(ImDrawVert)), (void*)list.VtxBuffer.Data, BufferUsageARB.StreamDraw);
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(list.IdxBuffer.Size * sizeof(ushort)), (void*)list.IdxBuffer.Data, BufferUsageARB.StreamDraw);

            for (var i = 0; i < list.CmdBuffer.Size; i++)
            {
                var command = list.CmdBuffer[i];
                if (command.UserCallback != IntPtr.Zero)
                    continue;
                var clip = (command.ClipRect - new Vector4(data.DisplayPos, data.DisplayPos.X, data.DisplayPos.Y)) * new Vector4(scale, scale.X, scale.Y);
                if (clip.X >= width || clip.Y >= height || clip.Z <= clip.X || clip.W <= clip.Y)
                    continue;
                _gl.Scissor((int)clip.X, (int)(height - clip.W), (uint)(clip.Z - clip.X), (uint)(clip.W - clip.Y));
                _gl.BindTexture(TextureTarget.Texture2D, (uint)command.TextureId);
                _gl.DrawElements(PrimitiveType.Triangles, command.ElemCount, DrawElementsType.UnsignedShort, (void*)(command.IdxOffset * sizeof(ushort)));
            }
        }

        _gl.BindVertexArray(0);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        var framebuffer = _view.FramebufferSize;
        _gl.Viewport(0, 0, (uint)framebuffer.X, (uint)framebuffer.Y);
    }

    private static void MouseButtonEvent(MouseButton button, bool down)
    {
        var index = button switch
        {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            _ => -1,
        };
        if (index >= 0)
            ImGui.GetIO().AddMouseButtonEvent(index, down);
    }

    private void KeyEvent(Key key, bool down)
    {
        var io = ImGui.GetIO();
        if (_keyboard is { } keyboard)
        {
            io.AddKeyEvent(ImGuiKey.ModCtrl, keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight));
            io.AddKeyEvent(ImGuiKey.ModShift, keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight));
            io.AddKeyEvent(ImGuiKey.ModAlt, keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight));
            io.AddKeyEvent(ImGuiKey.ModSuper, keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight));
        }
        if (Translate(key) is { } imguiKey)
            io.AddKeyEvent(imguiKey, down);
    }

    private static ImGuiKey? Translate(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ImGuiKey.A + (key - Key.A),
        >= Key.Number0 and <= Key.Number9 => ImGuiKey._0 + (key - Key.Number0),
        >= Key.F1 and <= Key.F12 => ImGuiKey.F1 + (key - Key.F1),
        >= Key.Keypad0 and <= Key.Keypad9 => ImGuiKey.Keypad0 + (key - Key.Keypad0),
        Key.Tab => ImGuiKey.Tab,
        Key.Left => ImGuiKey.LeftArrow,
        Key.Right => ImGuiKey.RightArrow,
        Key.Up => ImGuiKey.UpArrow,
        Key.Down => ImGuiKey.DownArrow,
        Key.PageUp => ImGuiKey.PageUp,
        Key.PageDown => ImGuiKey.PageDown,
        Key.Home => ImGuiKey.Home,
        Key.End => ImGuiKey.End,
        Key.Insert => ImGuiKey.Insert,
        Key.Delete => ImGuiKey.Delete,
        Key.Backspace => ImGuiKey.Backspace,
        Key.Space => ImGuiKey.Space,
        Key.Enter => ImGuiKey.Enter,
        Key.KeypadEnter => ImGuiKey.KeypadEnter,
        Key.Escape => ImGuiKey.Escape,
        Key.ControlLeft => ImGuiKey.LeftCtrl,
        Key.ControlRight => ImGuiKey.RightCtrl,
        Key.ShiftLeft => ImGuiKey.LeftShift,
        Key.ShiftRight => ImGuiKey.RightShift,
        Key.AltLeft => ImGuiKey.LeftAlt,
        Key.AltRight => ImGuiKey.RightAlt,
        Key.SuperLeft => ImGuiKey.LeftSuper,
        Key.SuperRight => ImGuiKey.RightSuper,
        Key.Apostrophe => ImGuiKey.Apostrophe,
        Key.Comma => ImGuiKey.Comma,
        Key.Minus => ImGuiKey.Minus,
        Key.Period => ImGuiKey.Period,
        Key.Slash => ImGuiKey.Slash,
        Key.Semicolon => ImGuiKey.Semicolon,
        Key.Equal => ImGuiKey.Equal,
        Key.LeftBracket => ImGuiKey.LeftBracket,
        Key.BackSlash => ImGuiKey.Backslash,
        Key.RightBracket => ImGuiKey.RightBracket,
        Key.GraveAccent => ImGuiKey.GraveAccent,
        _ => null,
    };

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
        _fontTexture.Dispose();
        ImGui.DestroyContext();
    }
}
