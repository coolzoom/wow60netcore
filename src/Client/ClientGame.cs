using System.Numerics;
using Engine;
using Engine.Rendering;
using Engine.World;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Client;

public sealed class ClientGame(int seed) : Game
{
    private static readonly Vector3 SkyColor = new(0.55f, 0.72f, 0.90f);
    private static readonly Vector3 LightDirection = new(-0.4f, -1f, -0.3f);
    private const float MouseSensitivity = 0.006f;
    private const float KeyTurnSpeed = 2.2f;

    private readonly List<Mesh> _meshes = new();
    private readonly List<(Mesh Mesh, Matrix4x4 Transform)> _props = new();
    private readonly OrbitCamera _camera = new();

    private Terrain _terrain = null!;
    private CharacterController _player = null!;
    private Shader _shader = null!;
    private Mesh _terrainMesh = null!;
    private Mesh _waterMesh = null!;
    private Mesh _playerBody = null!;
    private Mesh _playerHead = null!;
    private Mesh _playerNose = null!;

    protected override void OnLoad()
    {
        _terrain = Terrain.Generate(seed);
        _player = new CharacterController(_terrain, FindSpawn());
        _camera.MinHeightAt = _terrain.GetHeight;

        _shader = new Shader(Gl, Shaders.LitVertex, Shaders.LitFragment);
        _shader.Use();
        _terrainMesh = Upload(_terrain.BuildMesh());
        _waterMesh = Upload(MeshData.Plane(_terrain.HalfExtent * 2, new Vector3(0.15f, 0.35f, 0.60f)));
        _playerBody = Upload(MeshData.Box(new(0.8f, 1.2f, 0.5f), new(0.70f, 0.20f, 0.18f)));
        _playerHead = Upload(MeshData.Box(new(0.5f, 0.5f, 0.5f), new(0.93f, 0.78f, 0.62f)));
        _playerNose = Upload(MeshData.Box(new(0.15f, 0.15f, 0.2f), new(0.93f, 0.78f, 0.62f)));
        PlaceTrees();

        Gl.Enable(EnableCap.DepthTest);
        Gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    protected override void OnUpdate(float dt)
    {
        if (Input.IsKeyDown(Key.Escape))
        {
            Exit();
            return;
        }

        if (Input.IsMouseDown(MouseButton.Right) || Input.IsMouseDown(MouseButton.Left))
        {
            _camera.Yaw -= Input.MouseDelta.X * MouseSensitivity;
            _camera.Pitch += Input.MouseDelta.Y * MouseSensitivity;
        }
        if (Input.IsKeyDown(Key.Left) || Input.IsKeyDown(Key.Q)) _camera.Yaw += KeyTurnSpeed * dt;
        if (Input.IsKeyDown(Key.Right) || Input.IsKeyDown(Key.E)) _camera.Yaw -= KeyTurnSpeed * dt;
        _camera.Distance -= Input.ScrollDelta;

        var move = new Vector2(
            (Input.IsKeyDown(Key.D) ? 1 : 0) - (Input.IsKeyDown(Key.A) ? 1 : 0),
            (Input.IsKeyDown(Key.W) || Input.IsKeyDown(Key.Up) ? 1 : 0) - (Input.IsKeyDown(Key.S) || Input.IsKeyDown(Key.Down) ? 1 : 0));
        _player.Update(dt, new CharacterInput(move, Input.IsKeyDown(Key.Space)), _camera.Forward, _camera.Right);

        _camera.Target = _player.Position + new Vector3(0, 1.6f, 0);
    }

    protected override void OnRender(float dt)
    {
        Gl.ClearColor(SkyColor.X, SkyColor.Y, SkyColor.Z, 1f);
        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var size = FramebufferSize;
        _shader.Use();
        _shader.Set("uView", _camera.ViewMatrix);
        _shader.Set("uProjection", _camera.ProjectionMatrix(size.X / (float)Math.Max(size.Y, 1)));
        _shader.Set("uLightDirection", LightDirection);
        _shader.Set("uFogColor", SkyColor);
        _shader.Set("uFogDensity", 0.008f);
        _shader.Set("uAlpha", 1f);

        Draw(_terrainMesh, Matrix4x4.Identity);
        foreach (var (mesh, transform) in _props)
            Draw(mesh, transform);
        DrawPlayer();

        Gl.Enable(EnableCap.Blend);
        Gl.DepthMask(false);
        _shader.Set("uAlpha", 0.7f);
        Draw(_waterMesh, Matrix4x4.CreateTranslation(0, _terrain.WaterLevel, 0));
        Gl.DepthMask(true);
        Gl.Disable(EnableCap.Blend);
    }

    protected override void OnUnload()
    {
        foreach (var mesh in _meshes)
            mesh.Dispose();
        _shader.Dispose();
    }

    private void DrawPlayer()
    {
        var root = Matrix4x4.CreateRotationY(_player.Facing) * Matrix4x4.CreateTranslation(_player.Position);
        Draw(_playerBody, Matrix4x4.CreateTranslation(0, 0.6f, 0) * root);
        Draw(_playerHead, Matrix4x4.CreateTranslation(0, 1.45f, 0) * root);
        Draw(_playerNose, Matrix4x4.CreateTranslation(0, 1.45f, -0.3f) * root);
    }

    private void Draw(Mesh mesh, Matrix4x4 model)
    {
        _shader.Set("uModel", model);
        mesh.Draw();
    }

    private Mesh Upload(MeshData data)
    {
        var mesh = new Mesh(Gl, data);
        _meshes.Add(mesh);
        return mesh;
    }

    private Vector3 FindSpawn()
    {
        for (var radius = 0f; radius < _terrain.HalfExtent; radius += 2f)
        for (var angle = 0f; angle < MathF.Tau; angle += 0.5f)
        {
            var x = MathF.Cos(angle) * radius;
            var z = MathF.Sin(angle) * radius;
            if (_terrain.GetHeight(x, z) > _terrain.WaterLevel + 1f)
                return new Vector3(x, 0, z);
        }
        return Vector3.Zero;
    }

    private void PlaceTrees()
    {
        var trunk = Upload(MeshData.Box(new(0.4f, 2f, 0.4f), new(0.40f, 0.26f, 0.13f)));
        var crown = Upload(MeshData.Box(new(2f, 2.2f, 2f), new(0.16f, 0.42f, 0.16f)));
        var random = new Random(seed);
        var limit = _terrain.HalfExtent - 4f;

        for (var placed = 0; placed < 140;)
        {
            var x = (random.NextSingle() * 2 - 1) * limit;
            var z = (random.NextSingle() * 2 - 1) * limit;
            var ground = _terrain.GetHeight(x, z);
            var nearSpawn = Vector2.Distance(new(x, z), new(_player.Position.X, _player.Position.Z)) < 10f;
            if (nearSpawn || ground < _terrain.WaterLevel + 1f || ground > 10f)
                continue;

            var scale = 0.8f + random.NextSingle() * 0.6f;
            var basis = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(random.NextSingle() * MathF.Tau)
                * Matrix4x4.CreateTranslation(x, ground, z);
            _props.Add((trunk, Matrix4x4.CreateTranslation(0, 1f, 0) * basis));
            _props.Add((crown, Matrix4x4.CreateTranslation(0, 2.9f, 0) * basis));
            placed++;
        }
    }
}
