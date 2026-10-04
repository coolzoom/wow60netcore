using System.Numerics;

namespace Formats.Terrain;

/// <summary>M2 doodad placement (MDDF). Position is in render space.</summary>
public sealed record DoodadPlacement(string Model, uint UniqueId, Vector3 Position, Vector3 Rotation, float Scale)
{
    public const int Size = 36;

    public Matrix4x4 Transform => WorldSpace.Placement(Position, Rotation, Scale);

    internal static DoodadPlacement Read(byte[] data, int offset, string model) =>
        new(model, data.U32(offset + 4), data.Vec3(offset + 8), data.Vec3(offset + 20), data.U16(offset + 32) / 1024f);
}

/// <summary>WMO placement (MODF). Position and the world-aligned bounds are in render space.</summary>
public sealed record MapObjectPlacement(string Model, uint UniqueId, Vector3 Position, Vector3 Rotation, Vector3 BoundsMin, Vector3 BoundsMax)
{
    public const int Size = 64;

    public Matrix4x4 Transform => WorldSpace.Placement(Position, Rotation, 1f);

    internal static MapObjectPlacement Read(byte[] data, int offset, string model) =>
        new(model, data.U32(offset + 4), data.Vec3(offset + 8), data.Vec3(offset + 20), data.Vec3(offset + 32), data.Vec3(offset + 44));
}

public sealed record TextureLayer(int TextureIndex, uint Flags);

/// <summary>One of the 16x16 terrain chunks in a tile: 9x9 outer + 8x8 inner height vertices.</summary>
public sealed class AdtChunk
{
    public const int VertexCount = 145;
    public const int AlphaSize = 64;

    public required int IndexX { get; init; }
    public required int IndexY { get; init; }
    /// <summary>Render-space vertex positions, rows alternating 9 outer / 8 inner.</summary>
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    /// <summary>0..1 across the chunk, for alpha maps; tile the diffuse textures by multiplying.</summary>
    public required Vector2[] TexCoords { get; init; }
    public required TextureLayer[] Layers { get; init; }
    /// <summary>64x64 RGBA where R/G/B are the blend weights of layers 1/2/3.</summary>
    public required byte[] AlphaMap { get; init; }
    public required ushort Holes { get; init; }

    /// <summary>Holes cover 2x2 cells; bit index = (row / 2) * 4 + (col / 2).</summary>
    public bool IsHole(int cellRow, int cellColumn) => (Holes & (1 << ((cellRow / 2) * 4 + cellColumn / 2))) != 0;

    /// <summary>Triangle list (4 triangles per cell around its center vertex), skipping holes.</summary>
    public List<int> BuildIndices()
    {
        var indices = new List<int>(8 * 8 * 12);
        for (var row = 0; row < 8; row++)
        for (var column = 0; column < 8; column++)
        {
            if (IsHole(row, column))
                continue;
            var topLeft = row * 17 + column;
            var topRight = topLeft + 1;
            var bottomLeft = topLeft + 17;
            var bottomRight = bottomLeft + 1;
            var center = topLeft + 9;
            indices.AddRange([center, topRight, topLeft, center, bottomRight, topRight, center, bottomLeft, bottomRight, center, topLeft, bottomLeft]);
        }
        return indices;
    }

    public static (int Row, int Column, bool Inner) VertexGridPosition(int index)
    {
        var row = index / 17;
        var rest = index % 17;
        return rest < 9 ? (row, rest, false) : (row, rest - 9, true);
    }
}

/// <summary>Terrain tile (ADT v18, as used by the 1.12 client).</summary>
public sealed class AdtFile
{
    private const uint DoNotFixAlphaMap = 0x8000;

    public int TileX { get; }
    public int TileY { get; }
    public IReadOnlyList<string> Textures { get; }
    public IReadOnlyList<DoodadPlacement> Doodads { get; }
    public IReadOnlyList<MapObjectPlacement> MapObjects { get; }
    public IReadOnlyList<AdtChunk> Chunks { get; }

    public AdtFile(byte[] data, int tileX, int tileY)
    {
        TileX = tileX;
        TileY = tileY;

        Dictionary<int, string> modelNames = new(), wmoNames = new();
        int[] modelOffsets = [], wmoOffsets = [];
        var textures = new List<string>();
        var doodads = new List<DoodadPlacement>();
        var mapObjects = new List<MapObjectPlacement>();
        var chunks = new List<AdtChunk>(256);

        foreach (var chunk in ChunkReader.Read(data))
        {
            switch (chunk.Id)
            {
                case "MTEX":
                    textures.AddRange(ChunkReader.ReadStringTable(data.AsSpan(chunk.Offset, chunk.Size)).Values);
                    break;
                case "MMDX":
                    modelNames = ChunkReader.ReadStringTable(data.AsSpan(chunk.Offset, chunk.Size));
                    break;
                case "MMID":
                    modelOffsets = data.Structs<int>(chunk.Offset, chunk.Size / 4);
                    break;
                case "MWMO":
                    wmoNames = ChunkReader.ReadStringTable(data.AsSpan(chunk.Offset, chunk.Size));
                    break;
                case "MWID":
                    wmoOffsets = data.Structs<int>(chunk.Offset, chunk.Size / 4);
                    break;
                case "MDDF":
                    for (var o = chunk.Offset; o + DoodadPlacement.Size <= chunk.Offset + chunk.Size; o += DoodadPlacement.Size)
                        if (Resolve(data.I32(o), modelOffsets, modelNames) is { } model)
                            doodads.Add(DoodadPlacement.Read(data, o, model));
                    break;
                case "MODF":
                    for (var o = chunk.Offset; o + MapObjectPlacement.Size <= chunk.Offset + chunk.Size; o += MapObjectPlacement.Size)
                        if (Resolve(data.I32(o), wmoOffsets, wmoNames) is { } wmo)
                            mapObjects.Add(MapObjectPlacement.Read(data, o, wmo));
                    break;
                case "MCNK":
                    chunks.Add(ReadChunk(data, chunk));
                    break;
            }
        }

        Textures = textures;
        Doodads = doodads;
        MapObjects = mapObjects;
        Chunks = chunks;
    }

    public static string FileName(string mapDirectory, int tileX, int tileY) =>
        $"World\\Maps\\{mapDirectory}\\{mapDirectory}_{tileX}_{tileY}.adt";

    private static string? Resolve(int index, int[] offsets, Dictionary<int, string> names) =>
        (uint)index < offsets.Length && names.TryGetValue(offsets[index], out var name) ? name : null;

    private static AdtChunk ReadChunk(byte[] data, Chunk chunk)
    {
        var header = chunk.Offset;
        // Sub-chunk offsets in the header are relative to the MCNK chunk header (8 bytes before its data).
        var start = chunk.Offset - 8;
        var flags = data.U32(header);
        var indexX = data.I32(header + 4);
        var indexY = data.I32(header + 8);
        var layerCount = data.I32(header + 12);
        var heightOffset = start + data.I32(header + 20) + 8;
        var normalOffset = start + data.I32(header + 24) + 8;
        var layerOffset = start + data.I32(header + 28) + 8;
        var alphaOffset = start + data.I32(header + 36) + 8;
        var holes = data.U16(header + 60);
        // Chunk corner in world coordinates (X, Y, Z).
        var corner = data.Vec3(header + 104);

        var positions = new Vector3[AdtChunk.VertexCount];
        var normals = new Vector3[AdtChunk.VertexCount];
        var texCoords = new Vector2[AdtChunk.VertexCount];
        for (var i = 0; i < AdtChunk.VertexCount; i++)
        {
            var (row, column, inner) = AdtChunk.VertexGridPosition(i);
            var x = column + (inner ? 0.5f : 0f);
            var z = row + (inner ? 0.5f : 0f);
            positions[i] = new Vector3(
                WorldSpace.Origin - corner.Y + x * WorldSpace.UnitSize,
                corner.Z + data.F32(heightOffset + i * 4),
                WorldSpace.Origin - corner.X + z * WorldSpace.UnitSize);
            texCoords[i] = new Vector2(x / 8f, z / 8f);

            // MCNR stores signed bytes in world axes (X, Y, Z), scaled by 127.
            var n = new Vector3((sbyte)data[normalOffset + i * 3], (sbyte)data[normalOffset + i * 3 + 1], (sbyte)data[normalOffset + i * 3 + 2]);
            normals[i] = Vector3.Normalize(new Vector3(-n.Y, n.Z, -n.X));
        }

        var layers = new TextureLayer[Math.Clamp(layerCount, 0, 4)];
        var alpha = new byte[AdtChunk.AlphaSize * AdtChunk.AlphaSize * 4];
        for (var i = 0; i < layers.Length; i++)
        {
            var entry = layerOffset + i * 16;
            layers[i] = new TextureLayer(data.I32(entry), data.U32(entry + 4));
            if (i == 0)
                continue;

            // 4-bit uncompressed alpha: 64x64 values, two per byte, low nibble first.
            var source = alphaOffset + data.I32(entry + 8);
            for (var p = 0; p < AdtChunk.AlphaSize * AdtChunk.AlphaSize; p++)
            {
                var packed = source + p / 2 < data.Length ? data[source + p / 2] : 0;
                alpha[p * 4 + i - 1] = (byte)(((packed >> ((p & 1) * 4)) & 0xF) * 17);
            }

            // Only 63x63 values are meaningful unless the chunk says otherwise; repeat the last valid row/column.
            if ((flags & DoNotFixAlphaMap) == 0)
            {
                const int n = AdtChunk.AlphaSize;
                for (var y = 0; y < n; y++)
                    alpha[(y * n + n - 1) * 4 + i - 1] = alpha[(y * n + n - 2) * 4 + i - 1];
                for (var x = 0; x < n; x++)
                    alpha[((n - 1) * n + x) * 4 + i - 1] = alpha[((n - 2) * n + x) * 4 + i - 1];
            }
        }
        for (var p = 0; p < AdtChunk.AlphaSize * AdtChunk.AlphaSize; p++)
            alpha[p * 4 + 3] = 255;

        return new AdtChunk
        {
            IndexX = indexX,
            IndexY = indexY,
            Positions = positions,
            Normals = normals,
            TexCoords = texCoords,
            Layers = layers,
            AlphaMap = alpha,
            Holes = holes,
        };
    }
}
