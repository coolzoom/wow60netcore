using System.Numerics;
using Client.Online;
using Client.World;
using Formats.Models;
using FrameXml;
using Silk.NET.OpenGL;
using Model = FrameXml.Objects.Model;

namespace Client.Ui;

/// <summary>
/// The 3D scene behind the glue screens: the visible ModelFFX frame's M2 (login, character select and create
/// backgrounds) seen through its own camera, lit by its own lights and fogged with the frame's fog, with the
/// selected or customized character standing on the scene's attachment point 0.
/// </summary>
public sealed class GlueScene(GL gl, AssetCache assets, UiScreen ui, GlueApi api, GlueNetwork? network, ClientData? data) : IDisposable
{
    private readonly SceneRenderer _renderer = new(gl, assets);
    private readonly CharacterModels? _characters = data is null ? null : new CharacterModels(data, assets);
    private readonly Dictionary<string, SkinnedActor?> _actors = new(StringComparer.OrdinalIgnoreCase);
    private SceneModel? _scene;
    private uint _characterTime;
    public void Render(Vector2 size, float dt)
    {
        if (Frame() is not { } frame)
            return;
        var path = M2Model.NormalizePath(frame.ModelFile!);
        if (_scene?.Path.Equals(path, StringComparison.OrdinalIgnoreCase) != true)
        {
            _scene?.Dispose();
            _scene = new SceneModel(gl, assets, path);
        }
        if (!_scene.Load() || _scene.Camera(frame.Camera, size.X / Math.Max(size.Y, 1)) is not { } camera)
            return;
        var (view, billboard) = camera;

        _scene.Update(dt, billboard, view);
        _characterTime += (uint)Math.Max(1, dt * 1000);
        var (ambient, lights) = _scene.Lighting(_scene.Attachment(0)?.Translation ?? Vector3.Zero);
        var fog = frame.FogColor is { } color ? new Vector3(color.R, color.G, color.B) : Vector3.Zero;
        // Wide windows can see past the edge of a sky dome; there the scene fades to its fog rather than black.
        gl.ClearColor(fog.X, fog.Y, fog.Z, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        _renderer.Begin(view, ambient, lights, fog, frame.FogColor is null ? null : (frame.FogNear, frame.FogFar));
        _scene.Draw(_renderer, false);
        var character = Character(frame);
        character?.Invoke(false);
        _scene.Draw(_renderer, true);
        character?.Invoke(true);
        _scene.DrawParticles(_renderer);
        _renderer.End();
    }

    /// <summary>The frontmost shown model frame that has a scene file.</summary>
    private Model? Frame()
    {
        Model? found = null;
        foreach (var frame in ui.DrawOrder())
            if (frame is Model { ModelFile.Length: > 0 } model)
                found = model;
        return found;
    }

    /// <summary>Draws one pass of the character standing in the scene, or null when this screen shows none.</summary>
    private Action<bool>? Character(Model frame)
    {
        if (network is null || data is null || _characters is null || _scene!.Attachment(0) is not { } spot)
            return null;
        var (look, facing) = frame == api.CharSelectFrame ? (network.SelectedLook, api.CharacterSelectFacing)
            : frame == api.CharCustomizeFrame ? (network.CreateLook, api.CharacterCreateFacing)
            : (null, 0f);
        if (look is null || data.CharacterModel(look.Race, look.Sex) is not { } file)
            return null;
        var path = M2Model.NormalizePath(file);
        if (assets.Model(path) is not { } model)
            return null;

        var actor = Actor(path);
        if (actor is not null)
        {
            var stand = actor.Resolve(SkinnedActor.Stand);
            actor.Update(stand, _characterTime % Math.Max(1, actor.Length(stand)), _characterTime);
        }
        var transform = Matrix4x4.CreateRotationY(facing * MathF.PI / 180) * spot;
        var body = _characters.Body(look);
        var hair = _characters.HairTexture(look) is { } h ? assets.Texture(h) : null;
        var cape = _characters.CapeTexture(look) is { } c ? assets.Texture(c) : null;
        var geosets = _characters.Geosets(look);
        var items = _characters.Attachments(look, false).ToList();

        return transparent =>
        {
            _renderer.Draw(model, actor?.Mesh ?? model.Mesh, transform, transparent, batch => new BatchLook(
                batch.Texture is { } name ? assets.Texture(name) : batch.TextureType switch { 1 => body, 2 => cape, 6 => hair, _ => null },
                Vector4.One, Matrix4x4.Identity), geosets);
            foreach (var item in items)
            {
                if (assets.Model(item.Model) is not { } gear ||
                    CharacterModels.AttachmentTransform(model.Data, item.Attachment, actor, transform) is not { } at)
                    continue;
                var texture = item.Texture is { } t ? assets.Texture(t) : null;
                _renderer.Draw(gear, gear.Mesh, at, transparent, batch =>
                    new BatchLook(batch.Texture is { } name ? assets.Texture(name) : texture, Vector4.One, Matrix4x4.Identity));
            }
        };
    }

    /// <summary>A posable instance of a character model, once its data and skeleton have loaded.</summary>
    private SkinnedActor? Actor(string path)
    {
        if (_actors.TryGetValue(path, out var actor))
            return actor;
        if (!assets.TryGetSkeleton(path, out var skeleton) || !assets.TryGetModelData(path, out var model) || model is null)
            return null;
        return _actors[path] = skeleton is null ? null : new SkinnedActor(gl, model, skeleton);
    }

    public void Dispose()
    {
        _scene?.Dispose();
        foreach (var actor in _actors.Values)
            actor?.Dispose();
        _renderer.Dispose();
    }
}
