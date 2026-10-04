using System.Numerics;
using Engine.Rendering;
using Engine.World;

namespace Engine.Tests;

public class TerrainTests
{
    private static Terrain Slope() =>
        // Height equals the grid X index: a ramp rising toward +X.
        new(new float[,] { { 0, 0, 0 }, { 1, 1, 1 }, { 2, 2, 2 } }, cellSize: 1f, waterLevel: -10f);

    [Fact]
    public void GetHeight_InterpolatesBetweenSamples()
    {
        var terrain = Slope();
        Assert.Equal(0f, terrain.GetHeight(-1f, 0f), 4);
        Assert.Equal(0.5f, terrain.GetHeight(-0.5f, 0.3f), 4);
        Assert.Equal(2f, terrain.GetHeight(1f, -1f), 4);
    }

    [Fact]
    public void GetHeight_ClampsOutsideMap()
    {
        var terrain = Slope();
        Assert.Equal(2f, terrain.GetHeight(50f, 0f), 4);
        Assert.Equal(0f, terrain.GetHeight(-50f, 0f), 4);
    }

    [Fact]
    public void Generate_IsDeterministicPerSeed()
    {
        var a = Terrain.Generate(7, resolution: 33);
        var b = Terrain.Generate(7, resolution: 33);
        var c = Terrain.Generate(8, resolution: 33);
        Assert.Equal(a.GetHeight(3.3f, -5.1f), b.GetHeight(3.3f, -5.1f));
        Assert.NotEqual(a.GetHeight(3.3f, -5.1f), c.GetHeight(3.3f, -5.1f));
    }

    [Fact]
    public void BuildMesh_ProducesGridWithUpwardNormals()
    {
        var mesh = Terrain.Generate(1, resolution: 17).BuildMesh();
        Assert.Equal(17 * 17, mesh.VertexCount);
        Assert.Equal(16 * 16 * 6, mesh.Indices.Count);
        for (var v = 0; v < mesh.VertexCount; v++)
            Assert.True(mesh.Vertices[v * MeshData.FloatsPerVertex + 4] > 0, "normal Y must point up");
    }
}

public class OrbitCameraTests
{
    [Fact]
    public void Position_SitsBehindTargetAtDistance()
    {
        var camera = new OrbitCamera { Target = new(1, 2, 3), Yaw = 0, Pitch = 0, Distance = 10 };
        AssertClose(new Vector3(1, 2, 13), camera.Position);
        AssertClose(-Vector3.UnitZ, camera.Forward);
        AssertClose(Vector3.UnitX, camera.Right);
    }

    [Fact]
    public void PitchAndDistance_AreClamped()
    {
        var camera = new OrbitCamera { Pitch = 5, Distance = 1000 };
        Assert.Equal(OrbitCamera.MaxPitch, camera.Pitch);
        Assert.Equal(OrbitCamera.MaxDistance, camera.Distance);
    }

    [Fact]
    public void Position_StaysAboveGround()
    {
        var camera = new OrbitCamera { Pitch = -1f, Distance = 10, MinHeightAt = (_, _) => 5f };
        Assert.True(camera.Position.Y >= 5.5f);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");
}

public class CollisionMeshTests
{
    // Two stacked 10x10 floors at Y=0 and Y=5, plus a vertical wall that must be ignored.
    private static readonly CollisionMesh Building = new(
        [
            new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new(0, 0, 10),
            new(0, 5, 0), new(10, 5, 0), new(10, 5, 10), new(0, 5, 10),
            new(0, 0, 5), new(10, 0, 5), new(10, 20, 5),
        ],
        [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6, 8, 9, 10]);

    [Fact]
    public void Floor_PicksHighestSurfaceBelowProbe()
    {
        Assert.True(Building.TryGetFloor(3, 3, 100, out var top));
        Assert.Equal(5f, top, 4);
        Assert.True(Building.TryGetFloor(3, 3, 4, out var ground));
        Assert.Equal(0f, ground, 4);
    }

    [Fact]
    public void Floor_IgnoresWallsAndOutsidePoints()
    {
        Assert.Equal(4, Building.TriangleCount);
        Assert.False(Building.TryGetFloor(20, 3, 100, out _));
        Assert.False(Building.TryGetFloor(3, 3, -1, out _));
    }
}

public class CharacterControllerTests
{
    private static readonly Terrain Flat = new(new float[9, 9], cellSize: 1f, waterLevel: -10f);

    [Fact]
    public void Moving_FollowsCameraForwardAndFacesDirection()
    {
        var player = new CharacterController(Flat, Vector3.Zero);
        player.Update(0.1f, new CharacterInput(new Vector2(0, 1), Jump: false), -Vector3.UnitZ, Vector3.UnitX);

        Assert.Equal(-CharacterController.RunSpeed * 0.1f, player.Position.Z, 4);
        Assert.Equal(0f, player.Facing, 4);
        Assert.True(player.IsGrounded);
    }

    [Fact]
    public void SpeedMultiplier_ScalesRunAndFlySpeed()
    {
        var runner = new CharacterController(Flat, Vector3.Zero);
        runner.Update(0.01f, new CharacterInput(new Vector2(0, 1), Jump: false, SpeedMultiplier: 10f), -Vector3.UnitZ, Vector3.UnitX);
        Assert.Equal(-CharacterController.RunSpeed * 10f * 0.01f, runner.Position.Z, 4);

        var flyer = new CharacterController(Flat, Vector3.Zero);
        flyer.Update(0.1f, new CharacterInput(Vector2.Zero, Jump: false, Vertical: 1, Fly: true, SpeedMultiplier: 5f), -Vector3.UnitZ, Vector3.UnitX);
        Assert.Equal(CharacterController.FlySpeed * 5f * 0.1f, flyer.Position.Y, 3);
    }

    [Fact]
    public void Jump_RisesThenLands()
    {
        var player = new CharacterController(Flat, Vector3.Zero);
        player.Update(0.05f, new CharacterInput(Vector2.Zero, Jump: true), -Vector3.UnitZ, Vector3.UnitX);
        Assert.False(player.IsGrounded);
        Assert.True(player.Position.Y > 0);

        for (var i = 0; i < 60; i++)
            player.Update(0.05f, new CharacterInput(Vector2.Zero, Jump: false), -Vector3.UnitZ, Vector3.UnitX);
        Assert.True(player.IsGrounded);
        Assert.Equal(0f, player.Position.Y, 4);
    }

    [Fact]
    public void Movement_IsClampedInsideTerrain()
    {
        var player = new CharacterController(Flat, Vector3.Zero);
        for (var i = 0; i < 100; i++)
            player.Update(0.1f, new CharacterInput(new Vector2(1, 0), Jump: false), -Vector3.UnitZ, Vector3.UnitX);
        Assert.Equal(Flat.HalfExtent - 1f, player.Position.X, 4);
    }

    [Fact]
    public void Fly_ClimbsWithoutGravityAndLandsOnGround()
    {
        var player = new CharacterController(Flat, Vector3.Zero);
        player.Update(0.5f, new CharacterInput(Vector2.Zero, Jump: false, Vertical: 1, Fly: true), -Vector3.UnitZ, Vector3.UnitX);
        Assert.Equal(CharacterController.FlySpeed * 0.5f, player.Position.Y, 3);

        player.Update(1f, new CharacterInput(Vector2.Zero, Jump: false, Vertical: -1, Fly: true), -Vector3.UnitZ, Vector3.UnitX);
        Assert.Equal(0f, player.Position.Y, 4);
        Assert.True(player.IsGrounded);
    }

    [Fact]
    public void MissingGround_HoldsAltitude()
    {
        var player = new CharacterController(new NoGround(), new Vector3(0, 50, 0));
        player.Update(1f, new CharacterInput(new Vector2(0, 1), Jump: false), -Vector3.UnitZ, Vector3.UnitX);
        Assert.Equal(50f, player.Position.Y);
    }

    private sealed class NoGround : IHeightField
    {
        public Vector2 Min => new(-100);
        public Vector2 Max => new(100);
        public bool TryGetHeight(Vector3 probe, out float height)
        {
            height = 0;
            return false;
        }
    }
}
