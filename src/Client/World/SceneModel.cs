using System.Numerics;
using Client.Online;
using Formats.Models;
using Formats.Terrain;
using Silk.NET.OpenGL;

namespace Client.World;

/// <summary>
/// A whole scene in one M2, as the glue screens use them: animated geometry (bones with billboards, batch colors,
/// texture weights, UV animation), its own camera and lights, and particle emitters. Drawn at the origin.
/// </summary>
public sealed class SceneModel : IDisposable
{
    /// <summary>Seconds of particles simulated on load, so effects are already running when the scene appears.</summary>
    private const float WarmUp = 2.5f;

    private readonly GL _gl;
    private readonly AssetCache _assets;
    private readonly List<ParticleEmitter> _emitters = [];
    private GpuModel? _model;
    private M2Effects? _effects;
    private SkinnedActor? _actor;
    private bool _skeletonChecked;
    private uint _time;

    public SceneModel(GL gl, AssetCache assets, string path)
    {
        _gl = gl;
        _assets = assets;
        Path = M2Model.NormalizePath(path);
    }

    public string Path { get; }
    public bool IsReady => _model is not null && _effects is not null && _skeletonChecked;
    public GpuModel? Model => _model;
    public SkinnedActor? Actor => _actor;

    /// <summary>Loads what is still missing; true once everything is there.</summary>
    public bool Load()
    {
        if (IsReady)
            return true;
        _model ??= _assets.Model(Path);
        _effects ??= _assets.Effects(Path);
        if (!_skeletonChecked && _assets.TryGetSkeleton(Path, out var skeleton))
        {
            if (skeleton is null)
                _skeletonChecked = true;
            else if (_assets.TryGetModelData(Path, out var data) && data is not null)
            {
                _actor = new SkinnedActor(_gl, data, skeleton);
                _skeletonChecked = true;
            }
        }
        if (!IsReady)
            return false;
        for (var i = 0; i < _effects!.Particles.Count; i++)
            _emitters.Add(new ParticleEmitter(_gl, _effects.Particles[i], i * 7919 + 1));
        return true;
    }

    /// <summary>
    /// The scene camera at the current time: view and projection in render axes (vertical field of view from the
    /// diagonal one of a 4:3 screen, so wider windows show more at the sides) and the model-space camera basis.
    /// </summary>
    public (SceneView View, Matrix4x4 Billboard)? Camera(int index, float aspect)
    {
        if (_effects is not { Cameras.Count: > 0 } effects)
            return null;
        var camera = effects.Cameras[Math.Clamp(index, 0, effects.Cameras.Count - 1)];
        var eye = camera.PositionBase + Sample(camera.Position, Vector3.Zero, Vector3.Lerp);
        var target = camera.TargetBase + Sample(camera.Target, Vector3.Zero, Vector3.Lerp);
        var roll = Sample(camera.Roll, 0f, float.Lerp);

        var forward = Vector3.Normalize(target - eye);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Cross(right, forward);
        if (roll != 0)
        {
            var spin = Matrix4x4.CreateFromAxisAngle(forward, roll);
            right = Vector3.TransformNormal(right, spin);
            up = Vector3.TransformNormal(up, spin);
        }
        var billboard = new Matrix4x4(
            -forward.X, -forward.Y, -forward.Z, 0,
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            0, 0, 0, 1);

        var verticalFov = 2 * MathF.Atan(MathF.Tan(camera.FieldOfView / 2) / MathF.Sqrt(1 + 16f / 9f));
        var near = Math.Max(camera.NearClip, 0.05f);
        var far = Math.Max(camera.FarClip, near + 1);
        var view = Matrix4x4.CreateLookAt(WorldSpace.ModelToRender(eye), WorldSpace.ModelToRender(target), WorldSpace.ModelToRender(up));
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(verticalFov, Math.Max(aspect, 0.1f), near, far);
        return (new SceneView(view, projection, WorldSpace.ModelToRender(right), WorldSpace.ModelToRender(up)), billboard);
    }

    /// <summary>Advances the animation and particles by <paramref name="dt"/> seconds and poses the bones.</summary>
    public void Update(float dt, Matrix4x4 billboard, SceneView view)
    {
        if (!IsReady)
            return;
        var warm = _time == 0;
        _time += (uint)Math.Max(1, dt * 1000);
        _actor?.Update(0, _time, _time, billboard);
        var steps = warm ? (int)(WarmUp * 30) : 1;
        var step = warm ? 1f / 30 : dt;
        for (var s = 0; s < steps; s++)
            foreach (var emitter in _emitters)
            {
                var def = emitter.Definition;
                var bone = _actor is { } actor && def.Bone < actor.Bones.Count ? actor.Bones[def.Bone] : Matrix4x4.Identity;
                emitter.Update(step, bone, (track, fallback) => Sample(track, fallback, float.Lerp),
                    Sample(def.Enabled, (byte)1, (a, _, _) => a) != 0);
            }
        foreach (var emitter in _emitters)
            emitter.Build(view.Right, view.Up);
    }

    /// <summary>Ambient light and the scene's lights at the current time, in render axes.</summary>
    public (Vector3 Ambient, List<SceneLight> Lights) Lighting()
    {
        var ambient = Vector3.Zero;
        var lights = new List<SceneLight>();
        if (_effects is null)
            return (new Vector3(0.6f), lights);
        foreach (var light in _effects.Lights)
        {
            if (Sample(light.Visibility, (byte)1, (a, _, _) => a) == 0)
                continue;
            ambient += Sample(light.AmbientColor, Vector3.Zero, Vector3.Lerp) * Sample(light.AmbientIntensity, 0f, float.Lerp);
            var diffuse = Sample(light.DiffuseColor, Vector3.Zero, Vector3.Lerp) * Sample(light.DiffuseIntensity, 0f, float.Lerp);
            if (diffuse.LengthSquared() <= 0)
                continue;
            var position = WorldSpace.ModelToRender(light.Position);
            if (_actor is { } actor && light.Bone >= 0 && light.Bone < actor.Bones.Count)
                position = Vector3.Transform(position, actor.Bones[light.Bone]);
            lights.Add(new SceneLight(light.Type == M2Light.Point, position, diffuse,
                Sample(light.AttenuationStart, 0f, float.Lerp), Sample(light.AttenuationEnd, 0f, float.Lerp)));
        }
        if (_effects.Lights.Count == 0)
            ambient = new Vector3(0.6f);
        return (ambient, lights.OrderByDescending(l => l.Color.Length()).Take(SceneRenderer.MaxLights).ToList());
    }

    /// <summary>Where attachment point <paramref name="id"/> of the scene is now (render axes); characters stand on point 0.</summary>
    public Matrix4x4? Attachment(int id) =>
        _model is { } model ? CharacterModels.AttachmentTransform(model.Data, id, _actor, Matrix4x4.Identity) : null;

    public void Draw(SceneRenderer renderer, bool transparent)
    {
        if (_model is not { } model)
            return;
        renderer.Draw(model, _actor?.Mesh ?? model.Mesh, Matrix4x4.Identity, transparent, Look);
    }

    public void DrawParticles(SceneRenderer renderer)
    {
        if (_effects is null)
            return;
        foreach (var emitter in _emitters)
        {
            var texture = _effects.Textures.ElementAtOrDefault(emitter.Definition.Texture) is { } name ? _assets.Texture(name) : null;
            renderer.DrawParticles(emitter.Mesh, emitter.IndexCount, texture, emitter.Definition.Blend);
        }
    }

    private BatchLook Look(ModelBatchData batch)
    {
        var color = Vector4.One;
        var uv = Matrix4x4.Identity;
        if (_effects is { } effects)
        {
            if (batch.Color >= 0 && batch.Color < effects.Colors.Count)
            {
                var c = effects.Colors[batch.Color];
                color = new Vector4(Sample(c.Rgb, Vector3.One, Vector3.Lerp), Sample(c.Alpha, (short)0x7FFF, LerpFixed) / 32767f);
            }
            if (batch.Transparency >= 0 && batch.Transparency < effects.Transparencies.Count)
                color.W *= Sample(effects.Transparencies[batch.Transparency], (short)0x7FFF, LerpFixed) / 32767f;
            if (batch.UvAnimation >= 0 && batch.UvAnimation < effects.TextureTransforms.Count)
                uv = UvMatrix(effects.TextureTransforms[batch.UvAnimation]);
        }
        var texture = batch.Texture is { } name ? _assets.Texture(name) : null;
        return new BatchLook(texture, color, uv);
    }

    private Matrix4x4 UvMatrix(M2TextureTransform transform)
    {
        var translation = Sample(transform.Translation, Vector3.Zero, Vector3.Lerp);
        var rotation = Sample(transform.Rotation, Quaternion.Identity, (a, b, t) => Quaternion.Normalize(Quaternion.Slerp(a, b, t)));
        var scale = Sample(transform.Scale, Vector3.One, Vector3.Lerp);
        var center = new Vector3(0.5f, 0.5f, 0);
        return Matrix4x4.CreateTranslation(-center) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) *
               Matrix4x4.CreateTranslation(center + translation);
    }

    private static short LerpFixed(short a, short b, float t) => (short)(a + (b - a) * t);

    /// <summary>A track at the current time of the scene's first animation (the only one glue scenes have).</summary>
    private T Sample<T>(M2Track<T> track, T fallback, Func<T, T, float, T> lerp) where T : struct
    {
        if (!track.IsAnimated)
            return fallback;
        var sequenceTime = _actor?.Skeleton is { } skeleton ? skeleton.SequenceTime(0, _time) : _time;
        return track.Sample(0, sequenceTime, _time, _effects?.GlobalSequences ?? [], fallback, lerp);
    }

    public void Dispose()
    {
        _actor?.Dispose();
        foreach (var emitter in _emitters)
            emitter.Dispose();
        _emitters.Clear();
    }
}
