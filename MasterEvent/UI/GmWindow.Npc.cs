using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;
using MasterEvent.Services.Npc;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
    private NpcManager? npcManager;
    private string npcNewName = string.Empty;
    private string npcImportPath = string.Empty;
    private string? npcLastError;
    private static readonly TimeSpan NpcInfoLifetime = TimeSpan.FromSeconds(5);
    private string? npcLastInfoText;
    private long npcLastInfoAt;

    private string? npcLastInfo
    {
        get => npcLastInfoText;
        set
        {
            npcLastInfoText = value;
            npcLastInfoAt = Environment.TickCount64;
        }
    }
    private NpcInstance? npcSelected;

    // Hauteur mesurée de la carte de chaque PNJ, réutilisée à l'image suivante.
    private readonly Dictionary<Guid, float> npcCardHeights = new();

    private NpcModService? npcMods;
    private string npcMcdfPath = string.Empty;
    private bool npcMcdfBusy;
    private float npcMcdfProgress;

    private IReadOnlyList<(Guid Id, string Name)> npcDesigns = [];
    private Guid npcDesignSelected;
    private string npcDesignFilter = string.Empty;
    private bool npcDesignBusy;
    private string? npcDesignStatus;
    private bool npcSelfBusy;

    private NpcPresetStore? npcPresets;
    private string npcPresetName = string.Empty;
    private string? npcPresetPendingDelete;
    private string npcPresetFilter = string.Empty;
    private const int SearchThreshold = 6;

    public void SetNpcManager(NpcManager manager)
    {
        npcManager = manager;
    }

    public void SetNpcModService(NpcModService service)
    {
        npcMods = service;
    }

    public void SetNpcPresetStore(NpcPresetStore store)
    {
        npcPresets = store;
    }

    private void DrawNpcEmoteControl(NpcInstance npc)
    {
        if (npcManager == null) return;
        var drawn = npc.WeaponDrawn;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (drawn)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, MasterEventTheme.DangerButtonBg);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MasterEventTheme.DangerButtonHovered);
            }

            if (ImGui.Button(FontAwesomeIcon.Khanda.ToIconString() + "##npc_weapon"))
            {
                npc.SetWeaponDrawn(!drawn);
                npcManager.NotifyChanged();
            }

            if (drawn) ImGui.PopStyleColor(2);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get(drawn ? "Npc.WeaponSheathe" : "Npc.WeaponDraw"));
        ImGui.SameLine();

        var emotes = EmoteCatalog.Entries;
        var currentLabel = npc.EmoteId == 0
            ? Loc.Get("Npc.EmoteNone")
            : EmoteCatalog.NameOf(npc.EmoteId);

        ImGui.SetNextItemWidth(150f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##npc_emote", currentLabel))
        {
            if (ImGui.Selectable(Loc.Get("Npc.EmoteNone"), npc.EmoteId == 0))
            {
                npc.ClearEmote();
                npcManager.NotifyChanged();
            }

            foreach (var emote in emotes)
            {
                if (!ImGui.Selectable(emote.Name, emote.Id == npc.EmoteId)) continue;

                // Le mode courant est conservé au changement d'emote
                npc.SetEmote(emote.Id, npc.EmoteHeld);
                npcManager.NotifyChanged();
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.EmoteHint"));

        ImGui.SameLine();
        var held = npc.EmoteHeld;
        if (ImGui.Checkbox(Loc.Get("Npc.EmoteHold") + "##npc_emote_hold", ref held))
        {
            npc.SetEmote(npc.EmoteId, held);
            npcManager.NotifyChanged();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.EmoteHoldHint"));
        if (npc is { EmoteId: > 0, EmoteHeld: false })
        {
            ImGui.SameLine();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(FontAwesomeIcon.Redo.ToIconString() + "##npc_emote_replay"))
                    npc.ApplyEmote();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.EmoteReplay"));
        }
    }

    /// Enregistre un PNJ posé sous son propre nom.
    private void SaveNpcAsPreset(NpcInstance npc)
    {
        if (npcPresets == null) return;

        var preset = new NpcPreset
        {
            Name = npc.DisplayName,
            Appearance = npc.Appearance,
            ModsId = npc.LocalModId,
            EmoteId = npc.EmoteId,
            EmoteHeld = npc.EmoteHeld,
            WeaponDrawn = npc.WeaponDrawn,
            Stats = npc.Stats,
            HpMax = npc.HpMax,
            Attitude = npc.Attitude,
            Counters = npc.Counters,
            IsBoss = npc.IsBoss,
        };

        if (npcPresets.Save(preset, out var error))
        {
            npcLastInfo = string.Format(Loc.Get("Npc.PresetSavedFmt"), preset.Name);
            npcLastError = null;
        }
        else
        {
            npcLastError = error;
            npcLastInfo = null;
        }
    }

    private void DrawNpcPresets()
    {
        if (npcPresets == null || npcManager == null) return;

        var source = npcSelected ?? npcManager.Instances.FirstOrDefault(n => !n.IsReplicated);
        if (source != null)
        {
            if (string.IsNullOrEmpty(npcPresetName)) npcPresetName = source.DisplayName;

            ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("##npc_preset_name", Loc.Get("Npc.PresetNameHint"), ref npcPresetName, 48);
            ImGui.SameLine();

            var exists = npcPresets.Exists(npcPresetName);
            var saveLabel = exists ? Loc.Get("Npc.PresetOverwrite") : Loc.Get("Npc.PresetSave");
            if (ImGui.Button(saveLabel + "##npc_preset_save"))
            {
                var preset = new NpcPreset
                {
                    Name = npcPresetName.Trim(),
                    Appearance = source.Appearance,
                    ModsId = source.LocalModId,
                    EmoteId = source.EmoteId,
                    EmoteHeld = source.EmoteHeld,
                    WeaponDrawn = source.WeaponDrawn,
                    Stats = source.Stats,
                    HpMax = source.HpMax,
                    Attitude = source.Attitude,
                    Counters = source.Counters,
                    IsBoss = source.IsBoss,
                };

                if (npcPresets.Save(preset, out var err))
                {
                    npcLastInfo = string.Format(Loc.Get("Npc.PresetSavedFmt"), npcPresetName.Trim());
                    npcLastError = null;
                }
                else
                {
                    npcLastError = err;
                    npcLastInfo = null;
                }
            }
        }
        else
        {
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Npc.PresetNoSource"));
        }

        var names = npcPresets.GetNames();
        if (names.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Npc.PresetsEmpty"));
            return;
        }

        if (names.Count > SearchThreshold)
        {
            ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("##npc_preset_filter", Loc.Get("Npc.PresetSearch"), ref npcPresetFilter, 48);

            if (!string.IsNullOrEmpty(npcPresetFilter))
            {
                ImGui.SameLine();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.SmallButton(FontAwesomeIcon.Times.ToIconString() + "##npc_preset_filter_clear"))
                        npcPresetFilter = string.Empty;
                }
            }
        }

        var filter = npcPresetFilter.Trim();
        var visible = string.IsNullOrEmpty(filter)
            ? names
            : names.Where(n => n.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();

        if (visible.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Npc.PresetNoMatch"));
            return;
        }

        foreach (var name in visible)
        {
            ImGui.PushID($"npc_preset_{name}");

            if (ImGui.Button(Loc.Get("Npc.PresetSpawn") + "##spawn"))
            {
                var preset = npcPresets.Load(name);
                if (preset == null)
                {
                    npcLastError = Loc.Get("Npc.PresetLoadFailed");
                    npcLastInfo = null;
                }
                else
                {
                    // Emote et posture sont posées après l'apparition : l'objet natif n'existe
                    // pas avant, et une pose tenue verrouille la timeline.
                    var spawned = SpawnPreset(preset);
                    if (spawned != null)
                    {
                        spawned.Stats = preset.Stats?.Select(s => s.DeepCopy()).ToList();
                        spawned.HpMax = preset.HpMax;
                        spawned.Hp = preset.HpMax;
                        spawned.Attitude = preset.Attitude;
                        spawned.Counters = preset.Counters?.Select(c => c.DeepCopy()).ToList();
                        spawned.IsBoss = preset.IsBoss;
                        ApplyActiveTemplateTo(spawned);

                        if (preset.WeaponDrawn) spawned.SetWeaponDrawn(true);
                        if (preset.EmoteId != 0) spawned.SetEmote(preset.EmoteId, preset.EmoteHeld);
                    }
                }
            }

            ImGui.SameLine();

            if (npcPresetPendingDelete == name)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, MasterEventTheme.DangerButtonBg);
                if (ImGui.Button(Loc.Get("Npc.PresetConfirmDelete") + "##confirm"))
                {
                    npcPresets.Delete(name);
                    npcPresetPendingDelete = null;
                }
                ImGui.PopStyleColor();
            }
            else
            {
                var trashIcon = FontAwesomeIcon.TrashAlt.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button(trashIcon + "##delete"))
                        npcPresetPendingDelete = name;
                }
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(name);

            ImGui.PopID();
        }
    }

    // Résultat de la dernière action, juste sous l'en-tête : visible sans faire défiler.
    // Le succès s'estompe sur sa durée, doucement au début puis de plus en plus vite.
    private void DrawNpcNotices()
    {
        if (npcLastInfoText != null)
        {
            var age = (Environment.TickCount64 - npcLastInfoAt) / NpcInfoLifetime.TotalMilliseconds;
            if (age >= 1d)
            {
                npcLastInfoText = null;
            }
            else
            {
                var alpha = (float)(1d - age * age);
                LayoutControls.DrawNotice(npcLastInfoText, MasterEventTheme.SuccessColor,
                    FontAwesomeIcon.CheckCircle, alpha);
                ImGuiHelpers.ScaledDummy(4f);
            }
        }

        if (!string.IsNullOrEmpty(npcLastError))
        {
            LayoutControls.DrawNotice(npcLastError, MasterEventTheme.DangerColor);
            ImGuiHelpers.ScaledDummy(4f);
        }
    }

    private bool IsNpcNameValid => !string.IsNullOrWhiteSpace(npcNewName);
    private static float CardRightMargin => 12f * ImGuiHelpers.GlobalScale;

    private void DrawNpcCreator()
    {
        var style = ImGui.GetStyle();
        var avail = ImGui.GetContentRegionAvail().X - CardRightMargin;
        var meLabel = Loc.Get("Npc.SpawnAsMe");
        var meWidth = ImGui.CalcTextSize(meLabel).X + style.FramePadding.X * 2f;
        ImGui.SetNextItemWidth(avail - meWidth - style.ItemSpacing.X);
        ImGui.InputTextWithHint("##npc_new_name", Loc.Get("Npc.NameHintRequired"), ref npcNewName, 30);
        ImGui.SameLine();

        ImGui.BeginDisabled(!IsNpcNameValid || npcSelfBusy);
        if (ImGui.Button(meLabel + "##spawn_as_me"))
            SpawnAsMe();
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
            ImGui.TextColored(
                IsNpcNameValid ? MasterEventTheme.MutedTextColor : MasterEventTheme.WarningColor,
                Loc.Get(IsNpcNameValid ? "Npc.SpawnAsMeHint" : "Npc.NameRequired"));
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        if (npcSelfBusy)
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Npc.SelfAnalyzing"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (DrawImportRow(FontAwesomeIcon.FileImport, "anam", ref npcImportPath, ".chara",
                Loc.Get("Npc.ImportAnamnesis"), Loc.Get("Npc.ImportShort"), !IsNpcNameValid,
                picked => npcImportPath = picked))
            ImportAnamnesis();

        DrawMcdfImport();
        DrawDesignImport();
    }

    private void SpawnAsMe()
    {
        var name = npcNewName.Trim();

        void SpawnVanilla()
        {
            var appearance = NpcInstance.CaptureLocalPlayer(name);
            if (appearance == null)
            {
                npcLastError = Loc.Get("Npc.CaptureFailed");
                npcLastInfo = null;
                return;
            }

            TrySpawn(appearance);
        }

        if (npcMods is not { PenumbraAvailable: true, GlamourerAvailable: true } service)
        {
            SpawnVanilla();
            return;
        }

        npcSelfBusy = true;
        npcLastError = null;
        npcLastInfo = null;

        _ = service.CaptureSelfAsync().ContinueWith(task => Plugin.Framework.RunOnFrameworkThread(() =>
        {
            npcSelfBusy = false;
            var result = task.IsFaulted ? null : task.Result;

            if (result is { IsModded: true, Manifest: { } manifest, Bytes: { } bytes, Id: { } id }
                && service.CheckLocalRequirements(manifest) == null)
            {
                var baseLook = result.Appearance ?? NpcAppearanceFor(name);
                baseLook.Name = name;
                var spawned = SpawnModded(baseLook, manifest, bytes, id);
                if (spawned != null && result.HasWeapon) spawned.SetWeaponDrawn(true);
                return;
            }

            SpawnVanilla();
        }));
    }

    private static bool DrawImportRow(FontAwesomeIcon icon, string id, ref string path, string extension,
        string hint, string actionLabel, bool disabled, Action<string> onBrowsed)
    {
        var style = ImGui.GetStyle();
        DrawRowIcon(icon);
        var browseWidth = ImGui.GetFrameHeight() + style.FramePadding.X;
        var actionWidth = ImGui.CalcTextSize(actionLabel).X + style.FramePadding.X * 2f;
        var inputWidth = ImGui.GetContentRegionAvail().X - CardRightMargin - browseWidth - actionWidth
                         - style.ItemSpacing.X * 2f;

        ImGui.SetNextItemWidth(Math.Max(inputWidth, 80f * ImGuiHelpers.GlobalScale));
        ImGui.InputTextWithHint($"##npc_path_{id}", hint, ref path, 512);
        ImGui.SameLine();

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(FontAwesomeIcon.FolderOpen.ToIconString() + $"##browse_{id}"))
                BrowseFile(extension, onBrowsed);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.BrowseFile"));
        ImGui.SameLine();

        ImGui.BeginDisabled(disabled);
        var clicked = ImGui.Button(actionLabel + $"##act_{id}");
        ImGui.EndDisabled();
        return clicked;
    }
    private static void DrawRowIcon(FontAwesomeIcon icon)
    {
        ImGui.AlignTextToFramePadding();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.AccentColor, icon.ToIconString());
        ImGui.SameLine();
    }

    private void DrawDesignImport()
    {
        if (npcMods == null) return;

        if (!npcMods.GlamourerAvailable || !npcMods.PenumbraAvailable)
        {
            DrawRowIcon(FontAwesomeIcon.Palette);
            var missing = !npcMods.GlamourerAvailable ? "Glamourer" : "Penumbra";
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(MasterEventTheme.MutedTextColor,
                $"{Loc.Get("Npc.ImportDesign")} : {string.Format(Loc.Get("Npc.ModsPluginMissingFmt"), missing)}");
            return;
        }

        var style = ImGui.GetStyle();
        DrawRowIcon(FontAwesomeIcon.Palette);

        var refreshWidth = ImGui.GetFrameHeight() + style.FramePadding.X;
        var actionLabel = Loc.Get("Npc.ImportShort");
        var actionWidth = ImGui.CalcTextSize(actionLabel).X + style.FramePadding.X * 2f;
        var comboWidth = ImGui.GetContentRegionAvail().X - CardRightMargin - refreshWidth - actionWidth
                         - style.ItemSpacing.X * 2f;

        var selectedName = npcDesigns.FirstOrDefault(d => d.Id == npcDesignSelected).Name;
        ImGui.SetNextItemWidth(Math.Max(comboWidth, 80f * ImGuiHelpers.GlobalScale));
        if (ImGui.BeginCombo("##npc_design_combo",
                string.IsNullOrEmpty(selectedName) ? Loc.Get("Npc.ImportDesign") : selectedName))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##npc_design_filter", Loc.Get("Npc.DesignSearchHint"), ref npcDesignFilter, 64);

            var shown = 0;
            foreach (var (id, name) in npcDesigns)
            {
                if (npcDesignFilter.Length > 0 && !name.Contains(npcDesignFilter, StringComparison.CurrentCultureIgnoreCase))
                    continue;
                if (++shown > 80) break;

                if (ImGui.Selectable(name + "##design_" + id, id == npcDesignSelected))
                    npcDesignSelected = id;
            }

            if (npcDesigns.Count == 0)
                ImGui.TextDisabled(Loc.Get("Npc.DesignEmpty"));
            ImGui.EndCombo();
        }
        else if (npcDesigns.Count == 0)
        {
            npcDesigns = npcMods.ListDesigns();
        }

        ImGui.SameLine();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(FontAwesomeIcon.SyncAlt.ToIconString() + "##refresh_designs"))
                npcDesigns = npcMods.ListDesigns();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.DesignRefresh"));
        ImGui.SameLine();

        ImGui.BeginDisabled(!IsNpcNameValid || npcDesignBusy || npcDesignSelected == Guid.Empty);
        if (ImGui.Button(actionLabel + "##import_design"))
            ImportDesign();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Npc.DesignHint"));
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        if (npcDesignBusy && npcDesignStatus != null)
            ImGui.TextColored(MasterEventTheme.MutedTextColor, string.Format(Loc.Get("Npc.DesignCapturingFmt"), npcDesignStatus));
    }

    private void ImportDesign()
    {
        if (npcMods == null || npcManager == null || npcDesignSelected == Guid.Empty) return;

        var name = npcNewName.Trim();
        npcDesignBusy = true;
        npcDesignStatus = string.Empty;
        npcLastError = null;
        npcLastInfo = null;

        var service = npcMods;
        _ = service.CaptureDesignAsync(npcDesignSelected, step => npcDesignStatus = step)
            .ContinueWith(task => Plugin.Framework.RunOnFrameworkThread(() =>
            {
                npcDesignBusy = false;
                var result = task.IsFaulted ? new NpcModService.ImportResult(null, null, null, 0, task.Exception?.GetBaseException().Message) : task.Result;
                if (result.Manifest == null || result.Bytes == null || result.Id == null)
                {
                    npcLastError = string.Format(Loc.Get("Npc.DesignCaptureFailedFmt"), result.Error ?? Loc.Get("Npc.UnknownError"));
                    return;
                }

                if (service.CheckLocalRequirements(result.Manifest) is { } requirement)
                {
                    npcLastError = requirement;
                    return;
                }

                var baseLook = result.Appearance ?? NpcAppearanceFor(name);
                baseLook.Name = name;
                var spawned = SpawnModded(baseLook, result.Manifest, result.Bytes, result.Id);

                if (spawned != null && result.HasWeapon) spawned.SetWeaponDrawn(true);
                if (result.Rejected > 0)
                    npcLastInfo = string.Format(Loc.Get("Npc.ModsRejectedFmt"), result.Rejected);
            }));
    }

    private void DrawMcdfImport()
    {
        if (npcMods == null) return;

        if (DrawImportRow(FontAwesomeIcon.FileArchive, "mcdf", ref npcMcdfPath, ".mcdf",
                Loc.Get("Npc.ImportMcdf"), Loc.Get("Npc.ImportShort"), !IsNpcNameValid || npcMcdfBusy,
                picked => npcMcdfPath = picked))
            ImportMcdf();

        if (npcMcdfBusy)
            ImGui.TextColored(MasterEventTheme.MutedTextColor,
                string.Format(Loc.Get("Npc.ModsImportingFmt"), (int)(npcMcdfProgress * 100f)));
    }

    private void ImportMcdf()
    {
        if (npcMods == null || npcManager == null) return;

        var path = npcMcdfPath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            npcLastError = Loc.Get("Npc.ImportPathInvalid");
            npcLastInfo = null;
            return;
        }

        var name = !string.IsNullOrWhiteSpace(npcNewName) ? npcNewName.Trim() : Path.GetFileNameWithoutExtension(path);
        npcMcdfBusy = true;
        npcMcdfProgress = 0f;
        npcLastError = null;
        npcLastInfo = null;

        var service = npcMods;
        _ = service.ImportMcdfAsync(path, (done, total) => npcMcdfProgress = total > 0 ? (float)done / total : 0f)
            .ContinueWith(task => Plugin.Framework.RunOnFrameworkThread(() =>
            {
                npcMcdfBusy = false;
                var result = task.Result;
                if (result.Manifest == null || result.Bytes == null || result.Id == null)
                {
                    npcLastError = string.Format(Loc.Get("Npc.ImportErrorFmt"), result.Error ?? Loc.Get("Npc.ImportParseFailed"));
                    return;
                }

                if (service.CheckLocalRequirements(result.Manifest) is { } requirement)
                {
                    npcLastError = requirement;
                    return;
                }

                SpawnModded(NpcAppearanceFor(name), result.Manifest, result.Bytes, result.Id);
                if (result.Rejected > 0)
                    npcLastInfo = string.Format(Loc.Get("Npc.ModsRejectedFmt"), result.Rejected);
            }));
    }

    private static NpcAppearance NpcAppearanceFor(string name)
    {
        var appearance = NpcAppearance.Default();
        appearance.Name = name;
        return appearance;
    }

    private NpcInstance? SpawnModded(NpcAppearance appearance, NpcModManifest manifest, byte[] bytes, string id)
    {
        if (npcManager == null || npcMods == null) return null;

        if (!npcManager.TrySpawnModded(appearance, out var instance, out var error))
        {
            npcLastError = error ?? Loc.Get("Npc.UnknownError");
            npcLastInfo = null;
            return null;
        }

        ApplyActiveTemplateTo(instance!);
        npcSelected = instance;
        terrainSpawnSerial++;
        npcMods.ApplyLocal(instance!, manifest, bytes, id);
        npcLastInfo = string.Format(Loc.Get("Npc.SpawnedFmt"), instance!.DisplayName);
        npcLastError = null;
        return instance;
    }

    private void DrawNpcModStatus(NpcInstance npc)
    {
        if (npc.ModState == NpcModState.None) return;

        var percent = (int)(npc.ModProgress * 100f);
        var (text, color) = npc.ModState switch
        {
            NpcModState.Preparing or NpcModState.Applying => (Loc.Get("Npc.ModsApplying"), MasterEventTheme.MutedTextColor),
            NpcModState.Uploading => (string.Format(Loc.Get("Npc.ModsUploadingFmt"), percent), MasterEventTheme.MutedTextColor),
            NpcModState.Downloading => (string.Format(Loc.Get("Npc.ModsDownloadingFmt"), percent), MasterEventTheme.MutedTextColor),
            NpcModState.Applied => (Loc.Get("Npc.ModsApplied"), MasterEventTheme.TextStrong),
            NpcModState.LocalOnly => (npc.ModStatus ?? Loc.Get("Npc.ModsLocalOnly"), MasterEventTheme.MutedTextColor),
            _ => (npc.ModStatus ?? Loc.Get("Npc.UnknownError"), MasterEventTheme.DangerColor),
        };

        ImGui.TextColored(color, text);

        if (npc.ModState == NpcModState.LocalOnly && !npc.IsReplicated && npcMods is { CanPublish: true })
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.Get("Npc.ModsPublish") + "##publish_mods"))
                npcMods.RetryPublish(npc);
        }
    }

    // PNJ humains ou monstres : les deux partagent la carte, chaque catégorie a sa liste.
    private void DrawNpcList(bool creatures)
    {
        if (npcManager == null) return;

        // Copie : retirer un PNJ modifie la liste pendant le parcours.
        var shown = npcManager.Instances.Where(n => n.IsCreature == creatures).ToArray();
        if (shown.Length == 0)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get(creatures ? "Creature.Empty" : "Npc.Empty"));
            return;
        }

        var first = true;
        foreach (var npc in shown)
        {
            if (!first) ImGui.Spacing();
            first = false;
            DrawNpcCard(npc);
        }

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.WarningColor);
        var removeAll = QuietButton(Loc.Get("Npc.DespawnAll") + (creatures ? "##despawn_all_creatures" : "##despawn_all"));
        ImGui.PopStyleColor();
        if (removeAll)
        {
            foreach (var npc in shown)
                npcManager.Despawn(npc);
            npcLastInfo = Loc.Get("Npc.AllDespawned");
            npcLastError = null;
        }
    }

    // Carte d'un PNJ : même cadre et mêmes contrôles qu'un marqueur (barres, stats, attitude, jet),
    // complétés par la mise en scène propre au PNJ (emote, arme, position, enregistrement).
    private void DrawNpcCard(NpcInstance npc)
    {
        if (npcManager == null) return;

        var label = $"npc_{npc.ObjectIndex}";
        var scale = ImGuiHelpers.GlobalScale;

        ImGui.PushStyleColor(ImGuiCol.Border, MarkerCard.GetAttitudeBorderColor(npc.Attitude, true));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, MasterEventTheme.ThemeButtonBg with { W = MasterEventTheme.CardAlpha() });
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, MasterEventTheme.RadiusCard * scale);

        var removed = false;
        // Hauteur : celle du contenu mesurée à l'image précédente. Une fenêtre enfant ne sait pas
        // se dimensionner seule ; la première image utilise une estimation.
        var height = npcCardHeights.TryGetValue(npc.NetworkId, out var measured) ? measured : 230f * scale;
        var open = ImGui.BeginChild($"##{label}", new Vector2(ImGui.GetContentRegionAvail().X, height), true);
        if (open)
        {
            ImGui.PushID(label);

            // En-tête : pastille, nom, état, boss
            DrawRowIcon(npc.IsCreature ? FontAwesomeIcon.Dragon : FontAwesomeIcon.UserFriends);
            var alive = npc.IsAlive;
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(alive ? MasterEventTheme.TextStrong : MasterEventTheme.MutedTextColor, npc.DisplayName);
            if (!alive)
            {
                ImGui.SameLine();
                ImGui.TextColored(MasterEventTheme.DangerColor, Loc.Get("Npc.Dead"));
            }

            var boss = npc.IsBoss;
            var bossLabel = Loc.Get("Marker.Boss");
            var bossWidth = ImGui.CalcTextSize(bossLabel).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - bossWidth + ImGui.GetCursorPosX() - 4f * scale);
            if (ImGui.Checkbox(bossLabel + "##boss", ref boss))
                npc.IsBoss = boss;

            DrawNpcModStatus(npc);

            // Corps commun, suivi pour répercuter toute modification aux joueurs.
            var before = NpcVitalsFingerprint(npc);
            MarkerCard.DrawVitalsBody(npc, null, label, session.HpMode, session.ShowShield, false, session.MpMode);

            if (npc.Stats is not { Count: > 0 } && session.ActiveTemplate?.StatDefinitions is { Count: > 0 }
                && QuietButton(Loc.Get("Npc.StatsFromTemplate") + "##stats_from_template"))
            {
                ApplyActiveTemplateTo(npc);
            }

            if (NpcVitalsFingerprint(npc) != before)
                npcManager.NotifyChanged();

            ImGuiHelpers.ScaledDummy(2f);
            if (!npc.IsCreature) DrawNpcEmoteControl(npc);

            // Actions : jet à gauche, mise en scène et gestion à droite
            DrawNpcActionRow(npc, label, ref removed);

            npcCardHeights[npc.NetworkId] = ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y;

            ImGui.PopID();
        }
        ImGui.EndChild();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);

        if (removed)
        {
            npcManager.Despawn(npc);
            npcLastInfo = string.Format(Loc.Get("Npc.DespawnedFmt"), npc.DisplayName);
            npcLastError = null;
        }
    }

    // Empreinte des valeurs que le MJ peut modifier depuis la carte, pour détecter un changement.
    private static (int, int, int, Attitude, bool, int, int, int) NpcVitalsFingerprint(NpcInstance npc)
        => (npc.Hp, npc.HpMax, npc.Shield, npc.Attitude, npc.IsBoss, npc.TempModifier,
            npc.Stats?.Sum(x => x.Modifier) ?? 0, npc.Counters?.Sum(x => x.Value * 31 + x.Max) ?? 0);

    // Pied de carte aligné sur celui d'un marqueur : jet, puis « Déplacer » et « Retirer » à droite.
    // Réappliquer et enregistrer s'ajoutent en icônes, propres au PNJ.
    private void DrawNpcActionRow(NpcInstance npc, string label, ref bool removed)
    {
        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var moveLabel = Loc.Get("Marker.Move");
        var clearLabel = Loc.Get("Marker.Clear");

        var rightWidth = IconButtonWidth(FontAwesomeIcon.Dice) + spacing
                         + IconButtonWidth(FontAwesomeIcon.SyncAlt) + spacing
                         + IconButtonWidth(FontAwesomeIcon.Save) + spacing
                         + TextButtonWidth(moveLabel) + spacing
                         + TextButtonWidth(clearLabel);
        var target = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - rightWidth - 4f * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), target));

        // Jet, avec choix de stat si le PNJ en porte
        if (QuietIconButton(FontAwesomeIcon.Dice, "roll", Loc.Get("Npc.Roll")))
        {
            if (npc.Stats is { Count: > 0 })
                ImGui.OpenPopup("##npc_roll_popup");
            else
                session.RollDiceFor(npc);
        }

        if (ImGui.BeginPopup("##npc_roll_popup"))
        {
            DiceControls.DrawRollStatMenu(npc, label, statId => session.RollDiceFor(npc, statId), withHeader: true);
            ImGui.EndPopup();
        }
        ImGui.SameLine();

        if (QuietIconButton(FontAwesomeIcon.SyncAlt, "reapply", Loc.Get("Npc.Reapply")))
            npc.ApplyAppearance(npc.Appearance);
        ImGui.SameLine();

        var presetTip = npcPresets?.Exists(npc.DisplayName) == true
            ? string.Format(Loc.Get("Npc.PresetUpdateFmt"), npc.DisplayName)
            : string.Format(Loc.Get("Npc.PresetSaveFmt"), npc.DisplayName);
        if (QuietIconButton(FontAwesomeIcon.Save, "save_preset", presetTip))
            SaveNpcAsPreset(npc);
        ImGui.SameLine();

        if (ImGui.Button(moveLabel + "##move"))
        {
            npc.TeleportToLocalPlayer();
            npcManager?.NotifyChanged();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.TeleportToMe"));
        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
        if (ImGui.Button(clearLabel + "##clear")) removed = true;
        ImGui.PopStyleColor();
    }

    private void ApplyActiveTemplateTo(NpcInstance npc)
    {
        if (session.ActiveTemplate is not { } template) return;

        npc.Stats ??= [];
        npc.Counters ??= [];
        TemplateSyncHelper.SyncStatsAndCounters(npc.Stats, npc.Counters, template);

        if (npc.HpMax <= 0)
        {
            npc.HpMax = template.DefaultHpMax;
            npc.Hp = template.DefaultHpMax;
        }
    }

    // Un preset moddé retrouve sa tenue dans le cache local ; à défaut, il apparaît dans sa version de base.
    private NpcInstance? SpawnPreset(NpcPreset preset)
    {
        if (preset.ModsId != null && npcMods?.LoadFromCache(preset.ModsId) is
            { Manifest: { } manifest, Bytes: { } bytes, Id: { } id }
            && npcMods.CheckLocalRequirements(manifest) == null)
        {
            return SpawnModded(preset.Appearance, manifest, bytes, id);
        }

        return TrySpawn(preset.Appearance);
    }

    private NpcInstance? TrySpawn(NpcAppearance appearance)
    {
        if (npcManager == null) return null;
        if (npcManager.TrySpawn(appearance, out var instance, out var error))
        {
            ApplyActiveTemplateTo(instance!);

            npcSelected = instance;
            terrainSpawnSerial++;
            npcLastInfo = string.Format(Loc.Get("Npc.SpawnedFmt"), instance!.DisplayName);
            npcLastError = null;
            return instance;
        }
        else
        {
            npcLastError = error ?? Loc.Get("Npc.UnknownError");
            npcLastInfo = null;
            return null;
        }
    }

    private static void BrowseFile(string extensionFilter, Action<string> onPicked)
    {
        Plugin.FileDialogManager.OpenFileDialog(
            title: "Sélectionner un fichier",
            filters: extensionFilter,
            callback: (success, paths) =>
            {
                if (!success) return;
                if (paths.FirstOrDefault() is not { } path) return;
                if (string.IsNullOrEmpty(path)) return;
                onPicked(path);
            },
            selectionCountMax: 1,
            startPath: null);
    }

    private void ImportAnamnesis()
    {
        var path = npcImportPath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            npcLastError = Loc.Get("Npc.ImportPathInvalid");
            npcLastInfo = null;
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var appearance = NpcAppearance.FromAnamnesisJson(json);
            if (appearance == null)
            {
                npcLastError = Loc.Get("Npc.ImportParseFailed");
                npcLastInfo = null;
                return;
            }

            if (!string.IsNullOrWhiteSpace(npcNewName)) appearance.Name = npcNewName.Trim();
            else appearance.Name = Path.GetFileNameWithoutExtension(path);

            TrySpawn(appearance);
        }
        catch (Exception ex)
        {
            npcLastError = string.Format(Loc.Get("Npc.ImportErrorFmt"), ex.Message);
            npcLastInfo = null;
        }
    }
}
