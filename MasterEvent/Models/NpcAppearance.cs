using System.Text.Json;
using System.Text.Json.Serialization;

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

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, AppearanceJsonContext.Default.NpcAppearance);
    }

    public static NpcAppearance? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize(json, AppearanceJsonContext.Default.NpcAppearance);
        }
        catch
        {
            return null;
        }
    }

    // Format Anamnesis : fichier .chara avec champs PascalCase et propriétés Equipment imbriquées.
    // On lit ce qu'on connaît, le reste reste à zéro (apparence par défaut).
    public static NpcAppearance? FromAnamnesisJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var appearance = Default();

            byte ReadByte(string key) => root.TryGetProperty(key, out var v) && v.TryGetByte(out var b) ? b : (byte)0;
            ushort ReadUShort(JsonElement parent, string key) => parent.TryGetProperty(key, out var v) && v.TryGetUInt16(out var u) ? u : (ushort)0;
            byte ReadEquipByte(JsonElement parent, string key) => parent.TryGetProperty(key, out var v) && v.TryGetByte(out var b) ? b : (byte)0;

            appearance.Race = ReadByte("Race");
            appearance.Tribe = ReadByte("Tribe");
            appearance.Sex = ReadByte("Gender");
            appearance.BodyType = ReadByte("Age");
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
            appearance.FacialFeatures = ReadByte("FacialFeatures");
            appearance.FacialFeaturesColor = ReadByte("LimbalEyes");
            appearance.MuscleMass = ReadByte("MuscleTone");
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

[JsonSerializable(typeof(NpcAppearance))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class AppearanceJsonContext : JsonSerializerContext;
