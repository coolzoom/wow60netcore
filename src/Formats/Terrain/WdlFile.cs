namespace Formats.Terrain;

/// <summary>Low-detail heightmap of a whole map (WDL): a 17x17 grid of heights per ADT tile, used for far terrain.</summary>
public sealed class WdlFile
{
    public const int GridSize = 17;

    private readonly float[]?[,] _tiles = new float[]?[WorldSpace.TilesPerSide, WorldSpace.TilesPerSide];

    public WdlFile(byte[] data)
    {
        var maof = ChunkReader.Read(data).FirstOrDefault(c => c.Id == "MAOF");
        if (maof.Size < WorldSpace.TilesPerSide * WorldSpace.TilesPerSide * 4)
            return;

        for (var y = 0; y < WorldSpace.TilesPerSide; y++)
        for (var x = 0; x < WorldSpace.TilesPerSide; x++)
        {
            // Absolute file offset of the tile's MARE chunk header; 0 = no tile.
            var offset = (int)data.U32(maof.Offset + (y * WorldSpace.TilesPerSide + x) * 4);
            if (offset == 0 || offset + 8 + GridSize * GridSize * 2 > data.Length)
                continue;
            var heights = new float[GridSize * GridSize];
            for (var i = 0; i < heights.Length; i++)
                heights[i] = data.I16(offset + 8 + i * 2);
            _tiles[x, y] = heights;
        }
    }

    /// <summary>Row-major 17x17 heights (rows along +Z / south, columns along +X), or null if the tile is absent.</summary>
    public float[]? Heights(int x, int y) =>
        x is >= 0 and < WorldSpace.TilesPerSide && y is >= 0 and < WorldSpace.TilesPerSide ? _tiles[x, y] : null;

    public IEnumerable<(int X, int Y)> Tiles()
    {
        for (var y = 0; y < WorldSpace.TilesPerSide; y++)
        for (var x = 0; x < WorldSpace.TilesPerSide; x++)
            if (_tiles[x, y] is not null)
                yield return (x, y);
    }
}

/// <summary>Maps "Map\mapX_Y.blp" minimap names to their hashed files via textures\Minimap\md5translate.trs.</summary>
public sealed class MinimapIndex
{
    public const string TranslationFile = "textures\\Minimap\\md5translate.trs";

    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public MinimapIndex(byte[] translation)
    {
        foreach (var line in System.Text.Encoding.UTF8.GetString(translation).Split('\n'))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length == 2 && !parts[0].StartsWith("dir:", StringComparison.OrdinalIgnoreCase))
                _files[parts[0]] = "textures\\Minimap\\" + parts[1];
        }
    }

    public int Count => _files.Count;

    public string? Find(string mapDirectory, int tileX, int tileY) =>
        _files.GetValueOrDefault($"{mapDirectory}\\map{tileX}_{tileY}.blp");
}
