using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MasterEvent.Models;

public sealed class NpcAppearance
{
    public string Name { get; set; } = "PNJ";

    public byte Race { get; set; }
    public byte Tribe { get; set; }
    public byte Sex { get; set; }
    public byte BodyType { get; set; }
    public byte Height { get; set; }
    public byte Face { get; set; }
    public byte HairStyle { get; set; }
    public byte Highlights { get; set; }
    public byte SkinColor { get; set; }
    public byte HairColor { get; set; }
    public byte HighlightsColor { get; set; }
    public byte EyeColorRight { get; set; }
    public byte EyeColorLeft { get; set; }
    public byte EyeShape { get; set; }
    public byte Eyebrows { get; set; }
    public byte Nose { get; set; }
    public byte Jaw { get; set; }
    public byte Lipstick { get; set; }
    public byte LipColor { get; set; }
    public byte FacialFeatures { get; set; }
    public byte FacialFeaturesColor { get; set; }
    public byte MuscleMass { get; set; }
    public byte TailShape { get; set; }
    public byte BustSize { get; set; }
    public byte FacePaint { get; set; }
    public byte FacePaintColor { get; set; }

    public EquipPiece? Head { get; set; }
    public EquipPiece? Body { get; set; }
    public EquipPiece? Hands { get; set; }
    public EquipPiece? Legs { get; set; }
    public EquipPiece? Feet { get; set; }
    public EquipPiece? Ears { get; set; }
    public EquipPiece? Neck { get; set; }
    public EquipPiece? Wrists { get; set; }
    public EquipPiece? RingLeft { get; set; }
    public EquipPiece? RingRight { get; set; }

    public WeaponPiece? MainHand { get; set; }
    public WeaponPiece? OffHand { get; set; }

    public bool HideWeapons { get; set; }
    public bool HideHeadgear { get; set; }

    public sealed class EquipPiece
    {
        public ushort ModelId { get; set; }
        public byte Variant { get; set; }
        public byte Stain { get; set; }
        public byte Stain2 { get; set; }
    }

    public sealed class WeaponPiece
    {
        public ushort ModelId { get; set; }
        public ushort ModelBase { get; set; }
        public ushort Variant { get; set; }
        public byte Stain { get; set; }
        public byte Stain2 { get; set; }
    }

    public static NpcAppearance Default()
    {
        return new NpcAppearance
        {
            Name = "PNJ",
            Race = 1,
            Tribe = 1,
            Sex = 1,
            BodyType = 1,
            Height = 50,
            Face = 1,
            HairStyle = 1,
            SkinColor = 1,
            HairColor = 1,
            EyeColorRight = 1,
            EyeColorLeft = 1,
            EyeShape = 1,
            Eyebrows = 1,
            Nose = 1,
            Jaw = 1,
            Lipstick = 1,
            BustSize = 50,
        };
    }


    private static readonly Dictionary<string, byte> RaceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hyur"] = 1, ["Elezen"] = 2, ["Lalafell"] = 3, ["Miqote"] = 4,
        ["Roegadyn"] = 5, ["AuRa"] = 6, ["Hrothgar"] = 7, ["Viera"] = 8,
    };

    private static readonly Dictionary<string, byte> TribeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Midlander"] = 1, ["Highlander"] = 2, ["Wildwood"] = 3, ["Duskwight"] = 4,
        ["Plainsfolk"] = 5, ["Dunesfolk"] = 6, ["SeekerOfTheSun"] = 7, ["KeeperOfTheMoon"] = 8,
        ["SeaWolf"] = 9, ["Hellsguard"] = 10, ["Raen"] = 11, ["Xaela"] = 12,
        ["Helion"] = 13, ["Lost"] = 14, ["Rava"] = 15, ["Veena"] = 16,
    };

    private static readonly Dictionary<string, byte> GenderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Masculine"] = 0, ["Feminine"] = 1,
    };

    private static readonly Dictionary<string, byte> AgeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Normal"] = 1, ["Old"] = 3, ["Young"] = 4,
    };

    private static readonly Dictionary<string, byte> FacialFeatureFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["First"] = 1, ["Second"] = 2, ["Third"] = 4, ["Fourth"] = 8,
        ["Fifth"] = 16, ["Sixth"] = 32, ["Seventh"] = 64, ["LegacyTattoo"] = 128,
    };

    private static byte ReadValue(JsonElement parent, string key, Dictionary<string, byte>? names)
    {
        if (!parent.TryGetProperty(key, out var v)) return 0;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return v.TryGetByte(out var b) ? b : (byte)0;
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.String:
                var text = v.GetString()?.Trim();
                if (string.IsNullOrEmpty(text)) return 0;
                if (byte.TryParse(text, out var parsed)) return parsed;
                return names != null && names.TryGetValue(text, out var named) ? named : (byte)0;
            default:
                return 0;
        }
    }

    private static byte ReadFacialFeatures(JsonElement root)
    {
        if (!root.TryGetProperty("FacialFeatures", out var v) || v.ValueKind != JsonValueKind.String)
            return ReadValue(root, "FacialFeatures", null);

        var text = v.GetString();
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (byte.TryParse(text.Trim(), out var numeric)) return numeric;

        byte flags = 0;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (FacialFeatureFlags.TryGetValue(part, out var bit)) flags |= bit;
        }
        return flags;
    }

    public static NpcAppearance? FromAnamnesisJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var appearance = Default();
            byte ReadByte(string key) => ReadValue(root, key, null);
            byte ReadEnum(string key, Dictionary<string, byte> names) => ReadValue(root, key, names);
            ushort ReadUShort(JsonElement parent, string key) =>
                parent.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt16(out var u) ? u : (ushort)0;
            byte ReadEquipByte(JsonElement parent, string key) => ReadValue(parent, key, null);

            appearance.Race = ReadEnum("Race", RaceNames);
            appearance.Tribe = ReadEnum("Tribe", TribeNames);
            appearance.Sex = ReadEnum("Gender", GenderNames);
            appearance.BodyType = ReadEnum("Age", AgeNames);
            appearance.Height = ReadByte("Height");
            appearance.Face = ReadByte("Head");
            appearance.HairStyle = ReadByte("Hair");
            appearance.Highlights = ReadByte("EnableHighlights");
            appearance.SkinColor = ReadByte("Skintone");
            appearance.HairColor = ReadByte("HairTone");
            appearance.HighlightsColor = ReadByte("Highlights");
            appearance.EyeColorRight = ReadByte("REyeColor");
            appearance.EyeColorLeft = ReadByte("LEyeColor");
            appearance.EyeShape = ReadByte("Eyes");
            appearance.Eyebrows = ReadByte("Eyebrows");
            appearance.Nose = ReadByte("Nose");
            appearance.Jaw = ReadByte("Jaw");
            appearance.Lipstick = ReadByte("Mouth");
            appearance.LipColor = ReadByte("LipsToneFurPattern");
            appearance.FacialFeatures = ReadFacialFeatures(root);
            appearance.FacialFeaturesColor = ReadByte("LimbalEyes");
            appearance.MuscleMass = root.TryGetProperty("EarMuscleTailSize", out _) ? ReadByte("EarMuscleTailSize") : ReadByte("MuscleTone");
            appearance.TailShape = ReadByte("TailEarsType");
            appearance.BustSize = ReadByte("Bust");
            appearance.FacePaint = ReadByte("FacePaint");
            appearance.FacePaintColor = ReadByte("FacePaintColor");

            EquipPiece? ReadEquip(string section)
            {
                if (!root.TryGetProperty(section, out var node) || node.ValueKind != JsonValueKind.Object) return null;
                var piece = new EquipPiece
                {
                    ModelId = ReadUShort(node, "ModelBase"),
                    Variant = ReadEquipByte(node, "ModelVariant"),
                    Stain = ReadEquipByte(node, "DyeId"),
                    Stain2 = ReadEquipByte(node, "DyeId2"),
                };
                return piece;
            }

            WeaponPiece? ReadWeapon(string section)
            {
                if (!root.TryGetProperty(section, out var node) || node.ValueKind != JsonValueKind.Object) return null;
                var piece = new WeaponPiece
                {
                    ModelId = ReadUShort(node, "ModelSet"),
                    ModelBase = ReadUShort(node, "ModelBase"),
                    Variant = ReadUShort(node, "ModelVariant"),
                    Stain = ReadEquipByte(node, "DyeId"),
                    Stain2 = ReadEquipByte(node, "DyeId2"),
                };
                return piece;
            }

            appearance.Head = ReadEquip("HeadGear");
            appearance.Body = ReadEquip("Body");
            appearance.Hands = ReadEquip("Hands");
            appearance.Legs = ReadEquip("Legs");
            appearance.Feet = ReadEquip("Feet");
            appearance.Ears = ReadEquip("Ears");
            appearance.Neck = ReadEquip("Neck");
            appearance.Wrists = ReadEquip("Wrists");
            appearance.RingLeft = ReadEquip("LeftRing");
            appearance.RingRight = ReadEquip("RightRing");

            appearance.MainHand = ReadWeapon("MainHand");
            appearance.OffHand = ReadWeapon("OffHand");

            return appearance;
        }
        catch
        {
            return null;
        }
    }
}
