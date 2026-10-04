using System.Numerics;
using Engine.Rendering;
using Engine.World;
using Formats.Dbc;
using Formats.Models;
using Formats.Mpq;
using Formats.Terrain;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Client.World;

public sealed class ModelInstance(string model, Matrix4x4 transform, float scale)
{
    public string Model { get; } = model;
    public Matrix4x4 Transform { get; } = transform;
    public Matrix4x4 Inverse { get; } = Matrix4x4.Invert(transform, out var inverse) ? inverse : Matrix4x4.Identity;
    public float Scale { get; } = scale;
    public Vector3 Position => Transform.Translation;
    public bool IsMapObject => Model.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase);
    public int References { get; set; }
}

/// <param name="DoodadDistance">Use <see cref="float.PositiveInfinity"/> for no distance culling.</param>
/// <param name="LoadRadius">Detail tiles loaded around the player; <see cref="AllTiles"/> loads the whole map.</param>
/// <param name="DistantTerrain">Draw every other tile of the map as low-detail WDL terrain with minimap textures.</param>
public sealed record RenderSettings(
    bool Doodads = true, bool MapObjects = true, float DoodadDistance = 350f, int LoadRadius = 1, bool DistantTerrain = true)
{
    public const int AllTiles = int.MaxValue;
}

public sealed record SceneStats(int Tiles, int PendingTiles, int DistantTiles, int Doodads, int MapObjects, int DrawCalls, long TileBytes);

/// <summary>A loaded map: streams terrain tiles around a focus point and draws terrain, doodads and WMOs.</summary>
public sealed class WorldScene : IHeightField, IDisposable
{
    private readonly GL _gl;
    private readonly MpqFileSystem _files;
    private readonly AssetCache _assets;
    private readonly Dictionary<(int, int), TerrainTile> _tiles = new();
    private readonly Dictionary<(int, int), Task<TerrainTileData?>> _pendingTiles = new();
    private readonly Dictionary<uint, ModelInstance> _placed = new();
    private readonly List<ModelInstance> _userPlaced = new();
    private readonly List<ModelInstance> _globalObjects = new();
    private readonly List<(GpuModel Model, ModelBatchData Batch, Matrix4x4 Transform)> _transparent = new();
    private readonly Lazy<DistantTerrain?> _distant;
    private int _drawCalls;
    private int _distantDrawn;

    /// <summary>Tiles parsed concurrently; finished-but-not-uploaded tiles hold ~10 MB each, so this caps memory.</summary>
    private const int MaxTilesInFlight = 6;

    public MapEntry Map { get; }
    public WdtFile Wdt { get; }
    public RenderSettings Settings { get; set; } = new();

    public Vector2 Min => Vector2.Zero;
    public Vector2 Max => new(WorldSpace.TilesPerSide * WorldSpace.TileSize);

    public WorldScene(GL gl, MpqFileSystem files, AssetCache assets, MapEntry map)
    {
        _gl = gl;
        _files = files;
        _assets = assets;
        Map = map;
        Wdt = new WdtFile(files.Read(WdtPath(map.Directory)));
        if (Wdt is { GlobalWmo: { } wmo, GlobalWmoPlacement: { } placement })
            _globalObjects.Add(new ModelInstance(wmo, placement.Transform, 1f));
        _distant = new Lazy<DistantTerrain?>(CreateDistantTerrain);
    }

    private DistantTerrain? CreateDistantTerrain()
    {
        if (_files.TryRead($"World\\Maps\\{Map.Directory}\\{Map.Directory}.wdl") is not { } wdl)
            return null;
        var minimaps = _files.TryRead(MinimapIndex.TranslationFile) is { } trs ? new MinimapIndex(trs) : null;
        return new DistantTerrain(_gl, new WdlFile(wdl), minimaps, Map.Directory);
    }

    public static string WdtPath(string directory) => $"World\\Maps\\{directory}\\{directory}.wdt";

    /// <summary>
    /// A spawn point above something to stand on: the map's main WMO (dungeons), else the first WMO found
    /// scanning tiles outward from the map's middle, else the middle tile. Y is a "drop from here" height.
    /// </summary>
    public Vector3 DefaultSpawn()
    {
        if (Wdt.GlobalWmoPlacement is { } global)
            return Above(global);

        var tiles = Wdt.Tiles().ToList();
        if (tiles.Count == 0)
            return new Vector3(WorldSpace.Origin, HighestProbe, WorldSpace.Origin);

        var middle = new Vector2((float)tiles.Average(t => t.X), (float)tiles.Average(t => t.Y));
        tiles = tiles.OrderBy(t => Vector2.DistanceSquared(new Vector2(t.X, t.Y), middle)).ToList();
        foreach (var (x, y) in tiles.Take(16))
        {
            if (_files.TryRead(AdtFile.FileName(Map.Directory, x, y)) is { } data && new AdtFile(data, x, y).MapObjects.FirstOrDefault() is { } wmo)
                return Above(wmo);
        }
        return new Vector3((tiles[0].X + 0.5f) * WorldSpace.TileSize, HighestProbe, (tiles[0].Y + 0.5f) * WorldSpace.TileSize);

        static Vector3 Above(MapObjectPlacement wmo) =>
            new((wmo.BoundsMin.X + wmo.BoundsMax.X) / 2, wmo.BoundsMax.Y, (wmo.BoundsMin.Z + wmo.BoundsMax.Z) / 2);
    }

    /// <summary>A probe height above any terrain or building, i.e. "take the topmost surface".</summary>
    public const float HighestProbe = 10_000f;

    /// <summary>True once the tiles and WMOs around <paramref name="focus"/> are loaded, so floor queries there are final.</summary>
    public bool IsSettledAround(Vector3 focus)
    {
        var (cx, cy) = WorldSpace.TileAt(focus.X, focus.Z);
        for (var y = cy - 1; y <= cy + 1; y++)
        for (var x = cx - 1; x <= cx + 1; x++)
            if (Wdt.HasTile(x, y) && !_tiles.ContainsKey((x, y)))
                return false;

        var near = 2 * WorldSpace.TileSize;
        return MapObjects()
            .Where(i => Vector2.Distance(new(i.Position.X, i.Position.Z), new(focus.X, focus.Z)) < near)
            .All(i => _assets.TryGetModelData(i.Model, out _));
    }

    private IEnumerable<ModelInstance> MapObjects() =>
        _placed.Values.Where(i => i.IsMapObject).Concat(_globalObjects).Concat(_userPlaced.Where(i => i.IsMapObject));

    public SceneStats Stats => new(_tiles.Count, _pendingTiles.Count, _distantDrawn, _placed.Values.Count(i => !i.IsMapObject),
        _placed.Values.Count(i => i.IsMapObject) + _globalObjects.Count, _drawCalls,
        _tiles.Values.Sum(t => t.Bytes) + (_distant.IsValueCreated ? _distant.Value?.Bytes ?? 0 : 0));

    /// <summary>Places a model whose front (model +X) faces back along <paramref name="facing"/>.</summary>
    public void PlaceModel(string model, Vector3 position, float facing)
    {
        var transform = Matrix4x4.CreateRotationY(facing - MathF.PI / 2) * Matrix4x4.CreateTranslation(position);
        _userPlaced.Add(new ModelInstance(model, transform, 1f));
    }

    public bool TryGetHeight(Vector3 probe, out float height)
    {
        height = float.MinValue;
        var found = false;
        var terrain = 0f;
        var terrainKnown = _tiles.TryGetValue(WorldSpace.TileAt(probe.X, probe.Z), out var tile) && tile.TryGetHeight(probe.X, probe.Z, out terrain);
        if (terrainKnown && terrain <= probe.Y)
        {
            height = terrain;
            found = true;
        }

        foreach (var instance in MapObjects())
        {
            if (Vector2.Distance(new(instance.Position.X, instance.Position.Z), new(probe.X, probe.Z)) > 2 * WorldSpace.TileSize)
                continue;
            if (!_assets.TryGetModelData(instance.Model, out var data) || data?.Collision is not { } collision)
                continue;
            var center = Vector3.Transform(data.Center, instance.Transform);
            if (Vector2.Distance(new(center.X, center.Z), new(probe.X, probe.Z)) > data.Radius)
                continue;

            var local = Vector3.Transform(probe, instance.Inverse);
            if (!collision.TryGetFloor(local.X, local.Z, local.Y, out var floor))
                continue;
            var world = Vector3.Transform(local with { Y = floor }, instance.Transform);
            if (world.Y <= probe.Y + 0.01f && world.Y > height)
            {
                height = world.Y;
                found = true;
            }
        }

        if (!found && terrainKnown)
        {
            // Below every known surface (fell through): recover onto the terrain.
            height = terrain;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// Requests the nearest missing tiles within the load radius (a few at a time), uploads at most one finished
    /// tile per frame, and drops tiles that fell outside the radius.
    /// </summary>
    public void Update(Vector3 focus)
    {
        var (cx, cy) = WorldSpace.TileAt(focus.X, focus.Z);
        var radius = (long)Settings.LoadRadius;
        long Distance((int X, int Y) t) => Math.Max(Math.Abs(t.X - cx), Math.Abs(t.Y - cy));

        if (_pendingTiles.Count < MaxTilesInFlight)
        {
            var wanted = Wdt.Tiles()
                .Where(t => Distance(t) <= radius && !_tiles.ContainsKey(t) && !_pendingTiles.ContainsKey(t))
                .OrderBy(Distance)
                .Take(MaxTilesInFlight - _pendingTiles.Count);
            foreach (var (x, y) in wanted)
                _pendingTiles[(x, y)] = Task.Run(() => LoadTile(x, y));
        }

        foreach (var (key, task) in _pendingTiles.Where(p => p.Value.IsCompleted).OrderBy(p => Distance(p.Key)).Take(1).ToList())
        {
            _pendingTiles.Remove(key);
            if (task.Result is { } data)
                AddTile(new TerrainTile(_gl, key.Item1, key.Item2, data));
        }

        foreach (var key in _tiles.Keys.Where(k => Distance(k) > radius + 1).ToList())
            RemoveTile(key);
    }

    private TerrainTileData? LoadTile(int x, int y)
    {
        try
        {
            return TerrainTileData.Build(new AdtFile(_files.Read(AdtFile.FileName(Map.Directory, x, y)), x, y));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Failed to load tile {Map.Directory} {x},{y}: {e.Message}");
            return null;
        }
    }

    private void AddTile(TerrainTile tile)
    {
        _tiles[(tile.X, tile.Y)] = tile;
        // Objects crossing tile borders are listed in every tile they touch; uniqueId dedupes them.
        foreach (var doodad in tile.Doodads)
            Reference(doodad.UniqueId, () => new ModelInstance(M2Model.NormalizePath(doodad.Model), doodad.Transform, doodad.Scale));
        foreach (var wmo in tile.MapObjects)
            Reference(wmo.UniqueId | 0x8000_0000u, () => new ModelInstance(wmo.Model, wmo.Transform, 1f));
    }

    private void Reference(uint id, Func<ModelInstance> create)
    {
        if (!_placed.TryGetValue(id, out var instance))
            _placed[id] = instance = create();
        instance.References++;
    }

    private void RemoveTile((int, int) key)
    {
        var tile = _tiles[key];
        _tiles.Remove(key);
        foreach (var id in tile.Doodads.Select(d => d.UniqueId).Concat(tile.MapObjects.Select(w => w.UniqueId | 0x8000_0000u)))
            if (_placed.TryGetValue(id, out var instance) && --instance.References <= 0)
                _placed.Remove(id);
        tile.Dispose();
    }

    public void Render(Shader terrainShader, Shader modelShader, Shader unlitShader, Vector3 cameraPosition, Frustum frustum)
    {
        _drawCalls = 0;
        _distantDrawn = 0;
        if (Settings.DistantTerrain && _distant.Value is { } distant)
        {
            _distantDrawn = distant.Render(unlitShader, _assets, frustum, (x, y) => _tiles.ContainsKey((x, y)));
            _drawCalls += _distantDrawn;
        }
        RenderTerrain(terrainShader, frustum);

        modelShader.Use();
        _transparent.Clear();
        foreach (var instance in _placed.Values.Concat(_globalObjects).Concat(_userPlaced))
        {
            if (instance.IsMapObject ? !Settings.MapObjects : !Settings.Doodads)
                continue;
            if (_assets.Model(instance.Model) is not { } model)
                continue;

            var center = Vector3.Transform(model.Data.Center, instance.Transform);
            var radius = model.Data.Radius * instance.Scale;
            if (!instance.IsMapObject && Vector3.Distance(center, cameraPosition) - radius > Settings.DoodadDistance)
                continue;
            if (!frustum.Intersects(center, radius))
                continue;

            modelShader.Set("uModel", instance.Transform);
            foreach (var batch in model.Data.Batches)
            {
                if (batch.Blend is BlendMode.Alpha or BlendMode.Additive)
                {
                    _transparent.Add((model, batch, instance.Transform));
                    continue;
                }
                modelShader.Set("uAlphaTest", batch.Blend == BlendMode.AlphaKey ? 0.5f : -1f);
                DrawBatch(model, batch);
            }
        }

        _gl.Enable(EnableCap.Blend);
        _gl.DepthMask(false);
        modelShader.Set("uAlphaTest", 0.01f);
        foreach (var (model, batch, transform) in _transparent)
        {
            _gl.BlendFunc(BlendingFactor.SrcAlpha, batch.Blend == BlendMode.Additive ? BlendingFactor.One : BlendingFactor.OneMinusSrcAlpha);
            modelShader.Set("uModel", transform);
            DrawBatch(model, batch);
        }
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    private void RenderTerrain(Shader shader, Frustum frustum)
    {
        shader.Use();
        shader.Set("uModel", Matrix4x4.Identity);
        foreach (var tile in _tiles.Values)
        {
            if (!frustum.Intersects(tile.Center, WorldSpace.TileSize))
                continue;
            tile.AlphaAtlas.Bind(4);
            foreach (var chunk in tile.Chunks)
            {
                if (chunk.IndexCount == 0)
                    continue;
                var baseLayer = chunk.Layers.Length > 0 ? _assets.Texture(chunk.Layers[0]) ?? _assets.Fallback : _assets.Fallback;
                for (var layer = 0; layer < 4; layer++)
                {
                    var texture = layer < chunk.Layers.Length ? _assets.Texture(chunk.Layers[layer]) ?? baseLayer : baseLayer;
                    texture.Bind(layer);
                }
                tile.Mesh.DrawRange(chunk.IndexStart, chunk.IndexCount);
                _drawCalls++;
            }
        }
    }

    private void DrawBatch(GpuModel model, ModelBatchData batch)
    {
        var texture = batch.Texture is null ? _assets.Fallback : _assets.Texture(batch.Texture) ?? _assets.Fallback;
        texture.Bind(0);
        model.Mesh.DrawRange(batch.IndexStart, batch.IndexCount);
        _drawCalls++;
    }

    public void Dispose()
    {
        foreach (var tile in _tiles.Values)
            tile.Dispose();
        _tiles.Clear();
        if (_distant.IsValueCreated)
            _distant.Value?.Dispose();
    }
}
