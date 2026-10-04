using System.Numerics;

namespace Formats.Terrain;

/// <summary>
/// Conversions between WoW world coordinates (X north, Y west, Z up) and render space.
/// Render space is the client's "placement" space used by MDDF/MODF entries:
/// x = Origin - worldY, y = worldZ, z = Origin - worldX (right-handed, Y up).
/// </summary>
public static class WorldSpace
{
    public const float TileSize = 1600f / 3f;
    public const float ChunkSize = TileSize / 16f;
    public const float UnitSize = ChunkSize / 8f;
    public const float Origin = 32 * TileSize;
    public const int TilesPerSide = 64;

    public static Vector3 FromWorld(Vector3 world) => new(Origin - world.Y, world.Z, Origin - world.X);

    public static Vector3 ToWorld(Vector3 render) => new(Origin - render.Z, Origin - render.X, render.Y);

    /// <summary>Tile indices as used in ADT file names: Map_{X}_{Y}.adt.</summary>
    public static (int X, int Y) TileAt(float renderX, float renderZ) =>
        ((int)MathF.Floor(renderX / TileSize), (int)MathF.Floor(renderZ / TileSize));

    /// <summary>Model-local axes (X forward, Y left, Z up) to render axes.</summary>
    public static Vector3 ModelToRender(Vector3 v) => new(v.X, v.Z, -v.Y);

    /// <summary>Transform for a doodad or map object placed with MDDF/MODF position and rotation (degrees).</summary>
    public static Matrix4x4 Placement(Vector3 position, Vector3 rotationDegrees, float scale) =>
        Matrix4x4.CreateScale(scale)
        * Matrix4x4.CreateRotationX(Radians(rotationDegrees.Z))
        * Matrix4x4.CreateRotationZ(Radians(-rotationDegrees.X))
        * Matrix4x4.CreateRotationY(Radians(rotationDegrees.Y - 90f))
        * Matrix4x4.CreateTranslation(position);

    private static float Radians(float degrees) => degrees * MathF.PI / 180f;
}
