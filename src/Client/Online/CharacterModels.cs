using System.Numerics;
using Client.World;
using Formats.Blp;
using Formats.Models;
using Texture = Engine.Rendering.Texture;

namespace Client.Online;

/// <summary>An item model on a character: file, its texture, and the M2 attachment point it hangs from.</summary>
public sealed record AttachedModel(string Model, string? Texture, int Attachment);

/// <summary>
/// What a character model shows besides its mesh: the body skin composited from CharSections and the worn items'
/// body-region textures, which hair, beard and equipment geosets are visible, and the item models (helmet,
/// shoulders, weapons, shield) attached to its bones.
/// </summary>
public sealed class CharacterModels(ClientData data, AssetCache assets)
{
    public const int Head = 0, Shoulder = 2, Shirt = 3, Chest = 4, Waist = 5, Legs = 6, Feet = 7, Wrist = 8, Hands = 9,
        Back = 14, MainHand = 15, OffHand = 16, Ranged = 17, Tabard = 18;

    private const int FaceUpper = 8, FaceLower = 9;
    private const int AttachShield = 0, AttachRightHand = 1, AttachLeftHand = 2, AttachRightShoulder = 5, AttachLeftShoulder = 6,
        AttachHelmet = 11, SheathMainHand = 26, SheathOffHand = 27, SheathShield = 28;

    /// <summary>Body regions of the 256x256 skin layout: the eight item regions, then face upper and lower.</summary>
    private static readonly (int X, int Y, int W, int H)[] Regions =
    [
        (0, 0, 128, 64), (0, 64, 128, 64), (0, 128, 128, 32), (128, 0, 128, 64), (128, 64, 128, 32),
        (128, 96, 128, 64), (128, 160, 128, 64), (128, 224, 128, 32), (0, 160, 128, 32), (0, 192, 128, 64),
    ];

    private static readonly string[] RegionFolders =
        ["ArmUpperTexture", "ArmLowerTexture", "HandTexture", "TorsoUpperTexture", "TorsoLowerTexture", "LegUpperTexture", "LegLowerTexture", "FootTexture"];

    /// <summary>Order worn items are painted onto the skin; later slots cover earlier ones.</summary>
    private static readonly int[] PaintOrder = [Shirt, Legs, Feet, Chest, Wrist, Hands, Waist, Tabard];

    /// <summary>The body texture (type 1): an NPC's baked skin, or the player's composited skin.</summary>
    public Texture? Body(Appearance look)
    {
        if (look.BakedSkin is { } baked)
            return assets.Texture(baked);
        return assets.Texture(BodyKey(look), () => Compose(look));
    }

    /// <summary>True once <see cref="Body"/> has its texture on the GPU (or knows it cannot make one).</summary>
    public bool IsBodySettled(Appearance look) => assets.IsTextureSettled(look.BakedSkin ?? BodyKey(look));

    private string BodyKey(Appearance look) =>
        $"@body:{look.Race}:{look.Sex}:{look.Skin}:{look.Face}:{look.HairStyle}:{look.HairColor}:{look.FacialHair}:" +
        string.Join(",", PaintOrder.Select(s => Item(look, s)?.Id ?? 0));

    public string? HairTexture(Appearance look) =>
        data.SectionTexture(look.Race, look.Sex, ClientData.SectionHair, look.HairStyle, look.HairColor) is { Length: > 0 } hair ? hair : null;

    /// <summary>Texture type 2 on the body: the worn cape.</summary>
    public string? CapeTexture(Appearance look) =>
        Item(look, Back) is { } cape && cape.ModelTextures[0] is { Length: > 0 } texture ? $"Item\\ObjectComponents\\Cape\\{texture}.blp" : null;

    /// <summary>
    /// Submesh visibility: body, the hair style (or the bald scalp cap), beard pieces, ears, and for each equipment
    /// group the default piece or the variant the worn item selects (gloves 4xx, boots 5xx, sleeves 8xx, ...).
    /// </summary>
    public Func<int, bool> Geosets(Appearance look)
    {
        var groups = Enumerable.Repeat(1, 20).ToArray();
        (groups[7], groups[12], groups[15]) = (2, 0, 0);
        var facial = data.FacialHairGeosets(look.Race, look.Sex, look.FacialHair);
        (groups[1], groups[2], groups[3]) = (facial[0], facial[2], facial[1]);
        var hair = data.HairGeoset(look.Race, look.Sex, look.HairStyle);

        if (Item(look, Hands) is { GeosetGroups: [> 0 and var gloves, ..] })
            groups[4] = 1 + gloves;
        if (Item(look, Feet) is { GeosetGroups: [> 0 and var boots, ..] })
            groups[5] = 1 + boots;
        foreach (var slot in new[] { Shirt, Chest })
            if (Item(look, slot) is { GeosetGroups: [> 0 and var sleeves, ..] })
                groups[8] = 1 + sleeves;
        if (Item(look, Legs) is { GeosetGroups: [> 0 and var knees, ..] })
            groups[9] = 1 + knees;
        foreach (var slot in new[] { Legs, Chest })
            if (Item(look, slot) is { GeosetGroups: [_, _, > 0 and var robe] })
                groups[13] = 1 + robe;
        if (Item(look, Tabard) is not null)
            groups[12] = 2;
        if (Item(look, Back) is { } cape)
            groups[15] = 1 + cape.GeosetGroups[0];

        var hideHair = false;
        if (Item(look, Head) is { } helmet)
        {
            var hides = data.HelmetHides(helmet.HelmetVis[look.Sex == 0 ? 0 : 1]);
            var bit = 1 << (look.Race - 1);
            hideHair = (hides[0] & bit) != 0;
            if ((hides[1] & bit) != 0) groups[1] = 0;
            if ((hides[2] & bit) != 0) groups[2] = 0;
            if ((hides[3] & bit) != 0) groups[3] = 0;
            if ((hides[4] & bit) != 0) groups[7] = 0;
        }
        var scalp = hideHair || hair == 0 ? 1 : hair;

        return id =>
        {
            var group = id / 100;
            if (group == 0)
                return id == 0 || id == scalp;
            return group < groups.Length && groups[group] != 0 && id % 100 == groups[group];
        };
    }

    /// <summary>Helmet, shoulders and weapons; <paramref name="sheathed"/> puts weapons on the back and hip.</summary>
    public IEnumerable<AttachedModel> Attachments(Appearance look, bool sheathed)
    {
        if (Item(look, Head) is { } helmet && Model(helmet, 0, "Head", $"_{data.RacePrefix(look.Race)}{(look.Sex == 0 ? "M" : "F")}") is { } helm)
            yield return new AttachedModel(helm, Texture(helmet, 0, "Head"), AttachHelmet);
        if (Item(look, Shoulder) is { } shoulders)
        {
            if (Model(shoulders, 0, "Shoulder") is { } left)
                yield return new AttachedModel(left, Texture(shoulders, 0, "Shoulder"), AttachLeftShoulder);
            if (Model(shoulders, 1, "Shoulder") is { } right)
                yield return new AttachedModel(right, Texture(shoulders, 1, "Shoulder"), AttachRightShoulder);
        }
        if (Item(look, MainHand) is { } main && Weapon(main) is { } mainModel)
            yield return new AttachedModel(mainModel.Model, mainModel.Texture, sheathed ? SheathMainHand : AttachRightHand);
        if (Item(look, OffHand) is { } off && Weapon(off) is { } offModel)
            yield return new AttachedModel(offModel.Model, offModel.Texture,
                offModel.Shield ? sheathed ? SheathShield : AttachShield : sheathed ? SheathOffHand : AttachLeftHand);
    }

    private ItemDisplay? Item(Appearance look, int slot) =>
        look.Items is { } items && slot < items.Length ? data.ItemDisplay(items[slot]) : null;

    private string? Model(ItemDisplay item, int index, string folder, string suffix = "")
    {
        var name = item.Models[index];
        if (string.IsNullOrEmpty(name))
            return null;
        var path = $"Item\\ObjectComponents\\{folder}\\{Path.GetFileNameWithoutExtension(name)}{suffix}.m2";
        return assets.Exists(path) ? path : null;
    }

    private static string? Texture(ItemDisplay item, int index, string folder) =>
        item.ModelTextures[index] is { Length: > 0 } texture ? $"Item\\ObjectComponents\\{folder}\\{texture}.blp" : null;

    private (string Model, string? Texture, bool Shield)? Weapon(ItemDisplay item)
    {
        if (Model(item, 0, "Weapon") is { } weapon)
            return (weapon, Texture(item, 0, "Weapon"), false);
        if (Model(item, 0, "Shield") is { } shield)
            return (shield, Texture(item, 0, "Shield"), true);
        return null;
    }

    /// <summary>
    /// Base skin, underwear, face, beard and scalp from CharSections, then each worn item's region textures
    /// (gender-specific "_M"/"_F" file, else unisex "_U"), alpha-blended in <see cref="PaintOrder"/>.
    /// </summary>
    private RgbaImage? Compose(Appearance look)
    {
        var (race, sex) = (look.Race, look.Sex);
        if (data.Section(race, sex, ClientData.SectionSkin, 0, look.Skin)?.Texture is not { Length: > 0 } skinFile ||
            assets.Image(skinFile) is not { } skin)
            return null;
        var canvas = new RgbaImage(skin.Width, skin.Height, (byte[])skin.Pixels.Clone());

        var underwear = data.Section(race, sex, 4, 0, look.Skin);
        Paint(canvas, underwear?.Texture, 5);
        Paint(canvas, underwear?.Texture2, 3);
        var face = data.Section(race, sex, ClientData.SectionFace, look.Face, look.Skin);
        Paint(canvas, face?.Texture, FaceLower);
        Paint(canvas, face?.Texture2, FaceUpper);
        var beard = data.Section(race, sex, ClientData.SectionFacialHair, look.FacialHair, look.HairColor);
        Paint(canvas, beard?.Texture, FaceLower);
        Paint(canvas, beard?.Texture2, FaceUpper);
        var hair = data.Section(race, sex, ClientData.SectionHair, look.HairStyle, look.HairColor);
        Paint(canvas, hair?.Texture2, FaceLower);
        Paint(canvas, hair?.Texture3, FaceUpper);

        var gender = sex == 0 ? "M" : "F";
        foreach (var slot in PaintOrder)
        {
            if (Item(look, slot) is not { } item)
                continue;
            for (var region = 0; region < RegionFolders.Length; region++)
            {
                if (item.Regions[region] is not { Length: > 0 } name)
                    continue;
                var stem = $"Item\\TextureComponents\\{RegionFolders[region]}\\{name}";
                var file = new[] { $"{stem}_{gender}.blp", $"{stem}_U.blp", $"{stem}.blp" }.FirstOrDefault(assets.Exists);
                Paint(canvas, file, region);
            }
        }
        return canvas;
    }

    /// <summary>Alpha-blends a texture over one region, scaled to the canvas size (nearest sample).</summary>
    private void Paint(RgbaImage canvas, string? file, int region)
    {
        if (string.IsNullOrEmpty(file) || assets.Image(file) is not { } layer)
            return;
        var scale = canvas.Width / 256f;
        var (rx, ry, rw, rh) = Regions[region];
        int x0 = (int)(rx * scale), y0 = (int)(ry * scale), w = (int)(rw * scale), h = (int)(rh * scale);
        for (var y = 0; y < h && y0 + y < canvas.Height; y++)
        {
            var sy = y * layer.Height / h;
            for (var x = 0; x < w && x0 + x < canvas.Width; x++)
            {
                var sx = x * layer.Width / w;
                var s = (sy * layer.Width + sx) * 4;
                var d = ((y0 + y) * canvas.Width + x0 + x) * 4;
                var a = layer.Pixels[s + 3] / 255f;
                if (a <= 0)
                    continue;
                for (var c = 0; c < 3; c++)
                    canvas.Pixels[d + c] = (byte)(layer.Pixels[s + c] * a + canvas.Pixels[d + c] * (1 - a));
                canvas.Pixels[d + 3] = Math.Max(canvas.Pixels[d + 3], layer.Pixels[s + 3]);
            }
        }
    }

    /// <summary>World transform of an attachment point on a (possibly posed) model.</summary>
    public static Matrix4x4? AttachmentTransform(ModelData model, int attachment, SkinnedActor? actor, Matrix4x4 unitTransform)
    {
        if (!model.Attachments.TryGetValue(attachment, out var point))
            return null;
        var bone = actor is not null && point.Bone < actor.Bones.Count ? actor.Bones[point.Bone] : Matrix4x4.Identity;
        return Matrix4x4.CreateTranslation(point.Position) * bone * unitTransform;
    }
}
