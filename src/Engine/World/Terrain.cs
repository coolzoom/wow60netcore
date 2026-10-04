using System.Numerics;
using Engine.Rendering;

namespace Engine.World;

/// <summary>Square heightmap centered on the world origin, generated from layered value noise.</summary>
public sealed class Terrain : IHeightField
{
    private readonly float[,] _heights;

    public int Resolution { get; }
    public float CellSize { get; }
    public float WaterLevel { get; }
    public float HalfExtent => (Resolution - 1) * CellSize / 2;

    public Terrain(float[,] heights, float cellSize, float waterLevel)
    {
        if (heights.GetLength(0) != heights.GetLength(1) || heights.GetLength(0) < 2)
            throw new ArgumentException("Heightmap must be square with at least 2x2 samples.", nameof(heights));

        _heights = heights;
        Resolution = heights.GetLength(0);
        CellSize = cellSize;
        WaterLevel = waterLevel;
    }

    public static Terrain Generate(int seed, int resolution = 129, float cellSize = 2f, float amplitude = 18f)
    {
        var noise = new ValueNoise(seed);
        var heights = new float[resolution, resolution];
        var center = (resolution - 1) / 2f;

        for (var z = 0; z < resolution; z++)
        for (var x = 0; x < resolution; x++)
        {
            var h = noise.Fractal(x * 0.035f, z * 0.035f, octaves: 4);
            // Raise the rim so the playable area is a valley ringed by hills.
            var radial = MathF.Sqrt((x - center) * (x - center) + (z - center) * (z - center)) / center;
            var rim = MathF.Pow(Math.Clamp(radial, 0, 1), 3) * 1.2f;
            heights[x, z] = (h - 0.45f + rim) * amplitude;
        }

        return new Terrain(heights, cellSize, waterLevel: -2.5f);
    }

    public Vector2 Min => new(-HalfExtent + 1f);
    public Vector2 Max => new(HalfExtent - 1f);

    public bool TryGetHeight(Vector3 probe, out float height)
    {
        height = GetHeight(probe.X, probe.Z);
        return true;
    }

    /// <summary>Bilinearly interpolated ground height; positions outside the map clamp to the edge.</summary>
    public float GetHeight(float worldX, float worldZ)
    {
        var gx = Math.Clamp((worldX + HalfExtent) / CellSize, 0, Resolution - 1);
        var gz = Math.Clamp((worldZ + HalfExtent) / CellSize, 0, Resolution - 1);
        var x0 = Math.Min((int)gx, Resolution - 2);
        var z0 = Math.Min((int)gz, Resolution - 2);
        var tx = gx - x0;
        var tz = gz - z0;

        var top = float.Lerp(_heights[x0, z0], _heights[x0 + 1, z0], tx);
        var bottom = float.Lerp(_heights[x0, z0 + 1], _heights[x0 + 1, z0 + 1], tx);
        return float.Lerp(top, bottom, tz);
    }

    public MeshData BuildMesh()
    {
        var mesh = new MeshData();
        for (var z = 0; z < Resolution; z++)
        for (var x = 0; x < Resolution; x++)
        {
            var position = SamplePosition(x, z);
            mesh.AddVertex(position, Normal(x, z), ColorForHeight(position.Y));
        }

        for (var z = 0; z < Resolution - 1; z++)
        for (var x = 0; x < Resolution - 1; x++)
        {
            var i = (uint)(z * Resolution + x);
            var below = i + (uint)Resolution;
            mesh.AddTriangle(i, below, i + 1);
            mesh.AddTriangle(i + 1, below, below + 1);
        }

        return mesh;
    }

    private Vector3 SamplePosition(int x, int z) =>
        new(x * CellSize - HalfExtent, _heights[x, z], z * CellSize - HalfExtent);

    private Vector3 Normal(int x, int z)
    {
        var left = _heights[Math.Max(x - 1, 0), z];
        var right = _heights[Math.Min(x + 1, Resolution - 1), z];
        var back = _heights[x, Math.Max(z - 1, 0)];
        var front = _heights[x, Math.Min(z + 1, Resolution - 1)];
        return Vector3.Normalize(new Vector3(left - right, 2 * CellSize, back - front));
    }

    private Vector3 ColorForHeight(float height) => height switch
    {
        _ when height < WaterLevel + 0.6f => new(0.76f, 0.70f, 0.50f),
        < 6f => new(0.30f, 0.55f, 0.22f),
        < 12f => new(0.45f, 0.42f, 0.36f),
        _ => new(0.92f, 0.93f, 0.95f),
    };
}
