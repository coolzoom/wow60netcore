using System.Numerics;

namespace Engine.Rendering;

/// <summary>CPU-side geometry in <see cref="Mesh"/>'s interleaved vertex layout.</summary>
public sealed class MeshData
{
    public const int FloatsPerVertex = Mesh.FloatsPerVertex;

    private readonly List<float> _vertices = new();
    private readonly List<uint> _indices = new();

    public IReadOnlyList<float> Vertices => _vertices;
    public IReadOnlyList<uint> Indices => _indices;
    public int VertexCount => _vertices.Count / FloatsPerVertex;

    public uint AddVertex(Vector3 position, Vector3 normal, Vector3 color) =>
        AddVertex(position, normal, Vector2.Zero, new Vector4(color, 1f));

    public uint AddVertex(Vector3 position, Vector3 normal, Vector2 uv, Vector4 color)
    {
        var index = (uint)VertexCount;
        _vertices.AddRange([position.X, position.Y, position.Z, normal.X, normal.Y, normal.Z, uv.X, uv.Y, color.X, color.Y, color.Z, color.W]);
        return index;
    }

    public void AddTriangle(uint a, uint b, uint c) => _indices.AddRange([a, b, c]);

    public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector3 color)
    {
        var i0 = AddVertex(a, normal, color);
        var i1 = AddVertex(b, normal, color);
        var i2 = AddVertex(c, normal, color);
        var i3 = AddVertex(d, normal, color);
        AddTriangle(i0, i1, i2);
        AddTriangle(i0, i2, i3);
    }

    /// <summary>Axis-aligned box centered on the origin, faces wound counter-clockwise from outside.</summary>
    public static MeshData Box(Vector3 size, Vector3 color)
    {
        var h = size / 2;
        var mesh = new MeshData();
        mesh.AddQuad(new(-h.X, -h.Y, h.Z), new(h.X, -h.Y, h.Z), new(h.X, h.Y, h.Z), new(-h.X, h.Y, h.Z), Vector3.UnitZ, color);
        mesh.AddQuad(new(h.X, -h.Y, -h.Z), new(-h.X, -h.Y, -h.Z), new(-h.X, h.Y, -h.Z), new(h.X, h.Y, -h.Z), -Vector3.UnitZ, color);
        mesh.AddQuad(new(h.X, -h.Y, h.Z), new(h.X, -h.Y, -h.Z), new(h.X, h.Y, -h.Z), new(h.X, h.Y, h.Z), Vector3.UnitX, color);
        mesh.AddQuad(new(-h.X, -h.Y, -h.Z), new(-h.X, -h.Y, h.Z), new(-h.X, h.Y, h.Z), new(-h.X, h.Y, -h.Z), -Vector3.UnitX, color);
        mesh.AddQuad(new(-h.X, h.Y, h.Z), new(h.X, h.Y, h.Z), new(h.X, h.Y, -h.Z), new(-h.X, h.Y, -h.Z), Vector3.UnitY, color);
        mesh.AddQuad(new(-h.X, -h.Y, -h.Z), new(h.X, -h.Y, -h.Z), new(h.X, -h.Y, h.Z), new(-h.X, -h.Y, h.Z), -Vector3.UnitY, color);
        return mesh;
    }

    /// <summary>Horizontal square centered on the origin, facing up.</summary>
    public static MeshData Plane(float size, Vector3 color)
    {
        var h = size / 2;
        var mesh = new MeshData();
        mesh.AddQuad(new(-h, 0, h), new(h, 0, h), new(h, 0, -h), new(-h, 0, -h), Vector3.UnitY, color);
        return mesh;
    }
}
