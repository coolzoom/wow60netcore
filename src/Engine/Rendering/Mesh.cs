using Silk.NET.OpenGL;

namespace Engine.Rendering;

/// <summary>GPU mesh with the shared vertex layout: position(3) normal(3) uv(2) color(4).</summary>
public sealed class Mesh : IDisposable
{
    public const int FloatsPerVertex = 12;

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    public int IndexCount { get; }
    public long Bytes { get; }

    public Mesh(GL gl, MeshData data) : this(gl, data.Vertices.ToArray(), data.Indices.ToArray())
    {
    }

    /// <param name="dynamic">The vertices will be replaced often (<see cref="UpdateVertices"/>), e.g. skinned models.</param>
    public unsafe Mesh(GL gl, ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, bool dynamic = false)
    {
        _gl = gl;
        IndexCount = indices.Length;
        Bytes = (long)vertices.Length * sizeof(float) + (long)indices.Length * sizeof(uint);

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData(BufferTargetARB.ArrayBuffer, vertices, dynamic ? BufferUsageARB.DynamicDraw : BufferUsageARB.StaticDraw);

        _ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        gl.BufferData(BufferTargetARB.ElementArrayBuffer, indices, BufferUsageARB.StaticDraw);

        const uint stride = FloatsPerVertex * sizeof(float);
        (int Size, int Offset)[] attributes = [(3, 0), (3, 3), (2, 6), (4, 8)];
        for (uint i = 0; i < attributes.Length; i++)
        {
            gl.VertexAttribPointer(i, attributes[i].Size, VertexAttribPointerType.Float, false, stride, (void*)(attributes[i].Offset * sizeof(float)));
            gl.EnableVertexAttribArray(i);
        }

        gl.BindVertexArray(0);
    }

    public void Draw() => DrawRange(0, IndexCount);

    /// <summary>Overwrites the start of the vertex buffer (same layout and at most the original size).</summary>
    public void UpdateVertices(ReadOnlySpan<float> vertices)
    {
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, vertices);
    }

    public unsafe void DrawRange(int start, int count)
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedInt, (void*)(start * sizeof(uint)));
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
    }
}
