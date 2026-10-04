using System.Numerics;
using Engine.Rendering;
using Formats.Terrain;
using Silk.NET.OpenGL;
using Texture = Engine.Rendering.Texture;

namespace Client.World;

public sealed record TerrainChunkRange(int IndexStart, int IndexCount, string[] Layers);

/// <summary>CPU side of a terrain tile: one vertex buffer for all 256 chunks plus a 1024x1024 alpha-map atlas.</summary>
public sealed class TerrainTileData
{
    public const int AtlasSize = 16 * AdtChunk.AlphaSize;

    public required AdtFile Adt { get; init; }
    public required float[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    public required TerrainChunkRange[] Chunks { get; init; }
    public required byte[] AlphaAtlas { get; init; }

    public static TerrainTileData Build(AdtFile adt)
    {
        var vertices = new float[adt.Chunks.Count * AdtChunk.VertexCount * Mesh.FloatsPerVertex];
        var indices = new List<uint>();
        var ranges = new List<TerrainChunkRange>();
        var atlas = new byte[AtlasSize * AtlasSize * 4];

        for (var c = 0; c < adt.Chunks.Count; c++)
        {
            var chunk = adt.Chunks[c];
            var baseVertex = c * AdtChunk.VertexCount;
            for (var v = 0; v < AdtChunk.VertexCount; v++)
            {
                // Sample alpha texel centers only, so neighbouring chunks in the atlas never bleed in.
                var local = chunk.TexCoords[v] * (AdtChunk.AlphaSize - 1) + new Vector2(0.5f);
                var uv = (new Vector2(chunk.IndexX, chunk.IndexY) * AdtChunk.AlphaSize + local) / AtlasSize;
                var p = chunk.Positions[v];
                var n = chunk.Normals[v];
                var o = (baseVertex + v) * Mesh.FloatsPerVertex;
                vertices[o] = p.X; vertices[o + 1] = p.Y; vertices[o + 2] = p.Z;
                vertices[o + 3] = n.X; vertices[o + 4] = n.Y; vertices[o + 5] = n.Z;
                vertices[o + 6] = uv.X; vertices[o + 7] = uv.Y;
                vertices[o + 8] = vertices[o + 9] = vertices[o + 10] = vertices[o + 11] = 1f;
            }

            var start = indices.Count;
            indices.AddRange(chunk.BuildIndices().Select(i => (uint)(baseVertex + i)));
            var layers = chunk.Layers.Select(l => adt.Textures[l.TextureIndex]).ToArray();
            ranges.Add(new TerrainChunkRange(start, indices.Count - start, layers));

            for (var row = 0; row < AdtChunk.AlphaSize; row++)
                Array.Copy(chunk.AlphaMap, row * AdtChunk.AlphaSize * 4, atlas,
                    ((chunk.IndexY * AdtChunk.AlphaSize + row) * AtlasSize + chunk.IndexX * AdtChunk.AlphaSize) * 4, AdtChunk.AlphaSize * 4);
        }

        return new TerrainTileData { Adt = adt, Vertices = vertices, Indices = indices.ToArray(), Chunks = ranges.ToArray(), AlphaAtlas = atlas };
    }
}

/// <summary>GPU side of a terrain tile, plus exact height queries against its triangles.</summary>
public sealed class TerrainTile : IDisposable
{
    // Only chunk heights are kept on the CPU; the rest of the parsed ADT is dropped after upload.
    private readonly Vector3[]?[,] _chunkGrid = new Vector3[]?[16, 16];

    public int X { get; }
    public int Y { get; }
    public IReadOnlyList<DoodadPlacement> Doodads { get; }
    public IReadOnlyList<MapObjectPlacement> MapObjects { get; }
    public Mesh Mesh { get; }
    public Texture AlphaAtlas { get; }
    public IReadOnlyList<TerrainChunkRange> Chunks { get; }
    public Vector3 Center { get; }
    public long Bytes => Mesh.Bytes + AlphaAtlas.Bytes;

    public TerrainTile(GL gl, int x, int y, TerrainTileData data)
    {
        X = x;
        Y = y;
        Doodads = data.Adt.Doodads;
        MapObjects = data.Adt.MapObjects;
        Mesh = new Mesh(gl, data.Vertices, data.Indices);
        // Alpha maps are 4-bit in 1.12, so RGBA4 storage is lossless.
        AlphaAtlas = new Texture(gl, TerrainTileData.AtlasSize, TerrainTileData.AtlasSize, data.AlphaAtlas,
            repeat: false, mipmaps: false, storage: InternalFormat.Rgba4);
        Chunks = data.Chunks;
        foreach (var chunk in data.Adt.Chunks)
            if (chunk.IndexX is >= 0 and < 16 && chunk.IndexY is >= 0 and < 16)
                _chunkGrid[chunk.IndexX, chunk.IndexY] = chunk.Positions;

        var heights = data.Adt.Chunks.SelectMany(c => c.Positions).Select(p => p.Y).DefaultIfEmpty().ToList();
        Center = new Vector3((x + 0.5f) * WorldSpace.TileSize, (heights.Min() + heights.Max()) / 2, (y + 0.5f) * WorldSpace.TileSize);
    }

    public bool TryGetHeight(float x, float z, out float height)
    {
        height = 0;
        var localX = (x - X * WorldSpace.TileSize) / WorldSpace.ChunkSize;
        var localZ = (z - Y * WorldSpace.TileSize) / WorldSpace.ChunkSize;
        int chunkX = (int)localX, chunkZ = (int)localZ;
        if (localX < 0 || localZ < 0 || chunkX > 15 || chunkZ > 15 || _chunkGrid[chunkX, chunkZ] is not { } p)
            return false;

        var cellX = Math.Clamp((localX - chunkX) * 8, 0, 7.9999f);
        var cellZ = Math.Clamp((localZ - chunkZ) * 8, 0, 7.9999f);
        int column = (int)cellX, row = (int)cellZ;
        float fx = cellX - column, fz = cellZ - row;

        var topLeft = p[row * 17 + column];
        var topRight = p[row * 17 + column + 1];
        var bottomLeft = p[(row + 1) * 17 + column];
        var bottomRight = p[(row + 1) * 17 + column + 1];
        var center = p[row * 17 + 9 + column];

        // Each cell is a fan of four triangles around its center vertex.
        float dx = fx - 0.5f, dz = fz - 0.5f;
        var (a, b) = MathF.Abs(dz) > MathF.Abs(dx)
            ? dz < 0 ? (topLeft, topRight) : (bottomLeft, bottomRight)
            : dx < 0 ? (topLeft, bottomLeft) : (topRight, bottomRight);
        height = PlaneHeight(center, a, b, x, z);
        return true;
    }

    private static float PlaneHeight(Vector3 a, Vector3 b, Vector3 c, float x, float z)
    {
        var normal = Vector3.Cross(b - a, c - a);
        if (MathF.Abs(normal.Y) < 1e-6f)
            return a.Y;
        return a.Y - (normal.X * (x - a.X) + normal.Z * (z - a.Z)) / normal.Y;
    }

    public void Dispose()
    {
        Mesh.Dispose();
        AlphaAtlas.Dispose();
    }
}
