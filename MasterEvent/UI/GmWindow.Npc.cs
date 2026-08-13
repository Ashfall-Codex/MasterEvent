using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services.Npc;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
    private NpcManager? npcManager;
    private string npcNewName = string.Empty;
    private string npcImportPath = string.Empty;
    private string? npcLastError;
    private string? npcLastInfo;
    private NpcInstance? npcSelected;

    private NpcPresetStore? npcPresets;
    private string npcPresetName = string.Empty;
    private string? npcPresetPendingDelete;
    private string npcPresetFilter = string.Empty;
    private const int SearchThreshold = 6;

    public void SetNpcManager(NpcManager manager)
    {
        npcManager = manager;
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
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.18f, 0.18f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.65f, 0.24f, 0.24f, 1f));
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
            EmoteId = npc.EmoteId,
            EmoteHeld = npc.EmoteHeld,
            WeaponDrawn = npc.WeaponDrawn,
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

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        var bookIcon = FontAwesomeIcon.BookOpen.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.AccentColor, bookIcon);
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Npc.Presets"));

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
                    EmoteId = source.EmoteId,
                    EmoteHeld = source.EmoteHeld,
                    WeaponDrawn = source.WeaponDrawn,
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
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), Loc.Get("Npc.PresetNoSource"));
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
                    var spawned = TrySpawn(preset.Appearance);
                    if (spawned != null)
                    {
                        if (preset.WeaponDrawn) spawned.SetWeaponDrawn(true);
                        if (preset.EmoteId != 0) spawned.SetEmote(preset.EmoteId, preset.EmoteHeld);
                    }
                }
            }

            ImGui.SameLine();

            if (npcPresetPendingDelete == name)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
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

    private void DrawNpcContent()
    {
        if (npcManager == null)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Npc.Unavailable"));
            return;
        }

        if (!HasGmAccess())
        {
            var avail = ImGui.GetContentRegionAvail();
            var text = Loc.Get("Gm.PlayerViewLocked");
            var textSz = ImGui.CalcTextSize(text);
            ImGui.SetCursorPos(new Vector2(
                ImGui.GetCursorPosX() + (avail.X - textSz.X) / 2f,
                ImGui.GetCursorPosY() + (avail.Y - textSz.Y) / 2f));
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), text);
            return;
        }

        npcManager.PruneDead();

        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Npc.Title"));
        ImGui.SameLine();
        ImGui.TextDisabled($"({npcManager.Count}/{NpcManager.MaxConcurrentNpcs})");
        ImGui.Separator();

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(new Vector4(0.95f, 0.7f, 0.2f, 1f), Loc.Get("Npc.Warning"));
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        DrawNpcCreator();
        ImGui.Separator();
        DrawNpcList();
        DrawNpcPresets();

        if (!string.IsNullOrEmpty(npcLastError))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), npcLastError);
        }
        if (!string.IsNullOrEmpty(npcLastInfo))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.4f, 0.85f, 0.4f, 1f), npcLastInfo);
        }
    }

    private bool IsNpcNameValid => !string.IsNullOrWhiteSpace(npcNewName);

    private void DrawNpcCreator()
    {
        ImGui.TextUnformatted(Loc.Get("Npc.NewName"));
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##npc_new_name", ref npcNewName, 30);

        if (!IsNpcNameValid)
            ImGui.TextColored(new Vector4(0.85f, 0.65f, 0.3f, 1f), Loc.Get("Npc.NameRequired"));

        ImGuiHelpers.ScaledDummy(6f);

        // Spawn d'un PNJ vanilla avec apparence par défaut.
        ImGui.BeginDisabled(!IsNpcNameValid);
        if (ImGui.Button(Loc.Get("Npc.SpawnDefault") + "##spawn_default"))
        {
            var appearance = NpcAppearance.Default();
            if (!string.IsNullOrWhiteSpace(npcNewName)) appearance.Name = npcNewName.Trim();
            TrySpawn(appearance);
        }

        ImGui.SameLine();

        if (ImGui.Button(Loc.Get("Npc.SpawnAsMe") + "##spawn_as_me"))
        {
            var appearance = NpcInstance.CaptureLocalPlayer(npcNewName.Trim());
            if (appearance == null)
            {
                npcLastError = Loc.Get("Npc.CaptureFailed");
                npcLastInfo = null;
            }
            else
            {
                TrySpawn(appearance);
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
            ImGui.TextUnformatted(Loc.Get("Npc.SpawnAsMeHint"));
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.EndDisabled();

        ImGui.Spacing();

        // Import d'une apparence depuis un fichier Anamnesis (.chara).
        ImGui.TextUnformatted(Loc.Get("Npc.ImportAnamnesis"));
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##npc_import_path", ref npcImportPath, 512);
        ImGui.SameLine();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(FontAwesomeIcon.FolderOpen.ToIconString() + "##browse_anam"))
                BrowseFile(".chara", picked => npcImportPath = picked);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.BrowseFile"));
        ImGui.SameLine();
        ImGui.BeginDisabled(!IsNpcNameValid);
        if (ImGui.Button(Loc.Get("Npc.Import") + "##import_anam"))
            ImportAnamnesis();
        ImGui.EndDisabled();
    }

    private void DrawNpcList()
    {
        if (npcManager == null) return;

        if (npcManager.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Npc.Empty"));
            return;
        }

        foreach (var npc in npcManager.Instances)
        {
            ImGui.PushID($"npc_{npc.ObjectIndex}");

            var alive = npc.IsAlive;
            var labelColor = alive ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(0.6f, 0.6f, 0.6f, 1f);
            ImGui.TextColored(labelColor, $"#{npc.ObjectIndex} — {npc.DisplayName}");
            if (!alive)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), Loc.Get("Npc.Dead"));
            }

            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(FontAwesomeIcon.MapMarkerAlt.ToIconString() + "##teleport"))
                {
                    npc.TeleportToLocalPlayer();
                    npcManager.NotifyChanged();
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.TeleportToMe"));
            ImGui.SameLine();

            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(FontAwesomeIcon.SyncAlt.ToIconString() + "##reapply"))
                    npc.ApplyAppearance(npc.Appearance);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.Reapply"));
            ImGui.SameLine();

            DrawNpcEmoteControl(npc);

            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(FontAwesomeIcon.Save.ToIconString() + "##save_preset"))
                    SaveNpcAsPreset(npc);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(npcPresets?.Exists(npc.DisplayName) == true
                    ? string.Format(Loc.Get("Npc.PresetUpdateFmt"), npc.DisplayName)
                    : string.Format(Loc.Get("Npc.PresetSaveFmt"), npc.DisplayName));
            }
            ImGui.SameLine();

            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(FontAwesomeIcon.TrashAlt.ToIconString() + "##despawn"))
                {
                    npcManager.Despawn(npc);
                    npcLastInfo = string.Format(Loc.Get("Npc.DespawnedFmt"), npc.DisplayName);
                    npcLastError = null;
                    ImGui.PopID();
                    break;
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.Despawn"));

            ImGui.Separator();
            ImGui.PopID();
        }

        ImGui.Spacing();
        if (ImGui.Button(Loc.Get("Npc.DespawnAll") + "##despawn_all"))
        {
            npcManager.DespawnAll();
            npcLastInfo = Loc.Get("Npc.AllDespawned");
            npcLastError = null;
        }
    }

    private NpcInstance? TrySpawn(NpcAppearance appearance)
    {
        if (npcManager == null) return null;
        if (npcManager.TrySpawn(appearance, out var instance, out var error))
        {
            npcSelected = instance;
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
