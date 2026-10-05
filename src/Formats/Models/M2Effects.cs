using System.Numerics;

namespace Formats.Models;

/// <summary>An animated batch color: RGB and an alpha (fixed point, 0x7FFF = 1).</summary>
public sealed record M2Color(M2Track<Vector3> Rgb, M2Track<short> Alpha);

/// <summary>UV animation of a batch: translation, rotation and scale of its texture coordinates.</summary>
public sealed record M2TextureTransform(M2Track<Vector3> Translation, M2Track<Quaternion> Rotation, M2Track<Vector3> Scale);

/// <summary>
/// A camera (v256, 124 bytes). Position and target are animated offsets added to their base values, in model
/// space; the field of view is diagonal, in radians.
/// </summary>
public sealed record M2Camera(int Type, float FieldOfView, float FarClip, float NearClip, M2Track<Vector3> Position, Vector3 PositionBase,
    M2Track<Vector3> Target, Vector3 TargetBase, M2Track<float> Roll);

/// <summary>A light (v256, 212 bytes): type 0 directional (from its position toward the origin), 1 point.</summary>
public sealed record M2Light(int Type, int Bone, Vector3 Position, M2Track<Vector3> AmbientColor, M2Track<float> AmbientIntensity,
    M2Track<Vector3> DiffuseColor, M2Track<float> DiffuseIntensity, M2Track<float> AttenuationStart, M2Track<float> AttenuationEnd,
    M2Track<byte> Visibility)
{
    public const int Directional = 0, Point = 1;
}

/// <summary>
/// A particle emitter (v256, 504 bytes). Speeds are yards per second, lifespans seconds, rates particles per second.
/// Color, size and texture tile change over a particle's life: start to middle until <see cref="Midpoint"/>, then
/// middle to end. Tiles index a <see cref="Rows"/> x <see cref="Columns"/> grid of the texture.
/// </summary>
public sealed record M2ParticleEmitter(
    uint Flags, Vector3 Position, int Bone, int Texture, BlendMode Blend, int EmitterType, int Rows, int Columns,
    M2Track<float> Speed, M2Track<float> SpeedVariation, M2Track<float> VerticalRange, M2Track<float> HorizontalRange,
    M2Track<float> Gravity, M2Track<float> Lifespan, M2Track<float> EmissionRate, M2Track<float> AreaLength, M2Track<float> AreaWidth,
    float Midpoint, Vector4[] Colors, float[] Sizes, int[] LifespanTiles, int[] DecayTiles, float Drag, float Spin, M2Track<byte> Enabled)
{
    public const int Plane = 1, Sphere = 2;
}

/// <summary>
/// Everything of an M2 (v256) that animates besides the skeleton: batch colors, texture weights and UV transforms,
/// cameras, lights and particle emitters. Glue screen scenes are built from these.
/// </summary>
public sealed class M2Effects
{
    private const int ColorStride = 56, TransformStride = 84, CameraStride = 124, LightStride = 212, ParticleStride = 504;

    public IReadOnlyList<uint> GlobalSequences { get; private init; } = [];
    public IReadOnlyList<M2Color> Colors { get; private init; } = [];
    public IReadOnlyList<M2Track<short>> Transparencies { get; private init; } = [];
    public IReadOnlyList<M2TextureTransform> TextureTransforms { get; private init; } = [];
    public IReadOnlyList<M2Camera> Cameras { get; private init; } = [];
    public IReadOnlyList<M2Light> Lights { get; private init; } = [];
    public IReadOnlyList<M2ParticleEmitter> Particles { get; private init; } = [];
    /// <summary>File name of each texture (null for replaceable ones), which particle emitters index.</summary>
    public IReadOnlyList<string?> Textures { get; private init; } = [];

    public static M2Effects Read(byte[] data)
    {
        if (data.Length < 0x144 || data.U32(4) > 263)
            return new M2Effects();
        return new M2Effects
        {
            GlobalSequences = data.Structs<uint>(data.I32(0x18), data.I32(0x14)),
            Colors = Array(data, 0x54, ColorStride, o => new M2Color(Vec3Track(data, o), ShortTrack(data, o + 28))),
            Transparencies = Array(data, 0x64, 28, o => ShortTrack(data, o)),
            TextureTransforms = Array(data, 0x74, TransformStride, o => new M2TextureTransform(Vec3Track(data, o), QuatTrack(data, o + 28), Vec3Track(data, o + 56))),
            Cameras = Array(data, 0x124, CameraStride, o => new M2Camera(data.I32(o), data.F32(o + 4), data.F32(o + 8), data.F32(o + 12),
                M2Skeleton.ReadTrack(data, o + 16, 36, (d, p) => d.Vec3(p)), data.Vec3(o + 44),
                M2Skeleton.ReadTrack(data, o + 56, 36, (d, p) => d.Vec3(p)), data.Vec3(o + 84),
                M2Skeleton.ReadTrack(data, o + 96, 12, (d, p) => d.F32(p)))),
            Lights = Array(data, 0x11C, LightStride, o => new M2Light(data.U16(o), (short)data.U16(o + 2), data.Vec3(o + 4),
                Vec3Track(data, o + 16), FloatTrack(data, o + 44), Vec3Track(data, o + 72), FloatTrack(data, o + 100),
                FloatTrack(data, o + 128), FloatTrack(data, o + 156), ByteTrack(data, o + 184))),
            Particles = Array(data, 0x13C, ParticleStride, o => ReadParticle(data, o)),
            Textures = ReadTextureNames(data),
        };
    }

    private static M2ParticleEmitter ReadParticle(byte[] data, int o)
    {
        M2Track<float> Track(int index) => FloatTrack(data, o + 0x34 + index * 28);
        var p = o + 0x14C;
        var colors = new Vector4[3];
        for (var i = 0; i < 3; i++)
        {
            var argb = data.U32(p + 4 + i * 4);
            colors[i] = new Vector4((argb >> 16 & 0xFF) / 255f, (argb >> 8 & 0xFF) / 255f, (argb & 0xFF) / 255f, (argb >> 24) / 255f);
        }
        return new M2ParticleEmitter(data.U32(o + 4), data.Vec3(o + 8), data.U16(o + 0x14), data.U16(o + 0x16),
            (BlendMode)Math.Min((int)data.U16(o + 0x28), 6), data.U16(o + 0x2A), Math.Max(1, (int)data.U16(o + 0x30)), Math.Max(1, (int)data.U16(o + 0x32)),
            Track(0), Track(1), Track(2), Track(3), Track(4), Track(5), Track(6), Track(7), Track(8),
            data.F32(p), colors, [data.F32(p + 16), data.F32(p + 20), data.F32(p + 24)],
            [(short)data.U16(p + 28), (short)data.U16(p + 30), (short)data.U16(p + 32)],
            [(short)data.U16(p + 34), (short)data.U16(p + 36), (short)data.U16(p + 38)],
            data.F32(p + 72), data.F32(p + 76), ByteTrack(data, p + 144));
    }

    private static string?[] ReadTextureNames(byte[] data)
    {
        var names = new string?[data.I32(0x5C)];
        for (var i = 0; i < names.Length; i++)
        {
            var o = data.I32(0x60) + i * 16;
            names[i] = data.U32(o) == 0 && data.I32(o + 8) > 1 ? data.CString(data.I32(o + 12), data.I32(o + 8)) : null;
        }
        return names;
    }

    private static T[] Array<T>(byte[] data, int header, int stride, Func<int, T> read)
    {
        var count = data.I32(header);
        var offset = data.I32(header + 4);
        if (count <= 0 || offset < 0 || offset + (long)count * stride > data.Length)
            return [];
        var items = new T[count];
        for (var i = 0; i < count; i++)
            items[i] = read(offset + i * stride);
        return items;
    }

    private static M2Track<Vector3> Vec3Track(byte[] data, int o) => M2Skeleton.ReadTrack(data, o, 12, (d, p) => d.Vec3(p));
    private static M2Track<float> FloatTrack(byte[] data, int o) => M2Skeleton.ReadTrack(data, o, 4, (d, p) => d.F32(p));
    private static M2Track<short> ShortTrack(byte[] data, int o) => M2Skeleton.ReadTrack(data, o, 2, (d, p) => (short)d.U16(p));
    private static M2Track<byte> ByteTrack(byte[] data, int o) => M2Skeleton.ReadTrack(data, o, 1, (d, p) => d[p]);

    private static M2Track<Quaternion> QuatTrack(byte[] data, int o) =>
        M2Skeleton.ReadTrack(data, o, 16, (d, p) => new Quaternion(d.F32(p), d.F32(p + 4), d.F32(p + 8), d.F32(p + 12)));
}
