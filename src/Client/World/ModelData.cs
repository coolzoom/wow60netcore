using System.Numerics;
using Engine.Rendering;
using Engine.World;
using Formats.Models;
using Formats.Terrain;

namespace Client.World;

public sealed record ModelBatchData(int IndexStart, int IndexCount, string? Texture, BlendMode Blend, int TextureType = 0, int Geoset = 0);

/// <summary>Render-ready vertex/index buffers for an M2 or WMO, built off the main thread.</summary>
public sealed class ModelData
{
    public required float[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    public required IReadOnlyList<ModelBatchData> Batches { get; init; }
    public required Vector3 Center { get; init; }
    public required float Radius { get; init; }
    /// <summary>Walkable surfaces in model space (render axes); only built for WMOs.</summary>
    public CollisionMesh? Collision { get; init; }
    /// <summary>M2 attachment points by id: the bone they follow and their bind-pose position (render axes).</summary>
    public IReadOnlyDictionary<int, (int Bone, Vector3 Position)> Attachments { get; init; } = new Dictionary<int, (int, Vector3)>();

    public static ModelData FromM2(M2Model model)
    {
        var vertices = new float[model.Positions.Length * Mesh.FloatsPerVertex];
        for (var i = 0; i < model.Positions.Length; i++)
            Write(vertices, i, model.Positions[i], model.Normals[i], model.TexCoords[i], Vector4.One);

        var batches = model.Batches.Select(b => new ModelBatchData(b.IndexStart, b.IndexCount, b.Texture, b.Blend, b.TextureType, b.Geoset)).ToList();
        var data = Build(vertices, model.Indices.Select(i => (uint)i).ToArray(), batches, model.Positions);
        var attachments = new Dictionary<int, (int, Vector3)>();
        foreach (var a in model.Attachments)
            attachments.TryAdd(a.Id, (a.Bone, WorldSpace.ModelToRender(a.Position)));
        return new ModelData
        {
            Vertices = data.Vertices, Indices = data.Indices, Batches = data.Batches, Center = data.Center, Radius = data.Radius,
            Attachments = attachments,
        };
    }

    public static ModelData FromWmo(WmoModel model)
    {
        var vertices = new List<float>();
        var indices = new List<uint>();
        var batches = new List<ModelBatchData>();
        var positions = new List<Vector3>();
        var collisionIndices = new List<int>();

        foreach (var group in model.Groups)
        {
            var baseVertex = (uint)positions.Count;
            collisionIndices.AddRange(group.CollisionIndices.Select(i => (int)baseVertex + i));
            var groupVertices = new float[group.Positions.Length * Mesh.FloatsPerVertex];
            for (var i = 0; i < group.Positions.Length; i++)
            {
                var color = group.Colors is { } colors ? colors[i] with { W = 1f } : Vector4.One;
                Write(groupVertices, i, group.Positions[i], group.Normals[i], group.TexCoords[i], color);
            }
            vertices.AddRange(groupVertices);
            positions.AddRange(group.Positions);

            foreach (var (material, start, count) in group.Batches)
            {
                var info = model.Materials[material];
                batches.Add(new ModelBatchData(indices.Count, count, info.Texture.Length > 0 ? info.Texture : null, info.Blend));
                for (var i = 0; i < count; i++)
                    indices.Add(baseVertex + group.Indices[start + i]);
            }
        }

        var collision = new CollisionMesh(positions.Select(WorldSpace.ModelToRender).ToList(), collisionIndices);
        return Build(vertices.ToArray(), indices.ToArray(), batches, positions, collision);
    }

    private static ModelData Build(float[] vertices, uint[] indices, List<ModelBatchData> batches, IReadOnlyList<Vector3> modelPositions,
        CollisionMesh? collision = null)
    {
        var center = Vector3.Zero;
        var radius = 1f;
        if (modelPositions.Count > 0)
        {
            var render = modelPositions.Select(WorldSpace.ModelToRender).ToList();
            var min = render.Aggregate(Vector3.Min);
            var max = render.Aggregate(Vector3.Max);
            center = (min + max) / 2;
            radius = Math.Max(1f, Vector3.Distance(min, max) / 2);
        }
        return new ModelData { Vertices = vertices, Indices = indices, Batches = batches, Center = center, Radius = radius, Collision = collision };
    }

    private static void Write(float[] target, int index, Vector3 position, Vector3 normal, Vector2 uv, Vector4 color)
    {
        var p = WorldSpace.ModelToRender(position);
        var n = WorldSpace.ModelToRender(normal);
        var o = index * Mesh.FloatsPerVertex;
        target[o] = p.X; target[o + 1] = p.Y; target[o + 2] = p.Z;
        target[o + 3] = n.X; target[o + 4] = n.Y; target[o + 5] = n.Z;
        target[o + 6] = uv.X; target[o + 7] = uv.Y;
        target[o + 8] = color.X; target[o + 9] = color.Y; target[o + 10] = color.Z; target[o + 11] = color.W;
    }
}
