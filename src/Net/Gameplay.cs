namespace Net;

/// <summary>Static item data from SMSG_ITEM_QUERY_SINGLE_RESPONSE (the fields the client shows).</summary>
public sealed record ItemTemplate(uint Entry, uint Class, uint SubClass, string Name, uint DisplayId, uint Quality, uint Flags,
    uint BuyPrice, uint SellPrice, uint InventoryType, uint RequiredLevel, uint MaxCount, uint Stackable, uint ContainerSlots,
    IReadOnlyList<(uint Type, int Value)> Stats, IReadOnlyList<(float Min, float Max, uint School)> Damage, int Armor,
    IReadOnlyList<int> Resistances, uint Delay, IReadOnlyList<(uint Spell, uint Trigger)> Spells, uint Bonding, string Description,
    uint MaxDurability);

public enum CombatKind
{
    Melee,
    Spell,
    Periodic,
    Heal,
    Energize,
    Miss,
    Environment,
}

/// <summary>One line of the combat log, also used for floating combat text.</summary>
public sealed record CombatEvent(ulong Attacker, ulong Victim, int Amount, uint SpellId, CombatKind Kind, bool Critical, string Outcome,
    long TimeMs);

public sealed record LootSlot(byte Index, uint Item, uint Count, uint DisplayId, byte Type);

public sealed class LootWindow(ulong guid, uint gold, List<LootSlot> items)
{
    public ulong Guid { get; } = guid;
    public uint Gold { get; set; } = gold;
    public List<LootSlot> Items { get; } = items;
}

/// <summary>A cast in progress (any caster) as reported by SMSG_SPELL_START or MSG_CHANNEL_START.</summary>
public sealed record CastState(uint SpellId, long StartMs, uint DurationMs, bool Channel)
{
    public long EndMs => StartMs + DurationMs;
}

/// <summary>
/// Everything a player does once in the world beyond moving and chatting: selection, melee, spells and cooldowns,
/// action bar, inventory and loot. Packet layouts follow the 1.12.1 protocol as served by vMaNGOS.
/// </summary>
public sealed class Gameplay
{
    public const int ActionButtonCount = 120;
    public const int EquipmentSlots = 19, BagSlotStart = 19, BackpackStart = 23, BackpackSize = 16;
    public const byte InventoryBag = 255;
    private const uint HitMiss = 0x10, HitCritical = 0x80;
    private const uint TargetUnit = 0x2;
    private const int AuraPeriodicDamage = 3, AuraPeriodicHeal = 8, AuraObsModHealth = 20, AuraPeriodicEnergize = 24,
        AuraPeriodicDamagePercent = 89;

    /// <summary>VICTIMSTATE names, then SPELL_MISS names, as GlobalStrings keys of the combat text.</summary>
    private static readonly string[] VictimOutcome = ["", "", "DODGE", "PARRY", "INTERRUPT", "BLOCK", "EVADE", "IMMUNE", "DEFLECT"];
    private static readonly string[] SpellMissOutcome = ["", "MISS", "RESIST", "DODGE", "PARRY", "BLOCK", "EVADE", "IMMUNE", "IMMUNE", "DEFLECT",
        "ABSORB", "REFLECT"];

    private readonly GameSession _session;
    private readonly Dictionary<uint, ItemTemplate> _items = [];
    private readonly HashSet<uint> _itemQueries = [];
    private readonly List<uint> _spells = [];

    public Gameplay(GameSession session)
    {
        _session = session;
        session.On(Opcode.SMSG_INITIAL_SPELLS, OnInitialSpells);
        session.On(Opcode.SMSG_LEARNED_SPELL, p =>
        {
            var spell = p.Reader().U16();
            if (!_spells.Contains(spell))
                _spells.Add(spell);
            SpellLearned?.Invoke(spell);
        });
        session.On(Opcode.SMSG_REMOVED_SPELL, p => _spells.Remove(p.Reader().U16()));
        session.On(Opcode.SMSG_ACTION_BUTTONS, p =>
        {
            var r = p.Reader();
            for (var i = 0; i < ActionButtonCount && r.Remaining >= 4; i++)
                ActionButtons[i] = r.U32();
        });
        session.On(Opcode.SMSG_CAST_RESULT, OnCastResult);
        session.On(Opcode.SMSG_SPELL_START, OnSpellStart);
        session.On(Opcode.SMSG_SPELL_GO, OnSpellGo);
        session.On(Opcode.SMSG_SPELL_FAILED_OTHER, p =>
        {
            var r = p.Reader();
            EndCast(r.U64());
        });
        session.On(Opcode.SMSG_SPELL_DELAYED, p =>
        {
            var r = p.Reader();
            var caster = r.U64();
            var delay = r.U32();
            if (Casts.TryGetValue(caster, out var cast))
                Casts[caster] = cast with { DurationMs = cast.DurationMs + delay };
        });
        session.On(Opcode.MSG_CHANNEL_START, p =>
        {
            var r = p.Reader();
            var spell = r.U32();
            Casts[session.PlayerGuid] = new CastState(spell, session.NowMs, r.U32(), Channel: true);
        });
        session.On(Opcode.MSG_CHANNEL_UPDATE, p =>
        {
            var remaining = p.Reader().U32();
            if (remaining == 0)
                EndCast(session.PlayerGuid);
            else if (Casts.TryGetValue(session.PlayerGuid, out var cast))
                Casts[session.PlayerGuid] = cast with { DurationMs = (uint)(session.NowMs - cast.StartMs + remaining) };
        });
        session.On(Opcode.SMSG_SPELL_COOLDOWN, p =>
        {
            var r = p.Reader();
            if (r.U64() != session.PlayerGuid)
                return;
            while (r.Remaining >= 8)
            {
                var spell = r.U32();
                SetCooldown(spell, r.U32());
            }
        });
        session.On(Opcode.SMSG_CLEAR_COOLDOWN, p => Cooldowns.Remove(p.Reader().U32()));
        session.On(Opcode.SMSG_ITEM_COOLDOWN, _ => { });

        session.On(Opcode.SMSG_ATTACKSTART, p =>
        {
            var r = p.Reader();
            var attacker = r.U64();
            var victim = r.U64();
            Attacking[attacker] = victim;
            if (attacker == session.PlayerGuid)
                AutoAttacking = true;
        });
        session.On(Opcode.SMSG_ATTACKSTOP, p =>
        {
            var r = p.Reader();
            var attacker = r.PackedGuid();
            Attacking.Remove(attacker);
            if (attacker == session.PlayerGuid)
                AutoAttacking = false;
        });
        session.On(Opcode.SMSG_CANCEL_COMBAT, _ =>
        {
            Attacking.Remove(session.PlayerGuid);
            AutoAttacking = false;
        });
        session.On(Opcode.SMSG_ATTACKSWING_NOTINRANGE, _ => Error?.Invoke("ERR_BADATTACKPOS"));
        session.On(Opcode.SMSG_ATTACKSWING_BADFACING, _ => Error?.Invoke("ERR_BADATTACKFACING"));
        session.On(Opcode.SMSG_ATTACKSWING_NOTSTANDING, _ => Error?.Invoke("ERR_ATTACK_PACIFIED"));
        session.On(Opcode.SMSG_ATTACKSWING_DEADTARGET, _ => Error?.Invoke("ERR_INVALID_ATTACK_TARGET"));
        session.On(Opcode.SMSG_ATTACKSWING_CANT_ATTACK, _ => Error?.Invoke("ERR_INVALID_ATTACK_TARGET"));
        session.On(Opcode.SMSG_ATTACKERSTATEUPDATE, OnMeleeHit);
        session.On(Opcode.SMSG_SPELLNONMELEEDAMAGELOG, OnSpellDamage);
        session.On(Opcode.SMSG_PERIODICAURALOG, OnPeriodic);
        session.On(Opcode.SMSG_SPELLHEALLOG, p =>
        {
            var r = p.Reader();
            var target = r.PackedGuid();
            var healer = r.PackedGuid();
            var spell = r.U32();
            var amount = r.U32();
            Log(healer, target, (int)amount, spell, CombatKind.Heal, r.Remaining > 0 && r.U8() != 0, "");
        });
        session.On(Opcode.SMSG_SPELLENERGIZELOG, p =>
        {
            var r = p.Reader();
            var target = r.PackedGuid();
            var caster = r.PackedGuid();
            var spell = r.U32();
            r.U32();
            Log(caster, target, (int)r.U32(), spell, CombatKind.Energize, false, "");
        });
        session.On(Opcode.SMSG_ENVIRONMENTALDAMAGELOG, p =>
        {
            var r = p.Reader();
            var victim = r.U64();
            r.U8();
            Log(0, victim, (int)r.U32(), 0, CombatKind.Environment, false, "");
        });
        session.On(Opcode.SMSG_PARTYKILLLOG, _ => { });
        session.On(Opcode.SMSG_LOG_XPGAIN, p =>
        {
            var r = p.Reader();
            var victim = r.U64();
            var xp = r.U32();
            ExperienceGained?.Invoke(victim, xp);
        });
        session.On(Opcode.SMSG_LEVELUP_INFO, p => LevelUp?.Invoke(p.Reader().U32()));
        session.On(Opcode.SMSG_STANDSTATE_UPDATE, _ => { });

        session.On(Opcode.SMSG_ITEM_QUERY_SINGLE_RESPONSE, OnItemQuery);
        session.On(Opcode.SMSG_INVENTORY_CHANGE_FAILURE, p =>
        {
            var reason = p.Reader().U8();
            if (reason != 0)
                InventoryError?.Invoke(reason);
        });
        session.On(Opcode.SMSG_ITEM_PUSH_RESULT, p =>
        {
            var r = p.Reader();
            var player = r.U64();
            r.U32();
            r.U32();
            r.U32();
            r.U8();
            r.U32();
            var entry = r.U32();
            r.U32();
            r.U32();
            var count = r.U32();
            if (player != session.PlayerGuid)
                return;
            Item(entry);
            ItemReceived?.Invoke(entry, count);
        });

        session.On(Opcode.SMSG_LOOT_RESPONSE, OnLootResponse);
        session.On(Opcode.SMSG_LOOT_REMOVED, p =>
        {
            var slot = p.Reader().U8();
            Loot?.Items.RemoveAll(i => i.Index == slot);
        });
        session.On(Opcode.SMSG_LOOT_CLEAR_MONEY, _ =>
        {
            if (Loot is { } loot)
                loot.Gold = 0;
        });
        session.On(Opcode.SMSG_LOOT_MONEY_NOTIFY, p => MoneyLooted?.Invoke(p.Reader().U32()));
        session.On(Opcode.SMSG_LOOT_RELEASE_RESPONSE, _ => Loot = null);

        session.Objects.Removed += obj =>
        {
            Attacking.Remove(obj.Guid);
            Casts.Remove(obj.Guid);
            if (obj.Guid == Target)
                Target = 0;
        };
        session.EnteredWorld += _ => Reset();
        session.LoggedOut += Reset;
    }

    public ulong Target { get; private set; }
    public bool AutoAttacking { get; private set; }
    public IReadOnlyList<uint> Spells => _spells;
    /// <summary>Packed action bar buttons: action id in the low 24 bits, type (0 spell, 0x40 macro, 0x80 item) in the high byte.</summary>
    public uint[] ActionButtons { get; } = new uint[ActionButtonCount];
    /// <summary>Spell id to the session times (ms) its cooldown started and ends.</summary>
    public Dictionary<uint, (long Start, long End)> Cooldowns { get; } = [];
    /// <summary>Casts in progress by any unit in view, including the player.</summary>
    public Dictionary<ulong, CastState> Casts { get; } = [];
    /// <summary>Who is meleeing whom.</summary>
    public Dictionary<ulong, ulong> Attacking { get; } = [];
    public LootWindow? Loot { get; private set; }
    public CastState? PlayerCast => Casts.GetValueOrDefault(_session.PlayerGuid);

    public event Action<CombatEvent>? Combat;
    /// <summary>A unit swung (melee) at another; drives attack animations.</summary>
    public event Action<ulong, ulong>? Swing;
    /// <summary>A spell went off: caster, spell id.</summary>
    public event Action<ulong, uint>? SpellCast;
    /// <summary>A GlobalStrings key describing why an action failed.</summary>
    public event Action<string>? Error;
    /// <summary>A SpellCastResult code for the player's own cast.</summary>
    /// <summary>Spell, SpellCastResult and its two optional arguments (e.g. required item class and subclass mask).</summary>
    public event Action<uint, byte, uint, uint>? CastFailed;
    public event Action<byte>? InventoryError;
    public event Action<uint>? SpellLearned;
    public event Action<uint, uint>? ItemReceived;
    public event Action<uint>? MoneyLooted;
    public event Action<ulong, uint>? ExperienceGained;
    public event Action<uint>? LevelUp;
    public event Action<ItemTemplate>? ItemInfoReceived;

    private void Reset()
    {
        Target = 0;
        AutoAttacking = false;
        Casts.Clear();
        Attacking.Clear();
        Loot = null;
    }

    // ---- Selection and melee -------------------------------------------------------------------------------

    public void Select(ulong guid)
    {
        if (guid == Target)
            return;
        Target = guid;
        _session.Send(Opcode.CMSG_SET_SELECTION, new PacketWriter().U64(guid).ToArray());
    }

    public void StartAttack(ulong guid)
    {
        if (guid == 0)
            return;
        Select(guid);
        AutoAttacking = true;
        _session.Send(Opcode.CMSG_ATTACKSWING, new PacketWriter().U64(guid).ToArray());
    }

    public void StopAttack()
    {
        AutoAttacking = false;
        _session.Send(Opcode.CMSG_ATTACKSTOP, []);
    }

    /// <summary>0 stand, 1 sit (UNIT_STAND_STATE_*).</summary>
    public void SetStandState(uint state) => _session.Send(Opcode.CMSG_STANDSTATECHANGE, new PacketWriter().U32(state).ToArray());

    public void ReleaseSpirit() => _session.Send(Opcode.CMSG_REPOP_REQUEST, []);

    public void ReclaimCorpse() => _session.Send(Opcode.CMSG_RECLAIM_CORPSE, new PacketWriter().U64(_session.PlayerGuid).ToArray());

    public void Interact(ulong guid) => _session.Send(Opcode.CMSG_GOSSIP_HELLO, new PacketWriter().U64(guid).ToArray());

    public void UseGameObject(ulong guid) => _session.Send(Opcode.CMSG_GAMEOBJ_USE, new PacketWriter().U64(guid).ToArray());

    // ---- Spells --------------------------------------------------------------------------------------------

    /// <summary>Casts at <paramref name="target"/>, or at the caster when it is 0.</summary>
    public void Cast(uint spell, ulong target)
    {
        var w = new PacketWriter().U32(spell);
        WriteTargets(w, target);
        _session.Send(Opcode.CMSG_CAST_SPELL, w.ToArray());
    }

    public void CancelCast()
    {
        if (PlayerCast is { } cast)
            _session.Send(Opcode.CMSG_CANCEL_CAST, new PacketWriter().U32(cast.SpellId).ToArray());
    }

    public void SetActionButton(int button, uint packed)
    {
        ActionButtons[button] = packed;
        _session.Send(Opcode.CMSG_SET_ACTION_BUTTON, new PacketWriter().U8((byte)button).U32(packed).ToArray());
    }

    public void SetCooldown(uint spell, uint durationMs)
    {
        if (durationMs > 0)
            Cooldowns[spell] = (_session.NowMs, _session.NowMs + durationMs);
    }

    /// <summary>Seconds left on a spell's cooldown and the fraction of it remaining (0 when ready).</summary>
    public (float Seconds, float Fraction) Cooldown(uint spell)
    {
        if (!Cooldowns.TryGetValue(spell, out var cooldown))
            return (0, 0);
        var left = cooldown.End - _session.NowMs;
        if (left <= 0)
        {
            Cooldowns.Remove(spell);
            return (0, 0);
        }
        return (left / 1000f, left / (float)Math.Max(1, cooldown.End - cooldown.Start));
    }

    private static void WriteTargets(PacketWriter w, ulong target)
    {
        if (target == 0)
            w.U16(0);
        else
            w.U16((ushort)TargetUnit).PackedGuid(target);
    }

    private void OnInitialSpells(WorldPacket packet)
    {
        var r = packet.Reader();
        r.U8();
        _spells.Clear();
        var count = r.U16();
        for (var i = 0; i < count; i++)
        {
            _spells.Add(r.U16());
            r.U16();
        }
        var cooldowns = r.U16();
        for (var i = 0; i < cooldowns; i++)
        {
            var spell = r.U16();
            r.U16();
            r.U16();
            var recovery = r.I32();
            var category = r.I32();
            SetCooldown(spell, (uint)Math.Max(0, Math.Max(recovery, category)));
        }
    }

    private void OnCastResult(WorldPacket packet)
    {
        var r = packet.Reader();
        var spell = r.U32();
        if (r.U8() != 2)
            return;
        var reason = r.U8();
        var arg1 = r.Remaining >= 4 ? r.U32() : 0;
        var arg2 = r.Remaining >= 4 ? r.U32() : 0;
        EndCast(_session.PlayerGuid);
        CastFailed?.Invoke(spell, reason, arg1, arg2);
    }

    private void OnSpellStart(WorldPacket packet)
    {
        var r = packet.Reader();
        var caster = r.PackedGuid();
        r.PackedGuid();
        var spell = r.U32();
        r.U16();
        var time = r.U32();
        if (time > 0)
            Casts[caster] = new CastState(spell, _session.NowMs, time, Channel: false);
    }

    private void OnSpellGo(WorldPacket packet)
    {
        var r = packet.Reader();
        var caster = r.PackedGuid();
        r.PackedGuid();
        var spell = r.U32();
        r.U16();
        var hits = r.U8();
        r.Skip(hits * 8);
        var misses = r.U8();
        for (var i = 0; i < misses; i++)
        {
            var target = r.U64();
            var condition = r.U8();
            if (condition == 11)
                r.U8();
            Log(caster, target, 0, spell, CombatKind.Miss, false, SpellMissOutcome.ElementAtOrDefault(condition) ?? "MISS");
        }
        if (Casts.TryGetValue(caster, out var cast) && !cast.Channel)
            Casts.Remove(caster);
        SpellCast?.Invoke(caster, spell);
    }

    private void EndCast(ulong caster) => Casts.Remove(caster);

    private void OnMeleeHit(WorldPacket packet)
    {
        var r = packet.Reader();
        var hitInfo = r.U32();
        var attacker = r.PackedGuid();
        var victim = r.PackedGuid();
        var total = r.I32();
        var parts = r.U8();
        r.Skip(parts * 20);
        var victimState = r.U32();
        Swing?.Invoke(attacker, victim);
        var outcome = (hitInfo & HitMiss) != 0 ? "MISS" : VictimOutcome.ElementAtOrDefault((int)victimState) ?? "";
        Log(attacker, victim, total, 0, CombatKind.Melee, (hitInfo & HitCritical) != 0, outcome);
    }

    private void OnSpellDamage(WorldPacket packet)
    {
        var r = packet.Reader();
        var target = r.PackedGuid();
        var attacker = r.PackedGuid();
        var spell = r.U32();
        var damage = r.U32();
        r.U8();
        r.U32();
        r.I32();
        r.U8();
        r.U8();
        r.U32();
        var hitType = r.U32();
        Log(attacker, target, (int)damage, spell, CombatKind.Spell, (hitType & 0x2) != 0, "");
    }

    private void OnPeriodic(WorldPacket packet)
    {
        var r = packet.Reader();
        var target = r.PackedGuid();
        var caster = r.PackedGuid();
        var spell = r.U32();
        var count = r.U32();
        for (var i = 0; i < count; i++)
        {
            switch ((int)r.U32())
            {
                case AuraPeriodicDamage or AuraPeriodicDamagePercent:
                    var damage = r.U32();
                    r.Skip(12);
                    Log(caster, target, (int)damage, spell, CombatKind.Periodic, false, "");
                    break;
                case AuraPeriodicHeal or AuraObsModHealth:
                    Log(caster, target, (int)r.U32(), spell, CombatKind.Heal, false, "");
                    break;
                case AuraPeriodicEnergize:
                    r.U32();
                    Log(caster, target, (int)r.U32(), spell, CombatKind.Energize, false, "");
                    break;
                default:
                    return;
            }
        }
    }

    private void Log(ulong attacker, ulong victim, int amount, uint spell, CombatKind kind, bool critical, string outcome) =>
        Combat?.Invoke(new CombatEvent(attacker, victim, amount, spell, kind, critical, outcome, _session.NowMs));

    // ---- Items ---------------------------------------------------------------------------------------------

    /// <summary>Cached item data, querying the server the first time an entry is seen.</summary>
    public ItemTemplate? Item(uint entry)
    {
        if (entry == 0)
            return null;
        if (_items.TryGetValue(entry, out var item))
            return item;
        if (_itemQueries.Add(entry))
            _session.Send(Opcode.CMSG_ITEM_QUERY_SINGLE, new PacketWriter().U32(entry).U64(0).ToArray());
        return null;
    }

    /// <summary>The item object in an inventory slot of the player (bag 255) or of an equipped bag (bag 19..22).</summary>
    public WorldObject? ItemAt(byte bag, byte slot)
    {
        if (_session.Player is not { } player)
            return null;
        ulong guid;
        if (bag == InventoryBag)
            guid = player.Guid64(UpdateFields.PlayerInvSlotHead + slot * 2);
        else if (_session.Objects.Get(player.Guid64(UpdateFields.PlayerInvSlotHead + bag * 2)) is { } container)
            guid = container.Guid64(UpdateFields.ContainerSlot1 + slot * 2);
        else
            return null;
        return guid == 0 ? null : _session.Objects.Get(guid);
    }

    public int BagSize(byte bag) => bag == InventoryBag ? BackpackSize : (int)(ItemAt(InventoryBag, bag)?[UpdateFields.ContainerNumSlots] ?? 0);

    public void UseItem(byte bag, byte slot, ulong target = 0)
    {
        var w = new PacketWriter().U8(bag).U8(slot).U8(0);
        WriteTargets(w, target);
        _session.Send(Opcode.CMSG_USE_ITEM, w.ToArray());
    }

    public void AutoEquip(byte bag, byte slot) => _session.Send(Opcode.CMSG_AUTOEQUIP_ITEM, new PacketWriter().U8(bag).U8(slot).ToArray());

    /// <summary>Moves an item (e.g. something equipped) into the first free bag slot.</summary>
    public void StoreInBags(byte bag, byte slot) =>
        _session.Send(Opcode.CMSG_AUTOSTORE_BAG_ITEM, new PacketWriter().U8(bag).U8(slot).U8(InventoryBag).ToArray());

    public void SwapItem(byte sourceBag, byte sourceSlot, byte destinationBag, byte destinationSlot)
    {
        if (sourceBag == InventoryBag && destinationBag == InventoryBag)
            _session.Send(Opcode.CMSG_SWAP_INV_ITEM, new PacketWriter().U8(sourceSlot).U8(destinationSlot).ToArray());
        else
            _session.Send(Opcode.CMSG_SWAP_ITEM, new PacketWriter().U8(destinationBag).U8(destinationSlot).U8(sourceBag).U8(sourceSlot).ToArray());
    }

    public void DestroyItem(byte bag, byte slot, byte count) =>
        _session.Send(Opcode.CMSG_DESTROYITEM, new PacketWriter().U8(bag).U8(slot).U8(count).U8(0).U8(0).U8(0).ToArray());

    private void OnItemQuery(WorldPacket packet)
    {
        var r = packet.Reader();
        var entry = r.U32();
        if ((entry & 0x80000000) != 0)
            return;
        var itemClass = r.U32();
        var subClass = r.U32();
        var name = r.CString();
        r.CString();
        r.CString();
        r.CString();
        var display = r.U32();
        var quality = r.U32();
        var flags = r.U32();
        var buy = r.U32();
        var sell = r.U32();
        var inventoryType = r.U32();
        r.U32();
        r.U32();
        r.U32();
        var requiredLevel = r.U32();
        r.Skip(4 * 7);
        var maxCount = r.U32();
        var stackable = r.U32();
        var containerSlots = r.U32();
        var stats = new List<(uint, int)>();
        for (var i = 0; i < 10; i++)
        {
            var type = r.U32();
            var value = r.I32();
            if (value != 0)
                stats.Add((type, value));
        }
        var damage = new List<(float, float, uint)>();
        for (var i = 0; i < 5; i++)
        {
            var min = r.F32();
            var max = r.F32();
            var school = r.U32();
            if (max > 0)
                damage.Add((min, max, school));
        }
        var armor = r.I32();
        var resistances = new int[6];
        for (var i = 0; i < 6; i++)
            resistances[i] = r.I32();
        var delay = r.U32();
        r.U32();
        r.F32();
        var spells = new List<(uint, uint)>();
        for (var i = 0; i < 5; i++)
        {
            var spell = r.U32();
            var trigger = r.U32();
            r.Skip(4 * 4);
            if (spell != 0)
                spells.Add((spell, trigger));
        }
        var bonding = r.U32();
        var description = r.CString();
        r.Skip(4 * 10);
        var maxDurability = r.Remaining >= 4 ? r.U32() : 0;
        var item = new ItemTemplate(entry, itemClass, subClass, name, display, quality, flags, buy, sell, inventoryType, requiredLevel,
            maxCount, stackable, containerSlots, stats, damage, armor, resistances, delay, spells, bonding, description, maxDurability);
        _items[entry] = item;
        ItemInfoReceived?.Invoke(item);
    }

    // ---- Loot ----------------------------------------------------------------------------------------------

    public void OpenLoot(ulong guid) => _session.Send(Opcode.CMSG_LOOT, new PacketWriter().U64(guid).ToArray());

    public void TakeLoot(byte slot) => _session.Send(Opcode.CMSG_AUTOSTORE_LOOT_ITEM, new PacketWriter().U8(slot).ToArray());

    public void TakeLootMoney() => _session.Send(Opcode.CMSG_LOOT_MONEY, []);

    public void CloseLoot()
    {
        if (Loot is { } loot)
            _session.Send(Opcode.CMSG_LOOT_RELEASE, new PacketWriter().U64(loot.Guid).ToArray());
        Loot = null;
    }

    private void OnLootResponse(WorldPacket packet)
    {
        var r = packet.Reader();
        var guid = r.U64();
        var type = r.U8();
        if (type == 0)
        {
            Error?.Invoke("ERR_LOOT_DIDNT_KILL");
            return;
        }
        var gold = r.U32();
        var count = r.U8();
        var items = new List<LootSlot>();
        for (var i = 0; i < count; i++)
        {
            var index = r.U8();
            var entry = r.U32();
            var itemCount = r.U32();
            var display = r.U32();
            r.U32();
            r.U32();
            items.Add(new LootSlot(index, entry, itemCount, display, r.U8()));
            Item(entry);
        }
        Loot = new LootWindow(guid, gold, items);
    }
}
