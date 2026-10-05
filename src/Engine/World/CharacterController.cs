using System.Numerics;

namespace Engine.World;

/// <param name="Move">X = strafe right, Y = forward, each in [-1, 1].</param>
/// <param name="Vertical">Fly mode only: +1 up, -1 down.</param>
/// <param name="SpeedMultiplier">Scales run and fly speed; jumping and gravity are unaffected.</param>
public readonly record struct CharacterInput(Vector2 Move, bool Jump, float Vertical = 0, bool Fly = false, float SpeedMultiplier = 1f);

/// <summary>Ground-following character movement with jumping and an optional fly mode.</summary>
public sealed class CharacterController
{
    public const float RunSpeed = 7f;
    public const float FlySpeed = 40f;
    public const float JumpSpeed = 8f;
    public const float Gravity = 22f;
    /// <summary>How far above the feet a surface may be and still be stepped onto.</summary>
    public const float StepHeight = 1.5f;

    private readonly IHeightField _ground;
    private float _verticalVelocity;

    public Vector3 Position { get; private set; }
    /// <summary>Facing angle around Y, same convention as <see cref="OrbitCamera.Yaw"/>.</summary>
    public float Facing { get; private set; }
    public bool IsGrounded { get; private set; } = true;

    public CharacterController(IHeightField ground, Vector3 spawn)
    {
        _ground = ground;
        Teleport(spawn);
    }

    /// <summary>Moves to <paramref name="position"/>, snapping down to the highest ground below it when known.</summary>
    public void Teleport(Vector3 position)
    {
        Position = _ground.TryGetHeight(position + new Vector3(0, StepHeight, 0), out var ground) ? position with { Y = ground } : position;
        _verticalVelocity = 0;
        IsGrounded = true;
    }

    public void SetFacing(float facing) => Facing = facing;

    public void Update(float dt, CharacterInput input, Vector3 forward, Vector3 right)
    {
        var position = Position;

        var direction = forward * input.Move.Y + right * input.Move.X;
        if (direction.LengthSquared() > 0)
        {
            direction = Vector3.Normalize(direction);
            position += direction * (input.Fly ? FlySpeed : RunSpeed) * input.SpeedMultiplier * dt;
            Facing = MathF.Atan2(-direction.X, -direction.Z);
        }

        position.X = Math.Clamp(position.X, _ground.Min.X, _ground.Max.X);
        position.Z = Math.Clamp(position.Z, _ground.Min.Y, _ground.Max.Y);
        var hasGround = _ground.TryGetHeight(new Vector3(position.X, Position.Y + StepHeight, position.Z), out var ground);

        if (input.Fly)
        {
            _verticalVelocity = 0;
            position.Y += input.Vertical * FlySpeed * input.SpeedMultiplier * dt;
            IsGrounded = hasGround && position.Y <= ground;
            if (IsGrounded)
                position.Y = ground;
            Position = position;
            return;
        }

        if (!hasGround)
        {
            // Hold altitude until the ground under us has streamed in.
            Position = position;
            return;
        }

        if (input.Jump && IsGrounded)
        {
            _verticalVelocity = JumpSpeed;
            IsGrounded = false;
        }

        _verticalVelocity -= Gravity * dt;
        position.Y += _verticalVelocity * dt;

        if (position.Y <= ground)
        {
            position.Y = ground;
            _verticalVelocity = 0;
            IsGrounded = true;
        }

        Position = position;
    }
}
