using System.Numerics;

namespace Engine.World;

/// <summary>Third-person camera orbiting a target point. Yaw 0 looks toward -Z.</summary>
public sealed class OrbitCamera
{
    public const float MinPitch = -1.2f;
    public const float MaxPitch = 1.2f;
    public const float MinDistance = 3f;
    public const float MaxDistance = 40f;

    private float _pitch = 0.35f;
    private float _distance = 12f;

    public Vector3 Target { get; set; }
    public float Yaw { get; set; }
    public float FieldOfView { get; set; } = MathF.PI / 3;

    /// <summary>Lowest allowed camera height for its current XZ position, e.g. the ground.</summary>
    public Func<float, float, float>? MinHeightAt { get; set; }

    public float Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, MinPitch, MaxPitch);
    }

    public float Distance
    {
        get => _distance;
        set => _distance = Math.Clamp(value, MinDistance, MaxDistance);
    }

    /// <summary>Horizontal forward direction, for camera-relative movement.</summary>
    public Vector3 Forward => new(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
    public Vector3 Right => new(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));

    public Vector3 Position
    {
        get
        {
            var offset = new Vector3(
                MathF.Sin(Yaw) * MathF.Cos(Pitch),
                MathF.Sin(Pitch),
                MathF.Cos(Yaw) * MathF.Cos(Pitch)) * Distance;
            var position = Target + offset;
            if (MinHeightAt is { } minHeight)
                position.Y = MathF.Max(position.Y, minHeight(position.X, position.Z) + 0.5f);
            return position;
        }
    }

    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);

    public Matrix4x4 ProjectionMatrix(float aspectRatio) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspectRatio, 0.1f, 1000f);
}
