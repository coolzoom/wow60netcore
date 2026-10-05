using System.Numerics;
using Engine.Rendering;
using Formats.Models;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;
using Texture = Engine.Rendering.Texture;

namespace Client.World;

/// <summary>A camera in render axes, plus its right and up vectors for camera-facing quads.</summary>
public readonly record struct SceneView(Matrix4x4 View, Matrix4x4 Projection, Vector3 Right, Vector3 Up);

/// <summary>A light in render axes: directional ones shine from <see cref="Position"/> toward the origin.</summary>
public readonly record struct SceneLight(bool Point, Vector3 Position, Vector3 Color, float RangeStart, float RangeEnd);

/// <summary>How one batch looks this frame: its texture, color (with transparency in alpha) and UV transform.</summary>
public readonly record struct BatchLook(Texture? Texture, Vector4 Color, Matrix4x4 UvTransform);

/// <summary>
/// Draws animated M2 models with their material state: blend mode, unlit / unfogged / depth flags, batch colors and
/// UV animation. Callers draw every model's opaque pass, then every model's transparent pass.
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    public const int MaxLights = 4;

    private readonly GL _gl;
    private readonly AssetCache _assets;
    private Vector3 _fogColor;
    private Vector2 _fog;

    public SceneRenderer(GL gl, AssetCache assets)
    {
        _gl = gl;
        _assets = assets;
        Shader = new Shader(gl, Shaders.SceneVertex, Shaders.SceneFragment);
    }

    public Shader Shader { get; }

    /// <param name="fog">Linear fog start and end distance; null for none.</param>
    public void Begin(SceneView view, Vector3 ambient, IReadOnlyList<SceneLight> lights, Vector3 fogColor, (float Near, float Far)? fog)
    {
        _fogColor = fogColor;
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        Shader.Use();
        Shader.Set("uTexture", 0);
        Shader.Set("uView", view.View);
        Shader.Set("uProjection", view.Projection);
        Shader.Set("uAmbient", ambient);
        var count = Math.Min(lights.Count, MaxLights);
        Shader.Set("uLightCount", count);
        for (var i = 0; i < count; i++)
        {
            var light = lights[i];
            Shader.Set($"uLightPosition[{i}]", new Vector4(light.Position, light.Point ? 1 : 0));
            Shader.Set($"uLightColor[{i}]", light.Color);
            Shader.Set($"uLightRange[{i}]", new Vector2(light.RangeStart, light.RangeEnd));
        }
        _fog = fog is { } f ? new Vector2(f.Near, f.Far) : Vector2.Zero;
        Shader.Set("uFog", _fog);
    }

    /// <summary>Restores the state the 2D interface expects.</summary>
    public void End()
    {
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>
    /// Draws the opaque or the transparent batches of a model. <paramref name="look"/> supplies each batch's
    /// texture, color and UV transform; <paramref name="show"/> hides submeshes (character geosets).
    /// </summary>
    public void Draw(GpuModel model, Mesh mesh, Matrix4x4 transform, bool transparent, Func<ModelBatchData, BatchLook> look,
        Func<int, bool>? show = null)
    {
        Shader.Set("uModel", transform);
        var batches = model.Data.Batches;
        IEnumerable<ModelBatchData> order = transparent
            ? batches.Where(b => b.IsTransparent).OrderBy(b => b.PriorityPlane)
            : batches.Where(b => !b.IsTransparent);
        foreach (var batch in order)
        {
            if (show?.Invoke(batch.Geoset) == false)
                continue;
            var state = look(batch);
            if (state.Color.W <= 0.002f)
                continue;
            BlendStates.Apply(_gl, batch.Blend);
            _gl.DepthMask(!transparent && !batch.Flags.HasFlag(RenderFlags.NoDepthWrite));
            if (batch.Flags.HasFlag(RenderFlags.NoDepthTest))
                _gl.Disable(EnableCap.DepthTest);
            else
                _gl.Enable(EnableCap.DepthTest);
            Shader.Set("uAlphaTest", batch.Blend == BlendMode.AlphaKey ? 0.5f : transparent ? 0.004f : -1f);
            Shader.Set("uUnlit", batch.Flags.HasFlag(RenderFlags.Unlit) ? 1f : 0f);
            Shader.Set("uFog", batch.Flags.HasFlag(RenderFlags.Unfogged) ? Vector2.Zero : _fog);
            Shader.Set("uFogColor", BlendStates.FogColor(batch.Blend, _fogColor));
            Shader.Set("uColor", state.Color);
            Shader.Set("uTexMatrix", state.UvTransform);
            (state.Texture ?? _assets.Fallback).Bind(0);
            mesh.DrawRange(batch.IndexStart, batch.IndexCount);
        }
        _gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>Camera-facing particle quads (already in render axes), unlit, depth-tested without depth writes.</summary>
    public void DrawParticles(Mesh mesh, int indexCount, Texture? texture, BlendMode blend)
    {
        if (indexCount == 0)
            return;
        Shader.Set("uModel", Matrix4x4.Identity);
        Shader.Set("uTexMatrix", Matrix4x4.Identity);
        Shader.Set("uColor", Vector4.One);
        Shader.Set("uUnlit", 1f);
        Shader.Set("uAlphaTest", blend == BlendMode.AlphaKey ? 0.5f : 0.004f);
        Shader.Set("uFog", _fog);
        Shader.Set("uFogColor", BlendStates.FogColor(blend, _fogColor));
        BlendStates.Apply(_gl, blend == BlendMode.Opaque ? BlendMode.Alpha : blend);
        _gl.DepthMask(false);
        (texture ?? _assets.Fallback).Bind(0);
        mesh.DrawRange(0, indexCount);
    }

    public void Dispose() => Shader.Dispose();
}
