using System.IO.Compression;
using System.Numerics;

namespace Net;

public enum ObjectType : byte
{
    Object = 0,
    Item = 1,
    Container = 2,
    Unit = 3,
    Player = 4,
    GameObject = 5,
    DynamicObject = 6,
    Corpse = 7,
}

/// <summary>Update field indexes of the 1.12.1 client that this client reads.</summary>
public static class UpdateFields
{
    public const int ObjectGuid = 0x00;
    public const int ObjectType = 0x02;
    public const int ObjectEntry = 0x03;
    public const int ObjectScale = 0x04;
    public const int ItemOwner = 0x06;
    public const int ItemContained = 0x08;
    public const int ItemStackCount = 0x0E;
    /// <summary>ITEM_FIELD_FLAGS; bit 0 is soulbound.</summary>
    public const int ItemFlags = 0x15;
    public const int ItemDurability = 0x2E;
    public const int ItemMaxDurability = 0x2F;
    public const int ContainerNumSlots = 0x30;
    public const int ContainerSlot1 = 0x32;
    public const int UnitTarget = 0x10;
    public const int UnitChannelObject = 0x14;
    public const int UnitHealth = 0x16;
    /// <summary>UNIT_FIELD_POWER1..5: mana, rage, focus, energy, happiness.</summary>
    public const int UnitPower1 = 0x17;
    public const int UnitMaxHealth = 0x1C;
    public const int UnitMaxPower1 = 0x1D;
    public const int UnitLevel = 0x22;
    public const int UnitFactionTemplate = 0x23;
    public const int UnitBytes0 = 0x24;
    /// <summary>UNIT_VIRTUAL_ITEM_SLOT_DISPLAY: item display ids of a creature's main hand, off hand and ranged.</summary>
    public const int UnitVirtualItemDisplay = 0x25;
    public const int UnitFlags = 0x2E;
    /// <summary>UNIT_FIELD_AURA: 48 spell ids, the first 32 positive.</summary>
    public const int UnitAura = 0x2F;
    public const int UnitBaseAttackTime = 0x7E;
    public const int UnitDisplayId = 0x83;
    public const int UnitNativeDisplayId = 0x84;
    public const int UnitMountDisplayId = 0x85;
    public const int UnitMinDamage = 0x86;
    public const int UnitMaxDamage = 0x87;
    public const int UnitBytes1 = 0x8A;
    public const int UnitDynamicFlags = 0x8F;
    public const int UnitNpcFlags = 0x93;
    /// <summary>UNIT_FIELD_STAT0..4: strength, agility, stamina, intellect, spirit.</summary>
    public const int UnitStat0 = 0x96;
    /// <summary>UNIT_FIELD_RESISTANCES: armor first, then the six schools.</summary>
    public const int UnitResistances = 0x9B;
    /// <summary>UNIT_FIELD_BYTES_2; byte 0 is the sheath state (0 sheathed, 1 melee drawn, 2 ranged drawn).</summary>
    public const int UnitBytes2 = 0xA4;
    public const int UnitAttackPower = 0xA5;
    public const int PlayerFlags = 0xBE;
    public const int PlayerBytes = 0xC1;
    public const int PlayerBytes2 = 0xC2;
    /// <summary>PLAYER_VISIBLE_ITEM_1_0: item entry of equipment slot 0; slots are 12 fields apart.</summary>
    public const int PlayerVisibleItem1 = 0x104;
    public const int PlayerVisibleItemStride = 12;
    /// <summary>PLAYER_FIELD_INV_SLOT_HEAD: 23 item guids (19 equipment slots, 4 bags), then the 16 backpack slots.</summary>
    public const int PlayerInvSlotHead = 0x1E6;
    public const int PlayerPackSlot1 = 0x214;
    public const int PlayerXp = 0x2CC;
    public const int PlayerNextLevelXp = 0x2CD;
    public const int PlayerCoinage = 0x498;
    public const int GameObjectDisplayId = 0x08;
    public const int GameObjectPosX = 0x0F;
    public const int GameObjectFacing = 0x12;
}

/// <summary>An object the server has told us about, with its update fields and latest known movement.</summary>
public sealed class WorldObject(ulong guid, ObjectType type)
{
    private readonly Dictionary<int, uint> _fields = [];

    public ulong Guid { get; } = guid;
    public ObjectType Type { get; } = type;
    public MovementInfo Movement { get; set; }
    public float RunSpeed { get; set; } = 7f;
    public MonsterMove? Path { get; private set; }
    private long _pathStartMs;
    private Vector3 _movementBase;
    private long _movementStartMs = -1;

    /// <summary>WoW world coordinates (X north, Y west, Z up); interpolated along server paths.</summary>
    public Vector3 Position { get; private set; }
    public float Orientation { get; private set; }
    public string? Name { get; set; }

    public uint this[int field]
    {
        get => _fields.GetValueOrDefault(field);
        set => _fields[field] = value;
    }

    public float Float(int field) => BitConverter.UInt32BitsToSingle(this[field]);
    public ulong Guid64(int field) => this[field] | (ulong)this[field + 1] << 32;
    public uint Entry => this[UpdateFields.ObjectEntry];
    public float Scale => this[UpdateFields.ObjectScale] == 0 ? 1f : Float(UpdateFields.ObjectScale);
    public bool IsUnit => Type is ObjectType.Unit or ObjectType.Player;
    public uint DisplayId => Type == ObjectType.GameObject ? this[UpdateFields.GameObjectDisplayId] : this[UpdateFields.UnitDisplayId];
    public byte Race => (byte)this[UpdateFields.UnitBytes0];
    public byte Gender => (byte)(this[UpdateFields.UnitBytes0] >> 16);
    public uint Level => this[UpdateFields.UnitLevel];
    public uint Health => this[UpdateFields.UnitHealth];
    public uint MaxHealth => this[UpdateFields.UnitMaxHealth];
    public byte PowerType => (byte)(this[UpdateFields.UnitBytes0] >> 24);
    public uint Power => this[UpdateFields.UnitPower1 + PowerType];
    public uint MaxPower => this[UpdateFields.UnitMaxPower1 + PowerType];
    public byte Class => (byte)(this[UpdateFields.UnitBytes0] >> 8);
    public uint FactionTemplate => this[UpdateFields.UnitFactionTemplate];
    public ulong Target => Guid64(UpdateFields.UnitTarget);
    public byte StandState => (byte)this[UpdateFields.UnitBytes1];
    public byte ShapeshiftForm => (byte)(this[UpdateFields.UnitBytes1] >> 16);
    public byte SheathState => (byte)this[UpdateFields.UnitBytes2];
    public bool IsDead => IsUnit && (Health == 0 || StandState == 7);
    public bool InCombat => (this[UpdateFields.UnitFlags] & 0x80000) != 0;
    public bool Lootable => (this[UpdateFields.UnitDynamicFlags] & 0x1) != 0;
    /// <summary>Someone else holds the kill credit (the name shows grey).</summary>
    public bool TappedByOther => (this[UpdateFields.UnitDynamicFlags] & 0x4) != 0;
    public uint NpcFlags => this[UpdateFields.UnitNpcFlags];

    public void SetPosition(Vector3 position, float orientation)
    {
        Position = position;
        Orientation = orientation;
        Path = null;
    }

    /// <summary>A movement packet from another player: position now, extrapolated while its move flags say so.</summary>
    public void SetMovement(MovementInfo movement, long nowMs)
    {
        Movement = movement;
        SetPosition(movement.Position, movement.Orientation);
        _movementBase = movement.Position;
        _movementStartMs = nowMs;
    }

    /// <summary>Ground speed in yards per second while moving, 0 when standing.</summary>
    public float Speed
    {
        get
        {
            if (Path is { } path)
                return Vector3.Distance(path.Start, path.Destination) / Math.Max(path.DurationMs, 1) * 1000f;
            if ((Movement.Flags & MoveFlags.Moving) == 0 || (Movement.Flags & MoveFlags.Root) != 0)
                return 0;
            if ((Movement.Flags & MoveFlags.Backward) != 0)
                return 4.5f;
            return (Movement.Flags & MoveFlags.WalkMode) != 0 ? 2.5f : RunSpeed;
        }
    }

    public void SetPath(MonsterMove path, long nowMs)
    {
        Path = path.Stop || path.DurationMs == 0 ? null : path;
        _pathStartMs = nowMs;
        Position = path.Start;
        if (path.Stop || path.DurationMs == 0)
            Position = path.Destination;
        if (Path is null && path.FinalFacing is { } facing)
            Orientation = facing;
    }

    /// <summary>Advances along the current server path (linear, which is what ground NPCs mostly use).</summary>
    public void Advance(long nowMs)
    {
        if (Path is not { } path)
        {
            Extrapolate(nowMs);
            return;
        }
        var t = Math.Clamp((nowMs - _pathStartMs) / (float)path.DurationMs, 0f, 1f);
        var delta = path.Destination - path.Start;
        if (delta.LengthSquared() > 0.0001f)
            Orientation = MathF.Atan2(delta.Y, delta.X);
        Position = path.Start + delta * t;
        if (t >= 1f)
        {
            Path = null;
            if (path.FinalFacing is { } facing)
                Orientation = facing;
        }
    }

    /// <summary>Carries a moving player along its heading for up to a second past the last packet.</summary>
    private void Extrapolate(long nowMs)
    {
        var flags = Movement.Flags;
        if (_movementStartMs < 0 || Type != ObjectType.Player || (flags & MoveFlags.Moving) == 0 || (flags & MoveFlags.Root) != 0)
            return;
        var angle = Movement.Orientation;
        var direction = Vector2.Zero;
        if ((flags & MoveFlags.Forward) != 0) direction += new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        if ((flags & MoveFlags.Backward) != 0) direction -= new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        if ((flags & MoveFlags.StrafeLeft) != 0) direction += new Vector2(MathF.Cos(angle + MathF.PI / 2), MathF.Sin(angle + MathF.PI / 2));
        if ((flags & MoveFlags.StrafeRight) != 0) direction -= new Vector2(MathF.Cos(angle + MathF.PI / 2), MathF.Sin(angle + MathF.PI / 2));
        if (direction == Vector2.Zero)
            return;
        var seconds = Math.Min(nowMs - _movementStartMs, 1000) / 1000f;
        var offset = Vector2.Normalize(direction) * Speed * seconds;
        Position = _movementBase + new Vector3(offset, 0);
    }
}

/// <summary>All objects in view, maintained from SMSG_UPDATE_OBJECT and friends.</summary>
public sealed class ObjectStore
{
    private const byte UpdateValues = 0, UpdateMovement = 1, UpdateCreate = 2, UpdateCreate2 = 3, UpdateOutOfRange = 4, UpdateNear = 5;
    private const byte FlagTransport = 0x02, FlagMelee = 0x04, FlagHighGuid = 0x08, FlagAll = 0x10, FlagLiving = 0x20, FlagHasPosition = 0x40;
    private const uint SplineFinalPoint = 0x10000, SplineFinalTarget = 0x20000, SplineFinalAngle = 0x40000;

    private readonly Dictionary<ulong, WorldObject> _objects = [];

    public IReadOnlyCollection<WorldObject> All => _objects.Values;
    public event Action<WorldObject>? Created;
    public event Action<WorldObject>? Removed;

    public WorldObject? Get(ulong guid) => _objects.GetValueOrDefault(guid);

    public void Clear()
    {
        foreach (var obj in _objects.Values.ToList())
            Removed?.Invoke(obj);
        _objects.Clear();
    }

    public void Remove(ulong guid)
    {
        if (_objects.Remove(guid, out var obj))
            Removed?.Invoke(obj);
    }

    public static byte[] Inflate(byte[] body)
    {
        var size = BitConverter.ToInt32(body, 0);
        using var input = new ZLibStream(new MemoryStream(body, 4, body.Length - 4), CompressionMode.Decompress);
        var output = new byte[size];
        input.ReadExactly(output);
        return output;
    }

    public void ApplyUpdate(byte[] body, long nowMs)
    {
        var r = new PacketReader(body);
        var count = r.U32();
        r.U8();
        for (var i = 0; i < count; i++)
        {
            var kind = r.U8();
            switch (kind)
            {
                case UpdateValues:
                {
                    var guid = r.PackedGuid();
                    var values = ReadValues(r);
                    if (_objects.TryGetValue(guid, out var obj))
                        foreach (var (field, value) in values)
                            obj[field] = value;
                    break;
                }
                case UpdateMovement:
                {
                    var guid = r.U64();
                    var obj = _objects.GetValueOrDefault(guid) ?? new WorldObject(guid, ObjectType.Object);
                    ReadMovement(r, obj, nowMs);
                    break;
                }
                case UpdateCreate or UpdateCreate2:
                {
                    var guid = r.PackedGuid();
                    var type = (ObjectType)r.U8();
                    var isNew = !_objects.TryGetValue(guid, out var obj);
                    if (isNew || obj!.Type != type)
                        obj = new WorldObject(guid, type);
                    ReadMovement(r, obj, nowMs);
                    foreach (var (field, value) in ReadValues(r))
                        obj[field] = value;
                    if (type == ObjectType.GameObject && obj.Position == Vector3.Zero)
                        obj.SetPosition(new Vector3(obj.Float(UpdateFields.GameObjectPosX), obj.Float(UpdateFields.GameObjectPosX + 1),
                            obj.Float(UpdateFields.GameObjectPosX + 2)), obj.Float(UpdateFields.GameObjectFacing));
                    _objects[guid] = obj;
                    if (isNew)
                        Created?.Invoke(obj);
                    break;
                }
                case UpdateOutOfRange or UpdateNear:
                {
                    var guids = r.U32();
                    for (var g = 0; g < guids; g++)
                    {
                        var guid = r.PackedGuid();
                        if (kind == UpdateOutOfRange)
                            Remove(guid);
                    }
                    break;
                }
                default:
                    throw new InvalidDataException($"Unknown update block type {kind}.");
            }
        }
    }

    private static Dictionary<int, uint> ReadValues(PacketReader r)
    {
        var blocks = r.U8();
        var mask = new uint[blocks];
        for (var b = 0; b < blocks; b++)
            mask[b] = r.U32();
        var values = new Dictionary<int, uint>();
        for (var b = 0; b < blocks; b++)
            for (var bit = 0; bit < 32; bit++)
                if ((mask[b] & (1u << bit)) != 0)
                    values[b * 32 + bit] = r.U32();
        return values;
    }

    private static void ReadMovement(PacketReader r, WorldObject obj, long nowMs)
    {
        var flags = r.U8();
        if ((flags & FlagLiving) != 0)
        {
            var movement = MovementInfo.Read(r);
            obj.Movement = movement;
            obj.SetPosition(movement.Position, movement.Orientation);
            r.F32();
            obj.RunSpeed = r.F32();
            r.Skip(4 * 4);
            if ((movement.Flags & MoveFlags.SplineEnabled) != 0)
            {
                var splineFlags = r.U32();
                float? facing = null;
                if ((splineFlags & SplineFinalAngle) != 0)
                    facing = r.F32();
                else if ((splineFlags & SplineFinalTarget) != 0)
                    r.U64();
                else if ((splineFlags & SplineFinalPoint) != 0)
                    r.Vector3();
                var passed = r.U32();
                var duration = r.U32();
                r.U32();
                var nodes = r.U32();
                r.Skip((int)nodes * 12);
                var destination = r.Vector3();
                if (duration > passed)
                    obj.SetPath(new MonsterMove(obj.Guid, movement.Position, destination, duration - passed, facing, false), nowMs);
            }
        }
        else if ((flags & FlagHasPosition) != 0)
        {
            var position = r.Vector3();
            obj.SetPosition(position, r.F32());
        }
        if ((flags & FlagHighGuid) != 0)
            r.U32();
        if ((flags & FlagAll) != 0)
            r.U32();
        if ((flags & FlagMelee) != 0)
            r.PackedGuid();
        if ((flags & FlagTransport) != 0)
            r.U32();
    }
}
