namespace Formats.Terrain;

/// <summary>Map index: which of the 64x64 ADT tiles exist, plus the global WMO of WMO-only maps (dungeons).</summary>
public sealed class WdtFile
{
    private readonly bool[,] _tiles = new bool[WorldSpace.TilesPerSide, WorldSpace.TilesPerSide];

    public string? GlobalWmo { get; }
    public MapObjectPlacement? GlobalWmoPlacement { get; }

    public WdtFile(byte[] data)
    {
        string? wmoName = null;
        foreach (var chunk in ChunkReader.Read(data))
        {
            switch (chunk.Id)
            {
                case "MAIN":
                    // Entries are row-major by tile Y; flag bit 0 = ADT present.
                    for (var y = 0; y < WorldSpace.TilesPerSide; y++)
                    for (var x = 0; x < WorldSpace.TilesPerSide; x++)
                        _tiles[x, y] = (data.U32(chunk.Offset + (y * WorldSpace.TilesPerSide + x) * 8) & 1) != 0;
                    break;
                case "MWMO" when chunk.Size > 0:
                    wmoName = data.CString(chunk.Offset, chunk.Size);
                    break;
                case "MODF" when chunk.Size >= MapObjectPlacement.Size && wmoName is not null:
                    GlobalWmo = wmoName;
                    GlobalWmoPlacement = MapObjectPlacement.Read(data, chunk.Offset, wmoName);
                    break;
            }
        }
    }

    public bool HasTile(int x, int y) =>
        x is >= 0 and < WorldSpace.TilesPerSide && y is >= 0 and < WorldSpace.TilesPerSide && _tiles[x, y];

    public IEnumerable<(int X, int Y)> Tiles()
    {
        for (var y = 0; y < WorldSpace.TilesPerSide; y++)
        for (var x = 0; x < WorldSpace.TilesPerSide; x++)
            if (_tiles[x, y])
                yield return (x, y);
    }
}
