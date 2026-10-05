using System.Numerics;
using Engine.Rendering;
using Formats.Models;
using Silk.NET.OpenGL;

namespace Client.World;

/// <summary>
/// One animated instance of an M2: its own vertex buffer, re-skinned on the CPU from the shared model data each
/// time the pose changes. Vertices stay in render axes; bone matrices are converted to them.
/// </summary>
public sealed class SkinnedActor : IDisposable
{
    public const int Stand = 0, Death = 1, Spell = 2, Walk = 4, Run = 5, Dead = 6, AttackUnarmed = 16, Attack1H = 17,
        ReadyUnarmed = 25, Ready1H = 26, SpellPrecast = 31, SpellCast = 32, Jump = 38, Fall = 40, SwimIdle = 41, Swim = 42,
        Loot = 50, ReadySpellOmni = 52, SpellCastOmni = 54;

    /// <summary>Render axes (x, z, -y) from model axes and back.</summary>
    private static readonly Matrix4x4 ToRender = new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
    private static readonly Matrix4x4 FromRender = Matrix4x4.Transpose(ToRender);

    private readonly ModelData _data;
    private readonly M2Skeleton _skeleton;
    private readonly float[] _vertices;
    private readonly Matrix4x4[] _bones;

    public SkinnedActor(GL gl, ModelData data, M2Skeleton skeleton)
    {
        _data = data;
        _skeleton = skeleton;
        _vertices = (float[])data.Vertices.Clone();
        _bones = Enumerable.Repeat(Matrix4x4.Identity, skeleton.Bones.Count).ToArray();
        Mesh = new Mesh(gl, _vertices, data.Indices, dynamic: true);
    }

    public Mesh Mesh { get; }
    public M2Skeleton Skeleton => _skeleton;
    /// <summary>Current bone transforms in render axes (bind-pose point to posed point), for attachments.</summary>
    public IReadOnlyList<Matrix4x4> Bones => _bones;

    /// <summary>Sequence index for an animation id, falling back to <paramref name="fallback"/> then Stand.</summary>
    public int Resolve(int animationId, int fallback = Stand)
    {
        var index = _skeleton.FindSequence(animationId);
        if (index < 0)
            index = _skeleton.FindSequence(fallback);
        if (index < 0)
            index = _skeleton.FindSequence(Stand);
        return Math.Max(index, 0);
    }

    public uint Length(int sequence) => _skeleton.Sequences[sequence].Length;

    /// <summary>Poses the skeleton and re-skins the vertices into the actor's buffer.</summary>
    public void Update(int sequence, uint elapsed, uint globalTime)
    {
        _skeleton.Pose(sequence, elapsed, globalTime, _bones);
        for (var i = 0; i < _bones.Length; i++)
            _bones[i] = FromRender * _bones[i] * ToRender;

        var source = _data.Vertices;
        var indices = _skeleton.BoneIndices;
        var weights = _skeleton.BoneWeights;
        var count = Math.Min(source.Length / Mesh.FloatsPerVertex, indices.Length / 4);
        for (var v = 0; v < count; v++)
        {
            var o = v * Mesh.FloatsPerVertex;
            var position = new Vector3(source[o], source[o + 1], source[o + 2]);
            var normal = new Vector3(source[o + 3], source[o + 4], source[o + 5]);
            var skinnedPosition = Vector3.Zero;
            var skinnedNormal = Vector3.Zero;
            var total = 0f;
            for (var k = 0; k < 4; k++)
            {
                var weight = weights[v * 4 + k];
                if (weight == 0)
                    continue;
                var bone = indices[v * 4 + k];
                if (bone >= _bones.Length)
                    continue;
                var w = weight / 255f;
                skinnedPosition += Vector3.Transform(position, _bones[bone]) * w;
                skinnedNormal += Vector3.TransformNormal(normal, _bones[bone]) * w;
                total += w;
            }
            if (total <= 0)
                continue;
            _vertices[o] = skinnedPosition.X;
            _vertices[o + 1] = skinnedPosition.Y;
            _vertices[o + 2] = skinnedPosition.Z;
            if (skinnedNormal.LengthSquared() > 0)
                skinnedNormal = Vector3.Normalize(skinnedNormal);
            _vertices[o + 3] = skinnedNormal.X;
            _vertices[o + 4] = skinnedNormal.Y;
            _vertices[o + 5] = skinnedNormal.Z;
        }
        Mesh.UpdateVertices(_vertices);
    }

    public void Dispose() => Mesh.Dispose();
}
