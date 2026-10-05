using System.Numerics;
using Client.Ui;
using Formats.Mpq;
using FrameXml;
using FrameXml.Objects;
using ImGuiNET;
using Net;
using Silk.NET.Input;
using Silk.NET.OpenGL;

namespace Client.Online;

/// <summary>
/// The in-game interface the way WoW.exe builds it: Interface\FrameXML from the MPQs, run against <see cref="GameApi"/>,
/// fed with the events the client fires as the game state changes, and drawn over the world by <see cref="GlueRenderer"/>.
/// </summary>
public sealed class InGameUi : IDisposable
{
    private const float ClickSlop = 5f;

    private static readonly Dictionary<Key, string> KeyNames = new()
    {
        [Key.Enter] = "ENTER", [Key.KeypadEnter] = "ENTER", [Key.Escape] = "ESCAPE", [Key.Tab] = "TAB", [Key.Backspace] = "BACKSPACE",
        [Key.Left] = "LEFT", [Key.Right] = "RIGHT", [Key.Up] = "UP", [Key.Down] = "DOWN", [Key.Home] = "HOME", [Key.End] = "END",
        [Key.Delete] = "DELETE", [Key.Space] = "SPACE", [Key.PrintScreen] = "PRINTSCREEN", [Key.PageUp] = "PAGEUP", [Key.PageDown] = "PAGEDOWN",
        [Key.Minus] = "-", [Key.Equal] = "=", [Key.Slash] = "/", [Key.Number0] = "0", [Key.Number1] = "1", [Key.Number2] = "2",
        [Key.Number3] = "3", [Key.Number4] = "4", [Key.Number5] = "5", [Key.Number6] = "6", [Key.Number7] = "7", [Key.Number8] = "8",
        [Key.Number9] = "9",
    };
    private static readonly HashSet<Key> ModifierKeys =
        [Key.ShiftLeft, Key.ShiftRight, Key.ControlLeft, Key.ControlRight, Key.AltLeft, Key.AltRight, Key.SuperLeft, Key.SuperRight];

    /// <summary>InventoryResult codes to GlobalStrings keys.</summary>
    private static readonly string[] InventoryErrors =
    [
        "", "ERR_CANT_EQUIP_LEVEL_I", "ERR_CANT_EQUIP_SKILL", "ERR_WRONG_SLOT", "ERR_BAG_FULL", "ERR_BAG_IN_BAG", "ERR_TRADE_EQUIPPED_BAG",
        "ERR_AMMO_ONLY", "ERR_PROFICIENCY_NEEDED", "ERR_NO_SLOT_AVAILABLE", "ERR_CANT_EQUIP_EVER", "ERR_CANT_EQUIP_EVER", "ERR_NO_SLOT_AVAILABLE",
        "ERR_2HANDED_EQUIPPED", "ERR_2HSKILLNOTFOUND", "ERR_WRONG_BAG_TYPE", "ERR_WRONG_BAG_TYPE", "ERR_ITEM_MAX_COUNT", "ERR_NO_SLOT_AVAILABLE",
        "ERR_CANT_STACK", "ERR_NOT_EQUIPPABLE", "ERR_CANT_SWAP", "ERR_SLOT_EMPTY", "ERR_ITEM_NOT_FOUND", "ERR_DROP_BOUND_ITEM", "ERR_OUT_OF_RANGE",
        "ERR_TOO_FEW_TO_SPLIT", "ERR_SPLIT_FAILED", "ERR_SPELL_FAILED_REAGENTS_GENERIC", "ERR_NOT_ENOUGH_MONEY", "ERR_NOT_A_BAG",
        "ERR_DESTROY_NONEMPTY_BAG", "ERR_NOT_OWNER", "ERR_ONLY_ONE_QUIVER", "ERR_NO_BANK_SLOT", "ERR_NO_BANK_HERE", "ERR_ITEM_LOCKED",
        "ERR_GENERIC_STUNNED", "ERR_PLAYER_DEAD", "ERR_CLIENT_LOCKED_OUT", "ERR_INTERNAL_BAG_ERROR", "ERR_ONLY_ONE_BOLT", "ERR_ONLY_ONE_AMMO",
        "ERR_CANT_WRAP_STACKABLE", "ERR_CANT_WRAP_EQUIPPED", "ERR_CANT_WRAP_WRAPPED", "ERR_CANT_WRAP_BOUND", "ERR_CANT_WRAP_UNIQUE",
        "ERR_CANT_WRAP_BAGS", "ERR_LOOT_GONE", "ERR_INV_FULL", "ERR_BAG_FULL", "ERR_VENDOR_SOLD_OUT", "ERR_BAG_FULL", "ERR_ITEM_NOT_FOUND",
        "ERR_CANT_STACK", "ERR_BAG_FULL", "ERR_VENDOR_SOLD_OUT", "ERR_OBJECT_IS_BUSY", "ERR_CANT_BE_DISENCHANTED", "ERR_NOT_IN_COMBAT",
        "ERR_NOT_WHILE_DISARMED", "ERR_BAG_FULL", "ERR_CANT_EQUIP_RANK", "ERR_CANT_EQUIP_REPUTATION", "ERR_TOO_MANY_SPECIAL_BAGS",
        "ERR_LOOT_CANT_LOOT_THAT_NOW",
    ];
    /// <summary>SpellCastResult codes of the 1.12.1 client, as SPELL_FAILED_* suffixes.</summary>
    private static readonly string[] CastErrors =
    [
        "AFFECTING_COMBAT", "ALREADY_AT_FULL_HEALTH", "ALREADY_AT_FULL_POWER", "ALREADY_BEING_TAMED", "ALREADY_HAVE_CHARM", "ALREADY_HAVE_SUMMON",
        "ALREADY_OPEN", "AURA_BOUNCED", "AUTOTRACK_INTERRUPTED", "BAD_IMPLICIT_TARGETS", "BAD_TARGETS", "CANT_BE_CHARMED", "CANT_BE_DISENCHANTED",
        "CANT_BE_PROSPECTED", "CANT_CAST_ON_TAPPED", "CANT_DUEL_WHILE_INVISIBLE", "CANT_DUEL_WHILE_STEALTHED", "CANT_STEALTH", "CASTER_AURASTATE",
        "CASTER_DEAD", "CHARMED", "CHEST_IN_USE", "CONFUSED", "DONT_REPORT", "EQUIPPED_ITEM", "EQUIPPED_ITEM_CLASS", "EQUIPPED_ITEM_CLASS_MAINHAND",
        "EQUIPPED_ITEM_CLASS_OFFHAND", "ERROR", "FIZZLE", "FLEEING", "FOOD_LOWLEVEL", "HIGHLEVEL", "HUNGER_SATIATED", "IMMUNE", "INTERRUPTED",
        "INTERRUPTED_COMBAT", "ITEM_ALREADY_ENCHANTED", "ITEM_GONE", "ITEM_NOT_FOUND", "ITEM_NOT_READY", "LEVEL_REQUIREMENT", "LINE_OF_SIGHT",
        "LOWLEVEL", "LOW_CASTLEVEL", "MAINHAND_EMPTY", "MOVING", "NEED_AMMO", "NEED_AMMO_POUCH", "NEED_EXOTIC_AMMO", "NOPATH", "NOT_BEHIND",
        "NOT_FISHABLE", "NOT_HERE", "NOT_INFRONT", "NOT_IN_CONTROL", "NOT_KNOWN", "NOT_MOUNTED", "NOT_ON_TAXI", "NOT_ON_TRANSPORT", "NOT_READY",
        "NOT_SHAPESHIFT", "NOT_STANDING", "NOT_TRADEABLE", "NOT_TRADING", "NOT_UNSHEATHED", "NOT_WHILE_GHOST", "NO_AMMO", "NO_CHARGES_REMAIN",
        "NO_CHAMPION", "NO_COMBO_POINTS", "NO_DUELING", "NO_ENDURANCE", "NO_FISH", "NO_ITEMS_WHILE_SHAPESHIFTED", "NO_MOUNTS_ALLOWED", "NO_PET",
        "NO_POWER", "NOTHING_TO_DISPEL", "NOTHING_TO_STEAL", "ONLY_ABOVEWATER", "ONLY_DAYTIME", "ONLY_INDOORS", "ONLY_MOUNTED", "ONLY_NIGHTTIME",
        "ONLY_OUTDOORS", "ONLY_SHAPESHIFT", "ONLY_STEALTHED", "ONLY_UNDERWATER", "OUT_OF_RANGE", "PACIFIED", "POSSESSED", "REAGENTS",
        "REQUIRES_AREA", "REQUIRES_SPELL_FOCUS", "ROOTED", "SILENCED", "SPELL_IN_PROGRESS", "SPELL_LEARNED", "SPELL_UNAVAILABLE", "STUNNED",
        "TARGETS_DEAD", "TARGET_AFFECTING_COMBAT", "TARGET_AURASTATE", "TARGET_DUELING", "TARGET_ENEMY", "TARGET_ENRAGED", "TARGET_FRIENDLY",
        "TARGET_IN_COMBAT", "TARGET_IS_PLAYER", "TARGET_NOT_DEAD", "TARGET_NOT_IN_PARTY", "TARGET_NOT_LOOTED", "TARGET_NOT_PLAYER",
        "TARGET_NO_POCKETS", "TARGET_NO_WEAPONS", "TARGET_UNSKINNABLE", "THIRST_SATIATED", "TOO_CLOSE", "TOO_MANY_OF_ITEM", "TOTEMS",
        "TRAINING_POINTS", "TRY_AGAIN", "UNIT_NOT_BEHIND", "UNIT_NOT_INFRONT", "WRONG_PET_FOOD", "NOT_WHILE_FATIGUED", "TARGET_NOT_IN_INSTANCE",
        "NOT_WHILE_TRADING", "TARGET_NOT_IN_RAID", "DISENCHANT_WHILE_LOOTING", "PROSPECT_WHILE_LOOTING", "PROSPECT_NEED_MORE",
        "TARGET_FREEFORALL", "NO_EDIBLE_CORPSES", "ONLY_BATTLEGROUNDS", "TARGET_NOT_GHOST", "TOO_MANY_SKILLS", "TRANSFORM_UNUSABLE",
        "WRONG_WEATHER", "DAMAGE_IMMUNE", "PREVENTED_BY_MECHANIC", "PLAY_TIME", "REPUTATION", "MIN_SKILL", "UNKNOWN",
    ];
    private static readonly string[] PowerEvents = ["MANA", "RAGE", "FOCUS", "ENERGY", "HAPPINESS"];

    private sealed record UnitState(ulong Guid, uint Health, uint MaxHealth, byte PowerType, uint Power, uint MaxPower, uint Level, long Auras, bool Combat);

    private readonly GameSession _session;
    private readonly Gameplay _play;
    private readonly GameText _text;
    private readonly HashSet<MouseButton> _uiButtons = [];
    private readonly Dictionary<MouseButton, Vector2> _pressedAt = [];
    private readonly Dictionary<Key, string> _boundKeys = [];
    private readonly Dictionary<string, UnitState?> _units = new() { ["player"] = null, ["target"] = null };
    private readonly long[] _bags = new long[5];
    private long _equipment, _cooldowns, _actions;
    private ulong _target;
    private int _spellCount, _bonusBar = -1;
    private uint _xp, _money, _playerFlags;
    private bool _entered, _dead, _attacking, _itemsArrived;
    private bool? _lootOpen;
    private int _lootCount;
    private CastState? _cast;
    private bool _castFailed;
    private ulong _mouseover;
    private IKeyboard? _keyboard;
    private Vector2 _mouse;

    public InGameUi(GL gl, MpqFileSystem files, string? looseRoot, GameSession session, Gameplay play, GameText text, ClientData data)
    {
        _session = session;
        _play = play;
        _text = text;
        var source = new MpqUiFileSource(files, looseRoot);
        Ui = new UiScreen(source, new UiLog());
        Api = new GameApi(Ui, session, play, text, data);
        Renderer = new GlueRenderer(gl, Ui, source) { CommonChinese = true };

        var started = DateTime.UtcNow;
        var sources = files.AllFiles
            .Where(f => f.StartsWith(@"Interface\FrameXML\", StringComparison.OrdinalIgnoreCase) &&
                        (f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            .Select(f => source.Read(f) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : "");
        Api.Register(GameApi.CalledFunctions(sources));
        var loaded = Ui.LoadFrameXml(loadAddOns: false);
        foreach (var (key, command) in KeyBindings.Defaults)
            if (Ui.Keys.Action(key) is null)
                Ui.Keys.Set(key, command);
        var log = Path.Combine(Path.GetTempPath(), "NetCoreClient-FrameXML.log");
        File.WriteAllText(log, Ui.Log.ToString());
        Console.WriteLine($"FrameXML {(loaded ? "loaded" : "failed")}: {Ui.Loader.FilesLoaded} files, {Ui.Frames.Count} frames, " +
                          $"{Ui.Log.Problems.Count(p => p.Level == UiLogLevel.Error)} errors in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms; log: {log}");
        Renderer.CollectFonts();

        Api.ErrorRaised += ShowError;
        Api.Modifiers = () => (Down(Key.ShiftLeft, Key.ShiftRight), Down(Key.ControlLeft, Key.ControlRight), Down(Key.AltLeft, Key.AltRight));
        session.ChatReceived += OnChat;
        play.Combat += OnCombat;
        play.Error += OnError;
        play.CastFailed += OnCastFailed;
        play.InventoryError += OnInventoryError;
        play.ItemReceived += OnItemReceived;
        play.MoneyLooted += OnMoneyLooted;
        play.ExperienceGained += OnExperience;
        play.LevelUp += OnLevelUp;
        play.SpellLearned += OnSpellLearned;
        play.ItemInfoReceived += OnItemInfo;
    }

    public UiScreen Ui { get; }
    public GameApi Api { get; }
    public GlueRenderer Renderer { get; }

    /// <summary>The mouse is over (or was pressed on) the interface, so the world should ignore it.</summary>
    public bool OwnsMouse => _uiButtons.Count > 0 || Ui.Dragging || OverUi;
    private bool OverUi => Ui.MouseFocus is { } focus && focus is not WorldFrame;
    /// <summary>An edit box has keyboard focus: movement keys type text instead.</summary>
    public bool TextInput => Ui.KeyboardFocus is not null;
    /// <summary>Mouse position in window pixels.</summary>
    public Vector2 Mouse => _mouse;

    /// <summary>A click (press and release without dragging) on the world rather than the interface.</summary>
    public event Action<MouseButton, Vector2>? WorldClicked;

    public void Dispose()
    {
        Api.ErrorRaised -= ShowError;
        _session.ChatReceived -= OnChat;
        _play.Combat -= OnCombat;
        _play.Error -= OnError;
        _play.CastFailed -= OnCastFailed;
        _play.InventoryError -= OnInventoryError;
        _play.ItemReceived -= OnItemReceived;
        _play.MoneyLooted -= OnMoneyLooted;
        _play.ExperienceGained -= OnExperience;
        _play.LevelUp -= OnLevelUp;
        _play.SpellLearned -= OnSpellLearned;
        _play.ItemInfoReceived -= OnItemInfo;
        Renderer.Dispose();
        var log = Path.Combine(Path.GetTempPath(), "NetCoreClient-FrameXML.log");
        File.WriteAllText(log, Ui.Log.ToString());
        Console.WriteLine($"FrameXML session: {Ui.Log.Problems.Count(p => p.Level == UiLogLevel.Error)} errors; log: {log}");
    }

    // ---- Input ---------------------------------------------------------------------------------------------

    public void Attach(IInputContext input) => _keyboard = input.Keyboards.FirstOrDefault();

    private bool Down(params Key[] keys) => _keyboard is { } k && keys.Any(k.IsKeyPressed);

    public void MouseMove(Vector2 window, Vector2 windowSize)
    {
        _mouse = window;
        var scale = windowSize.Y / UiScreen.ScreenHeight;
        Ui.MouseMove(new Vector2(window.X / scale, (windowSize.Y - window.Y) / scale));
    }

    public void MouseDown(MouseButton button)
    {
        _pressedAt[button] = _mouse;
        if (!OverUi)
            return;
        _uiButtons.Add(button);
        Ui.MouseDown(ButtonName(button));
    }

    public void MouseUp(MouseButton button)
    {
        if (_uiButtons.Remove(button) || Ui.Dragging)
        {
            Ui.MouseUp(ButtonName(button));
            return;
        }
        if (_pressedAt.Remove(button, out var at) && Vector2.Distance(at, _mouse) < ClickSlop)
        {
            if (Api.CursorHasSomething)
                Api.ClearCursor();
            else
                WorldClicked?.Invoke(button, _mouse);
        }
    }

    /// <summary>True when the interface took the wheel (scrolling chat, zooming the minimap).</summary>
    public bool MouseWheel(float delta)
    {
        if (!OverUi)
            return false;
        Ui.MouseWheel(delta);
        return true;
    }

    public void KeyDown(Key key)
    {
        if (ModifierKeys.Contains(key))
            return;
        var name = KeyNames.TryGetValue(key, out var known) ? known : key.ToString().ToUpperInvariant();
        if (TextInput)
        {
            Ui.KeyDown(name);
            return;
        }
        var bound = (Down(Key.AltLeft, Key.AltRight) ? "ALT-" : "") + (Down(Key.ControlLeft, Key.ControlRight) ? "CTRL-" : "") +
                    (Down(Key.ShiftLeft, Key.ShiftRight) ? "SHIFT-" : "") + name;
        if (Ui.Keys.Press(Ui, bound, true))
            _boundKeys[key] = bound;
        else if (bound != name && Ui.Keys.Press(Ui, name, true))
            _boundKeys[key] = name;
        else
            Ui.KeyDown(name);
    }

    public void KeyUp(Key key)
    {
        if (_boundKeys.Remove(key, out var bound))
            Ui.Keys.Press(Ui, bound, false);
    }

    public void Char(char c)
    {
        if (!char.IsControl(c))
            Ui.Char(c.ToString());
    }

    private static string ButtonName(MouseButton button) => button switch
    {
        MouseButton.Right => "RightButton",
        MouseButton.Middle => "MiddleButton",
        _ => "LeftButton",
    };

    /// <summary>The world unit under the cursor; shows its tooltip the way the client does for mouseover units.</summary>
    public void SetMouseover(ulong guid)
    {
        if (OverUi)
            guid = 0;
        if (guid == _mouseover)
            return;
        _mouseover = guid;
        Api.MouseoverGuid = guid;
        Ui.FireEvent("UPDATE_MOUSEOVER_UNIT");
        Ui.Lua.Execute(guid != 0
            ? "GameTooltip_SetDefaultAnchor(GameTooltip, UIParent); GameTooltip:SetUnit('mouseover'); GameTooltip:Show()"
            : "if GameTooltip:GetOwner() == UIParent then GameTooltip:Hide() end", "mouseover");
    }

    // ---- Events from game state ------------------------------------------------------------------------------

    public void Update(float dt)
    {
        if (_session.Player is { } player)
        {
            if (!_entered)
                EnterWorld();
            Diff(player);
        }
        Ui.Update(dt);
    }

    private void EnterWorld()
    {
        _entered = true;
        foreach (var name in new[] { "PLAYER_LOGIN", "UPDATE_CHAT_WINDOWS", "UPDATE_BINDINGS", "PLAYER_ENTERING_WORLD",
                     "UPDATE_SHAPESHIFT_FORMS", "SPELLS_CHANGED", "PLAYER_MONEY", "UPDATE_EXHAUSTION", "ACTIONBAR_PAGE_CHANGED" })
            Ui.FireEvent(name);
        Ui.FireEvent("PLAYER_XP_UPDATE", "player");
        Ui.FireEvent("UNIT_INVENTORY_CHANGED", "player");
        for (var bag = 0; bag <= 4; bag++)
            Ui.FireEvent("BAG_UPDATE", bag);
        if (StartScript is { Length: > 0 } script)
            Ui.Lua.Execute(script, "ui-script");
    }

    /// <summary>Lua run once the player is in the world.</summary>
    public string? StartScript { get; init; }

    private UnitState? StateOf(string token) => Api.Unit(token) is { } u
        ? new UnitState(u.Guid, u.Health, u.MaxHealth, u.PowerType, u.Power, u.MaxPower, u.Level, AuraHash(u), u.InCombat)
        : null;

    private static long AuraHash(WorldObject unit)
    {
        long hash = 17;
        for (var i = 0; i < 48; i++)
            hash = hash * 31 + unit[UpdateFields.UnitAura + i];
        return hash;
    }

    private void Diff(WorldObject player)
    {
        if (_play.Target != _target)
        {
            Api.TargetChanged(_target);
            _target = _play.Target;
            _units["target"] = StateOf("target");
            Ui.FireEvent("PLAYER_TARGET_CHANGED");
        }
        foreach (var token in _units.Keys.ToList())
        {
            var now = StateOf(token);
            var before = _units[token];
            _units[token] = now;
            if (now is null || before is null || before.Guid != now.Guid)
                continue;
            if (now.Health != before.Health) Ui.FireEvent("UNIT_HEALTH", token);
            if (now.MaxHealth != before.MaxHealth) Ui.FireEvent("UNIT_MAXHEALTH", token);
            if (now.PowerType != before.PowerType) Ui.FireEvent("UNIT_DISPLAYPOWER", token);
            var power = PowerEvents.ElementAtOrDefault(now.PowerType) ?? "MANA";
            if (now.Power != before.Power) Ui.FireEvent("UNIT_" + power, token);
            if (now.MaxPower != before.MaxPower) Ui.FireEvent("UNIT_MAX" + power, token);
            if (now.Level != before.Level) Ui.FireEvent("UNIT_LEVEL", token);
            if (now.Auras != before.Auras)
            {
                Ui.FireEvent("UNIT_AURA", token);
                if (token == "player")
                    Ui.FireEvent("PLAYER_AURAS_CHANGED");
            }
            if (token == "player" && now.Combat != before.Combat)
                Ui.FireEvent(now.Combat ? "PLAYER_REGEN_DISABLED" : "PLAYER_REGEN_ENABLED");
            if (token == "player" && now.Power != before.Power)
                Ui.FireEvent("ACTIONBAR_UPDATE_USABLE");
        }

        if (player[UpdateFields.PlayerXp] != _xp)
        {
            _xp = player[UpdateFields.PlayerXp];
            Ui.FireEvent("PLAYER_XP_UPDATE", "player");
        }
        if (player[UpdateFields.PlayerCoinage] != _money)
        {
            _money = player[UpdateFields.PlayerCoinage];
            Ui.FireEvent("PLAYER_MONEY");
        }
        if (_play.AutoAttacking != _attacking)
        {
            _attacking = _play.AutoAttacking;
            Ui.FireEvent(_attacking ? "PLAYER_ENTER_COMBAT" : "PLAYER_LEAVE_COMBAT");
            Ui.FireEvent("ACTIONBAR_UPDATE_STATE");
        }
        var flags = player[UpdateFields.PlayerFlags];
        if (player.IsDead != _dead)
        {
            _dead = player.IsDead;
            Ui.FireEvent(_dead ? "PLAYER_DEAD" : "PLAYER_ALIVE");
        }
        if ((_playerFlags & 0x10) != 0 && (flags & 0x10) == 0)
            Ui.FireEvent("PLAYER_UNGHOST");
        _playerFlags = flags;

        if (Api.BonusBarOffset() != _bonusBar)
        {
            _bonusBar = Api.BonusBarOffset();
            Ui.FireEvent("UPDATE_BONUS_ACTIONBAR");
            Ui.FireEvent("UPDATE_SHAPESHIFT_FORMS");
        }
        if (_play.Spells.Count != _spellCount)
        {
            _spellCount = _play.Spells.Count;
            Ui.FireEvent("SPELLS_CHANGED");
        }
        var actions = Hash(_play.ActionButtons.Select(a => (long)a));
        if (actions != _actions)
        {
            _actions = actions;
            Ui.FireEvent("ACTIONBAR_SLOT_CHANGED", 0);
        }
        var cooldowns = Hash(_play.Cooldowns.Select(c => (long)c.Key * 7919 + c.Value.End));
        if (cooldowns != _cooldowns)
        {
            _cooldowns = cooldowns;
            Ui.FireEvent("ACTIONBAR_UPDATE_COOLDOWN");
            Ui.FireEvent("SPELL_UPDATE_COOLDOWN");
            Ui.FireEvent("BAG_UPDATE_COOLDOWN");
        }

        DiffItems();
        DiffCast();
        DiffLoot();
    }

    private static long Hash(IEnumerable<long> values)
    {
        long hash = 17;
        foreach (var v in values)
            hash = hash * 31 + v;
        return hash;
    }

    private long SlotHash(byte bag, byte slot) =>
        _play.ItemAt(bag, slot) is { } item ? (long)item.Guid * 31 + item[UpdateFields.ItemStackCount] : 0;

    private void DiffItems()
    {
        var all = _itemsArrived;
        _itemsArrived = false;
        var equipment = Hash(Enumerable.Range(0, Gameplay.BackpackStart).Select(i => SlotHash(Gameplay.InventoryBag, (byte)i)));
        if (equipment != _equipment || all)
        {
            _equipment = equipment;
            Ui.FireEvent("UNIT_INVENTORY_CHANGED", "player");
            Ui.FireEvent("ACTIONBAR_SLOT_CHANGED", 0);
        }
        for (var bag = 0; bag <= 4; bag++)
        {
            var (serverBag, first, count) = bag == 0
                ? (Gameplay.InventoryBag, Gameplay.BackpackStart, Gameplay.BackpackSize)
                : ((byte)(Gameplay.BagSlotStart + bag - 1), 0, _play.BagSize((byte)(Gameplay.BagSlotStart + bag - 1)));
            var hash = Hash(Enumerable.Range(first, count).Select(i => SlotHash(serverBag, (byte)i)).Prepend(count));
            if (hash == _bags[bag] && !all)
                continue;
            _bags[bag] = hash;
            Ui.FireEvent("BAG_UPDATE", bag);
        }
    }

    private void OnItemInfo(ItemTemplate _) => _itemsArrived = true;

    private void DiffCast()
    {
        var cast = _play.PlayerCast;
        if (cast == _cast)
            return;
        if (_cast is { } previous && (cast is null || cast.StartMs != previous.StartMs))
        {
            if (previous.Channel)
                Ui.FireEvent("SPELLCAST_CHANNEL_STOP");
            else if (!_castFailed)
                Ui.FireEvent("SPELLCAST_STOP");
        }
        _castFailed = false;
        if (cast is not null && (_cast is null || cast.StartMs != _cast.StartMs))
        {
            var name = _text.SpellName(cast.SpellId);
            if (cast.Channel)
                Ui.FireEvent("SPELLCAST_CHANNEL_START", (double)cast.DurationMs, name);
            else
                Ui.FireEvent("SPELLCAST_START", name, (double)cast.DurationMs);
        }
        _cast = cast;
        Ui.FireEvent("ACTIONBAR_UPDATE_STATE");
    }

    private void DiffLoot()
    {
        var open = _play.Loot is not null;
        var count = _play.Loot is { } loot ? loot.Items.Count + (loot.Gold > 0 ? 1 : 0) : 0;
        if (open == _lootOpen && count == _lootCount)
            return;
        if (open != _lootOpen)
            Ui.FireEvent(open ? "LOOT_OPENED" : "LOOT_CLOSED");
        else if (open)
            Ui.Lua.Execute("if LootFrame:IsVisible() then LootFrame_Update() end", "loot");
        _lootOpen = open;
        _lootCount = count;
    }

    // ---- Messages ------------------------------------------------------------------------------------------

    /// <summary>A CHAT_MSG_* event with every argument ChatFrame_OnEvent reads (it calls strlen on arg3 and arg4).</summary>
    private void Chat(string type, string text, string sender = "", string channel = "") =>
        Ui.FireEvent("CHAT_MSG_" + type, text, sender, "", channel, "", "", 0, 0, channel);

    private void OnChat(ChatMessage message)
    {
        if (GameApi.ChatEvent(message) is { } name)
            Chat(name["CHAT_MSG_".Length..], message.Text, message.SenderName, message.Channel);
    }

    private void ShowError(string text) => Ui.FireEvent("UI_ERROR_MESSAGE", text);

    private void OnError(string key) => ShowError(_text[key]);

    private void OnCastFailed(uint spell, byte reason, uint arg1, uint arg2)
    {
        Api.ResetGlobalCooldown();
        if (_play.PlayerCast is null && _cast is not null)
        {
            _castFailed = true;
            Ui.FireEvent(reason < CastErrors.Length && CastErrors[reason].StartsWith("INTERRUPTED") ? "SPELLCAST_INTERRUPTED" : "SPELLCAST_FAILED");
        }
        else
            Ui.FireEvent("SPELLCAST_FAILED");
        if (reason >= CastErrors.Length || CastErrors[reason] is "DONT_REPORT" or "SPELL_IN_PROGRESS")
            return;
        var key = $"SPELL_FAILED_{CastErrors[reason]}";
        if (!_text.Has(key))
            ShowError(_text["SPELL_FAILED_UNKNOWN"]);
        else
            ShowError(_text.Format(key, CastErrors[reason].StartsWith("EQUIPPED_ITEM_CLASS") ? _text.ItemKind(arg1, arg2) : ""));
    }

    private void OnInventoryError(byte reason) =>
        ShowError(reason < InventoryErrors.Length ? _text[InventoryErrors[reason]] : $"物品错误 {reason}");

    private void OnItemReceived(uint entry, uint count)
    {
        var item = _play.Item(entry);
        var link = item is not null ? GameApi.ItemLink(item) : $"[物品 {entry}]";
        Chat("LOOT", count > 1 ? _text.Format("LOOT_ITEM_SELF_MULTIPLE", link, count) : _text.Format("LOOT_ITEM_SELF", link));
    }

    private void OnMoneyLooted(uint copper) => Chat("MONEY", _text.Format("YOU_LOOT_MONEY", Api.Money(copper)));

    private string NameOf(ulong guid) =>
        guid == _session.PlayerGuid ? "你" : _session.Objects.Get(guid)?.Name ?? _session.NameOf(guid) ?? "未知目标";

    private void OnExperience(ulong victim, uint xp) =>
        Chat("COMBAT_XP_GAIN", victim != 0 ? _text.Format("COMBATLOG_XPGAIN_FIRSTPERSON", NameOf(victim), xp) : $"你获得了{xp}点经验值。");

    private void OnLevelUp(uint level) => Ui.FireEvent("PLAYER_LEVEL_UP", (double)level, 0, 0, 0, 0, 0, 0, 0, 0);

    private void OnSpellLearned(uint spell)
    {
        Chat("SYSTEM", $"你学会了新的法术：{_text.Spell(spell)?.FullName ?? spell.ToString()}。");
        Ui.FireEvent("LEARNED_SPELL_IN_TAB", 1);
    }

    /// <summary>Combat log lines, as the client's CHAT_MSG_COMBAT_* and CHAT_MSG_SPELL_* events.</summary>
    private void OnCombat(CombatEvent e)
    {
        var self = _session.PlayerGuid;
        var spell = e.SpellId != 0 ? _text.SpellName(e.SpellId) : "";
        var outcome = e.Outcome.Length > 0 ? _text[e.Outcome] : "";
        var crit = e.Critical ? "（致命一击）" : "";
        var mine = e.Attacker == self;
        var atMe = e.Victim == self;
        var (type, line) = e.Kind switch
        {
            CombatKind.Melee when outcome.Length > 0 => (mine ? "COMBAT_SELF_MISSES" : atMe ? "COMBAT_CREATURE_VS_SELF_MISSES" : "COMBAT_CREATURE_VS_CREATURE_MISSES",
                $"{NameOf(e.Attacker)}的攻击被{NameOf(e.Victim)}{outcome}了。"),
            CombatKind.Melee => (mine ? "COMBAT_SELF_HITS" : atMe ? "COMBAT_CREATURE_VS_SELF_HITS" : "COMBAT_CREATURE_VS_CREATURE_HITS",
                $"{NameOf(e.Attacker)}击中{NameOf(e.Victim)}，造成{e.Amount}点伤害{crit}。"),
            CombatKind.Spell => (mine ? "SPELL_SELF_DAMAGE" : atMe ? "SPELL_CREATURE_VS_SELF_DAMAGE" : "SPELL_CREATURE_VS_CREATURE_DAMAGE",
                $"{NameOf(e.Attacker)}的{spell}击中{NameOf(e.Victim)}，造成{e.Amount}点伤害{crit}。"),
            CombatKind.Periodic => (atMe ? "SPELL_PERIODIC_SELF_DAMAGE" : "SPELL_PERIODIC_CREATURE_DAMAGE",
                $"{NameOf(e.Victim)}受到{NameOf(e.Attacker)}的{spell}造成的{e.Amount}点伤害。"),
            CombatKind.Heal => (mine || atMe ? "SPELL_SELF_BUFF" : "SPELL_CREATURE_VS_CREATURE_BUFF",
                $"{NameOf(e.Attacker)}的{spell}为{NameOf(e.Victim)}恢复了{e.Amount}点生命值{crit}。"),
            CombatKind.Energize => (atMe ? "SPELL_SELF_BUFF" : "SPELL_CREATURE_VS_CREATURE_BUFF",
                $"{NameOf(e.Victim)}从{NameOf(e.Attacker)}的{spell}获得{e.Amount}点能量。"),
            CombatKind.Miss => (mine ? "SPELL_SELF_DAMAGE" : atMe ? "SPELL_CREATURE_VS_SELF_DAMAGE" : "SPELL_CREATURE_VS_CREATURE_DAMAGE",
                $"{NameOf(e.Attacker)}的{spell}被{NameOf(e.Victim)}{outcome}了。"),
            _ => ("COMBAT_MISC_INFO", $"{NameOf(e.Victim)}受到{e.Amount}点伤害。"),
        };
        Chat(type, line);
    }

    // ---- Drawing -------------------------------------------------------------------------------------------

    public void Draw(Vector2 windowSize, float dt)
    {
        Renderer.Draw(windowSize, dt);
        if (Api.CursorIcon is { } icon)
            Renderer.DrawCursorIcon(icon, _mouse);
    }

    /// <summary>The game's normal font, for names and combat text the engine draws in the world.</summary>
    public ImFontPtr WorldFont => Renderer.FontOf("GameFontNormal");
}
