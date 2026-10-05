using System.Numerics;

namespace Formats.Models;

/// <summary>One animation of an M2 (v256): its AnimationData id and its span on the model's shared timeline.</summary>
public sealed record M2Sequence(int Id, int SubId, uint Start, uint End, float MoveSpeed, uint Flags, int Next)
{
    public uint Length => Math.Max(1, End - Start);
}

/// <summary>
/// A keyframed value (v256 layout: interpolation, global sequence, per-animation index ranges, timestamps on the
/// shared timeline, values). Sampled linearly; hermite and bezier keys are treated as linear.
/// </summary>
public sealed class M2Track<T> where T : struct
{
    private readonly (uint Start, uint End)[] _ranges;
    private readonly uint[] _times;
    private readonly T[] _values;

    public M2Track(int interpolation, int globalSequence, (uint, uint)[] ranges, uint[] times, T[] values)
    {
        Interpolation = interpolation;
        GlobalSequence = globalSequence;
        _ranges = ranges;
        _times = times;
        _values = values;
    }

    public int Interpolation { get; }
    public int GlobalSequence { get; }
    public bool IsAnimated => _values.Length > 0;

    /// <param name="animation">Index into the model's sequences.</param>
    /// <param name="time">Time on the shared timeline (sequence start plus elapsed).</param>
    public T Sample(int animation, uint time, uint globalTime, IReadOnlyList<uint> globalSequences, T fallback, Func<T, T, float, T> lerp)
    {
        if (_values.Length == 0)
            return fallback;
        int first, last;
        if (GlobalSequence >= 0 && GlobalSequence < globalSequences.Count)
        {
            time = globalSequences[GlobalSequence] == 0 ? 0 : globalTime % globalSequences[GlobalSequence];
            (first, last) = (0, _times.Length - 1);
        }
        else if (animation < _ranges.Length)
            (first, last) = ((int)_ranges[animation].Start, (int)_ranges[animation].End);
        else
            (first, last) = (0, _times.Length - 1);

        last = Math.Min(last, Math.Min(_times.Length, _values.Length) - 1);
        if (first < 0 || first > last)
            return _values.Length == 1 ? _values[0] : fallback;
        if (first == last || time <= _times[first])
            return _values[first];
        if (time >= _times[last])
            return _values[last];

        var hi = Array.BinarySearch(_times, first, last - first + 1, time);
        if (hi >= 0)
            return _values[hi];
        hi = ~hi;
        var lo = hi - 1;
        if (Interpolation == 0)
            return _values[lo];
        var span = _times[hi] - _times[lo];
        return lerp(_values[lo], _values[hi], span == 0 ? 0 : (time - _times[lo]) / (float)span);
    }
}

public sealed record M2Bone(int Parent, uint Flags, Vector3 Pivot, M2Track<Vector3> Translation, M2Track<Quaternion> Rotation, M2Track<Vector3> Scale)
{
    public const uint Billboard = 0x8;
}

/// <summary>Bones, animations and per-vertex skin weights of an M2 (v256), for CPU skinning in model space.</summary>
public sealed class M2Skeleton
{
    private const int SequenceStride = 68;
    private const int BoneStride = 108;
    private const int VertexStride = 48;

    public IReadOnlyList<M2Sequence> Sequences { get; }
    public IReadOnlyList<M2Bone> Bones { get; }
    public IReadOnlyList<uint> GlobalSequences { get; }
    /// <summary>Four bone indices and four weights (0-255) per vertex.</summary>
    public byte[] BoneIndices { get; }
    public byte[] BoneWeights { get; }

    private M2Skeleton(IReadOnlyList<M2Sequence> sequences, IReadOnlyList<M2Bone> bones, IReadOnlyList<uint> globals, byte[] indices, byte[] weights)
    {
        Sequences = sequences;
        Bones = bones;
        GlobalSequences = globals;
        BoneIndices = indices;
        BoneWeights = weights;
    }

    /// <summary>The skeleton of an MD20 file, or null when it has no bones or animations to play.</summary>
    public static M2Skeleton? Read(byte[] data)
    {
        if (data.Length < 0xB4 || data.U32(4) > 263)
            return null;

        var globals = data.Structs<uint>(data.I32(0x18), data.I32(0x14));
        var sequenceCount = data.I32(0x1C);
        var sequenceOffset = data.I32(0x20);
        var sequences = new List<M2Sequence>(sequenceCount);
        for (var i = 0; i < sequenceCount; i++)
        {
            var o = sequenceOffset + i * SequenceStride;
            sequences.Add(new M2Sequence(data.U16(o), data.U16(o + 2), data.U32(o + 4), data.U32(o + 8), data.F32(o + 12),
                data.U32(o + 16), (short)data.U16(o + 64)));
        }

        var boneCount = data.I32(0x34);
        var boneOffset = data.I32(0x38);
        if (boneCount == 0 || sequenceCount == 0)
            return null;
        var bones = new List<M2Bone>(boneCount);
        for (var i = 0; i < boneCount; i++)
        {
            var o = boneOffset + i * BoneStride;
            bones.Add(new M2Bone((short)data.U16(o + 8), data.U32(o + 4), data.Vec3(o + 96),
                ReadTrack(data, o + 12, 12, (d, p) => d.Vec3(p)),
                ReadTrack(data, o + 40, 16, (d, p) => new Quaternion(d.F32(p), d.F32(p + 4), d.F32(p + 8), d.F32(p + 12))),
                ReadTrack(data, o + 68, 12, (d, p) => d.Vec3(p))));
        }

        var vertexCount = data.I32(0x44);
        var vertexOffset = data.I32(0x48);
        var weights = new byte[vertexCount * 4];
        var indices = new byte[vertexCount * 4];
        for (var v = 0; v < vertexCount; v++)
        {
            var o = vertexOffset + v * VertexStride;
            Array.Copy(data, o + 12, weights, v * 4, 4);
            Array.Copy(data, o + 16, indices, v * 4, 4);
        }
        return new M2Skeleton(sequences, bones, globals, indices, weights);
    }

    private static M2Track<T> ReadTrack<T>(byte[] data, int o, int valueSize, Func<byte[], int, T> read) where T : struct
    {
        var interpolation = data.U16(o);
        var global = (short)data.U16(o + 2);
        var rangeCount = data.I32(o + 4);
        var rangeOffset = data.I32(o + 8);
        var ranges = new (uint, uint)[rangeCount];
        for (var i = 0; i < rangeCount; i++)
            ranges[i] = (data.U32(rangeOffset + i * 8), data.U32(rangeOffset + i * 8 + 4));
        var times = data.Structs<uint>(data.I32(o + 16), data.I32(o + 12));
        var valueCount = data.I32(o + 20);
        var valueOffset = data.I32(o + 24);
        var values = new T[valueCount];
        for (var i = 0; i < valueCount; i++)
            values[i] = read(data, valueOffset + i * valueSize);
        return new M2Track<T>(interpolation, global, ranges, times, values);
    }

    /// <summary>First sequence with an AnimationData id (variation 0 preferred), or -1.</summary>
    public int FindSequence(int animationId)
    {
        var index = -1;
        for (var i = 0; i < Sequences.Count; i++)
            if (Sequences[i].Id == animationId && (index < 0 || Sequences[i].SubId == 0))
            {
                index = i;
                if (Sequences[i].SubId == 0)
                    break;
            }
        return index;
    }

    /// <summary>Model-space bone matrices for sequence <paramref name="sequence"/> at <paramref name="elapsed"/> ms into it.</summary>
    public void Pose(int sequence, uint elapsed, uint globalTime, Matrix4x4[] matrices)
    {
        var seq = Sequences[Math.Clamp(sequence, 0, Sequences.Count - 1)];
        var time = seq.Start + elapsed % seq.Length;
        for (var i = 0; i < Bones.Count; i++)
        {
            var bone = Bones[i];
            var translation = bone.Translation.Sample(sequence, time, globalTime, GlobalSequences, Vector3.Zero, Vector3.Lerp);
            var rotation = bone.Rotation.Sample(sequence, time, globalTime, GlobalSequences, Quaternion.Identity,
                (a, b, t) => Quaternion.Normalize(Quaternion.Slerp(a, b, t)));
            var scale = bone.Scale.Sample(sequence, time, globalTime, GlobalSequences, Vector3.One, Vector3.Lerp);
            var local = Matrix4x4.CreateTranslation(-bone.Pivot) * Matrix4x4.CreateScale(scale) *
                        Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(bone.Pivot + translation);
            matrices[i] = bone.Parent >= 0 && bone.Parent < i ? local * matrices[bone.Parent] : local;
        }
    }
}
