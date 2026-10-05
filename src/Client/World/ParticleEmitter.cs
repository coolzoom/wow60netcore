using System.Numerics;
using Engine.Rendering;
using Formats.Models;
using Formats.Terrain;
using Silk.NET.OpenGL;

namespace Client.World;

/// <summary>
/// Simulates one M2 particle emitter in render axes and builds its camera-facing quads. Particles are emitted from
/// the emitter's bone and then move freely (gravity pulls along render -Y).
/// </summary>
public sealed class ParticleEmitter : IDisposable
{
    private const int MaxParticles = 1500;

    private struct Particle
    {
        public Vector3 Position, Velocity, Direction;
        public float Age, Life;
        public int Tile;
    }

    private readonly M2ParticleEmitter _def;
    private readonly List<Particle> _particles = [];
    private readonly float[] _vertices = new float[MaxParticles * 4 * Mesh.FloatsPerVertex];
    private readonly Random _random;
    private float _pending;

    public ParticleEmitter(GL gl, M2ParticleEmitter def, int seed)
    {
        _def = def;
        _random = new Random(seed);
        var indices = new uint[MaxParticles * 6];
        for (uint i = 0, v = 0; i < indices.Length; i += 6, v += 4)
            (indices[i], indices[i + 1], indices[i + 2], indices[i + 3], indices[i + 4], indices[i + 5]) = (v, v + 1, v + 2, v, v + 2, v + 3);
        Mesh = new Mesh(gl, _vertices, indices, dynamic: true);
    }

    public M2ParticleEmitter Definition => _def;
    public Mesh Mesh { get; }
    /// <summary>Indices to draw after the last <see cref="Build"/>.</summary>
    public int IndexCount { get; private set; }

    /// <param name="bone">The emitter bone's current transform (render axes).</param>
    /// <param name="sample">Samples one of the emitter's float tracks at the current animation time.</param>
    public void Update(float dt, Matrix4x4 bone, Func<M2Track<float>, float, float> sample, bool enabled)
    {
        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _particles.RemoveAt(i);
                continue;
            }
            p.Velocity += new Vector3(0, -sample(_def.Gravity, 0) * dt, 0) - p.Direction * (_def.Drag * dt);
            p.Position += p.Velocity * dt;
            _particles[i] = p;
        }

        if (!enabled)
            return;
        _pending += Math.Max(0, sample(_def.EmissionRate, 0)) * dt;
        var life = sample(_def.Lifespan, 1);
        while (_pending >= 1 && _particles.Count < MaxParticles)
        {
            _pending -= 1;
            if (life > 0)
                _particles.Add(Spawn(bone, life, sample));
        }
        _pending = Math.Min(_pending, 1);
    }

    private Particle Spawn(Matrix4x4 bone, float life, Func<M2Track<float>, float, float> sample)
    {
        var length = sample(_def.AreaLength, 0);
        var width = sample(_def.AreaWidth, 0);
        var direction = Spread(sample(_def.VerticalRange, 0), sample(_def.HorizontalRange, 0));
        Vector3 local;
        if (_def.EmitterType == M2ParticleEmitter.Sphere)
            local = _def.Position + direction * (length + (width - length) * _random.NextSingle());
        else
            local = _def.Position + new Vector3((_random.NextSingle() - 0.5f) * length, (_random.NextSingle() - 0.5f) * width, 0);

        var speed = sample(_def.Speed, 0) * (1 + (_random.NextSingle() * 2 - 1) * sample(_def.SpeedVariation, 0));
        var worldDirection = Vector3.TransformNormal(WorldSpace.ModelToRender(direction), bone);
        worldDirection = worldDirection.LengthSquared() > 0 ? Vector3.Normalize(worldDirection) : Vector3.UnitY;
        var tiles = _def.Rows * _def.Columns;
        return new Particle
        {
            Position = Vector3.Transform(WorldSpace.ModelToRender(local), bone),
            Velocity = worldDirection * speed,
            Direction = worldDirection,
            Life = life,
            Tile = tiles > 1 && !HasTileAnimation ? _random.Next(tiles) : 0,
        };
    }

    /// <summary>A direction around model +Z: up to <paramref name="vertical"/> radians off it, turned up to ±<paramref name="horizontal"/> around it.</summary>
    private Vector3 Spread(float vertical, float horizontal)
    {
        var polar = _random.NextSingle() * vertical;
        var azimuth = (_random.NextSingle() * 2 - 1) * horizontal;
        var sin = MathF.Sin(polar);
        return new Vector3(sin * MathF.Cos(azimuth), sin * MathF.Sin(azimuth), MathF.Cos(polar));
    }

    private bool HasTileAnimation => _def.LifespanTiles[0] != _def.LifespanTiles[1] || _def.DecayTiles[0] != _def.DecayTiles[1];

    /// <summary>Writes every particle as a quad facing the camera, rotated by the emitter's spin.</summary>
    public void Build(Vector3 right, Vector3 up)
    {
        var count = 0;
        var tiles = _def.Rows * _def.Columns;
        foreach (var p in _particles)
        {
            var t = Math.Clamp(p.Age / p.Life, 0, 1);
            var color = Curve(_def.Colors[0], _def.Colors[1], _def.Colors[2], t);
            var size = Curve(_def.Sizes[0], _def.Sizes[1], _def.Sizes[2], t);
            if (size <= 0 || color.W <= 0.002f)
                continue;
            var tile = Math.Clamp(Tile(p, t), 0, tiles - 1);
            var (column, row) = (tile % _def.Columns, tile / _def.Columns);
            var u0 = column / (float)_def.Columns;
            var v0 = row / (float)_def.Rows;
            var u1 = u0 + 1f / _def.Columns;
            var v1 = v0 + 1f / _def.Rows;

            var angle = _def.Spin * p.Age;
            var (sin, cos) = MathF.SinCos(angle);
            var r = (right * cos + up * sin) * size;
            var u = (up * cos - right * sin) * size;
            var o = count * 4 * Mesh.FloatsPerVertex;
            Write(o, p.Position - r + u, u0, v0, color);
            Write(o + Mesh.FloatsPerVertex, p.Position - r - u, u0, v1, color);
            Write(o + 2 * Mesh.FloatsPerVertex, p.Position + r - u, u1, v1, color);
            Write(o + 3 * Mesh.FloatsPerVertex, p.Position + r + u, u1, v0, color);
            count++;
        }
        IndexCount = count * 6;
        if (count > 0)
            Mesh.UpdateVertices(_vertices.AsSpan(0, count * 4 * Mesh.FloatsPerVertex));
    }

    /// <summary>The flipbook frame: lifespan tiles until the midpoint, then decay tiles.</summary>
    private int Tile(Particle p, float t)
    {
        if (!HasTileAnimation)
            return p.Tile;
        var mid = Math.Clamp(_def.Midpoint, 0.001f, 0.999f);
        var (range, f) = t < mid ? (_def.LifespanTiles, t / mid) : (_def.DecayTiles, (t - mid) / (1 - mid));
        var repeat = Math.Max(1, range[2]);
        var frames = range[1] - range[0];
        return frames <= 0 ? range[0] : range[0] + (int)(f * repeat * frames) % frames;
    }

    private float Curve(float a, float b, float c, float t)
    {
        var mid = Math.Clamp(_def.Midpoint, 0.001f, 0.999f);
        return t < mid ? a + (b - a) * (t / mid) : b + (c - b) * ((t - mid) / (1 - mid));
    }

    private Vector4 Curve(Vector4 a, Vector4 b, Vector4 c, float t)
    {
        var mid = Math.Clamp(_def.Midpoint, 0.001f, 0.999f);
        return t < mid ? Vector4.Lerp(a, b, t / mid) : Vector4.Lerp(b, c, (t - mid) / (1 - mid));
    }

    private void Write(int o, Vector3 position, float u, float v, Vector4 color)
    {
        _vertices[o] = position.X; _vertices[o + 1] = position.Y; _vertices[o + 2] = position.Z;
        _vertices[o + 3] = 0; _vertices[o + 4] = 1; _vertices[o + 5] = 0;
        _vertices[o + 6] = u; _vertices[o + 7] = v;
        _vertices[o + 8] = color.X; _vertices[o + 9] = color.Y; _vertices[o + 10] = color.Z; _vertices[o + 11] = color.W;
    }

    public void Dispose() => Mesh.Dispose();
}
