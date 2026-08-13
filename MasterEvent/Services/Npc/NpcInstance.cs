using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using MasterEvent.Models;
using static FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer;
using EmoteController = FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController;

namespace MasterEvent.Services.Npc;

// Représente un PNJ vivant dans le monde. La structure native sous-jacente
// peut disparaître à tout moment (changement de zone, despawn forcé) ; on
// conserve l'index dans le ClientObjectManager pour pouvoir requêter à la
// volée et invalider proprement.
public sealed unsafe class NpcInstance
{
    private readonly IFramework framework;
    private readonly IPluginLog log;

    // Slot interne dans le ClientObjectManager (renvoyé par CreateBattleCharacter).
    // Sert à requêter l'objet natif via ClientObjectManager.GetObjectByIndex.
    // ⚠️ Ce n'est PAS l'index visible côté Dalamud / IPC : pour ça il faut
    // GameObjectIndex (lu sur GameObject.ObjectIndex, offset 140).
    public ushort ObjectIndex { get; }
    public string DisplayName { get; private set; }
    public NpcAppearance Appearance { get; private set; }
    public Guid NetworkId { get; }
    public ushort Territory { get; }
    public bool IsReplicated { get; }
    public ushort? GameObjectIndex
    {
        get
        {
            if (!TryGetCharacter(out var chara)) return null;
            return chara->ObjectIndex;
        }
    }

    public string IdentifierName => MakeIdentifierName(ObjectIndex);

    private bool drawRequested;
    private bool drawEnabled;
    private bool disposed;
    public event Action? Drawn;

    public NpcInstance(ushort objectIndex, NpcAppearance initialAppearance, Guid networkId,
        ushort territory, bool isReplicated, IFramework framework, IPluginLog log)
    {
        this.framework = framework;
        this.log = log;
        ObjectIndex = objectIndex;
        Appearance = initialAppearance;
        NetworkId = networkId;
        Territory = territory;
        IsReplicated = isReplicated;
        DisplayName = string.IsNullOrWhiteSpace(initialAppearance.Name) ? "PNJ" : initialAppearance.Name;
    }

    public bool IsAlive => !disposed && TryGetCharacter(out _);

    public bool TryGetCharacter(out Character* chara)
    {
        chara = null;
        if (disposed) return false;

        var manager = ClientObjectManager.Instance();
        if (manager == null) return false;

        var go = manager->GetObjectByIndex(ObjectIndex);
        if (go == null) return false;
        if (go->ObjectKind != ObjectKind.BattleNpc) return false;

        chara = (Character*)go;
        return true;
    }

    public bool WeaponDrawn { get; private set; }
    public List<StatValue>? Stats { get; set; }
    public int TempModifier { get; set; }
    public int Hp { get; set; }
    public int HpMax { get; set; }
    public int Shield { get; set; }
    public Attitude Attitude { get; set; } = Attitude.Neutral;
    public List<CustomCounter>? Counters { get; set; }
    public bool IsBoss { get; set; }
    public void SetWeaponDrawn(bool drawn)
    {
        WeaponDrawn = drawn;
        ApplyWeaponDrawn();
    }

    public void ApplyWeaponDrawn()
    {
        if (!TryGetCharacter(out var chara)) return;
        chara->Timeline.IsWeaponDrawn = WeaponDrawn;
    }

    public ushort EmoteId { get; private set; }
    public bool EmoteHeld { get; private set; }
    public void SetEmote(ushort emoteId, bool held)
    {
        EmoteId = emoteId;
        EmoteHeld = held;
        ApplyEmote();
    }

    public void ClearEmote()
    {
        EmoteId = 0;
        EmoteHeld = false;

        if (!TryGetCharacter(out var chara)) return;
        chara->SetMode(CharacterModes.Normal, 0);
        chara->Timeline.BaseOverride = 0;
    }

    public void ApplyEmote()
    {
        if (EmoteId == 0) return;
        if (!TryGetCharacter(out var chara)) return;

        if (EmoteHeld)
        {
            var timelineId = ResolveEmoteTimeline(EmoteId);
            if (timelineId == 0) return;

            chara->SetMode(CharacterModes.AnimLock, 0);
            chara->Timeline.BaseOverride = timelineId;
            return;
        }

        var battleChara = (BattleChara*)chara;
        if (battleChara->EmoteController.IsEmoting()) return;

        var option = new EmoteController.PlayEmoteOption { TargetId = 0, Flags = 1 };
        battleChara->EmoteController.PlayEmote(EmoteId, &option);
    }

    private static ushort ResolveEmoteTimeline(ushort emoteId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
            if (sheet.GetRowOrDefault(emoteId) is not { } row) return 0;
            return row.ActionTimeline[0].ValueNullable is { } timeline
                ? (ushort)timeline.RowId
                : (ushort)0;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Timeline de l'emote {emoteId} introuvable : {ex.Message}");
            return 0;
        }
    }

    public Vector3? GetPosition()
    {
        if (!TryGetCharacter(out var chara)) return null;
        var pos = chara->Position;
        return new Vector3(pos.X, pos.Y, pos.Z);
    }

    public float? GetRotation()
    {
        if (!TryGetCharacter(out var chara)) return null;
        return chara->Rotation;
    }

    public void TeleportToLocalPlayer()
    {
        var local = (Character*)(Plugin.ObjectTable.LocalPlayer?.Address ?? IntPtr.Zero);
        if (local == null) return;
        if (!TryGetCharacter(out var chara)) return;

        var pos = local->Position;
        chara->SetPosition(pos.X, pos.Y, pos.Z);
        chara->SetRotation(local->Rotation);
    }

    public void SetPosition(Vector3 position)
    {
        if (!TryGetCharacter(out var chara)) return;
        chara->SetPosition(position.X, position.Y, position.Z);
    }

    public void SetRotation(float radians)
    {
        if (!TryGetCharacter(out var chara)) return;
        chara->SetRotation(radians);
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        DisplayName = name;
    }

    public void WriteIdentifierName()
    {
        if (!TryGetCharacter(out var chara)) return;
        WriteName(chara, MakeIdentifierName(ObjectIndex));
    }

    public void ApplyAppearance(NpcAppearance appearance)
    {
        Appearance = appearance;
        if (!TryGetCharacter(out var chara)) return;

        chara->Scale = 1f;

        var data = chara->DrawData.CustomizeData.Data;
        data[CustomizeOffset.Race] = appearance.Race;
        data[CustomizeOffset.Sex] = appearance.Sex;
        data[CustomizeOffset.BodyType] = appearance.BodyType;
        data[CustomizeOffset.Height] = appearance.Height;
        data[CustomizeOffset.Tribe] = appearance.Tribe;
        data[CustomizeOffset.Face] = appearance.Face;
        data[CustomizeOffset.HairStyle] = appearance.HairStyle;
        data[CustomizeOffset.Highlights] = appearance.Highlights;
        data[CustomizeOffset.SkinColor] = appearance.SkinColor;
        data[CustomizeOffset.EyeColorRight] = appearance.EyeColorRight;
        data[CustomizeOffset.HairColor] = appearance.HairColor;
        data[CustomizeOffset.HighlightsColor] = appearance.HighlightsColor;
        data[CustomizeOffset.FacialFeatures] = appearance.FacialFeatures;
        data[CustomizeOffset.FacialFeaturesColor] = appearance.FacialFeaturesColor;
        data[CustomizeOffset.Eyebrows] = appearance.Eyebrows;
        data[CustomizeOffset.EyeColorLeft] = appearance.EyeColorLeft;
        data[CustomizeOffset.EyeShape] = appearance.EyeShape;
        data[CustomizeOffset.Nose] = appearance.Nose;
        data[CustomizeOffset.Jaw] = appearance.Jaw;
        data[CustomizeOffset.Lipstick] = appearance.Lipstick;
        data[CustomizeOffset.LipColorFurPattern] = appearance.LipColor;
        data[CustomizeOffset.MuscleMass] = appearance.MuscleMass;
        data[CustomizeOffset.TailShape] = appearance.TailShape;
        data[CustomizeOffset.BustSize] = appearance.BustSize;
        data[CustomizeOffset.FacePaint] = appearance.FacePaint;
        data[CustomizeOffset.FacePaintColor] = appearance.FacePaintColor;

        ApplyEquipmentSlot(chara, EquipmentSlot.Head, appearance.Head);
        ApplyEquipmentSlot(chara, EquipmentSlot.Body, appearance.Body);
        ApplyEquipmentSlot(chara, EquipmentSlot.Hands, appearance.Hands);
        ApplyEquipmentSlot(chara, EquipmentSlot.Legs, appearance.Legs);
        ApplyEquipmentSlot(chara, EquipmentSlot.Feet, appearance.Feet);
        ApplyEquipmentSlot(chara, EquipmentSlot.Ears, appearance.Ears);
        ApplyEquipmentSlot(chara, EquipmentSlot.Neck, appearance.Neck);
        ApplyEquipmentSlot(chara, EquipmentSlot.Wrists, appearance.Wrists);
        ApplyEquipmentSlot(chara, EquipmentSlot.LFinger, appearance.RingLeft);
        ApplyEquipmentSlot(chara, EquipmentSlot.RFinger, appearance.RingRight);

        ApplyWeaponSlot(chara, WeaponSlot.MainHand, appearance.MainHand);
        ApplyWeaponSlot(chara, WeaponSlot.OffHand, appearance.OffHand);

        chara->DrawData.HideWeapons(appearance.HideWeapons);
        chara->DrawData.HideHeadgear(0, appearance.HideHeadgear);

        // Pas d'écriture sur Name : il est figé par WriteIdentifierName au spawn.
    }

    public void RequestDraw()
    {
        if (drawRequested) return;
        drawRequested = true;
        ScheduleDraw(retries: 30);
    }

    private void ScheduleDraw(int retries)
    {
        if (retries <= 0)
        {
            log.Warning($"[MasterEvent] PNJ #{ObjectIndex} : timeout en attente de IsReadyToDraw.");
            return;
        }

        framework.RunOnTick(() =>
        {
            if (disposed) return;
            if (!TryGetCharacter(out var chara)) return;

            if (chara->IsReadyToDraw())
            {
                chara->EnableDraw();
                if (!drawEnabled)
                {
                    drawEnabled = true;
                    log.Info($"[MasterEvent] PNJ #{ObjectIndex} dessiné (essais restants : {retries}).");
                    Drawn?.Invoke();
                }
            }
            else
            {
                ScheduleDraw(retries - 1);
            }
        });
    }

    public void Despawn()
    {
        if (disposed) return;
        disposed = true;

        var manager = ClientObjectManager.Instance();
        if (manager == null) return;

        var go = manager->GetObjectByIndex(ObjectIndex);
        if (go == null) return;

        go->DisableDraw();
        manager->DeleteObjectByIndex(ObjectIndex, 0);
    }

    public void MarkDisposed() => disposed = true;

    public static NpcAppearance? CaptureLocalPlayer(string name)
    {
        var address = Plugin.ObjectTable.LocalPlayer?.Address ?? IntPtr.Zero;
        if (address == IntPtr.Zero) return null;

        var chara = (Character*)address;
        var appearance = NpcAppearance.Default();
        appearance.Name = string.IsNullOrWhiteSpace(name) ? "PNJ" : name;
        var data = chara->DrawData.CustomizeData.Data;
        appearance.Race = data[CustomizeOffset.Race];
        appearance.Sex = data[CustomizeOffset.Sex];
        appearance.BodyType = data[CustomizeOffset.BodyType];
        appearance.Height = data[CustomizeOffset.Height];
        appearance.Tribe = data[CustomizeOffset.Tribe];
        appearance.Face = data[CustomizeOffset.Face];
        appearance.HairStyle = data[CustomizeOffset.HairStyle];
        appearance.Highlights = data[CustomizeOffset.Highlights];
        appearance.SkinColor = data[CustomizeOffset.SkinColor];
        appearance.EyeColorRight = data[CustomizeOffset.EyeColorRight];
        appearance.HairColor = data[CustomizeOffset.HairColor];
        appearance.HighlightsColor = data[CustomizeOffset.HighlightsColor];
        appearance.FacialFeatures = data[CustomizeOffset.FacialFeatures];
        appearance.FacialFeaturesColor = data[CustomizeOffset.FacialFeaturesColor];
        appearance.Eyebrows = data[CustomizeOffset.Eyebrows];
        appearance.EyeColorLeft = data[CustomizeOffset.EyeColorLeft];
        appearance.EyeShape = data[CustomizeOffset.EyeShape];
        appearance.Nose = data[CustomizeOffset.Nose];
        appearance.Jaw = data[CustomizeOffset.Jaw];
        appearance.Lipstick = data[CustomizeOffset.Lipstick];
        appearance.LipColor = data[CustomizeOffset.LipColorFurPattern];
        appearance.MuscleMass = data[CustomizeOffset.MuscleMass];
        appearance.TailShape = data[CustomizeOffset.TailShape];
        appearance.BustSize = data[CustomizeOffset.BustSize];
        appearance.FacePaint = data[CustomizeOffset.FacePaint];
        appearance.FacePaintColor = data[CustomizeOffset.FacePaintColor];

        appearance.Head = ReadEquipmentSlot(chara, EquipmentSlot.Head);
        appearance.Body = ReadEquipmentSlot(chara, EquipmentSlot.Body);
        appearance.Hands = ReadEquipmentSlot(chara, EquipmentSlot.Hands);
        appearance.Legs = ReadEquipmentSlot(chara, EquipmentSlot.Legs);
        appearance.Feet = ReadEquipmentSlot(chara, EquipmentSlot.Feet);
        appearance.Ears = ReadEquipmentSlot(chara, EquipmentSlot.Ears);
        appearance.Neck = ReadEquipmentSlot(chara, EquipmentSlot.Neck);
        appearance.Wrists = ReadEquipmentSlot(chara, EquipmentSlot.Wrists);
        appearance.RingLeft = ReadEquipmentSlot(chara, EquipmentSlot.LFinger);
        appearance.RingRight = ReadEquipmentSlot(chara, EquipmentSlot.RFinger);

        appearance.MainHand = ReadWeaponSlot(chara, WeaponSlot.MainHand);
        appearance.OffHand = ReadWeaponSlot(chara, WeaponSlot.OffHand);

        appearance.HideHeadgear = chara->DrawData.IsHatHidden;
        appearance.HideWeapons = chara->DrawData.IsWeaponHidden;

        Plugin.Log.Info($"[MasterEvent] Apparence capturée : race={appearance.Race} tribu={appearance.Tribe} "
            + $"sexe={appearance.Sex} corps={appearance.BodyType} taille={appearance.Height} "
            + $"visage={appearance.Face} cheveux={appearance.HairStyle} | "
            + $"torse={appearance.Body?.ModelId} jambes={appearance.Legs?.ModelId} "
            + $"arme={appearance.MainHand?.ModelId} | casque masqué={appearance.HideHeadgear} "
            + $"armes masquées={appearance.HideWeapons}");

        return appearance;
    }

    private static NpcAppearance.EquipPiece ReadEquipmentSlot(Character* chara, EquipmentSlot slot)
    {
        var e = chara->DrawData.Equipment(slot);
        return new NpcAppearance.EquipPiece
        {
            ModelId = e.Id,
            Variant = e.Variant,
            Stain = e.Stain0,
            Stain2 = e.Stain1,
        };
    }

    private static NpcAppearance.WeaponPiece ReadWeaponSlot(Character* chara, WeaponSlot slot)
    {
        var w = chara->DrawData.Weapon(slot).ModelId;
        return new NpcAppearance.WeaponPiece
        {
            ModelId = w.Id,
            ModelBase = w.Type,
            Variant = w.Variant,
            Stain = w.Stain0,
            Stain2 = w.Stain1,
        };
    }

    private static void ApplyEquipmentSlot(Character* chara, EquipmentSlot slot, NpcAppearance.EquipPiece? piece)
    {
        if (piece == null) return;
        chara->DrawData.Equipment(slot) = new EquipmentModelId
        {
            Id = piece.ModelId,
            Variant = piece.Variant,
            Stain0 = piece.Stain,
            Stain1 = piece.Stain2,
        };
    }

    private static void ApplyWeaponSlot(Character* chara, WeaponSlot slot, NpcAppearance.WeaponPiece? piece)
    {
        if (piece == null) return;
        chara->DrawData.Weapon(slot).ModelId = new WeaponModelId
        {
            Id = piece.ModelId,
            Type = piece.ModelBase,
            Variant = piece.Variant,
            Stain0 = piece.Stain,
            Stain1 = piece.Stain2,
        };
    }

    private static class CustomizeOffset
    {
        public const int Race = 0;
        public const int Sex = 1;
        public const int BodyType = 2;
        public const int Height = 3;
        public const int Tribe = 4;
        public const int Face = 5;
        public const int HairStyle = 6;
        public const int Highlights = 7;
        public const int SkinColor = 8;
        public const int EyeColorRight = 9;
        public const int HairColor = 10;
        public const int HighlightsColor = 11;
        public const int FacialFeatures = 12;
        public const int FacialFeaturesColor = 13;
        public const int Eyebrows = 14;
        public const int EyeColorLeft = 15;
        public const int EyeShape = 16;
        public const int Nose = 17;
        public const int Jaw = 18;
        public const int Lipstick = 19;
        public const int LipColorFurPattern = 20;
        public const int MuscleMass = 21;
        public const int TailShape = 22;
        public const int BustSize = 23;
        public const int FacePaint = 24;
        public const int FacePaintColor = 25;
    }

    private static void WriteName(Character* chara, string name)
    {
        var trimmed = name.Length > 30 ? name[..30] : name;
        var bytes = System.Text.Encoding.UTF8.GetBytes(trimmed);
        var maxLen = Math.Min(bytes.Length, 31);
        for (var i = 0; i < maxLen; i++)
            chara->Name[i] = bytes[i];
        chara->Name[maxLen] = 0;
    }

    private static readonly string[] IdentifierSurnames =
    {
        "Alpha", "Bravo", "Charlie", "Delta",
        "Echo", "Foxtrot", "Golf", "Hotel",
        "India", "Juliett", "Kilo", "Lima",
        "Mike", "November", "Oscar", "Papa",
    };

    private static string MakeIdentifierName(ushort slot)
    {
        if (slot == 0) return "Pnj Tango";
        return $"Pnj {IdentifierSurnames[(slot - 1) % IdentifierSurnames.Length]}";
    }
}
