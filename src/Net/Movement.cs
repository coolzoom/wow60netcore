using System.Numerics;

namespace Net;

[Flags]
public enum MoveFlags : uint
{
    None = 0,
    Forward = 0x1,
    Backward = 0x2,
    StrafeLeft = 0x4,
    StrafeRight = 0x8,
    TurnLeft = 0x10,
    TurnRight = 0x20,
    WalkMode = 0x100,
    Root = 0x1000,
    Jumping = 0x2000,
    FallingFar = 0x4000,
    Swimming = 0x200000,
    SplineEnabled = 0x400000,
    Flying = 0x1000000,
    OnTransport = 0x2000000,
    SplineElevation = 0x4000000,

    Moving = Forward | Backward | StrafeLeft | StrafeRight,
}

/// <summary>
/// The movement block shared by MSG_MOVE_* packets and living objects in update packets. Position is in WoW
/// world coordinates (X north, Y west, Z up), orientation in radians counter-clockwise from north.
/// </summary>
public struct MovementInfo
{
    public MoveFlags Flags;
    public uint Time;
    public Vector3 Position;
    public float Orientation;
    public ulong TransportGuid;
    public Vector3 TransportPosition;
    public float TransportOrientation;
    public float Pitch;
    public uint FallTime;
    public float JumpVelocity, JumpSin, JumpCos, JumpXySpeed;
    public float SplineElevation;

    public static MovementInfo Read(PacketReader r)
    {
        var m = new MovementInfo
        {
            Flags = (MoveFlags)r.U32(),
            Time = r.U32(),
            Position = r.Vector3(),
            Orientation = r.F32(),
        };
        if ((m.Flags & MoveFlags.OnTransport) != 0)
        {
            m.TransportGuid = r.U64();
            m.TransportPosition = r.Vector3();
            m.TransportOrientation = r.F32();
        }
        if ((m.Flags & MoveFlags.Swimming) != 0)
            m.Pitch = r.F32();
        m.FallTime = r.U32();
        if ((m.Flags & MoveFlags.Jumping) != 0)
        {
            m.JumpVelocity = r.F32();
            m.JumpCos = r.F32();
            m.JumpSin = r.F32();
            m.JumpXySpeed = r.F32();
        }
        if ((m.Flags & MoveFlags.SplineElevation) != 0)
            m.SplineElevation = r.F32();
        return m;
    }

    public readonly void Write(PacketWriter w)
    {
        w.U32((uint)Flags).U32(Time).F32(Position.X).F32(Position.Y).F32(Position.Z).F32(Orientation);
        if ((Flags & MoveFlags.OnTransport) != 0)
            w.U64(TransportGuid).F32(TransportPosition.X).F32(TransportPosition.Y).F32(TransportPosition.Z).F32(TransportOrientation);
        if ((Flags & MoveFlags.Swimming) != 0)
            w.F32(Pitch);
        w.U32(FallTime);
        if ((Flags & MoveFlags.Jumping) != 0)
            w.F32(JumpVelocity).F32(JumpCos).F32(JumpSin).F32(JumpXySpeed);
        if ((Flags & MoveFlags.SplineElevation) != 0)
            w.F32(SplineElevation);
    }
}

/// <summary>A server-driven path from SMSG_MONSTER_MOVE or an update block's spline.</summary>
public sealed record MonsterMove(ulong Guid, Vector3 Start, Vector3 Destination, uint DurationMs, float? FinalFacing, bool Stop)
{
    private const uint SplineFlying = 0x200;

    public static MonsterMove Parse(byte[] body)
    {
        var r = new PacketReader(body);
        var guid = r.PackedGuid();
        var start = r.Vector3();
        r.U32();
        var type = r.U8();
        if (type == 1)
            return new MonsterMove(guid, start, start, 0, null, Stop: true);
        float? facing = null;
        switch (type)
        {
            case 2: r.Vector3(); break;
            case 3: r.U64(); break;
            case 4: facing = r.F32(); break;
        }
        var flags = r.U32();
        var duration = r.U32();
        var count = r.U32();
        Vector3 destination;
        if ((flags & SplineFlying) != 0)
        {
            destination = start;
            for (var i = 0; i < count; i++)
                destination = r.Vector3();
        }
        else
        {
            destination = count > 0 ? r.Vector3() : start;
        }
        return new MonsterMove(guid, start, destination, duration, facing, Stop: false);
    }
}
