using System.Numerics;
using Silk.NET.OpenGL;

namespace Engine.Rendering;

public sealed class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly uint _program;
    private readonly Dictionary<string, int> _uniforms = new();

    public Shader(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;
        var vertex = Compile(ShaderType.VertexShader, vertexSource);
        var fragment = Compile(ShaderType.FragmentShader, fragmentSource);

        _program = gl.CreateProgram();
        gl.AttachShader(_program, vertex);
        gl.AttachShader(_program, fragment);
        gl.LinkProgram(_program);
        gl.GetProgram(_program, ProgramPropertyARB.LinkStatus, out var linked);
        if (linked == 0)
            throw new InvalidOperationException($"Shader link failed: {gl.GetProgramInfoLog(_program)}");

        gl.DetachShader(_program, vertex);
        gl.DetachShader(_program, fragment);
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);
    }

    public void Use() => _gl.UseProgram(_program);

    public unsafe void Set(string name, Matrix4x4 value) =>
        _gl.UniformMatrix4(Location(name), 1, false, (float*)&value);

    public void Set(string name, Vector3 value) => _gl.Uniform3(Location(name), value.X, value.Y, value.Z);

    public void Set(string name, Vector2 value) => _gl.Uniform2(Location(name), value.X, value.Y);

    public void Set(string name, Vector4 value) => _gl.Uniform4(Location(name), value.X, value.Y, value.Z, value.W);

    public void Set(string name, float value) => _gl.Uniform1(Location(name), value);

    public void Set(string name, int value) => _gl.Uniform1(Location(name), value);

    private int Location(string name)
    {
        if (!_uniforms.TryGetValue(name, out var location))
        {
            location = _gl.GetUniformLocation(_program, name);
            _uniforms[name] = location;
        }
        return location;
    }

    /// <summary>
    /// Sources are GLSL ES 3.00. Desktop core contexts (macOS stops at 4.1, without ES3 compatibility) get the same
    /// code as GLSL 3.30, which accepts and ignores the precision qualifiers.
    /// </summary>
    public static string ForContext(GL gl, string source)
    {
        var isEs = gl.GetStringS(StringName.Version).StartsWith("OpenGL ES", StringComparison.Ordinal);
        return isEs ? source : source.Replace("#version 300 es", "#version 330 core", StringComparison.Ordinal);
    }

    private uint Compile(ShaderType type, string source)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, ForContext(_gl, source));
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var compiled);
        if (compiled == 0)
            throw new InvalidOperationException($"{type} compile failed: {_gl.GetShaderInfoLog(shader)}");
        return shader;
    }

    public void Dispose() => _gl.DeleteProgram(_program);
}
