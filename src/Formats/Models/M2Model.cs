using System.Numerics;

namespace Formats.Models;

public enum BlendMode
{
    Opaque = 0,
    AlphaKey = 1,
    Alpha = 2,
    Additive = 3,
}

/// <summary>
/// A draw range sharing one texture and blend state. Texture is null for runtime-replaceable skins, whose kind is
/// <see cref="TextureType"/> (1 character skin, 2 cape, 6 hair, 11-13 creature skins). Geoset is the submesh id
/// characters use to pick hair styles and equipment pieces (0 = always shown).
/// </summary>
public sealed record ModelBatch(int IndexStart, int IndexCount, string? Texture, BlendMode Blend, bool TwoSided,
    int TextureType = 0, int Geoset = 0);

/// <summary>A point items attach to (1 right hand, 2 left hand, 0 shield, 5/6 shoulders, 11 helmet, 26-28 sheathed).</summary>
public sealed record M2Attachment(int Id, int Bone, Vector3 Position);

/// <summary>Static geometry of an M2 model (v256, 1.12), from its highest-detail view in bind pose.</summary>
public sealed class M2Model
{
    private const uint Magic = 0x3032444D; // "MD20"
    private const int VertexStride = 48;
    private const int ViewStride = 44;
    private const int SubmeshStride = 32;
    private const int BatchStride = 24;
    private const int AttachmentStride = 48;

    /// <summary>Model-local positions (X forward, Y left, Z up); see WorldSpace.ModelToRender.</summary>
    public Vector3[] Positions { get; }
    public Vector3[] Normals { get; }
    public Vector2[] TexCoords { get; }
    public ushort[] Indices { get; }
    public IReadOnlyList<ModelBatch> Batches { get; }
    public IReadOnlyList<M2Attachment> Attachments { get; }
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

        var attachments = new List<M2Attachment>();
        if (data.Length >= 0x10C)
        {
            var attachmentCount = data.I32(0x104);
            var attachmentOffset = data.I32(0x108);
            for (var i = 0; i < attachmentCount && attachmentOffset + (i + 1) * AttachmentStride <= data.Length; i++)
            {
                var o = attachmentOffset + i * AttachmentStride;
                attachments.Add(new M2Attachment((int)data.U32(o), data.U16(o + 4), data.Vec3(o + 8)));
            }
        }
        Attachments = attachments;

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

        var (textureNames, textureTypes) = ReadTextures(data);
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

            var textureIndex = textureCombo < textureLookup.Length ? textureLookup[textureCombo] : -1;
            var texture = textureIndex >= 0 && textureIndex < textureNames.Length ? textureNames[textureIndex] : null;
            var textureType = textureIndex >= 0 && textureIndex < textureTypes.Length ? textureTypes[textureIndex] : 0;
            var flags = material < materials.Length ? materials[material] & 0xFFFF : 0;
            var blend = material < materials.Length ? (BlendMode)Math.Min(materials[material] >> 16, 3) : BlendMode.Opaque;
            batches.Add(new ModelBatch(indexStart, indexCount, texture, blend, TwoSided: (flags & 0x4) != 0, textureType, data.U16(s)));
        }
        Batches = batches;
    }

    /// <summary>ADTs reference models by their old ".mdx" name; the archives store ".m2".</summary>
    public static string NormalizePath(string path) =>
        path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
            ? path[..^4] + ".m2"
            : path;

    private static (string?[] Names, int[] Types) ReadTextures(byte[] data)
    {
        var count = data.I32(0x5C);
        var offset = data.I32(0x60);
        var names = new string?[count];
        var types = new int[count];
        for (var i = 0; i < count; i++)
        {
            var o = offset + i * 16;
            // Type 0 = file name; other types are skins chosen at runtime from DBC data.
            types[i] = (int)data.U32(o);
            names[i] = types[i] == 0 && data.I32(o + 8) > 1 ? data.CString(data.I32(o + 12), data.I32(o + 8)) : null;
        }
        return (names, types);
    }
}
