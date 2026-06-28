using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using MasterEvent.Models;
using static FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer;

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

    private static void ApplyEquipmentSlot(Character* chara, EquipmentSlot slot, NpcAppearance.EquipPiece? piece)
    {
        if (piece == null) return;
        var modelId = new EquipmentModelId
        {
            Id = piece.ModelId,
            Variant = piece.Variant,
            Stain0 = piece.Stain,
            Stain1 = piece.Stain2,
        };
        chara->DrawData.LoadEquipment(slot, &modelId, false);
    }

    private static void ApplyWeaponSlot(Character* chara, WeaponSlot slot, NpcAppearance.WeaponPiece? piece)
    {
        if (piece == null) return;
        var weapon = new WeaponModelId
        {
            Id = piece.ModelId,
            Type = piece.ModelBase,
            Variant = piece.Variant,
            Stain0 = piece.Stain,
            Stain1 = piece.Stain2,
        };

        chara->DrawData.LoadWeapon(slot, weapon, 0, 1, 0, 0, true);
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
