using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Formats.Dbc;
using Formats.Mpq;

namespace Client.Online;

public sealed record SpellInfo(uint Id, string Name, string Rank, string Description, string? Icon, uint School, uint CastTimeMs,
    float MaxRange, uint PowerType, uint PowerCost, uint RecoveryMs, uint GlobalCooldownMs, bool Passive, bool Hidden, int ImplicitTarget)
{
    /// <summary>EffectImplicitTargetA values of the first effect.</summary>
    public const int TargetSelf = 1, TargetEnemy = 6, TargetAlly = 21, TargetAny = 25, TargetParty = 57;

    public string FullName => Rank.Length > 0 ? $"{Name}（{Rank}）" : Name;
    public bool NeedsEnemy => ImplicitTarget == TargetEnemy;
    public bool Helpful => ImplicitTarget is TargetAlly or TargetAny or TargetParty;
}

public enum Reaction
{
    Hostile,
    Neutral,
    Friendly,
}

/// <summary>
/// What the in-game UI shows besides models: spell and item tables (names, icons, cast times), faction reactions,
/// and the client's own localized strings from GlobalStrings.lua.
/// </summary>
public sealed partial class GameText
{
    private const uint AttrPassive = 0x40, AttrHidden = 0x80;
    private const int FactionPlayerMask = 1;

    private readonly Dictionary<uint, SpellInfo> _spells = [];
    private readonly Dictionary<int, string> _itemIcons = [];
    private readonly Dictionary<int, FactionTemplate> _factions = [];
    private readonly Dictionary<int, string> _itemClasses = [];
    private readonly Dictionary<(int, int), string> _itemSubclasses = [];
    private readonly Dictionary<string, string> _strings = [];

    private sealed record FactionTemplate(int Faction, int Flags, int OurMask, int FriendlyMask, int HostileMask, int[] Enemies, int[] Friends);

    public GameText(MpqFileSystem files)
    {
        DbcFile Dbc(string name) => new(files.Read($"DBFilesClient\\{name}.dbc"));
        static IEnumerable<int> Rows(DbcFile dbc) => Enumerable.Range(0, dbc.RecordCount);

        var icons = Dbc("SpellIcon");
        var iconPaths = Rows(icons).ToDictionary(r => icons.GetInt(r, 0), r => icons.GetString(r, 1));
        var castTimes = Dbc("SpellCastTimes");
        var castMs = Rows(castTimes).ToDictionary(r => castTimes.GetInt(r, 0), r => castTimes.GetInt(r, 1));
        var ranges = Dbc("SpellRange");
        var maxRange = Rows(ranges).ToDictionary(r => ranges.GetInt(r, 0), r => ranges.GetFloat(r, 2));
        var durations = Dbc("SpellDuration");
        var durationMs = Rows(durations).ToDictionary(r => durations.GetInt(r, 0), r => durations.GetInt(r, 1));

        var spells = Dbc("Spell");
        foreach (var r in Rows(spells))
        {
            var id = (uint)spells.GetInt(r, 0);
            var attributes = (uint)spells.GetInt(r, 6);
            var icon = iconPaths.GetValueOrDefault(spells.GetInt(r, 117));
            var description = Describe(spells, r, spells.GetLocalizedString(r, 138), durationMs.GetValueOrDefault(spells.GetInt(r, 30)));
            _spells[id] = new SpellInfo(id, spells.GetLocalizedString(r, 120), spells.GetLocalizedString(r, 129), description,
                icon is { Length: > 0 } ? icon + ".blp" : null, (uint)spells.GetInt(r, 1), (uint)Math.Max(0, castMs.GetValueOrDefault(spells.GetInt(r, 18))),
                maxRange.GetValueOrDefault(spells.GetInt(r, 36)), (uint)spells.GetInt(r, 31), (uint)spells.GetInt(r, 32),
                (uint)Math.Max(spells.GetInt(r, 19), spells.GetInt(r, 20)), spells.GetInt(r, 157) == 133 ? (uint)spells.GetInt(r, 158) : 0,
                (attributes & AttrPassive) != 0, (attributes & AttrHidden) != 0, spells.GetInt(r, 82));
        }

        var displays = Dbc("ItemDisplayInfo");
        foreach (var r in Rows(displays))
            if (displays.GetString(r, 5) is { Length: > 0 } icon)
                _itemIcons[displays.GetInt(r, 0)] = $"Interface\\Icons\\{icon}.blp";

        var classes = Dbc("ItemClass");
        foreach (var r in Rows(classes))
            _itemClasses[classes.GetInt(r, 0)] = classes.GetLocalizedString(r, 3);
        var subclasses = Dbc("ItemSubClass");
        foreach (var r in Rows(subclasses))
            _itemSubclasses[(subclasses.GetInt(r, 0), subclasses.GetInt(r, 1))] = subclasses.GetLocalizedString(r, 10);

        var factions = Dbc("FactionTemplate");
        foreach (var r in Rows(factions))
            _factions[factions.GetInt(r, 0)] = new FactionTemplate(factions.GetInt(r, 1), factions.GetInt(r, 2), factions.GetInt(r, 3),
                factions.GetInt(r, 4), factions.GetInt(r, 5), [.. Enumerable.Range(6, 4).Select(f => factions.GetInt(r, f))],
                [.. Enumerable.Range(10, 4).Select(f => factions.GetInt(r, f))]);

        if (files.TryRead("Interface\\FrameXML\\GlobalStrings.lua") is { } lua)
            foreach (Match m in StringLine().Matches(Encoding.UTF8.GetString(lua)))
                _strings[m.Groups[1].Value] = Regex.Unescape(m.Groups[2].Value);
    }

    public SpellInfo? Spell(uint id) => _spells.GetValueOrDefault(id);
    public string SpellName(uint id) => _spells.GetValueOrDefault(id)?.Name ?? $"法术 {id}";
    public string? ItemIcon(uint displayId) => _itemIcons.GetValueOrDefault((int)displayId);

    /// <summary>"单手剑" for a mask naming one subclass, otherwise the class name ("武器").</summary>
    public string ItemKind(uint itemClass, uint subclassMask)
    {
        if (subclassMask != 0 && (subclassMask & (subclassMask - 1)) == 0 &&
            _itemSubclasses.GetValueOrDefault(((int)itemClass, System.Numerics.BitOperations.TrailingZeroCount(subclassMask))) is { Length: > 0 } sub)
            return sub;
        return _itemClasses.GetValueOrDefault((int)itemClass) ?? "";
    }

    /// <summary>A GlobalStrings.lua value, or the key itself when the client has no such string.</summary>
    public string this[string key] => _strings.GetValueOrDefault(key, key);
    public bool Has(string key) => _strings.ContainsKey(key);

    /// <summary>Fills a GlobalStrings printf template (%s, %d, %c as a sign, %.1f, %.3g) with <paramref name="args"/>.</summary>
    public string Format(string key, params object[] args)
    {
        var next = 0;
        var signed = false;
        return Printf().Replace(this[key], m =>
        {
            if (m.Value == "%%")
                return "%";
            if (next >= args.Length)
                return "";
            var arg = args[next++];
            var afterSign = signed;
            signed = m.Groups[2].Value == "c";
            return m.Groups[2].Value switch
            {
                "c" => Convert.ToDouble(arg, CultureInfo.InvariantCulture) < 0 ? "-" : "+",
                "d" => (afterSign ? Math.Abs(Convert.ToInt64(arg, CultureInfo.InvariantCulture)) : Convert.ToInt64(arg, CultureInfo.InvariantCulture))
                    .ToString(CultureInfo.InvariantCulture),
                "f" => Convert.ToDouble(arg, CultureInfo.InvariantCulture).ToString("F1", CultureInfo.InvariantCulture),
                "g" => Convert.ToDouble(arg, CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture),
                _ => arg.ToString() ?? "",
            };
        });
    }

    /// <summary>How a unit with faction template <paramref name="theirs"/> regards the player (template <paramref name="mine"/>).</summary>
    public Reaction React(uint theirs, uint mine)
    {
        if (!_factions.TryGetValue((int)theirs, out var them) || !_factions.TryGetValue((int)mine, out var me))
            return Reaction.Neutral;
        if (Hostile(them, me) || Hostile(me, them))
            return Reaction.Hostile;
        if (them.Enemies.Contains(me.Faction))
            return Reaction.Hostile;
        if (them.Friends.Contains(me.Faction) || (them.FriendlyMask & me.OurMask) != 0 || (them.OurMask & me.FriendlyMask) != 0)
            return Reaction.Friendly;
        return Reaction.Neutral;
    }

    /// <summary>Hostile to players regardless of their faction (aggressive monsters).</summary>
    public bool HostileToPlayers(uint template) =>
        _factions.TryGetValue((int)template, out var t) && (t.HostileMask & FactionPlayerMask) != 0;

    private static bool Hostile(FactionTemplate a, FactionTemplate b)
    {
        if (b.Faction != 0 && a.Enemies.Contains(b.Faction))
            return true;
        if (b.Faction != 0 && a.Friends.Contains(b.Faction))
            return false;
        return (a.HostileMask & b.OurMask) != 0;
    }

    /// <summary>Fills the common description tokens: $s1..3 (effect amounts), $o1..3 (periodic totals), $d (duration).</summary>
    private static string Describe(DbcFile spells, int r, string text, int durationMs)
    {
        if (!text.Contains('$'))
            return text;
        string Amount(int effect, bool total)
        {
            var basePoints = spells.GetInt(r, 76 + effect) + 1;
            var dice = spells.GetInt(r, 64 + effect);
            if (total)
            {
                var amplitude = spells.GetInt(r, 94 + effect);
                var ticks = amplitude > 0 ? durationMs / amplitude : 1;
                return (Math.Abs(basePoints) * Math.Max(1, ticks)).ToString(CultureInfo.InvariantCulture);
            }
            return dice > 1 ? $"{Math.Abs(basePoints)}到{Math.Abs(basePoints) + dice - 1}" : Math.Abs(basePoints).ToString(CultureInfo.InvariantCulture);
        }
        return Token().Replace(text, m =>
        {
            var kind = m.Groups[2].Value.ToLowerInvariant();
            if (m.Groups[1].Value.Length > 0)
                return m.Value;
            var index = m.Groups[3].Value.Length > 0 ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) - 1 : 0;
            return kind switch
            {
                "s" when index is >= 0 and < 3 => Amount(index, false),
                "o" when index is >= 0 and < 3 => Amount(index, true),
                "d" => durationMs >= 60000 ? $"{durationMs / 60000}分钟" : $"{durationMs / 1000}秒",
                _ => m.Value,
            };
        });
    }

    [GeneratedRegex(@"^(\w+)\s*=\s*""((?:[^""\\]|\\.)*)"";", RegexOptions.Multiline)]
    private static partial Regex StringLine();

    [GeneratedRegex(@"%%|%(\.\d+)?([cdsfg])")]
    private static partial Regex Printf();

    [GeneratedRegex(@"\$(\d*)([sodSOD])(\d?)")]
    private static partial Regex Token();
}
