using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using FrameXml;
using FrameXml.Lua;
using MoonSharp.Interpreter;
using Net;
using static FrameXml.Lua.LuaBindings;

namespace Client.Online;

/// <summary>
/// The C functions FrameXML calls in the world (unit, action bar, spell, container, loot, chat and system API of
/// the 1.12 client), answered from the game session. Functions FrameXML calls that are not implemented here return
/// nothing (GetNum* functions return 0) so the default UI loads and runs.
/// </summary>
public sealed partial class GameApi
{
    private const uint AttackSpell = 6603;
    private const uint PlayerFlagGhost = 0x10;
    private const int NumBonusBars = 6, ButtonsPerBar = 12;

    private static readonly string[] ClassTokens = ["", "WARRIOR", "PALADIN", "HUNTER", "ROGUE", "PRIEST", "", "SHAMAN", "MAGE", "WARLOCK", "", "DRUID"];
    private static readonly string[] PowerTokens = ["MANA", "RAGE", "FOCUS", "ENERGY", "HAPPINESS"];
    private static readonly HashSet<int> AllianceRaces = [1, 3, 4, 7];

    private readonly UiScreen _ui;
    private readonly GameSession _session;
    private readonly Gameplay _play;
    private readonly GameText _text;
    private readonly ClientData _data;
    private readonly Dictionary<string, string> _cvars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["autoSelfCast"] = "0", ["showTargetOfTarget"] = "0", ["UnitNamePlayerGuild"] = "1", ["UnitNameOwn"] = "0",
        ["cameraDistanceMax"] = "15", ["mouseSpeed"] = "1", ["deselectOnClick"] = "1", ["lootUnderMouse"] = "0",
        ["autoLootCorpse"] = "0", ["interactOnLeftClick"] = "0", ["showGameTips"] = "1", ["showLootSpam"] = "1",
        ["CombatLogPeriodicSpells"] = "1", ["CombatDeathLogRange"] = "60", ["CombatLogRangeCreature"] = "30",
        ["chatBubbles"] = "1", ["profanityFilter"] = "0", ["showNewbieTips"] = "1", ["SoundOutputSystem"] = "0",
        ["statusBarText"] = "1", ["guildMemberNotify"] = "1", ["gxResolution"] = "1440x900", ["gxWindow"] = "1",
    };

    private long _gcdStart, _gcdEnd;
    private ulong _lastTarget;

    /// <summary>What the cursor holds: an action (packed like SMSG_ACTION_BUTTONS) or an item in a bag slot.</summary>
    private uint _cursorAction;
    private (byte Bag, byte Slot)? _cursorItem;

    public GameApi(UiScreen ui, GameSession session, Gameplay play, GameText text, ClientData data)
    {
        _ui = ui;
        _session = session;
        _play = play;
        _text = text;
        _data = data;
    }

    // ---- Host hooks ----------------------------------------------------------------------------------------

    public Func<ulong, Vector3?> PositionOf { get; set; } = _ => null;
    public Action<bool, bool> TargetNearest { get; set; } = (_, _) => { };
    public Action<WorldObject> Interact { get; set; } = _ => { };
    public Func<string> ZoneText { get; set; } = () => "";
    public Func<float> Framerate { get; set; } = () => 0;
    public Func<(bool Shift, bool Ctrl, bool Alt)> Modifiers { get; set; } = () => (false, false, false);
    public Action Logout { get; set; } = () => { };
    public Action Quit { get; set; } = () => { };
    public Action<bool> ToggleUi { get; set; } = _ => { };
    /// <summary>A UI error to show (red text at the top), e.g. "Not enough rage".</summary>
    public event Action<string>? ErrorRaised;
    /// <summary>The unit under the mouse in the world ("mouseover").</summary>
    public ulong MouseoverGuid { get; set; }

    /// <summary>Icon of what the cursor holds, drawn under the mouse by the host.</summary>
    public string? CursorIcon => _cursorItem is { } at
        ? _play.ItemAt(at.Bag, at.Slot) is { } item && _play.Item(item.Entry) is { } info ? _text.ItemIcon(info.DisplayId) : null
        : _cursorAction != 0 ? ActionIcon(_cursorAction) : null;

    public bool CursorHasSomething => _cursorItem is not null || _cursorAction != 0;

    // ---- Registration --------------------------------------------------------------------------------------

    /// <summary>Defines the API in the UI's Lua state; call before LoadFrameXml.</summary>
    public void Register(IEnumerable<string> calledNames)
    {
        RegisterSystem();
        RegisterUnits();
        RegisterActions();
        RegisterSpells();
        RegisterItems();
        RegisterLoot();
        RegisterChat();
        RegisterTooltips();
        RegisterWidgets();

        var g = _ui.Lua.Globals;
        var empty = new List<string>();
        foreach (var name in calledNames)
            if (g.Get(name).IsNil())
            {
                if (StubResult(name) is { } result)
                    Fn(name, result);
                else
                    empty.Add($"function {name}() end");
            }
        // A CLR callback always returns at least one value; a Lua function can return none.
        _ui.Lua.Execute(string.Join('\n', empty), "stubs");
    }

    /// <summary>
    /// What an engine function the client doesn't implement yet returns: counts, money and costs are 0 and
    /// cooldowns are (0, 0, 0), since FrameXML does arithmetic on those; anything else returns no values
    /// (so vararg handlers such as QuestTimerFrame_Update(GetQuestTimers()) see arg.n == 0).
    /// </summary>
    private static Func<LuaArgs, DynValue>? StubResult(string name)
    {
        if (name.Contains("Cooldown", StringComparison.Ordinal))
            return _ => Tuple(N(0), N(0), N(0));
        if (name.Contains("GetNum", StringComparison.Ordinal) || name.StartsWith("Get", StringComparison.Ordinal) &&
            (name.EndsWith("Money", StringComparison.Ordinal) || name.EndsWith("Cost", StringComparison.Ordinal) || name.EndsWith("Price", StringComparison.Ordinal) ||
             name.EndsWith("Count", StringComparison.Ordinal) || name.EndsWith("Points", StringComparison.Ordinal)))
            return _ => N(0);
        return null;
    }

    /// <summary>Names called like C functions in the given FrameXML sources and not defined by them in Lua.</summary>
    public static HashSet<string> CalledFunctions(IEnumerable<string> sources)
    {
        var defined = new HashSet<string>();
        var called = new HashSet<string>();
        foreach (var text in sources)
        {
            foreach (Match m in DefinedFunction().Matches(text))
                defined.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            foreach (Match m in CalledFunction().Matches(text))
                called.Add(m.Groups[1].Value);
        }
        called.ExceptWith(defined);
        return called;
    }

    [GeneratedRegex(@"(?:function\s+([A-Za-z_]\w*)\s*\()|(?:(?<![.:\w])([A-Za-z_]\w*)\s*=\s*function)")]
    private static partial Regex DefinedFunction();

    [GeneratedRegex(@"(?<![.:\w])([A-Z]\w*)\s*\(")]
    private static partial Regex CalledFunction();

    private void Fn(string name, Func<LuaArgs, DynValue> body) =>
        _ui.Lua.Globals[name] = DynValue.NewCallback((_, args) => body(new LuaArgs(args, 0)), name);

    private void Do(string name, Action<LuaArgs> body) => Fn(name, a => { body(a); return DynValue.Nil; });

    // ---- System --------------------------------------------------------------------------------------------

    private void RegisterSystem()
    {
        Fn("GetCVar", a => S(_cvars.GetValueOrDefault(a.Str(0) ?? "") ?? "0"));
        Fn("GetCVarDefault", a => S(_cvars.GetValueOrDefault(a.Str(0) ?? "") ?? "0"));
        Do("SetCVar", a => _cvars[a.Str(0) ?? ""] = a.Str(1) ?? "");
        Do("RegisterCVar", a => _cvars.TryAdd(a.Str(0) ?? "", a.Str(1) ?? ""));
        Fn("GetScreenResolutions", _ => Tuple(S("1024x768"), S("1280x960"), S("1600x1200")));
        Fn("GetCurrentResolution", _ => N(1));
        Fn("GetRefreshRates", _ => N(60));
        Fn("GetMultisampleFormats", _ => Tuple(N(24), N(24), N(1)));
        Fn("GetCurrentMultisampleFormat", _ => N(1));
        Fn("GetSkillLineInfo", _ => Tuple(S(""), DynValue.Nil, DynValue.Nil, N(0), N(0), N(0), N(1), DynValue.Nil, DynValue.Nil,
            DynValue.Nil, N(0), DynValue.Nil, S("")));
        Fn("GetBuildInfo", _ => Tuple(S("1.12.1"), S("5875"), S("Sep 19 2006")));
        Fn("GetLocale", _ => S("zhCN"));
        Fn("GetRealmName", _ => S(_session.Realm?.Name ?? ""));
        Fn("GetFramerate", _ => N(Framerate()));
        Fn("GetNetStats", _ => Tuple(N(0), N(0), N(50)));
        Fn("GetGameTime", _ => Tuple(N(DateTime.Now.Hour), N(DateTime.Now.Minute)));
        Fn("GetZoneText", _ => S(ZoneText()));
        Fn("GetRealZoneText", _ => S(ZoneText()));
        Fn("GetMinimapZoneText", _ => S(ZoneText()));
        Fn("GetSubZoneText", _ => S(""));
        Fn("GetZonePVPInfo", _ => S("contested"));
        Fn("GetPlayerMapPosition", _ => Tuple(N(0), N(0)));
        Fn("IsShiftKeyDown", _ => B(Modifiers().Shift));
        Fn("IsControlKeyDown", _ => B(Modifiers().Ctrl));
        Fn("IsAltKeyDown", _ => B(Modifiers().Alt));
        Do("PlaySound", _ => { });
        Do("PlaySoundFile", _ => { });
        Do("PlayMusic", _ => { });
        Do("StopMusic", _ => { });
        Do("RegisterForSave", _ => { });
        Do("RegisterForSavePerCharacter", _ => { });
        Do("SetCursor", _ => { });
        Do("ResetCursor", _ => { });
        Do("Logout", _ => Logout());
        Do("Quit", _ => Quit());
        Do("ForceLogout", _ => Logout());
        Do("ForceQuit", _ => Quit());
        Do("CancelLogout", _ => { });
        Do("Screenshot", _ => { });
        Do("ReloadUI", _ => { });
        Fn("IsLoggedIn", _ => B(true));
        Fn("CloseMenus", _ => DynValue.Nil);
        Do("ShowUIPanel", a => _ui.Bindings.ObjectOf<FrameXml.Objects.Frame>(a[0])?.Show());
        Do("HideUIPanel", a => _ui.Bindings.ObjectOf<FrameXml.Objects.Frame>(a[0])?.Hide());

        Fn("GetBindingKey", a => Tuple(_ui.Keys.KeysFor(a.Str(0) ?? "").Select(S).ToArray()));
        Fn("GetBindingAction", a => S(_ui.Keys.Action(a.Str(0) ?? "") ?? ""));
        Fn("GetNumBindings", _ => N(_ui.Keys.Count));
        Fn("GetBinding", a => _ui.Keys.Get(a.Int(0)) is { } b
            ? Tuple([S(b.Name), S(b.Header ?? ""), .. b.Keys.Select(S)])
            : DynValue.Nil);
        Fn("SetBinding", a => { _ui.Keys.Set(a.Str(0) ?? "", a.Str(1)); return B(true); });
        Do("SaveBindings", _ => { });
        Do("LoadBindings", _ => { });
        Fn("GetCurrentBindingSet", _ => N(1));
        Do("RunBinding", a => _ui.Keys.Run(_ui, a.Str(0) ?? "", (a.Str(1) ?? "down") != "up"));

        Do("SitOrStand", _ => _play.SetStandState(_session.Player?.StandState == 0 ? 1u : 0u));
        Do("ToggleSheath", _ => { });
        Do("ToggleRun", _ => { });
        Do("ToggleAutoRun", _ => { });
        Fn("GetMoney", _ => N(_session.Player?[UpdateFields.PlayerCoinage] ?? 0));
        Fn("GetCursorMoney", _ => N(0));
        Fn("IsResting", _ => DynValue.Nil);
        Fn("GetRestState", _ => Tuple(N(1), S(_text["PLAYER_STATE_NORMAL"] is var s && s != "PLAYER_STATE_NORMAL" ? s : "正常"), N(1)));
        Fn("GetXPExhaustion", _ => DynValue.Nil);
        Fn("GetReleaseTimeRemaining", _ => N(360));
        Fn("GetCorpseRecoveryDelay", _ => N(0));
        Do("RepopMe", _ => _play.ReleaseSpirit());
        Do("RetrieveCorpse", _ => _play.ReclaimCorpse());
        Fn("HasSoulstone", _ => DynValue.Nil);
        Fn("IsInGuild", _ => DynValue.Nil);
        Fn("GetGuildInfo", _ => DynValue.Nil);
        Fn("GetDefaultLanguage", _ => S(_session.Player is { } p && !AllianceRaces.Contains(p.Race) ? "兽人语" : "通用语"));
        Fn("GetNumLanguages", _ => N(1));
        Fn("GetLanguageByIndex", _ => S(_session.Player is { } p && !AllianceRaces.Contains(p.Race) ? "兽人语" : "通用语"));
        Fn("GetBonusBarOffset", _ => N(BonusBarOffset()));
        Fn("GetActionBarToggles", _ => DynValue.Nil);
        Fn("GetNumShapeshiftForms", _ => N(0));
        Fn("GetComboPoints", _ => N(0));
        Fn("UnitCharacterPoints", _ => Tuple(N(0), N(0)));
        Fn("GetPVPRankInfo", _ => Tuple(S(""), N(0)));
        Fn("UnitPVPRank", _ => N(0));
        Fn("GetPVPLifetimeStats", _ => Tuple(N(0), N(0), N(0)));
        Fn("GetInventoryAlertStatus", _ => N(0));
    }

    // ---- Units ---------------------------------------------------------------------------------------------

    public WorldObject? Unit(string? token)
    {
        if (token is null)
            return null;
        var objects = _session.Objects;
        switch (token.ToLowerInvariant())
        {
            case "player": return _session.Player;
            case "target": return _play.Target == 0 ? null : objects.Get(_play.Target);
            case "mouseover": return MouseoverGuid == 0 ? null : objects.Get(MouseoverGuid);
            case "targettarget": return _play.Target != 0 && objects.Get(_play.Target) is { } t && t.Target != 0 ? objects.Get(t.Target) : null;
            case "playertarget": return _play.Target == 0 ? null : objects.Get(_play.Target);
            default: return null;
        }
    }

    public Reaction ReactionOf(WorldObject unit) =>
        unit.Guid == _session.PlayerGuid ? Reaction.Friendly : _text.React(unit.FactionTemplate, _session.Player?.FactionTemplate ?? 1);

    private bool IsGhost(WorldObject unit) => unit.Type == ObjectType.Player && (unit[UpdateFields.PlayerFlags] & PlayerFlagGhost) != 0;

    /// <summary>Rage and happiness are stored ×10 on the server; the UI shows 0-100.</summary>
    private static uint PowerScale(WorldObject unit) => unit.PowerType is 1 or 4 ? 10u : 1u;

    private void RegisterUnits()
    {
        Func<LuaArgs, DynValue> unit(Func<WorldObject, DynValue> body) => a => Unit(a.Str(0)) is { } u ? body(u) : DynValue.Nil;

        Fn("UnitExists", unit(_ => B(true)));
        Fn("UnitName", unit(u => S(u.Name ?? _session.NameOf(u.Guid) ?? "")));
        Fn("UnitPVPName", unit(u => S(u.Name ?? "")));
        Fn("UnitLevel", unit(u => N(u.Level)));
        Fn("UnitHealth", unit(u => N(u.Health)));
        Fn("UnitHealthMax", unit(u => N(u.MaxHealth)));
        Fn("UnitMana", unit(u => N(u.Power / PowerScale(u))));
        Fn("UnitManaMax", unit(u => N(u.MaxPower / PowerScale(u))));
        Fn("UnitPowerType", unit(u => Tuple(N(u.PowerType), S(PowerTokens.ElementAtOrDefault(u.PowerType) ?? "MANA"))));
        Fn("UnitClass", unit(u =>
        {
            var token = ClassTokens.ElementAtOrDefault(u.Class) ?? "";
            if (u.Type != ObjectType.Player)
                token = "WARRIOR";
            return Tuple(S(_data.Class(u.Class)?.Name ?? token), S(token));
        }));
        Fn("UnitRace", unit(u => u.Type == ObjectType.Player && _data.Race(u.Race) is { } race ? Tuple(S(race.Name), S(race.FileString)) : DynValue.Nil));
        Fn("UnitSex", unit(u => N(u.Gender == 0 ? 2 : 3)));
        Fn("UnitIsPlayer", unit(u => B(u.Type == ObjectType.Player)));
        Fn("UnitPlayerControlled", unit(u => B(u.Type == ObjectType.Player)));
        Fn("UnitIsUnit", a => B(Unit(a.Str(0)) is { } x && Unit(a.Str(1)) is { } y && x.Guid == y.Guid));
        Fn("UnitIsDead", unit(u => B(u.IsDead && !IsGhost(u))));
        Fn("UnitIsGhost", unit(u => B(IsGhost(u))));
        Fn("UnitIsDeadOrGhost", unit(u => B(u.IsDead || IsGhost(u))));
        Fn("UnitIsCorpse", unit(_ => DynValue.Nil));
        Fn("UnitIsConnected", unit(_ => B(true)));
        Fn("UnitIsVisible", unit(_ => B(true)));
        Fn("UnitAffectingCombat", unit(u => B(u.InCombat)));
        Fn("UnitCanAttack", a => B(Unit(a.Str(0)) is { } x && Unit(a.Str(1)) is { } y && x.Guid != y.Guid &&
                                   ReactionOf(x.Guid == _session.PlayerGuid ? y : x) != Reaction.Friendly && !y.IsDead));
        Fn("UnitCanCooperate", a => B(Unit(a.Str(0)) is { } x && Unit(a.Str(1)) is { } y && x.Type == ObjectType.Player && y.Type == ObjectType.Player &&
                                      ReactionOf(x.Guid == _session.PlayerGuid ? y : x) == Reaction.Friendly));
        Fn("UnitIsFriend", a => B(Unit(a.Str(0)) is { } x && Unit(a.Str(1)) is { } y && ReactionOf(x.Guid == _session.PlayerGuid ? y : x) == Reaction.Friendly));
        Fn("UnitIsEnemy", a => B(Unit(a.Str(0)) is { } x && Unit(a.Str(1)) is { } y && ReactionOf(x.Guid == _session.PlayerGuid ? y : x) == Reaction.Hostile));
        Fn("UnitReaction", a => Unit(a.Str(0)) is { } x && Unit(a.Str(1)) is { } y
            ? N(ReactionOf(x.Guid == _session.PlayerGuid ? y : x) switch { Reaction.Hostile => 2, Reaction.Neutral => 4, _ => 5 })
            : DynValue.Nil);
        Fn("UnitIsTapped", unit(u => B(u.TappedByOther)));
        Fn("UnitIsTappedByPlayer", unit(u => B(!u.TappedByOther && u.InCombat)));
        Fn("UnitIsTrivial", unit(_ => DynValue.Nil));
        Fn("UnitClassification", unit(_ => S("normal")));
        Fn("UnitCreatureType", unit(_ => S("")));
        Fn("UnitCreatureFamily", unit(_ => DynValue.Nil));
        Fn("UnitFactionGroup", unit(u => u.Type == ObjectType.Player
            ? AllianceRaces.Contains(u.Race) ? Tuple(S("Alliance"), S(_text["FACTION_ALLIANCE"])) : Tuple(S("Horde"), S(_text["FACTION_HORDE"]))
            : DynValue.Nil));
        Fn("UnitIsPVP", unit(_ => DynValue.Nil));
        Fn("UnitIsPVPFreeForAll", unit(_ => DynValue.Nil));
        Fn("UnitInParty", unit(u => B(u.Guid == _session.PlayerGuid)));
        Fn("UnitInRaid", unit(_ => DynValue.Nil));
        Fn("UnitIsPartyLeader", unit(_ => DynValue.Nil));
        Fn("UnitIsCharmed", unit(_ => DynValue.Nil));
        Fn("UnitOnTaxi", unit(_ => DynValue.Nil));
        Fn("UnitXP", unit(u => N(u[UpdateFields.PlayerXp])));
        Fn("UnitXPMax", unit(u => N(Math.Max(1, u[UpdateFields.PlayerNextLevelXp]))));
        Fn("UnitStat", a => Unit(a.Str(0)) is { } u && a.Int(1) is >= 1 and <= 5 and var i
            ? Tuple(N(u[UpdateFields.UnitStat0 + i - 1]), N(u[UpdateFields.UnitStat0 + i - 1]), N(0), N(0))
            : DynValue.Nil);
        Fn("UnitArmor", unit(u => Tuple(N(u[UpdateFields.UnitResistances]), N(u[UpdateFields.UnitResistances]), N(u[UpdateFields.UnitResistances]), N(0), N(0))));
        Fn("UnitResistance", a => Unit(a.Str(0)) is { } u && a.Int(1) is >= 0 and <= 6 and var i
            ? Tuple(N(u[UpdateFields.UnitResistances + i]), N(u[UpdateFields.UnitResistances + i]), N(0), N(0))
            : DynValue.Nil);
        Fn("UnitAttackPower", unit(u => Tuple(N(u[UpdateFields.UnitAttackPower]), N(0), N(0))));
        Fn("UnitRangedAttackPower", unit(_ => Tuple(N(0), N(0), N(0))));
        Fn("UnitDamage", unit(u => Tuple(N(u.Float(UpdateFields.UnitMinDamage)), N(u.Float(UpdateFields.UnitMaxDamage)), N(0), N(0), N(0), N(0), N(1))));
        Fn("UnitRangedDamage", unit(_ => Tuple(N(0), N(0), N(0), N(0), N(0), N(1))));
        Fn("UnitAttackSpeed", unit(u => Tuple(N(u[UpdateFields.UnitBaseAttackTime] / 1000.0), DynValue.Nil)));
        Fn("UnitAttackBothHands", unit(u => Tuple(N(u.Level * 5), N(0), N(0), N(0))));
        Fn("UnitRangedAttack", unit(u => Tuple(N(u.Level * 5), N(0))));
        Fn("UnitDefense", unit(u => Tuple(N(u.Level * 5), N(0))));
        Fn("UnitBuff", a => Aura(a, helpful: true));
        Fn("UnitDebuff", a => Aura(a, helpful: false));

        Fn("GetPlayerBuff", a =>
        {
            var filter = a.Str(1) ?? "HELPFUL";
            var harmful = filter.Contains("HARMFUL", StringComparison.OrdinalIgnoreCase);
            var slots = AuraSlots(_session.Player, !harmful).ToList();
            var index = a.Int(0);
            return index >= 0 && index < slots.Count ? Tuple(N(slots[index]), N(1)) : Tuple(N(-1), N(0));
        });
        Fn("GetPlayerBuffTexture", a => S(AuraSpell(a.Int(0)) is { } s ? _text.Spell(s)?.Icon : null));
        Fn("GetPlayerBuffApplications", _ => N(0));
        Fn("GetPlayerBuffTimeLeft", _ => N(0));
        Fn("GetPlayerBuffDispelType", _ => DynValue.Nil);
        Do("CancelPlayerBuff", a =>
        {
            if (AuraSpell(a.Int(0)) is { } spell)
                _session.Send(Opcode.CMSG_CANCEL_AURA, new PacketWriter().U32(spell).ToArray());
        });

        Do("TargetUnit", a => _play.Select(Unit(a.Str(0))?.Guid ?? 0));
        Fn("ClearTarget", _ =>
        {
            if (_play.Target == 0)
                return DynValue.Nil;
            _play.Select(0);
            return B(true);
        });
        Do("TargetNearestEnemy", a => TargetNearest(true, a.Bool(0)));
        Do("TargetNearestFriend", a => TargetNearest(false, a.Bool(0)));
        Do("TargetLastEnemy", _ => _play.Select(_lastTarget));
        Do("TargetLastTarget", _ => _play.Select(_lastTarget));
        Do("TargetByName", a =>
        {
            var name = a.Str(0) ?? "";
            if (_session.Objects.All.FirstOrDefault(o => o.IsUnit && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) is { } found)
                _play.Select(found.Guid);
        });
        Do("AssistUnit", a =>
        {
            if (Unit(a.Str(0)) is { Target: not 0 } u)
                _play.Select(u.Target);
        });
        Do("AttackTarget", _ =>
        {
            if (_play.AutoAttacking)
                _play.StopAttack();
            else if (_play.Target != 0)
                _play.StartAttack(_play.Target);
        });
        Do("InteractUnit", a =>
        {
            if (Unit(a.Str(0)) is { } u)
                Interact(u);
        });
        Do("FollowUnit", _ => { });
        Fn("CheckInteractDistance", a => Unit(a.Str(0)) is { } u && Distance(u) is { } d
            ? B(d <= a.Int(1) switch { 1 => 10f, 2 => 11.11f, 3 => 10f, _ => 28f })
            : DynValue.Nil);
    }

    /// <summary>Remembers the previous target for TargetLastTarget.</summary>
    public void TargetChanged(ulong previous)
    {
        if (previous != 0)
            _lastTarget = previous;
    }

    private float? Distance(WorldObject unit) =>
        PositionOf(unit.Guid) is { } there && PositionOf(_session.PlayerGuid) is { } here ? Vector3.Distance(there, here) : null;

    /// <summary>Aura slots holding a spell: 0-31 are helpful, 32-47 harmful.</summary>
    private static IEnumerable<int> AuraSlots(WorldObject? unit, bool helpful)
    {
        if (unit is null)
            yield break;
        var (from, to) = helpful ? (0, 32) : (32, 48);
        for (var i = from; i < to; i++)
            if (unit[UpdateFields.UnitAura + i] != 0)
                yield return i;
    }

    private uint? AuraSpell(int slot) =>
        _session.Player is { } p && slot is >= 0 and < 48 && p[UpdateFields.UnitAura + slot] is var s and not 0 ? s : null;

    private DynValue Aura(LuaArgs a, bool helpful)
    {
        var unit = Unit(a.Str(0));
        var slot = AuraSlots(unit, helpful).ElementAtOrDefault(a.Int(1) - 1);
        if (unit is null || a.Int(1) < 1 || (slot == 0 && unit[UpdateFields.UnitAura] == 0) || !AuraSlots(unit, helpful).Contains(slot))
            return DynValue.Nil;
        var icon = _text.Spell(unit[UpdateFields.UnitAura + slot])?.Icon ?? @"Interface\Icons\INV_Misc_QuestionMark";
        return helpful ? Tuple(S(icon), N(1)) : Tuple(S(icon), N(1), DynValue.Nil);
    }

    public uint AuraAt(string? token, int index, bool helpful) =>
        Unit(token) is { } u && AuraSlots(u, helpful).ElementAtOrDefault(index - 1) is var slot && index >= 1 && AuraSlots(u, helpful).Contains(slot)
            ? u[UpdateFields.UnitAura + slot]
            : 0;

    // ---- Action bar ----------------------------------------------------------------------------------------

    /// <summary>The bonus bar (stances, cat/bear forms, stealth) the main action bar shows: 1-4, or 0 for none.</summary>
    public int BonusBarOffset() => _session.Player?.ShapeshiftForm switch
    {
        0x01 or 0x11 or 0x1E => 1,
        0x12 => 2,
        0x05 or 0x08 or 0x13 => 3,
        _ => 0,
    };

    private uint ActionAt(int id) => id >= 1 && id <= Gameplay.ActionButtonCount ? _play.ActionButtons[id - 1] : 0;

    private string? ActionIcon(uint packed) => (packed >> 24) switch
    {
        0 => _text.Spell(packed & 0xFFFFFF)?.Icon,
        0x80 => _play.Item(packed & 0xFFFFFF) is { } item ? _text.ItemIcon(item.DisplayId) : @"Interface\Icons\INV_Misc_QuestionMark",
        _ => null,
    };

    /// <summary>GetTime()-based start and duration of the spell's cooldown, or the global cooldown when longer.</summary>
    private (double Start, double Duration) SpellCooldown(uint spell)
    {
        var now = _session.NowMs;
        var (start, end) = _play.Cooldowns.GetValueOrDefault(spell);
        if (end <= now)
            (start, end) = (0, 0);
        if (_text.Spell(spell) is { GlobalCooldownMs: > 0 } && _gcdEnd > now && _gcdEnd > end)
            (start, end) = (_gcdStart, _gcdEnd);
        if (end <= now)
            return (0, 0);
        return (_ui.Time - (now - start) / 1000.0, (end - start) / 1000.0);
    }

    private void RegisterActions()
    {
        Fn("HasAction", a => B(ActionAt(a.Int(0)) != 0));
        Fn("GetActionTexture", a => ActionAt(a.Int(0)) is var p and not 0 ? S(ActionIcon(p) ?? @"Interface\Icons\INV_Misc_QuestionMark") : DynValue.Nil);
        Fn("GetActionText", _ => DynValue.Nil);
        Fn("GetActionCount", a => ActionAt(a.Int(0)) is var p && p >> 24 == 0x80 ? N(CountItem(p & 0xFFFFFF)) : N(0));
        Fn("GetActionCooldown", a =>
        {
            var p = ActionAt(a.Int(0));
            if (p == 0 || p >> 24 != 0)
                return Tuple(N(0), N(0), N(1));
            var (start, duration) = SpellCooldown(p & 0xFFFFFF);
            return Tuple(N(start), N(duration), N(1));
        });
        Fn("IsUsableAction", a =>
        {
            var p = ActionAt(a.Int(0));
            if (p == 0 || _session.Player is not { } player)
                return DynValue.Nil;
            if (p >> 24 == 0x80)
                return B(CountItem(p & 0xFFFFFF) > 0);
            if (_text.Spell(p & 0xFFFFFF) is { PowerCost: > 0 } spell && player.PowerType == spell.PowerType && player.Power < spell.PowerCost)
                return Tuple(DynValue.Nil, N(1));
            return B(!player.IsDead);
        });
        Fn("IsActionInRange", a =>
        {
            var p = ActionAt(a.Int(0));
            if (p == 0 || p >> 24 != 0 || _text.Spell(p & 0xFFFFFF) is not { NeedsEnemy: true, MaxRange: > 0 } spell ||
                Unit("target") is not { } target || Distance(target) is not { } d)
                return DynValue.Nil;
            return N(d <= spell.MaxRange + 3f ? 1 : 0);
        });
        Fn("ActionHasRange", a => B(ActionAt(a.Int(0)) is var p && p >> 24 == 0 && _text.Spell(p & 0xFFFFFF) is { MaxRange: > 5 }));
        Fn("IsCurrentAction", a => B(ActionAt(a.Int(0)) is var p && p != 0 && p >> 24 == 0 &&
                                     ((p & 0xFFFFFF) == AttackSpell ? _play.AutoAttacking : _play.PlayerCast?.SpellId == (p & 0xFFFFFF))));
        Fn("IsAttackAction", a => B(ActionAt(a.Int(0)) == AttackSpell));
        Fn("IsAutoRepeatAction", _ => DynValue.Nil);
        Fn("IsConsumableAction", a => B(ActionAt(a.Int(0)) >> 24 == 0x80));
        Do("UseAction", a =>
        {
            var id = a.Int(0);
            if (a.Bool(1) && CursorHasSomething)
            {
                PlaceAction(id);
                return;
            }
            UseActionId(id, a.Bool(2));
        });
        Do("PickupAction", a =>
        {
            var id = a.Int(0);
            var held = _cursorAction;
            _cursorItem = null;
            _cursorAction = ActionAt(id);
            if (_cursorAction != 0 || held != 0)
                SetAction(id, held);
        });
        Do("PlaceAction", a => PlaceAction(a.Int(0)));
        Do("ChangeActionBarPage", _ => _ui.FireEvent("ACTIONBAR_PAGE_CHANGED"));
        Do("SetActionBarToggles", _ => { });

        Fn("CursorHasItem", _ => B(_cursorItem is not null));
        Fn("CursorHasSpell", _ => B(_cursorAction != 0));
        Fn("CursorHasMoney", _ => DynValue.Nil);
        Fn("CursorCanGoInSlot", _ => B(_cursorItem is not null));
        Do("ClearCursor", _ => ClearCursor());
        Do("DeleteCursorItem", _ =>
        {
            if (_cursorItem is { } at && _play.ItemAt(at.Bag, at.Slot) is { } item)
                _play.DestroyItem(at.Bag, at.Slot, (byte)Math.Max(1, item[UpdateFields.ItemStackCount]));
            ClearCursor();
        });
        Fn("SpellIsTargeting", _ => DynValue.Nil);
        Fn("SpellStopTargeting", _ => DynValue.Nil);
        Do("SpellTargetUnit", _ => { });
        Fn("SpellCanTargetUnit", _ => DynValue.Nil);
        Fn("SpellStopCasting", _ =>
        {
            if (_play.PlayerCast is null)
                return DynValue.Nil;
            _play.CancelCast();
            return B(true);
        });
    }

    private void SetAction(int id, uint packed)
    {
        if (id < 1 || id > Gameplay.ActionButtonCount)
            return;
        _play.SetActionButton(id - 1, packed);
        _ui.FireEvent("ACTIONBAR_SLOT_CHANGED", id);
    }

    /// <summary>Drops what the cursor holds on an action button; whatever was there goes onto the cursor.</summary>
    private void PlaceAction(int id)
    {
        var packed = _cursorItem is { } at && _play.ItemAt(at.Bag, at.Slot) is { } item ? 0x80u << 24 | item.Entry : _cursorAction;
        if (packed == 0)
            return;
        var previous = ActionAt(id);
        ClearCursor();
        SetAction(id, packed);
        _cursorAction = previous;
    }

    public void ClearCursor()
    {
        var hadItem = _cursorItem is not null;
        _cursorAction = 0;
        _cursorItem = null;
        if (hadItem)
            _ui.FireEvent("ITEM_LOCK_CHANGED");
    }

    public void UseActionId(int id, bool onSelf = false)
    {
        var packed = ActionAt(id);
        if (packed == 0)
            return;
        var action = packed & 0xFFFFFF;
        switch (packed >> 24)
        {
            case 0:
                CastSpell(action, onSelf);
                break;
            case 0x80:
                if (FindItem(action) is { } at)
                    UseItem(at.Bag, at.Slot);
                break;
        }
    }

    public void CastSpell(uint spellId, bool onSelf = false)
    {
        if (spellId == AttackSpell)
        {
            if (_play.AutoAttacking)
                _play.StopAttack();
            else if (_play.Target != 0)
                _play.StartAttack(_play.Target);
            return;
        }
        var spell = _text.Spell(spellId);
        if (spell is { Passive: true })
            return;
        var now = _session.NowMs;
        if (_play.Cooldown(spellId).Seconds > 0 || (spell?.GlobalCooldownMs > 0 && now < _gcdEnd))
        {
            ErrorRaised?.Invoke(_text["ERR_SPELL_COOLDOWN"]);
            return;
        }
        ulong target = 0;
        var selected = _play.Target != 0 ? _session.Objects.Get(_play.Target) : null;
        if (spell is { NeedsEnemy: true })
        {
            if (selected is null || selected.Guid == _session.PlayerGuid)
            {
                ErrorRaised?.Invoke(_text["SPELL_FAILED_BAD_IMPLICIT_TARGETS"]);
                return;
            }
            target = selected.Guid;
        }
        else if (spell is { Helpful: true })
            target = !onSelf && selected is not null && ReactionOf(selected) == Reaction.Friendly ? selected.Guid : _session.PlayerGuid;
        else if (spell is not { ImplicitTarget: SpellInfo.TargetSelf } && selected is not null)
            target = selected.Guid;
        _play.Cast(spellId, target);
        if (spell is { GlobalCooldownMs: > 0 } s)
        {
            (_gcdStart, _gcdEnd) = (now, now + s.GlobalCooldownMs);
            _ui.FireEvent("ACTIONBAR_UPDATE_COOLDOWN");
            _ui.FireEvent("SPELL_UPDATE_COOLDOWN");
        }
    }

    /// <summary>A failed cast gives the global cooldown back.</summary>
    public void ResetGlobalCooldown()
    {
        _gcdEnd = 0;
        _ui.FireEvent("ACTIONBAR_UPDATE_COOLDOWN");
        _ui.FireEvent("SPELL_UPDATE_COOLDOWN");
    }

    // ---- Spellbook -----------------------------------------------------------------------------------------

    /// <summary>Spellbook order: active spells by name and rank, then passive ones.</summary>
    private List<SpellInfo> _book = [];
    private int _bookVersion = -1;

    private List<SpellInfo> Book()
    {
        if (_bookVersion != _play.Spells.Count)
        {
            _book = _play.Spells.Select(_text.Spell).OfType<SpellInfo>().Where(s => !s.Hidden)
                .OrderBy(s => s.Passive).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Rank, StringComparer.Ordinal).ToList();
            _bookVersion = _play.Spells.Count;
        }
        return _book;
    }

    private SpellInfo? BookSpell(LuaArgs a) =>
        (a.Str(1) ?? "spell").Equals("pet", StringComparison.OrdinalIgnoreCase) ? null : Book().ElementAtOrDefault(a.Int(0) - 1);

    private void RegisterSpells()
    {
        Fn("GetNumSpellTabs", _ => N(1));
        Fn("GetSpellTabInfo", a => a.Int(0) == 1
            ? Tuple(S(_text["GENERAL"]), S(@"Interface\Icons\INV_Misc_Book_09"), N(0), N(Book().Count))
            : DynValue.Nil);
        Fn("GetSpellName", a => BookSpell(a) is { } s ? Tuple(S(s.Name), S(s.Passive ? (s.Rank.Length > 0 ? s.Rank + " " : "") + _text["SPELL_PASSIVE"] : s.Rank)) : DynValue.Nil);
        Fn("GetSpellTexture", a => S(BookSpell(a)?.Icon));
        Fn("GetSpellCooldown", a =>
        {
            if (BookSpell(a) is not { } s)
                return Tuple(N(0), N(0), N(1));
            var (start, duration) = SpellCooldown(s.Id);
            return Tuple(N(start), N(duration), N(1));
        });
        Fn("IsPassiveSpell", a => B(BookSpell(a) is { Passive: true }));
        Fn("IsSpellInRange", _ => DynValue.Nil);
        Fn("GetSpellAutocast", _ => DynValue.Nil);
        Do("CastSpell", a =>
        {
            if (BookSpell(a) is { } s)
                CastSpell(s.Id);
        });
        Do("CastSpellByName", a =>
        {
            var name = a.Str(0) ?? "";
            var paren = name.IndexOfAny(['(', '（']);
            var bare = (paren > 0 ? name[..paren] : name).Trim();
            if (Book().Where(s => s.Name == bare).LastOrDefault() is { } spell)
                CastSpell(spell.Id, a.Bool(1));
        });
        Do("PickupSpell", a =>
        {
            if (BookSpell(a) is { Passive: false } s)
            {
                _cursorItem = null;
                _cursorAction = s.Id;
            }
        });
        Do("UpdateSpells", _ => { });
    }
}
