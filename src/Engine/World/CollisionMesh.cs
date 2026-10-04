using System.Numerics;

namespace Engine.World;

/// <summary>Walkable triangles bucketed on a 2D XZ grid, for "what floor is under me" queries.</summary>
public sealed class CollisionMesh
{
    private const float CellSize = 8f;

    private readonly Vector3[] _vertices;
    private readonly int[] _triangles;
    private readonly Dictionary<(int, int), List<int>> _grid = new();

    public int TriangleCount => _triangles.Length / 3;

    /// <param name="minNormalY">Triangles steeper than this (walls) are ignored.</param>
    public CollisionMesh(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, float minNormalY = 0.3f)
    {
        _vertices = vertices.ToArray();
        var triangles = new List<int>();
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            Vector3 a = _vertices[indices[i]], b = _vertices[indices[i + 1]], c = _vertices[indices[i + 2]];
            var normal = Vector3.Cross(b - a, c - a);
            var length = normal.Length();
            if (length < 1e-6f || MathF.Abs(normal.Y / length) < minNormalY)
                continue;

            var triangle = triangles.Count;
            triangles.AddRange([indices[i], indices[i + 1], indices[i + 2]]);
            var min = Vector3.Min(a, Vector3.Min(b, c));
            var max = Vector3.Max(a, Vector3.Max(b, c));
            for (var gx = Cell(min.X); gx <= Cell(max.X); gx++)
            for (var gz = Cell(min.Z); gz <= Cell(max.Z); gz++)
            {
                if (!_grid.TryGetValue((gx, gz), out var bucket))
                    _grid[(gx, gz)] = bucket = new List<int>();
                bucket.Add(triangle);
            }
        }
        _triangles = triangles.ToArray();
    }

    /// <summary>Highest surface at (x, z) whose height is at most <paramref name="maxY"/>.</summary>
    public bool TryGetFloor(float x, float z, float maxY, out float height)
    {
        height = float.MinValue;
        if (!_grid.TryGetValue((Cell(x), Cell(z)), out var bucket))
            return false;

        foreach (var t in bucket)
        {
            if (HeightAt(_vertices[_triangles[t]], _vertices[_triangles[t + 1]], _vertices[_triangles[t + 2]], x, z) is { } h
                && h <= maxY && h > height)
                height = h;
        }
        return height > float.MinValue;
    }

    private static float? HeightAt(Vector3 a, Vector3 b, Vector3 c, float x, float z)
    {
        var d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(d) < 1e-9f)
            return null;
        var u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
        var v = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
        var w = 1 - u - v;
        const float epsilon = -1e-4f;
        if (u < epsilon || v < epsilon || w < epsilon)
            return null;
        return u * a.Y + v * b.Y + w * c.Y;
    }

    private static int Cell(float value) => (int)MathF.Floor(value / CellSize);
}
