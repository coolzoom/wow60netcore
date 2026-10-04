using System.Numerics;
using Engine.Rendering;
using Formats.Terrain;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Client.World;

/// <summary>
/// Whole-map far terrain: the WDL low-detail heightmap (17x17 per tile) textured with the tile's minimap image,
/// which already has lighting baked in. One shared mesh; tiles that are loaded in full detail are skipped.
/// </summary>
public sealed class DistantTerrain : IDisposable
{
    private const int Grid = WdlFile.GridSize;
    private const int MinimapSize = 256;

    private readonly Mesh _mesh;
    private readonly List<(int X, int Y, int IndexStart, int IndexCount, Vector3 Center, string? Texture)> _tiles = new();

    public int TileCount => _tiles.Count;
    public long Bytes => _mesh.Bytes;

    public DistantTerrain(GL gl, WdlFile wdl, MinimapIndex? minimaps, string mapDirectory)
    {
        var vertices = new List<float>();
        var indices = new List<uint>();
        foreach (var (tx, ty) in wdl.Tiles())
        {
            var heights = wdl.Heights(tx, ty)!;
            var baseVertex = (uint)(vertices.Count / Mesh.FloatsPerVertex);
            for (var row = 0; row < Grid; row++)
            for (var column = 0; column < Grid; column++)
            {
                var x = (tx + column / 16f) * WorldSpace.TileSize;
                var z = (ty + row / 16f) * WorldSpace.TileSize;
                // Inset UVs by half a texel so linear filtering never wraps to the opposite edge.
                var u = (column / 16f * (MinimapSize - 1) + 0.5f) / MinimapSize;
                var v = (row / 16f * (MinimapSize - 1) + 0.5f) / MinimapSize;
                vertices.AddRange([x, heights[row * Grid + column], z, 0, 1, 0, u, v, 1, 1, 1, 1]);
            }

            var start = indices.Count;
            for (var row = 0; row < Grid - 1; row++)
            for (var column = 0; column < Grid - 1; column++)
            {
                var i = baseVertex + (uint)(row * Grid + column);
                indices.AddRange([i, i + Grid, i + 1, i + 1, i + Grid, i + Grid + 1]);
            }

            var average = heights.Average();
            var center = new Vector3((tx + 0.5f) * WorldSpace.TileSize, average, (ty + 0.5f) * WorldSpace.TileSize);
            _tiles.Add((tx, ty, start, indices.Count - start, center, minimaps?.Find(mapDirectory, tx, ty)));
        }
        _mesh = new Mesh(gl, vertices.ToArray(), indices.ToArray());
    }

    public int Render(Shader shader, AssetCache assets, Frustum frustum, Func<int, int, bool> isDetailed)
    {
        var drawn = 0;
        shader.Use();
        shader.Set("uModel", Matrix4x4.Identity);
        foreach (var tile in _tiles)
        {
            if (isDetailed(tile.X, tile.Y) || !frustum.Intersects(tile.Center, WorldSpace.TileSize))
                continue;
            var texture = tile.Texture is null ? assets.Fallback : assets.Texture(tile.Texture) ?? assets.Fallback;
            texture.Bind(0);
            _mesh.DrawRange(tile.IndexStart, tile.IndexCount);
            drawn++;
        }
        return drawn;
    }

    public void Dispose() => _mesh.Dispose();
}
