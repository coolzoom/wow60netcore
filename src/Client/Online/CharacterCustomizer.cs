using Net;

namespace Client.Online;

/// <summary>
/// Race, sex, class and appearance picked on the character create screen. Every choice offered comes from
/// CharSections / CharacterFacialHairStyles, the same tables the server validates a new character against.
/// </summary>
public sealed class CharacterCustomizer
{
    /// <summary>Order of the race buttons: Alliance then Horde.</summary>
    private static readonly int[] RaceOrder = [1, 3, 4, 7, 2, 5, 6, 8];
    private const int RaceTauren = 6, RaceNightElf = 4, RaceUndead = 5;

    private readonly ClientData _data;
    private readonly Random _random = new();

    public CharacterCustomizer(ClientData data)
    {
        _data = data;
        Races = RaceOrder.Select(data.Race).OfType<RaceInfo>()
            .Where(r => data.Combinations.Any(c => c.Race == r.Id))
            .ToList();
        SetRace(0);
    }

    public IReadOnlyList<RaceInfo> Races { get; }
    public int RaceIndex { get; private set; }
    public RaceInfo Race => Races[RaceIndex];
    /// <summary>0 male, 1 female.</summary>
    public int Sex { get; private set; }
    public IReadOnlyList<ClassInfo> Classes { get; private set; } = [];
    public int ClassIndex { get; private set; }
    public ClassInfo Class => Classes[ClassIndex];

    public int Skin { get; private set; }
    public int Face { get; private set; }
    public int HairStyle { get; private set; }
    public int HairColor { get; private set; }
    public int FacialHair { get; private set; }

    public string FacialHairKind => Sex == 0 ? Race.MaleFacialHair : Race.FemaleFacialHair;

    public void SetRace(int index)
    {
        RaceIndex = Math.Clamp(index, 0, Races.Count - 1);
        Classes = _data.Classes.Where(c => _data.Combinations.Contains((Race.Id, c.Id))).ToList();
        ClassIndex = 0;
        Normalize();
    }

    public void SetSex(int sex)
    {
        Sex = sex == 0 ? 0 : 1;
        Normalize();
    }

    public void SetClass(int index) => ClassIndex = Math.Clamp(index, 0, Classes.Count - 1);

    /// <summary>A random race, sex and appearance, as the screen starts with.</summary>
    public void Reset()
    {
        Sex = _random.Next(2);
        SetRace(_random.Next(Races.Count));
        Randomize();
    }

    public void Randomize()
    {
        Skin = Pick(Skins());
        Face = Pick(Faces());
        HairStyle = Pick(HairStyles());
        HairColor = Pick(HairColors());
        FacialHair = Pick(FacialHairStyles());
    }

    /// <summary>Steps customization <paramref name="id"/> (1 skin, 2 face, 3 hair style, 4 hair color, 5 facial hair).</summary>
    public void Cycle(int id, int delta)
    {
        switch (id)
        {
            case 1: Skin = Step(Skins(), Skin, delta); break;
            case 2: Face = Step(Faces(), Face, delta); break;
            case 3: HairStyle = Step(HairStyles(), HairStyle, delta); break;
            case 4: HairColor = Step(HairColors(), HairColor, delta); break;
            case 5: FacialHair = Step(FacialHairStyles(), FacialHair, delta); break;
        }
        Normalize();
    }

    /// <summary>How the character being created looks, in its class's starting outfit.</summary>
    public Appearance Look => new(Race.Id, Sex, Skin, Face, HairStyle, HairColor, FacialHair, null,
        _data.StartOutfit(Race.Id, Class.Id, Sex));

    public CharacterCreateInfo Build(string name) => new(name, (byte)Race.Id, (byte)Class.Id, (byte)Sex, (byte)Skin, (byte)Face,
        (byte)HairStyle, (byte)HairColor, (byte)FacialHair);

    private List<int> Options(int section, Func<CharSection, bool> match, Func<CharSection, int> value) =>
        _data.Sections.Where(s => s.Race == Race.Id && s.Sex == Sex && s.Section == section && !s.NpcOnly && match(s))
            .Select(value).Distinct().Order().ToList();

    private List<int> Skins() => Options(ClientData.SectionSkin, s => s.Variation == 0, s => s.Color);
    private List<int> Faces() => Options(ClientData.SectionFace, s => s.Color == Skin, s => s.Variation);
    private List<int> HairStyles() => Options(ClientData.SectionHair, _ => true, s => s.Variation);
    private List<int> HairColors() => Options(ClientData.SectionHair, s => s.Variation == HairStyle, s => s.Color);

    private List<int> FacialHairStyles()
    {
        var styles = _data.FacialHairStyles(Race.Id, Sex).ToList();
        var needsTexture = Race.Id != RaceTauren && (Sex == 0 || Race.Id is RaceNightElf or RaceUndead);
        return needsTexture
            ? styles.Where(f => _data.SectionTexture(Race.Id, Sex, ClientData.SectionFacialHair, f, HairColor) is not null).ToList()
            : styles;
    }

    /// <summary>Keeps every value valid after a choice it depends on changed (skin → face, style → color → beard).</summary>
    private void Normalize()
    {
        Skin = Keep(Skins(), Skin);
        Face = Keep(Faces(), Face);
        HairStyle = Keep(HairStyles(), HairStyle);
        HairColor = Keep(HairColors(), HairColor);
        FacialHair = Keep(FacialHairStyles(), FacialHair);
    }

    private static int Keep(List<int> options, int value) => options.Count == 0 || options.Contains(value) ? value : options[0];

    private static int Step(List<int> options, int value, int delta)
    {
        if (options.Count == 0)
            return value;
        var index = options.IndexOf(value);
        return options[((index < 0 ? 0 : index + delta) % options.Count + options.Count) % options.Count];
    }

    private int Pick(List<int> options) => options.Count == 0 ? 0 : options[_random.Next(options.Count)];
}
