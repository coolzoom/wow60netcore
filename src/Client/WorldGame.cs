using System.Numerics;
using Client.Ui;
using Client.World;
using Engine;
using Engine.Rendering;
using Engine.World;
using Formats.Dbc;
using Formats.Mpq;
using Formats.Terrain;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Client;

/// <summary>Explores maps from the client's MPQ data with a third-person character.</summary>
public sealed class WorldGame(string dataDirectory, string mapDirectory, Vector3? spawnWorld, string? browseFilter = null) : Game
{
    private static readonly Vector3 SkyColor = new(0.55f, 0.70f, 0.88f);
    private static readonly Vector3 SunDirection = Vector3.Normalize(new(-0.4f, -1f, -0.3f));
    private const float MouseSensitivity = 0.006f;
    private const float KeyTurnSpeed = 2.2f;
    private const float FogDensity = 0.0022f;
    private const float FoggedFarPlane = 2000f;
    /// <summary>Far enough to see across a whole continent (64 tiles ≈ 34000 yards) from any point.</summary>
    private const float ClearFarPlane = 50000f;

    private readonly OrbitCamera _camera = new() { Distance = 15f };
    private MpqFileSystem _files = null!;
    private AssetCache _assets = null!;
    private Shader _terrainShader = null!;
    private Shader _modelShader = null!;
    private Shader _litShader = null!;
    private Shader _unlitShader = null!;
    private Mesh _playerBody = null!;
    private Mesh _playerHead = null!;
    private ImGuiController _imgui = null!;
    private WorldUi _ui = null!;
    private WorldScene? _scene;
    private CharacterController _player = null!;
    private bool _snapToGround;

    public IReadOnlyList<MapEntry> Maps { get; private set; } = [];
    public WorldScene? Scene => _scene;
    public CharacterController Player => _player;
    public MpqFileSystem Files => _files;
    public AssetCache Assets => _assets;
    public bool Flying { get; set; }
    public bool FogEnabled { get; set; } = true;
    public float SpeedMultiplier { get; set; } = 1f;

    /// <summary>On-screen controls (phones): held buttons set these each frame, added to keyboard input.</summary>
    public Vector2 TouchMove { get; set; }
    public float TouchVertical { get; set; }
    public bool TouchJump { get; set; }
    public void Zoom(float yards) => _camera.Distance += yards;

    /// <summary>When on, the frame rate is capped at the display refresh rate.</summary>
    public bool VSync
    {
        get => Window.VSync;
        set => Window.VSync = value;
    }
    public RenderSettings Settings { get; set; } = new();

    protected override void OnLoad()
    {
        _files = new MpqFileSystem(dataDirectory);
        _assets = new AssetCache(Gl, _files);
        Maps = MapDbc.Read(_files.Read("DBFilesClient\\Map.dbc"))
            .Where(m => _files.Exists(WorldScene.WdtPath(m.Directory)))
            .ToList();

        _terrainShader = new Shader(Gl, Shaders.LitVertex, Shaders.TerrainFragment);
        _terrainShader.Use();
        for (var i = 0; i < 4; i++)
            _terrainShader.Set($"uLayer{i}", i);
        _terrainShader.Set("uAlphaMap", 4);
        _terrainShader.Set("uDetailScale", 1f / WorldSpace.UnitSize);

        _modelShader = new Shader(Gl, Shaders.LitVertex, Shaders.TexturedFragment);
        _modelShader.Use();
        _modelShader.Set("uTexture", 0);

        _litShader = new Shader(Gl, Shaders.LitVertex, Shaders.LitFragment);
        _unlitShader = new Shader(Gl, Shaders.LitVertex, Shaders.UnlitTexturedFragment);
        _unlitShader.Use();
        _unlitShader.Set("uTexture", 0);
        _playerBody = new Mesh(Gl, MeshData.Box(new(0.8f, 1.2f, 0.5f), new(0.70f, 0.20f, 0.18f)));
        _playerHead = new Mesh(Gl, MeshData.Box(new(0.5f, 0.5f, 0.5f), new(0.93f, 0.78f, 0.62f)));

        _imgui = new ImGuiController(Gl, Window, InputContext, UiFont.Create(_files), () => ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard,
            Options.UiScale, softKeyboard: Options.TouchControls);
        _ui = new WorldUi(this, browseFilter);

        var map = Maps.FirstOrDefault(m => m.Directory.Equals(mapDirectory, StringComparison.OrdinalIgnoreCase)) ?? Maps[0];
        LoadMap(map, spawnWorld is { } world ? WorldSpace.FromWorld(world) : null);

        Gl.Enable(EnableCap.DepthTest);
    }

    public void LoadMap(MapEntry map, Vector3? spawn = null)
    {
        _scene?.Dispose();
        _scene = new WorldScene(Gl, _files, _assets, map) { Settings = Settings };
        var position = spawn ?? _scene.DefaultSpawn();
        _player = new CharacterController(_scene, position);
        _snapToGround = true;
        _camera.MinHeightAt = (x, z) =>
            _scene.TryGetHeight(new Vector3(x, _player.Position.Y + 2f, z), out var h) ? h : float.MinValue;
    }

    /// <summary>
    /// Teleports to WoW world coordinates (as shown by in-game .gps). Once the area has loaded, the character
    /// drops onto the highest surface at or below <paramref name="worldZ"/>, or the topmost one if it is null.
    /// </summary>
    public void TeleportWorld(float worldX, float worldY, float? worldZ)
    {
        var render = WorldSpace.FromWorld(new Vector3(worldX, worldY, worldZ ?? WorldScene.HighestProbe));
        _player.Teleport(render);
        _snapToGround = true;
    }

    public void PlaceModelInFront(string model)
    {
        var forward = new Vector3(-MathF.Sin(_player.Facing), 0, -MathF.Cos(_player.Facing));
        var position = _player.Position + forward * 8f;
        if (_scene!.TryGetHeight(position + new Vector3(0, CharacterController.StepHeight, 0), out var ground))
            position.Y = ground;
        _scene.PlaceModel(model, position, _player.Facing);
    }

    protected override void OnUpdate(float dt)
    {
        var scene = _scene!;
        scene.Settings = Settings;
        scene.Update(_player.Position);

        // Flying to an explicit height: stay there instead of dropping to the ground.
        if (_snapToGround && Flying && _player.Position.Y < WorldScene.HighestProbe)
            _snapToGround = false;

        if (_snapToGround)
        {
            // Hold position until terrain and nearby WMOs are in, otherwise we would land under city floors.
            if (scene.IsSettledAround(_player.Position) && scene.TryGetHeight(_player.Position, out _))
            {
                _player.Teleport(_player.Position);
                _snapToGround = false;
            }
            _camera.Target = _player.Position + new Vector3(0, 1.6f, 0);
            return;
        }

        var io = ImGui.GetIO();
        if (!io.WantCaptureMouse)
        {
            if (Input.IsMouseDown(MouseButton.Right) || Input.IsMouseDown(MouseButton.Left))
            {
                _camera.Yaw -= Input.MouseDelta.X * MouseSensitivity;
                _camera.Pitch += Input.MouseDelta.Y * MouseSensitivity;
            }
            _camera.Distance -= Input.ScrollDelta * 2f;
        }

        var move = Vector2.Zero;
        var vertical = 0f;
        var jump = false;
        if (!io.WantCaptureKeyboard)
        {
            if (Input.IsKeyDown(Key.Q)) _camera.Yaw += KeyTurnSpeed * dt;
            if (Input.IsKeyDown(Key.E)) _camera.Yaw -= KeyTurnSpeed * dt;
            move = new Vector2(
                (Input.IsKeyDown(Key.D) ? 1 : 0) - (Input.IsKeyDown(Key.A) ? 1 : 0),
                (Input.IsKeyDown(Key.W) ? 1 : 0) - (Input.IsKeyDown(Key.S) ? 1 : 0));
            jump = Input.IsKeyDown(Key.Space);
            vertical = (Input.IsKeyDown(Key.Space) ? 1 : 0) - (Input.IsKeyDown(Key.ShiftLeft) || Input.IsKeyDown(Key.X) ? 1 : 0);
        }
        move = Vector2.Clamp(move + TouchMove, -Vector2.One, Vector2.One);
        vertical = Math.Clamp(vertical + TouchVertical, -1f, 1f);
        jump |= TouchJump;

        _player.Update(dt, new CharacterInput(move, jump, vertical, Flying, SpeedMultiplier), _camera.Forward, _camera.Right);
        _camera.Target = _player.Position + new Vector3(0, 1.6f, 0);
    }

    protected override void OnRender(float dt)
    {
        _assets.BeginFrame();
        Gl.ClearColor(SkyColor.X, SkyColor.Y, SkyColor.Z, 1f);
        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var size = FramebufferSize;
        var view = _camera.ViewMatrix;
        // Without fog the view reaches across the map; a larger near plane keeps depth precision at that range.
        var (near, far) = FogEnabled ? (0.5f, FoggedFarPlane) : (1f, ClearFarPlane);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(_camera.FieldOfView, size.X / (float)Math.Max(size.Y, 1), near, far);
        foreach (var shader in new[] { _terrainShader, _modelShader, _litShader, _unlitShader })
        {
            shader.Use();
            shader.Set("uView", view);
            shader.Set("uProjection", projection);
            shader.Set("uLightDirection", SunDirection);
            shader.Set("uFogColor", SkyColor);
            shader.Set("uFogDensity", FogEnabled ? FogDensity : 0f);
        }

        _scene!.Render(_terrainShader, _modelShader, _unlitShader, _camera.Position, new Frustum(view * projection));
        DrawPlayer();

        _imgui.Update(dt);
        _ui.Draw(dt);
        _imgui.Render();
    }

    private void DrawPlayer()
    {
        _litShader.Use();
        _litShader.Set("uAlpha", 1f);
        var root = Matrix4x4.CreateRotationY(_player.Facing) * Matrix4x4.CreateTranslation(_player.Position);
        _litShader.Set("uModel", Matrix4x4.CreateTranslation(0, 0.6f, 0) * root);
        _playerBody.Draw();
        _litShader.Set("uModel", Matrix4x4.CreateTranslation(0, 1.45f, 0) * root);
        _playerHead.Draw();
    }

    protected override void OnUnload()
    {
        _imgui.Dispose();
        _scene?.Dispose();
        _assets.Dispose();
        _playerBody.Dispose();
        _playerHead.Dispose();
        _terrainShader.Dispose();
        _modelShader.Dispose();
        _litShader.Dispose();
        _unlitShader.Dispose();
        _files.Dispose();
    }
}
