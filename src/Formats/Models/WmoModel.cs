using System.Numerics;

namespace Formats.Models;

public sealed record WmoMaterial(string Texture, BlendMode Blend, bool TwoSided);

/// <summary>One WMO group file: geometry with batches keyed by material index.</summary>
public sealed class WmoGroup
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] TexCoords { get; init; }
    /// <summary>Baked vertex lighting (RGBA 0..1), or null if the group has none.</summary>
    public required Vector4[]? Colors { get; init; }
    /// <summary>Visible triangle indices sorted by material.</summary>
    public required ushort[] Indices { get; init; }
    /// <summary>Every triangle, including invisible collision-only ones.</summary>
    public required ushort[] CollisionIndices { get; init; }
    public required IReadOnlyList<(int Material, int IndexStart, int IndexCount)> Batches { get; init; }

    public static WmoGroup Read(byte[] data)
    {
        var mogp = ChunkReader.Read(data).First(c => c.Id == "MOGP");
        const int headerSize = 68;

        Vector3[] positions = [], normals = [];
        Vector2[] texCoords = [];
        Vector4[]? colors = null;
        ushort[] indices = [];
        byte[] triangleMaterials = [];

        foreach (var chunk in ChunkReader.Read(data, mogp.Offset + headerSize, mogp.Offset + mogp.Size))
        {
            switch (chunk.Id)
            {
                case "MOPY":
                    // 2 bytes per triangle: flags, material id (0xFF = collision only).
                    triangleMaterials = Enumerable.Range(0, chunk.Size / 2).Select(t => data[chunk.Offset + t * 2 + 1]).ToArray();
                    break;
                case "MOVI":
                    indices = data.Structs<ushort>(chunk.Offset, chunk.Size / 2);
                    break;
                case "MOVT":
                    positions = data.Structs<Vector3>(chunk.Offset, chunk.Size / 12);
                    break;
                case "MONR":
                    normals = data.Structs<Vector3>(chunk.Offset, chunk.Size / 12);
                    break;
                case "MOTV":
                    texCoords = data.Structs<Vector2>(chunk.Offset, chunk.Size / 8);
                    break;
                case "MOCV":
                    colors = Enumerable.Range(0, chunk.Size / 4)
                        .Select(i => new Vector4(data[chunk.Offset + i * 4 + 2], data[chunk.Offset + i * 4 + 1], data[chunk.Offset + i * 4], 255) / 255f)
                        .ToArray();
                    break;
            }
        }

        var triangles = Math.Min(indices.Length / 3, triangleMaterials.Length);
        var sorted = new List<ushort>(triangles * 3);
        var batches = new List<(int, int, int)>();
        foreach (var group in Enumerable.Range(0, triangles).Where(t => triangleMaterials[t] != 0xFF).GroupBy(t => triangleMaterials[t]))
        {
            var start = sorted.Count;
            foreach (var t in group)
                sorted.AddRange([indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2]]);
            batches.Add((group.Key, start, sorted.Count - start));
        }

        return new WmoGroup
        {
            Positions = positions,
            Normals = normals.Length == positions.Length ? normals : new Vector3[positions.Length],
            TexCoords = texCoords.Length == positions.Length ? texCoords : new Vector2[positions.Length],
            Colors = colors?.Length == positions.Length ? colors : null,
            Indices = sorted.ToArray(),
            CollisionIndices = indices[..(indices.Length / 3 * 3)],
            Batches = batches,
        };
    }
}

/// <summary>World map object (building/dungeon): root file with materials plus its group files.</summary>
public sealed class WmoModel
{
    private const int MaterialStride = 64;

    public IReadOnlyList<WmoMaterial> Materials { get; }
    public IReadOnlyList<WmoGroup> Groups { get; }

    public WmoModel(byte[] root, Func<int, byte[]?> readGroup)
    {
        var groupCount = 0;
        Dictionary<int, string> textures = new();
        var materials = new List<WmoMaterial>();

        foreach (var chunk in ChunkReader.Read(root))
        {
            switch (chunk.Id)
            {
                case "MOHD":
                    groupCount = root.I32(chunk.Offset + 4);
                    break;
                case "MOTX":
                    textures = ChunkReader.ReadStringTable(root.AsSpan(chunk.Offset, chunk.Size));
                    break;
                case "MOMT":
                    for (var o = chunk.Offset; o + MaterialStride <= chunk.Offset + chunk.Size; o += MaterialStride)
                    {
                        var flags = root.U32(o);
                        var blend = (BlendMode)Math.Min(root.U32(o + 8), 3);
                        materials.Add(new WmoMaterial(textures.GetValueOrDefault(root.I32(o + 12), ""), blend, TwoSided: (flags & 0x4) != 0));
                    }
                    break;
            }
        }

        Materials = materials;
        Groups = Enumerable.Range(0, groupCount)
            .Select(readGroup)
            .OfType<byte[]>()
            .Select(WmoGroup.Read)
            .ToList();
    }

    public static string GroupFileName(string rootPath, int index) => $"{rootPath[..^4]}_{index:D3}.wmo";
}
