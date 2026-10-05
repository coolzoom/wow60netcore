using Formats.Dbc;
using Formats.Models;
using Formats.Mpq;

namespace Client.Online;

public sealed record RaceInfo(int Id, string Name, string FileString, string Faction, string Hair, string MaleFacialHair,
    string FemaleFacialHair);

public sealed record ClassInfo(int Id, string Name, string FileString);

/// <summary>One CharSections row: a texture choice for a race and sex (BaseSection 0 skin, 1 face, 2 facial hair, 3 hair).</summary>
public sealed record CharSection(int Race, int Sex, int Section, int Variation, int Color, string Texture, bool NpcOnly,
    string Texture2 = "", string Texture3 = "");

/// <summary>
/// Look of a character model: race, sex and appearance choices, an optional pre-baked skin (NPCs), and the
/// ItemDisplayInfo id worn in each equipment slot (index = inventory slot 0-18, 0 = empty).
/// </summary>
public sealed record Appearance(int Race, int Sex, int Skin, int Face, int HairStyle, int HairColor, int FacialHair, string? BakedSkin,
    int[]? Items = null);

/// <summary>
/// One ItemDisplayInfo row: up to two models with their textures (helmets, shoulders, weapons), geoset group values,
/// helmet hide rules (male, female) and the eight body-region textures painted onto the character skin.
/// </summary>
public sealed record ItemDisplay(int Id, string[] Models, string[] ModelTextures, int[] GeosetGroups, int[] HelmetVis, string[] Regions);

public sealed record DisplayInfo(string Model, float Scale, string?[] Skins, Appearance? Extra);

/// <summary>Client DBC tables the glue screens and the online world read (names, appearance choices, models).</summary>
public sealed class ClientData
{
    public const int SectionSkin = 0, SectionFace = 1, SectionFacialHair = 2, SectionHair = 3;

    private readonly Dictionary<int, string> _areas = [];
    private readonly Dictionary<int, DisplayInfo> _creatureDisplays = [];
    private readonly Dictionary<int, string> _gameObjectModels = [];
    private readonly Dictionary<(int Race, int Sex, int Variation), int> _hairGeosets = [];
    private readonly Dictionary<(int Race, int Sex, int Variation), int[]> _facialHairGeosets = [];
    private readonly Dictionary<int, ItemDisplay> _itemDisplays = [];
    private readonly Dictionary<int, int[]> _helmetVis = [];
    private readonly Dictionary<int, string> _racePrefixes = [];

    /// <summary>Inventory slots of CreatureDisplayInfoExtra's NPCItemDisplay columns (head ... tabard).</summary>
    private static readonly int[] NpcItemSlots = [0, 2, 3, 4, 5, 6, 7, 8, 9, 18];

    public IReadOnlyList<RaceInfo> Races { get; }
    public IReadOnlyList<ClassInfo> Classes { get; }
    /// <summary>Playable race/class combinations (CharBaseInfo).</summary>
    public IReadOnlySet<(int Race, int Class)> Combinations { get; }
    public IReadOnlyList<CharSection> Sections { get; }
    public IReadOnlyList<MapEntry> Maps { get; }

    public ClientData(MpqFileSystem files)
    {
        DbcFile Dbc(string name) => new(files.Read($"DBFilesClient\\{name}.dbc"));

        var races = Dbc("ChrRaces");
        Races = Rows(races).Select(r => new RaceInfo(races.GetInt(r, 0), races.GetLocalizedString(r, 17), races.GetString(r, 15),
                races.GetInt(r, 2) is 1 or 3 or 4 or 115 ? "Alliance" : "Horde", races.GetString(r, 28), races.GetString(r, 26),
                races.GetString(r, 27)))
            .ToList();

        var classes = Dbc("ChrClasses");
        Classes = Rows(classes).Select(r => new ClassInfo(classes.GetInt(r, 0), classes.GetLocalizedString(r, 5), classes.GetString(r, 14))).ToList();

        var baseInfo = files.Read("DBFilesClient\\CharBaseInfo.dbc");
        var combos = new HashSet<(int, int)>();
        var count = BitConverter.ToInt32(baseInfo, 4);
        var size = BitConverter.ToInt32(baseInfo, 12);
        for (var i = 0; i < count; i++)
            combos.Add((baseInfo[20 + i * size], baseInfo[21 + i * size]));
        Combinations = combos;

        var sections = Dbc("CharSections");
        Sections = Rows(sections).Select(r => new CharSection(sections.GetInt(r, 1), sections.GetInt(r, 2), sections.GetInt(r, 3),
            sections.GetInt(r, 4), sections.GetInt(r, 5), sections.GetString(r, 6), sections.GetInt(r, 9) != 0,
            sections.GetString(r, 7), sections.GetString(r, 8))).ToList();

        var hair = Dbc("CharHairGeosets");
        foreach (var r in Rows(hair))
            _hairGeosets[(hair.GetInt(r, 1), hair.GetInt(r, 2), hair.GetInt(r, 3))] = hair.GetInt(r, 4);
        var facial = Dbc("CharacterFacialHairStyles");
        foreach (var r in Rows(facial))
            _facialHairGeosets[(facial.GetInt(r, 0), facial.GetInt(r, 1), facial.GetInt(r, 2))] =
                [facial.GetInt(r, 6), facial.GetInt(r, 7), facial.GetInt(r, 8)];

        var areas = Dbc("AreaTable");
        foreach (var r in Rows(areas))
            _areas[areas.GetInt(r, 0)] = areas.GetLocalizedString(r, 11);

        var models = Dbc("CreatureModelData");
        var modelPaths = Rows(models).ToDictionary(r => models.GetInt(r, 0), r => M2Model.NormalizePath(models.GetString(r, 2)));
        var extras = Dbc("CreatureDisplayInfoExtra");
        var appearances = Rows(extras).ToDictionary(r => extras.GetInt(r, 0), r =>
        {
            var items = new int[19];
            for (var i = 0; i < NpcItemSlots.Length; i++)
                items[NpcItemSlots[i]] = extras.GetInt(r, 8 + i);
            return new Appearance(extras.GetInt(r, 1), extras.GetInt(r, 2), extras.GetInt(r, 3), extras.GetInt(r, 4), extras.GetInt(r, 5),
                extras.GetInt(r, 6), extras.GetInt(r, 7),
                extras.GetString(r, 18) is { Length: > 0 } baked ? $"Textures\\BakedNpcTextures\\{baked}" : null, items);
        });

        var itemDisplays = Dbc("ItemDisplayInfo");
        foreach (var r in Rows(itemDisplays))
        {
            var id = itemDisplays.GetInt(r, 0);
            _itemDisplays[id] = new ItemDisplay(id, [itemDisplays.GetString(r, 1), itemDisplays.GetString(r, 2)],
                [itemDisplays.GetString(r, 3), itemDisplays.GetString(r, 4)],
                [itemDisplays.GetInt(r, 7), itemDisplays.GetInt(r, 8), itemDisplays.GetInt(r, 9)],
                [itemDisplays.GetInt(r, 13), itemDisplays.GetInt(r, 14)],
                [.. Enumerable.Range(15, 8).Select(f => itemDisplays.GetString(r, f))]);
        }
        var helmets = Dbc("HelmetGeosetVisData");
        foreach (var r in Rows(helmets))
            _helmetVis[helmets.GetInt(r, 0)] = [.. Enumerable.Range(1, 5).Select(f => helmets.GetInt(r, f))];
        foreach (var r in Rows(races))
            _racePrefixes[races.GetInt(r, 0)] = races.GetString(r, 6);
        var displays = Dbc("CreatureDisplayInfo");
        foreach (var r in Rows(displays))
        {
            if (!modelPaths.TryGetValue(displays.GetInt(r, 1), out var model) || model.Length == 0)
                continue;
            var directory = model[..(model.LastIndexOf('\\') + 1)];
            var skins = Enumerable.Range(6, 3).Select(f => displays.GetString(r, f) is { Length: > 0 } s ? $"{directory}{s}.blp" : null).ToArray();
            var scale = displays.GetFloat(r, 4);
            _creatureDisplays[displays.GetInt(r, 0)] = new DisplayInfo(model, scale > 0 ? scale : 1f, skins,
                appearances.GetValueOrDefault(displays.GetInt(r, 3)));
        }

        var objects = Dbc("GameObjectDisplayInfo");
        foreach (var r in Rows(objects))
            if (objects.GetString(r, 1) is { Length: > 0 } path)
                _gameObjectModels[objects.GetInt(r, 0)] = M2Model.NormalizePath(path);

        Maps = MapDbc.Read(files.Read("DBFilesClient\\Map.dbc")).ToList();
    }

    private static IEnumerable<int> Rows(DbcFile dbc) => Enumerable.Range(0, dbc.RecordCount);

    public RaceInfo? Race(int id) => Races.FirstOrDefault(r => r.Id == id);
    public ClassInfo? Class(int id) => Classes.FirstOrDefault(c => c.Id == id);
    public string AreaName(int id) => _areas.GetValueOrDefault(id, "");
    public DisplayInfo? CreatureDisplay(int id) => _creatureDisplays.GetValueOrDefault(id);
    public string? GameObjectModel(int id) => _gameObjectModels.GetValueOrDefault(id);
    public MapEntry? Map(int id) => Maps.FirstOrDefault(m => m.Id == id);

    public int HairGeoset(int race, int sex, int style) => _hairGeosets.GetValueOrDefault((race, sex, style), 1);

    /// <summary>Geosets for the 1xx, 3xx and 2xx groups (zero = group hidden).</summary>
    public int[] FacialHairGeosets(int race, int sex, int style) => _facialHairGeosets.GetValueOrDefault((race, sex, style), [1, 1, 1]);

    public IEnumerable<int> FacialHairStyles(int race, int sex) =>
        _facialHairGeosets.Keys.Where(k => k.Race == race && k.Sex == sex).Select(k => k.Variation).Order();

    /// <summary>Texture of a section choice, or null when the combination does not exist.</summary>
    public string? SectionTexture(int race, int sex, int section, int variation, int color) =>
        Section(race, sex, section, variation, color)?.Texture;

    public CharSection? Section(int race, int sex, int section, int variation, int color) =>
        Sections.FirstOrDefault(s => s.Race == race && s.Sex == sex && s.Section == section && s.Variation == variation && s.Color == color);

    public ItemDisplay? ItemDisplay(int id) => id == 0 ? null : _itemDisplays.GetValueOrDefault(id);

    /// <summary>HelmetGeosetVisData masks (hair, facial 1xx, 2xx, 3xx, ears) by race bit; empty when nothing is hidden.</summary>
    public int[] HelmetHides(int visId) => _helmetVis.GetValueOrDefault(visId) ?? [0, 0, 0, 0, 0];

    /// <summary>Two-letter model prefix of a race ("Hu", "Or", ...), used in helmet file names.</summary>
    public string RacePrefix(int race) => _racePrefixes.GetValueOrDefault(race, "Hu");
}
