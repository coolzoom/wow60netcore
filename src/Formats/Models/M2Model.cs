using System.Numerics;

namespace Formats.Models;

public enum BlendMode
{
    Opaque = 0,
    AlphaKey = 1,
    Alpha = 2,
    Additive = 3,
}

/// <summary>A draw range sharing one texture and blend state. Texture is null for runtime-replaceable skins.</summary>
public sealed record ModelBatch(int IndexStart, int IndexCount, string? Texture, BlendMode Blend, bool TwoSided);

/// <summary>Static geometry of an M2 model (v256, 1.12), from its highest-detail view in bind pose.</summary>
public sealed class M2Model
{
    private const uint Magic = 0x3032444D; // "MD20"
    private const int VertexStride = 48;
    private const int ViewStride = 44;
    private const int SubmeshStride = 32;
    private const int BatchStride = 24;

    /// <summary>Model-local positions (X forward, Y left, Z up); see WorldSpace.ModelToRender.</summary>
    public Vector3[] Positions { get; }
    public Vector3[] Normals { get; }
    public Vector2[] TexCoords { get; }
    public ushort[] Indices { get; }
    public IReadOnlyList<ModelBatch> Batches { get; }
    public string Name { get; }

    public M2Model(byte[] data)
    {
        if (data.Length < 0xB4 || data.U32(0) != Magic)
            throw new InvalidDataException("Not an MD20 model.");
        var version = data.U32(4);
        if (version > 263)
            throw new NotSupportedException($"M2 version {version} uses external skin files and is not supported.");

        Name = data.CString((int)data.U32(0x0C), (int)data.U32(0x08));

        var vertexCount = data.I32(0x44);
        var vertexOffset = data.I32(0x48);
        Positions = new Vector3[vertexCount];
        Normals = new Vector3[vertexCount];
        TexCoords = new Vector2[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var o = vertexOffset + i * VertexStride;
            Positions[i] = data.Vec3(o);
            Normals[i] = data.Vec3(o + 20);
            TexCoords[i] = data.Vec2(o + 32);
        }

        if (data.I32(0x4C) == 0)
        {
            Indices = [];
            Batches = [];
            return;
        }

        var view = data.I32(0x50);
        var vertexLookup = data.Structs<ushort>(data.I32(view + 4), data.I32(view));
        var triangles = data.Structs<ushort>(data.I32(view + 12), data.I32(view + 8));
        Indices = triangles.Select(t => vertexLookup[t]).ToArray();

        var textureNames = ReadTextures(data);
        var textureLookup = data.Structs<ushort>(data.I32(0x98), data.I32(0x94));
        var materials = data.Structs<uint>(data.I32(0x88), data.I32(0x84));

        var submeshOffset = data.I32(view + 28);
        var submeshCount = data.I32(view + 24);
        var batchCount = data.I32(view + 32);
        var batchOffset = data.I32(view + 36);

        var batches = new List<ModelBatch>(batchCount);
        for (var b = 0; b < batchCount; b++)
        {
            var o = batchOffset + b * BatchStride;
            var submesh = data.U16(o + 4);
            var material = data.U16(o + 10);
            var textureCombo = data.U16(o + 16);
            if (submesh >= submeshCount)
                continue;

            var s = submeshOffset + submesh * SubmeshStride;
            var indexStart = data.U16(s + 8);
            var indexCount = data.U16(s + 10);

            var texture = textureCombo < textureLookup.Length && textureLookup[textureCombo] < textureNames.Length
                ? textureNames[textureLookup[textureCombo]]
                : null;
            var flags = material < materials.Length ? materials[material] & 0xFFFF : 0;
            var blend = material < materials.Length ? (BlendMode)Math.Min(materials[material] >> 16, 3) : BlendMode.Opaque;
            batches.Add(new ModelBatch(indexStart, indexCount, texture, blend, TwoSided: (flags & 0x4) != 0));
        }
        Batches = batches;
    }

    /// <summary>ADTs reference models by their old ".mdx" name; the archives store ".m2".</summary>
    public static string NormalizePath(string path) =>
        path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
            ? path[..^4] + ".m2"
            : path;

    private static string?[] ReadTextures(byte[] data)
    {
        var count = data.I32(0x5C);
        var offset = data.I32(0x60);
        var names = new string?[count];
        for (var i = 0; i < count; i++)
        {
            var o = offset + i * 16;
            // Type 0 = file name; other types are skins chosen at runtime from DBC data.
            names[i] = data.U32(o) == 0 && data.I32(o + 8) > 1 ? data.CString(data.I32(o + 12), data.I32(o + 8)) : null;
        }
        return names;
    }
}
