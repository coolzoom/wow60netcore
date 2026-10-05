using System.Numerics;

namespace Net;

public sealed record EquipmentSlot(uint DisplayId, byte InventoryType);

/// <summary>One character of SMSG_CHAR_ENUM.</summary>
public sealed record CharacterInfo(
    ulong Guid, string Name, byte Race, byte Class, byte Gender, byte Skin, byte Face, byte HairStyle, byte HairColor,
    byte FacialHair, byte Level, uint Zone, uint Map, Vector3 Position, uint Guild, uint Flags, bool FirstLogin,
    IReadOnlyList<EquipmentSlot> Equipment)
{
    public const uint FlagGhost = 0x2000;
    public bool IsGhost => (Flags & FlagGhost) != 0;

    /// <summary>19 equipment slots and the first bag.</summary>
    public const int EnumSlots = 20;

    public static IReadOnlyList<CharacterInfo> ParseEnum(byte[] body)
    {
        var r = new PacketReader(body);
        var count = r.U8();
        var list = new List<CharacterInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var guid = r.U64();
            var name = r.CString();
            var race = r.U8();
            var cls = r.U8();
            var gender = r.U8();
            var skin = r.U8();
            var face = r.U8();
            var hairStyle = r.U8();
            var hairColor = r.U8();
            var facialHair = r.U8();
            var level = r.U8();
            var zone = r.U32();
            var map = r.U32();
            var position = r.Vector3();
            var guild = r.U32();
            var flags = r.U32();
            var firstLogin = r.U8() != 0;
            r.Skip(12);
            var equipment = new EquipmentSlot[EnumSlots];
            for (var s = 0; s < EnumSlots; s++)
                equipment[s] = new EquipmentSlot(r.U32(), r.U8());
            list.Add(new CharacterInfo(guid, name, race, cls, gender, skin, face, hairStyle, hairColor, facialHair, level, zone,
                map, position, guild, flags, firstLogin, equipment));
        }
        return list;
    }
}

/// <summary>Fields of CMSG_CHAR_CREATE.</summary>
public sealed record CharacterCreateInfo(string Name, byte Race, byte Class, byte Gender, byte Skin, byte Face,
    byte HairStyle, byte HairColor, byte FacialHair)
{
    public byte[] Serialize() => new PacketWriter().CString(Name).U8(Race).U8(Class).U8(Gender).U8(Skin).U8(Face)
        .U8(HairStyle).U8(HairColor).U8(FacialHair).U8(0).ToArray();
}
