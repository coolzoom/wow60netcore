using FrameXml.Lua;
using FrameXml.Objects;
using MoonSharp.Interpreter;
using Net;
using static FrameXml.Lua.LuaBindings;

namespace Client.Online;

public sealed partial class GameApi
{
    /// <summary>Inventory slot names of GetInventorySlotInfo, in slot order (id = index + 1), and their empty-slot art.</summary>
    private static readonly (string Name, string Texture)[] InventorySlots =
    [
        ("HeadSlot", "Head"), ("NeckSlot", "Neck"), ("ShoulderSlot", "Shoulder"), ("ShirtSlot", "Shirt"), ("ChestSlot", "Chest"),
        ("WaistSlot", "Waist"), ("LegsSlot", "Legs"), ("FeetSlot", "Feet"), ("WristSlot", "Wrists"), ("HandsSlot", "Hands"),
        ("Finger0Slot", "Finger"), ("Finger1Slot", "Finger"), ("Trinket0Slot", "Trinket"), ("Trinket1Slot", "Trinket"), ("BackSlot", "Chest"),
        ("MainHandSlot", "MainHand"), ("SecondaryHandSlot", "SecondaryHand"), ("RangedSlot", "Ranged"), ("TabardSlot", "Tabard"),
        ("Bag0Slot", "Bag"), ("Bag1Slot", "Bag"), ("Bag2Slot", "Bag"), ("Bag3Slot", "Bag"),
    ];
    private static readonly string[] InventoryTypeKeys =
    [
        "", "INVTYPE_HEAD", "INVTYPE_NECK", "INVTYPE_SHOULDER", "INVTYPE_BODY", "INVTYPE_CHEST", "INVTYPE_WAIST", "INVTYPE_LEGS", "INVTYPE_FEET",
        "INVTYPE_WRIST", "INVTYPE_HAND", "INVTYPE_FINGER", "INVTYPE_TRINKET", "INVTYPE_WEAPON", "INVTYPE_SHIELD", "INVTYPE_RANGED", "INVTYPE_CLOAK",
        "INVTYPE_2HWEAPON", "INVTYPE_BAG", "INVTYPE_TABARD", "INVTYPE_ROBE", "INVTYPE_WEAPONMAINHAND", "INVTYPE_WEAPONOFFHAND", "INVTYPE_HOLDABLE",
        "INVTYPE_AMMO", "INVTYPE_THROWN", "INVTYPE_RANGEDRIGHT", "INVTYPE_QUIVER", "INVTYPE_RELIC",
    ];
    private static readonly string[] QualityHex = ["ff9d9d9d", "ffffffff", "ff1eff00", "ff0070dd", "ffa335ee", "ffff8000", "ffe6cc80"];
    private static readonly Color4[] QualityColors =
    [
        new(0.62f, 0.62f, 0.62f), new(1, 1, 1), new(0.12f, 1, 0), new(0, 0.44f, 0.87f), new(0.64f, 0.21f, 0.93f), new(1, 0.5f, 0), new(0.9f, 0.8f, 0.5f),
    ];

    /// <summary>Lua bag id (0 backpack, 1-4 bags) and 1-based slot to the server's bag and slot.</summary>
    private static (byte Bag, byte Slot)? Container(int bag, int slot) => bag switch
    {
        0 when slot is >= 1 and <= Gameplay.BackpackSize => (Gameplay.InventoryBag, (byte)(Gameplay.BackpackStart + slot - 1)),
        >= 1 and <= 4 when slot >= 1 => ((byte)(Gameplay.BagSlotStart + bag - 1), (byte)(slot - 1)),
        _ => null,
    };

    /// <summary>Inventory slot id (1-19 equipment, 20-23 bags) to the server slot in the player's inventory.</summary>
    private static (byte Bag, byte Slot)? Inventory(int id) => id is >= 1 and <= 23 ? (Gameplay.InventoryBag, (byte)(id - 1)) : null;

    private (WorldObject Instance, ItemTemplate? Info)? ItemIn((byte Bag, byte Slot)? at) =>
        at is { } p && _play.ItemAt(p.Bag, p.Slot) is { } item ? (item, _play.Item(item.Entry)) : null;

    private string ItemIconOf(ItemTemplate? info) =>
        info is not null && _text.ItemIcon(info.DisplayId) is { } icon ? icon : @"Interface\Icons\INV_Misc_QuestionMark";

    public static string ItemLink(ItemTemplate item) =>
        $"|c{QualityHex[Math.Min(item.Quality, 6)]}|Hitem:{item.Entry}:0:0:0|h[{item.Name}]|h|r";

    private uint CountItem(uint entry)
    {
        uint total = 0;
        foreach (var (bag, slot) in BagPositions())
            if (_play.ItemAt(bag, slot) is { } item && item.Entry == entry)
                total += Math.Max(1, item[UpdateFields.ItemStackCount]);
        return total;
    }

    private (byte Bag, byte Slot)? FindItem(uint entry)
    {
        foreach (var (bag, slot) in BagPositions())
            if (_play.ItemAt(bag, slot)?.Entry == entry)
                return (bag, slot);
        return null;
    }

    private IEnumerable<(byte Bag, byte Slot)> BagPositions()
    {
        for (var i = 0; i < Gameplay.BackpackSize; i++)
            yield return (Gameplay.InventoryBag, (byte)(Gameplay.BackpackStart + i));
        for (byte bag = Gameplay.BagSlotStart; bag < Gameplay.BackpackStart; bag++)
            for (var i = 0; i < _play.BagSize(bag); i++)
                yield return (bag, (byte)i);
    }

    private void UseItem(byte bag, byte slot)
    {
        if (_play.ItemAt(bag, slot) is not { } item || _play.Item(item.Entry) is not { } info)
            return;
        if (info.InventoryType != 0 && info.Spells.Count == 0)
            _play.AutoEquip(bag, slot);
        else
            _play.UseItem(bag, slot, _play.Target);
    }

    /// <summary>
    /// Picking up an item puts it on the cursor; clicking another slot while holding one moves or swaps it there.
    /// </summary>
    private void PickupItem((byte Bag, byte Slot)? at)
    {
        if (at is not { } target)
            return;
        if (_cursorItem is { } held)
        {
            if (held != target)
                _play.SwapItem(held.Bag, held.Slot, target.Bag, target.Slot);
            ClearCursor();
            return;
        }
        if (_play.ItemAt(target.Bag, target.Slot) is null)
            return;
        _cursorAction = 0;
        _cursorItem = target;
        _ui.FireEvent("ITEM_LOCK_CHANGED");
    }

    private void RegisterItems()
    {
        Fn("GetContainerNumSlots", a => N(a.Int(0) == 0 ? Gameplay.BackpackSize : a.Int(0) is >= 1 and <= 4 ? _play.BagSize((byte)(Gameplay.BagSlotStart + a.Int(0) - 1)) : 0));
        Fn("GetContainerItemInfo", a =>
        {
            var at = Container(a.Int(0), a.Int(1));
            if (ItemIn(at) is not { } item)
                return DynValue.Nil;
            return Tuple(S(ItemIconOf(item.Info)), N(Math.Max(1, item.Instance[UpdateFields.ItemStackCount])), B(_cursorItem == at),
                N(item.Info?.Quality ?? 1), DynValue.Nil);
        });
        Fn("GetContainerItemLink", a => ItemIn(Container(a.Int(0), a.Int(1)))?.Info is { } info ? S(ItemLink(info)) : DynValue.Nil);
        Fn("GetContainerItemCooldown", _ => Tuple(N(0), N(0), N(0)));
        Do("UseContainerItem", a =>
        {
            if (Container(a.Int(0), a.Int(1)) is { } at)
                UseItem(at.Bag, at.Slot);
        });
        Do("PickupContainerItem", a => PickupItem(Container(a.Int(0), a.Int(1))));
        Do("SplitContainerItem", a => PickupItem(Container(a.Int(0), a.Int(1))));
        Fn("GetBagName", a => a.Int(0) == 0 ? S(_text["BACKPACK_TOOLTIP"]) : ItemIn(Inventory(19 + a.Int(0)))?.Info is { } bag ? S(bag.Name) : DynValue.Nil);
        Fn("ContainerIDToInventoryID", a => N(19 + a.Int(0)));
        Fn("GetInventorySlotInfo", a =>
        {
            var index = Array.FindIndex(InventorySlots, s => s.Name.Equals(a.Str(0), StringComparison.OrdinalIgnoreCase));
            return index < 0
                ? DynValue.Nil
                : Tuple(N(index + 1), S($@"Interface\Paperdoll\UI-PaperDoll-Slot-{InventorySlots[index].Texture}"), DynValue.Nil);
        });
        Fn("GetInventoryItemTexture", a => Unit(a.Str(0)) is { } u && u.Guid == _session.PlayerGuid && ItemIn(Inventory(a.Int(1))) is { } item
            ? S(ItemIconOf(item.Info))
            : DynValue.Nil);
        Fn("GetInventoryItemCount", a => ItemIn(Inventory(a.Int(1))) is { } item ? N(Math.Max(1, item.Instance[UpdateFields.ItemStackCount])) : N(0));
        Fn("GetInventoryItemQuality", a => ItemIn(Inventory(a.Int(1)))?.Info is { } info ? N(info.Quality) : DynValue.Nil);
        Fn("GetInventoryItemLink", a => ItemIn(Inventory(a.Int(1)))?.Info is { } info ? S(ItemLink(info)) : DynValue.Nil);
        Fn("GetInventoryItemCooldown", _ => Tuple(N(0), N(0), N(0)));
        Fn("GetInventoryItemBroken", a => B(ItemIn(Inventory(a.Int(1))) is { } item && item.Instance[UpdateFields.ItemMaxDurability] > 0 &&
                                            item.Instance[UpdateFields.ItemDurability] == 0));
        Fn("IsInventoryItemLocked", a => B(_cursorItem is { } held && held == Inventory(a.Int(0))));
        Do("PickupInventoryItem", a => PickupItem(Inventory(a.Int(0))));
        Do("PickupBagFromSlot", a => PickupItem(Inventory(a.Int(0))));
        Do("UseInventoryItem", a =>
        {
            if (Inventory(a.Int(0)) is { } at)
                _play.UseItem(at.Bag, at.Slot, _play.Target);
        });
        Do("EquipCursorItem", a => PickupItem(Inventory(a.Int(0))));
        Do("AutoEquipCursorItem", _ =>
        {
            if (_cursorItem is { } held)
                _play.AutoEquip(held.Bag, held.Slot);
            ClearCursor();
        });
        Do("PutItemInBackpack", _ => StoreCursorItem());
        Do("PutItemInBag", _ => StoreCursorItem());
        Fn("HasKey", _ => DynValue.Nil);
        Fn("GetItemInfo", a =>
        {
            var text = a.Str(0) ?? "";
            var start = text.IndexOf("item:", StringComparison.Ordinal);
            var digits = new string(text[(start < 0 ? 0 : start + 5)..].TakeWhile(char.IsDigit).ToArray());
            if (!uint.TryParse(digits, out var entry) || _play.Item(entry) is not { } info)
                return DynValue.Nil;
            return Tuple(S(info.Name), S($"item:{entry}:0:0:0"), N(info.Quality), N(info.RequiredLevel), S(_text.ItemKind(info.Class, 0)),
                S(_text.ItemKind(info.Class, 1u << (int)info.SubClass)), N(Math.Max(1, info.Stackable)),
                S(InventoryTypeKeys.ElementAtOrDefault((int)info.InventoryType) ?? ""), S(ItemIconOf(info)));
        });
        Fn("GetItemQualityColor", a => QualityColors.ElementAtOrDefault(a.Int(0)) is var c
            ? Tuple(N(c.R), N(c.G), N(c.B), S("|c" + QualityHex[Math.Clamp(a.Int(0), 0, 6)]))
            : DynValue.Nil);
        Fn("GetRepairAllCost", _ => Tuple(N(0), DynValue.Nil));
        Fn("InRepairMode", _ => DynValue.Nil);
        Fn("GetNumBankSlots", _ => Tuple(N(0), DynValue.Nil));
    }

    private void StoreCursorItem()
    {
        if (_cursorItem is { } held)
            _play.StoreInBags(held.Bag, held.Slot);
        ClearCursor();
    }

    // ---- Loot ----------------------------------------------------------------------------------------------

    /// <summary>Loot window rows as the UI numbers them: coins first (when any), then the items.</summary>
    private List<(bool Coin, LootSlot? Item)> LootRows()
    {
        var rows = new List<(bool, LootSlot?)>();
        if (_play.Loot is not { } loot)
            return rows;
        if (loot.Gold > 0)
            rows.Add((true, null));
        rows.AddRange(loot.Items.Select(i => (false, (LootSlot?)i)));
        return rows;
    }

    private void RegisterLoot()
    {
        Fn("GetNumLootItems", _ => N(LootRows().Count));
        Fn("GetLootSlotInfo", a =>
        {
            var (coin, slot) = LootRows().ElementAtOrDefault(a.Int(0) - 1);
            if (!coin && slot is null)
                return DynValue.Nil;
            if (coin)
                return Tuple(S(@"Interface\Icons\INV_Misc_Coin_01"), S(Money(_play.Loot!.Gold).Replace(" ", "\n")), N(0), N(0));
            var info = _play.Item(slot!.Item);
            return Tuple(S(info is null ? _text.ItemIcon(slot.DisplayId) ?? @"Interface\Icons\INV_Misc_QuestionMark" : ItemIconOf(info)),
                S(info?.Name ?? ""), N(slot.Count), N(info?.Quality ?? 1));
        });
        Fn("LootSlotIsItem", a => B(LootRows().ElementAtOrDefault(a.Int(0) - 1) is (false, not null)));
        Fn("LootSlotIsCoin", a => B(LootRows().ElementAtOrDefault(a.Int(0) - 1) is (true, _)));
        Fn("GetLootSlotLink", a => LootRows().ElementAtOrDefault(a.Int(0) - 1) is (false, { } slot) && _play.Item(slot.Item) is { } info
            ? S(ItemLink(info))
            : DynValue.Nil);
        Do("LootSlot", a =>
        {
            switch (LootRows().ElementAtOrDefault(a.Int(0) - 1))
            {
                case (true, _): _play.TakeLootMoney(); break;
                case (false, { } slot): _play.TakeLoot(slot.Index); break;
            }
        });
        Do("CloseLoot", _ => _play.CloseLoot());
        Fn("IsFishingLoot", _ => DynValue.Nil);
        Fn("GetLootThreshold", _ => N(2));
        Fn("GetLootMethod", _ => Tuple(S("freeforall"), DynValue.Nil, DynValue.Nil));
    }

    public string Money(uint copper)
    {
        var parts = new List<string>();
        if (copper >= 10000) parts.Add($"{copper / 10000}金");
        if (copper % 10000 >= 100) parts.Add($"{copper % 10000 / 100}银");
        if (copper % 100 > 0 || parts.Count == 0) parts.Add($"{copper % 100}铜");
        return string.Join(" ", parts);
    }

    // ---- Chat ----------------------------------------------------------------------------------------------

    /// <summary>Message groups (ChatTypeGroup keys) of the default chat windows: 1 general, 2 combat log.</summary>
    private static readonly string[][] ChatWindowMessages =
    [
        ["SYSTEM", "SAY", "YELL", "WHISPER", "PARTY", "GUILD", "CREATURE", "CHANNEL", "SKILL", "LOOT", "COMBAT_XP_GAIN", "COMBAT_HONOR_GAIN",
         "COMBAT_FACTION_CHANGE", "COMBAT_MISC_INFO", "SPELL_FAILED_LOCALPLAYER", "COMBAT_ERROR"],
        ["COMBAT_SELF_HITS", "COMBAT_SELF_MISSES", "COMBAT_CREATURE_VS_SELF_HITS", "COMBAT_CREATURE_VS_SELF_MISSES", "SPELL_SELF_DAMAGE",
         "SPELL_SELF_BUFF", "SPELL_CREATURE_VS_SELF_DAMAGE", "SPELL_CREATURE_VS_SELF_BUFF", "SPELL_PERIODIC_SELF_DAMAGE", "SPELL_PERIODIC_SELF_BUFFS",
         "SPELL_PERIODIC_CREATURE_DAMAGE", "SPELL_CREATURE_VS_CREATURE_DAMAGE", "COMBAT_CREATURE_VS_CREATURE_HITS", "COMBAT_CREATURE_VS_CREATURE_MISSES",
         "COMBAT_FRIENDLY_DEATH", "COMBAT_HOSTILE_DEATH", "SPELL_AURA_GONE_SELF", "SPELL_AURA_GONE_OTHER", "COMBAT_XP_GAIN"],
    ];

    private void RegisterChat()
    {
        Fn("GetChatWindowInfo", a => a.Int(0) switch
        {
            1 => Tuple(S(_text["GENERAL"]), N(0), N(0), N(0), N(0), N(0.4), B(true), DynValue.Nil, N(1)),
            2 => Tuple(S(_text["COMBAT_LOG"]), N(0), N(0), N(0), N(0), N(0.4), B(true), DynValue.Nil, N(2)),
            >= 3 and <= 7 => Tuple(S(""), N(0), N(0), N(0), N(0), N(0.4), DynValue.Nil, DynValue.Nil, DynValue.Nil),
            _ => DynValue.Nil,
        });
        Fn("GetChatWindowMessages", a => ChatWindowMessages.ElementAtOrDefault(a.Int(0) - 1) is { } groups ? Tuple(groups.Select(S).ToArray()) : DynValue.Nil);
        Fn("GetChatWindowChannels", _ => DynValue.Nil);
        Fn("GetChatTypeIndex", _ => N(0));
        Fn("GetChannelName", _ => Tuple(N(0), DynValue.Nil));
        Fn("GetChannelList", _ => DynValue.Nil);
        foreach (var name in new[] { "SetChatWindowName", "SetChatWindowSize", "SetChatWindowColor", "SetChatWindowAlpha", "SetChatWindowShown",
                     "SetChatWindowLocked", "SetChatWindowDocked", "SetChatWindowUninteractable", "AddChatWindowMessages", "RemoveChatWindowMessages",
                     "AddChatWindowChannel", "RemoveChatWindowChannel", "ChangeChatColor", "ResetChatColors", "ResetChatWindows",
                     "JoinChannelByName", "LeaveChannelByName", "LoggingChat", "LoggingCombat" })
            Do(name, _ => { });
        Do("SendChatMessage", a =>
        {
            var text = a.Str(0) ?? "";
            if (text.Length == 0)
                return;
            var type = (a.Str(1) ?? "SAY").ToUpperInvariant() switch
            {
                "YELL" => ChatMessage.Yell,
                "EMOTE" => ChatMessage.Emote,
                "PARTY" or "RAID" => ChatMessage.Party,
                "GUILD" or "OFFICER" => ChatMessage.Guild,
                "WHISPER" => ChatMessage.Whisper,
                _ => ChatMessage.Say,
            };
            _session.SendChat(type, text, type == ChatMessage.Whisper ? a.Str(3) : null);
        });
        Do("DoEmote", _ => { });
        Do("RandomRoll", _ => { });
    }

    /// <summary>CHAT_MSG_* event of a server chat message.</summary>
    public static string? ChatEvent(ChatMessage message) => message.Type switch
    {
        ChatMessage.Say => "CHAT_MSG_SAY",
        ChatMessage.Party => "CHAT_MSG_PARTY",
        ChatMessage.Guild => "CHAT_MSG_GUILD",
        ChatMessage.Yell => "CHAT_MSG_YELL",
        ChatMessage.Whisper => "CHAT_MSG_WHISPER",
        ChatMessage.WhisperInform => "CHAT_MSG_WHISPER_INFORM",
        ChatMessage.Emote => "CHAT_MSG_EMOTE",
        ChatMessage.TextEmote => "CHAT_MSG_TEXT_EMOTE",
        ChatMessage.System => "CHAT_MSG_SYSTEM",
        ChatMessage.MonsterSay => "CHAT_MSG_MONSTER_SAY",
        ChatMessage.MonsterYell => "CHAT_MSG_MONSTER_YELL",
        ChatMessage.MonsterEmote => "CHAT_MSG_MONSTER_EMOTE",
        ChatMessage.MonsterWhisper => "CHAT_MSG_MONSTER_WHISPER",
        ChatMessage.ChannelMessage => "CHAT_MSG_CHANNEL",
        _ => null,
    };

    // ---- Tooltips ------------------------------------------------------------------------------------------

    private static readonly Color4 White = new(1, 1, 1), Gold = new(1, 0.82f, 0), Green = new(0.12f, 1, 0), Red = new(1, 0.13f, 0.13f);

    private void RegisterTooltips()
    {
        var bindings = _ui.Bindings;
        bindings.Define<GameTooltip>("SetAction", (t, a) =>
        {
            var packed = ActionAt(a.Int(0));
            if (packed >> 24 == 0 && packed != 0 && _text.Spell(packed & 0xFFFFFF) is { } spell)
                SpellTooltip(t, spell);
            else if (packed >> 24 == 0x80 && _play.Item(packed & 0xFFFFFF) is { } item)
                ItemTooltip(t, item, null);
            return B(packed != 0);
        });
        bindings.Define<GameTooltip>("SetSpell", (t, a) =>
        {
            if (BookSpell(a) is { } spell)
                SpellTooltip(t, spell);
            return DynValue.Nil;
        });
        bindings.Define<GameTooltip>("SetBagItem", (t, a) =>
        {
            if (ItemIn(Container(a.Int(0), a.Int(1))) is { Info: { } info } item)
                ItemTooltip(t, info, item.Instance);
            return B(true);
        });
        bindings.Define<GameTooltip>("SetInventoryItem", (t, a) =>
        {
            if (ItemIn(Inventory(a.Int(1))) is { Info: { } info } item)
            {
                ItemTooltip(t, info, item.Instance);
                return Tuple(B(true), DynValue.Nil, N(0));
            }
            return DynValue.Nil;
        });
        bindings.Define<GameTooltip>("SetLootItem", (t, a) =>
        {
            if (LootRows().ElementAtOrDefault(a.Int(0) - 1) is (false, { } slot) && _play.Item(slot.Item) is { } info)
                ItemTooltip(t, info, null);
            return DynValue.Nil;
        });
        bindings.Define<GameTooltip>("SetHyperlink", (t, a) =>
        {
            var link = a.Str(0) ?? "";
            var start = link.IndexOf("item:", StringComparison.Ordinal);
            var digits = new string(link[(start < 0 ? 0 : start + 5)..].TakeWhile(char.IsDigit).ToArray());
            if (uint.TryParse(digits, out var entry) && _play.Item(entry) is { } info)
                ItemTooltip(t, info, null);
            return DynValue.Nil;
        });
        bindings.Define<GameTooltip>("SetUnit", (t, a) =>
        {
            if (Unit(a.Str(0)) is { } unit)
                UnitTooltip(t, unit);
            return DynValue.Nil;
        });
        bindings.Define<GameTooltip>("SetUnitBuff", (t, a) => AuraTooltip(t, AuraAt(a.Str(0), a.Int(1), helpful: true)));
        bindings.Define<GameTooltip>("SetUnitDebuff", (t, a) => AuraTooltip(t, AuraAt(a.Str(0), a.Int(1), helpful: false)));
        bindings.Define<GameTooltip>("SetPlayerBuff", (t, a) => AuraTooltip(t, AuraSpell(a.Int(0)) ?? 0));
        foreach (var name in new[] { "SetShapeshift", "SetPetAction", "SetQuestItem", "SetQuestLogItem", "SetMerchantItem", "SetBuybackItem",
                     "SetTradePlayerItem", "SetTradeTargetItem", "SetCraftItem", "SetCraftSpell", "SetTrainerService", "SetTradeSkillItem",
                     "SetInboxItem", "SetSendMailItem", "SetAuctionItem", "SetAuctionSellItem", "SetTalent", "SetSkillLine", "SetTrackingSpell",
                     "SetQuestRewardSpell", "SetQuestLogRewardSpell", "SetInventoryItemByID", "SetMoney" })
            bindings.Define<GameTooltip>(name, (_, _) => DynValue.Nil);
        bindings.Define<GameTooltip>("IsEquippedItem", (_, _) => DynValue.Nil);
    }

    private DynValue AuraTooltip(GameTooltip t, uint spellId)
    {
        if (_text.Spell(spellId) is { } spell)
        {
            t.Lines.Clear();
            t.AddLine(spell.Name, null, Gold, White);
            if (spell.Description.Length > 0)
                t.AddLine(spell.Description, null, White, White, wrap: true);
        }
        return DynValue.Nil;
    }

    private void UnitTooltip(GameTooltip t, WorldObject unit)
    {
        t.Lines.Clear();
        var color = unit.Type == ObjectType.Player ? White : ReactionOf(unit) switch
        {
            Reaction.Hostile => new Color4(1, 0.1f, 0.1f),
            Reaction.Neutral => new Color4(1, 1, 0),
            _ => new Color4(0.1f, 1, 0.1f),
        };
        t.AddLine(unit.Name ?? "", null, color, White);
        var level = unit.Level > 0 ? unit.Level.ToString() : "??";
        if (unit.Type == ObjectType.Player)
            t.AddLine(_text.Format("TOOLTIP_UNIT_LEVEL_CLASS", level, $"{_data.Race(unit.Race)?.Name} {_data.Class(unit.Class)?.Name}".Trim()), null, White, White);
        else
            t.AddLine(_text.Format("TOOLTIP_UNIT_LEVEL", level), null, White, White);
        if (unit.IsDead)
            t.AddLine(_text["CORPSE"], null, new Color4(0.5f, 0.5f, 0.5f), White);
    }

    private void SpellTooltip(GameTooltip t, SpellInfo spell)
    {
        t.Lines.Clear();
        t.AddLine(spell.Name, spell.Rank.Length > 0 ? spell.Rank : null, White, new Color4(0.5f, 0.5f, 0.5f));
        if (spell.PowerCost > 0 || spell.MaxRange > 0)
        {
            var cost = spell.PowerCost > 0
                ? _text.Format(spell.PowerType switch { 1 => "RAGE_COST", 3 => "ENERGY_COST", _ => "MANA_COST" }, spell.PowerType == 1 ? spell.PowerCost / 10 : spell.PowerCost)
                : "";
            var range = spell.MaxRange > 5 ? _text.Format("SPELL_RANGE", spell.MaxRange.ToString("0")) : null;
            t.AddLine(cost, range, White, White);
        }
        if (!spell.Passive)
        {
            var cast = spell.CastTimeMs > 0 ? _text.Format("SPELL_CAST_TIME_SEC", spell.CastTimeMs / 1000f) : _text["SPELL_CAST_TIME_INSTANT"];
            var recast = spell.RecoveryMs >= 60000 ? _text.Format("SPELL_RECAST_TIME_MIN", spell.RecoveryMs / 60000f)
                : spell.RecoveryMs > 0 ? _text.Format("SPELL_RECAST_TIME_SEC", spell.RecoveryMs / 1000f) : null;
            t.AddLine(cast, recast, White, White);
        }
        if (spell.Description.Length > 0)
            t.AddLine(spell.Description, null, Gold, White, wrap: true);
    }

    private void ItemTooltip(GameTooltip t, ItemTemplate item, WorldObject? instance)
    {
        t.Lines.Clear();
        t.AddLine(item.Name, null, QualityColors[Math.Min(item.Quality, 6)], White);
        if (instance is not null && (instance[UpdateFields.ItemFlags] & 1) != 0)
            t.AddLine(_text["ITEM_SOULBOUND"], null, White, White);
        else if (item.Bonding is >= 1 and <= 3)
            t.AddLine(_text[item.Bonding switch { 1 => "ITEM_BIND_ON_PICKUP", 2 => "ITEM_BIND_ON_EQUIP", _ => "ITEM_BIND_ON_USE" }], null, White, White);
        if (item.MaxCount == 1)
            t.AddLine(_text["ITEM_UNIQUE"], null, White, White);
        if (item.InventoryType > 0 && item.InventoryType < InventoryTypeKeys.Length)
            t.AddLine(_text[InventoryTypeKeys[item.InventoryType]], item.Class is 2 or 4 ? _text.ItemKind(item.Class, 1u << (int)item.SubClass) : null, White, White);
        foreach (var (min, max, _) in item.Damage.Take(1))
        {
            t.AddLine(_text.Format("DAMAGE_TEMPLATE", (int)min, (int)max), item.Delay > 0 ? $"{_text["SPEED"]} {item.Delay / 1000f:0.00}" : null, White, White);
            if (item.Delay > 0)
                t.AddLine(_text.Format("DPS_TEMPLATE", (min + max) / 2 / (item.Delay / 1000f)), null, White, White);
        }
        if (item.Armor > 0)
            t.AddLine(_text.Format("ARMOR_TEMPLATE", item.Armor), null, White, White);
        foreach (var (type, value) in item.Stats)
        {
            var key = type switch { 0 => "ITEM_MOD_MANA", 1 => "ITEM_MOD_HEALTH", 3 => "ITEM_MOD_AGILITY", 4 => "ITEM_MOD_STRENGTH",
                5 => "ITEM_MOD_INTELLECT", 6 => "ITEM_MOD_SPIRIT", 7 => "ITEM_MOD_STAMINA", _ => null };
            if (key is not null)
                t.AddLine(_text.Format(key, value, value), null, White, White);
        }
        if (instance is not null && instance[UpdateFields.ItemMaxDurability] > 0)
            t.AddLine(_text.Format("DURABILITY_TEMPLATE", instance[UpdateFields.ItemDurability], instance[UpdateFields.ItemMaxDurability]), null, White, White);
        if (item.RequiredLevel > 1)
            t.AddLine(_text.Format("ITEM_MIN_LEVEL", item.RequiredLevel), null, (_session.Player?.Level ?? 0) >= item.RequiredLevel ? White : Red, White);
        foreach (var (spellId, trigger) in item.Spells)
        {
            if (_text.Spell(spellId) is not { Description.Length: > 0 } spell)
                continue;
            var prefix = _text[trigger switch { 1 => "ITEM_SPELL_TRIGGER_ONEQUIP", 2 => "ITEM_SPELL_TRIGGER_ONPROC", _ => "ITEM_SPELL_TRIGGER_ONUSE" }];
            t.AddLine(prefix + spell.Description, null, Green, White, wrap: true);
        }
        if (item.Description.Length > 0)
            t.AddLine($"“{item.Description}”", null, Gold, White, wrap: true);
        if (item.SellPrice > 0)
            t.AddLine($"{_text["SALE_PRICE_COLON"]}{Money(item.SellPrice)}", null, White, White);
    }

    // ---- Widgets the engine drives ---------------------------------------------------------------------------

    private void RegisterWidgets()
    {
        var bindings = _ui.Bindings;
        bindings.Define<Minimap>("GetZoom", (_, _) => N(0));
        bindings.Define<Minimap>("GetZoomLevels", (_, _) => N(5));
        bindings.Define<Minimap>("GetPingPosition", (_, _) => Tuple(N(0), N(0)));
        foreach (var name in new[] { "SetZoom", "SetMaskTexture", "SetIconTexture", "SetBlipTexture", "SetArrowModel", "SetPlayerModel", "PingLocation",
                     "SetPlayerTexture", "SetPlayerTextureHeight", "SetPlayerTextureWidth", "SetClassBlipTexture", "SetPOIArrowTexture", "SetCorpsePOIArrowTexture" })
            bindings.Define<Minimap>(name, (_, _) => DynValue.Nil);
        bindings.Define<FrameXml.Objects.Model>("SetUnit", (_, _) => DynValue.Nil);
        bindings.Define<LootButton>("SetSlot", (_, _) => DynValue.Nil);

        Do("SetPortraitTexture", a =>
        {
            if (bindings.ObjectOf<FrameXml.Objects.Texture>(a[0]) is not { } texture)
                return;
            var unit = Unit(a.Str(1));
            var file = unit switch
            {
                null => @"Interface\CharacterFrame\TemporaryPortrait",
                { Type: ObjectType.Player } p when _data.Race(p.Race) is { } race =>
                    $@"Interface\CharacterFrame\TemporaryPortrait-{(p.Gender == 0 ? "Male" : "Female")}-{race.FileString}",
                _ => @"Interface\CharacterFrame\TemporaryPortrait-Monster",
            };
            texture.SetTexture(file);
        });
    }
}
