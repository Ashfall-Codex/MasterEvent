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

    public ushort ObjectIndex { get; }
    public string DisplayName { get; private set; }
    public NpcAppearance Appearance { get; private set; }

    private bool drawRequested;
    private bool disposed;

    public NpcInstance(ushort objectIndex, NpcAppearance initialAppearance, IFramework framework, IPluginLog log)
    {
        this.framework = framework;
        this.log = log;
        ObjectIndex = objectIndex;
        Appearance = initialAppearance;
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
        if (!TryGetCharacter(out var chara)) return;
        WriteName(chara, name);
    }

    public void ApplyAppearance(NpcAppearance appearance)
    {
        Appearance = appearance;
        if (!TryGetCharacter(out var chara)) return;

        chara->Scale = 1f;

        var data = chara->DrawData.CustomizeData.Data;
        data[(int)CustomizeIndex.Race] = appearance.Race;
        data[(int)CustomizeIndex.Sex] = appearance.Sex;
        data[(int)CustomizeIndex.BodyType] = appearance.BodyType;
        data[(int)CustomizeIndex.Height] = appearance.Height;
        data[(int)CustomizeIndex.Tribe] = appearance.Tribe;
        data[(int)CustomizeIndex.Face] = appearance.Face;
        data[(int)CustomizeIndex.HairStyle] = appearance.HairStyle;
        data[(int)CustomizeIndex.Highlights] = appearance.Highlights;
        data[(int)CustomizeIndex.SkinColor] = appearance.SkinColor;
        data[(int)CustomizeIndex.EyeColorRight] = appearance.EyeColorRight;
        data[(int)CustomizeIndex.HairColor] = appearance.HairColor;
        data[(int)CustomizeIndex.HighlightsColor] = appearance.HighlightsColor;
        data[(int)CustomizeIndex.FacialFeatures] = appearance.FacialFeatures;
        data[(int)CustomizeIndex.FacialFeaturesColor] = appearance.FacialFeaturesColor;
        data[(int)CustomizeIndex.Eyebrows] = appearance.Eyebrows;
        data[(int)CustomizeIndex.EyeColorLeft] = appearance.EyeColorLeft;
        data[(int)CustomizeIndex.EyeShape] = appearance.EyeShape;
        data[(int)CustomizeIndex.Nose] = appearance.Nose;
        data[(int)CustomizeIndex.Jaw] = appearance.Jaw;
        data[(int)CustomizeIndex.Lipstick] = appearance.Lipstick;
        data[(int)CustomizeIndex.LipColorFurPattern] = appearance.LipColor;
        data[(int)CustomizeIndex.MuscleMass] = appearance.MuscleMass;
        data[(int)CustomizeIndex.TailShape] = appearance.TailShape;
        data[(int)CustomizeIndex.BustSize] = appearance.BustSize;
        data[(int)CustomizeIndex.FacePaint] = appearance.FacePaint;
        data[(int)CustomizeIndex.FacePaintColor] = appearance.FacePaintColor;

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

        chara->DrawData.HideWeapons(appearance.HideWeapons);
        chara->DrawData.HideHeadgear(0, appearance.HideHeadgear);

        WriteName(chara, DisplayName);
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

    private static void WriteName(Character* chara, string name)
    {
        var trimmed = name.Length > 30 ? name[..30] : name;
        var bytes = System.Text.Encoding.UTF8.GetBytes(trimmed);
        var maxLen = Math.Min(bytes.Length, 31);
        for (var i = 0; i < maxLen; i++)
            chara->Name[i] = bytes[i];
        chara->Name[maxLen] = 0;
    }
}
