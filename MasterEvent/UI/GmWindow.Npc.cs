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
using MasterEvent.Services.Npc;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
    private NpcManager? npcManager;
    private MasterEvent.API.Glamourer? glamourer;
    private MasterEvent.API.Penumbra? penumbra;
    private string npcNewName = string.Empty;
    private string npcImportPath = string.Empty;
    private string npcMcdfImportPath = string.Empty;
    private string? npcLastError;
    private string? npcLastInfo;
    private NpcInstance? npcSelected;
    private bool penumbraGroupBindActive;
    private bool penumbraUserOptedOut;
    private Guid? penumbraBoundCollection;
    private Guid? penumbraSavedMaleNpc;
    private Guid? penumbraSavedFemaleNpc;
    private readonly Dictionary<NpcInstance, McdfSession> mcdfSessions = new();

    private sealed class McdfSession
    {
        public Guid TempCollection { get; init; }
        public required string ExtractDir { get; init; }
        public required List<string> ExtractedFiles { get; init; }
    }

    private IReadOnlyList<MasterEvent.API.Glamourer.DesignEntry> glamourerDesigns = Array.Empty<MasterEvent.API.Glamourer.DesignEntry>();
    private int glamourerSelectedIndex = -1;
    private string glamourerSearchFilter = string.Empty;
    private enum NpcSpawnMode { Anamnesis, Glamourer, Mcdf }
    private NpcSpawnMode spawnMode = NpcSpawnMode.Anamnesis;


    public void SetNpcManager(NpcManager manager)
    {
        npcManager = manager;
        manager.OnNpcDespawning += npc =>
        {
            if (glamourer is { Available: true })
                glamourer.RevertByName(npc.IdentifierName);

            // Cleanup la session MCDF associée à ce PNJ si on en avait une.
            if (mcdfSessions.Remove(npc, out var session))
                DisposeMcdfSession(session);
        };
    }

    public void SetGlamourer(MasterEvent.API.Glamourer api)
    {
        glamourer = api;
        glamourer.OnAvailabilityChanged += RefreshGlamourerDesigns;
        RefreshGlamourerDesigns();
    }

    public void SetPenumbra(MasterEvent.API.Penumbra api)
    {
        penumbra = api;
    }

    public void OnNpcTabUnloading()
    {
        CleanupAllMcdfSessions();
        if (penumbraGroupBindActive)
            DeactivatePenumbraGroupBind();
    }

    private void RefreshGlamourerDesigns()
    {
        if (glamourer == null || !glamourer.Available)
        {
            glamourerDesigns = Array.Empty<MasterEvent.API.Glamourer.DesignEntry>();
            glamourerSelectedIndex = -1;
            return;
        }
        glamourerDesigns = glamourer.ListDesigns();
        if (glamourerSelectedIndex >= glamourerDesigns.Count) glamourerSelectedIndex = -1;
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
        {
            ImGui.TextColored(new Vector4(0.85f, 0.65f, 0.3f, 1f), Loc.Get("Npc.NameRequired"));
        }

        ImGuiHelpers.ScaledDummy(4f);

        var glamourerOk = glamourer is { Available: true };
        var mcdfOk = glamourerOk;
        if (spawnMode == NpcSpawnMode.Glamourer && !glamourerOk) spawnMode = NpcSpawnMode.Anamnesis;
        if (spawnMode == NpcSpawnMode.Mcdf && !mcdfOk) spawnMode = NpcSpawnMode.Anamnesis;

        DrawSpawnModeTabs(glamourerOk, mcdfOk);

        ImGuiHelpers.ScaledDummy(6f);

        switch (spawnMode)
        {
            case NpcSpawnMode.Anamnesis: DrawAnamnesisMode(); break;
            case NpcSpawnMode.Glamourer: DrawGlamourerMode(); break;
            case NpcSpawnMode.Mcdf:      DrawMcdfMode(); break;
        }

        if (spawnMode == NpcSpawnMode.Glamourer)
        {
            ImGui.Separator();
            DrawPenumbraCreator();
        }
    }
    private void DrawSpawnModeTabs(bool glamourerOk, bool mcdfOk)
    {
        var labels = new[]
        {
            Loc.Get("Npc.Mode.Anamnesis"),
            Loc.Get("Npc.Mode.Glamourer"),
            Loc.Get("Npc.Mode.Mcdf"),
        };
        var icons = new[]
        {
            FontAwesomeIcon.UserEdit,
            FontAwesomeIcon.Tshirt,
            FontAwesomeIcon.FileImport,
        };
        var enabled = new[] { true, glamourerOk, mcdfOk };
        var modes = new[] { NpcSpawnMode.Anamnesis, NpcSpawnMode.Glamourer, NpcSpawnMode.Mcdf };
        var disabledTooltips = new[]
        {
            string.Empty,
            Loc.Get("Glamourer.Unavailable"),
            Loc.Get("Glamourer.Unavailable"),
        };

        const float btnH = 48f;
        const float spacing = 4f;
        const float rounding = 6f;

        var avail = ImGui.GetContentRegionAvail().X;
        var btnW = (avail - spacing * 2f) / 3f;

        var dl = ImGui.GetWindowDrawList();
        var bgNormal = new Vector4(0.13f, 0.13f, 0.15f, 0.95f);
        var bgHover = new Vector4(0.22f, 0.16f, 0.16f, 1f);
        var bgActive = MasterEventTheme.AccentColor with { W = 0.95f };
        var border = new Vector4(0.35f, 0.28f, 0.28f, 0.55f);
        var textNormal = new Vector4(0.82f, 0.78f, 0.78f, 1f);
        var textActive = new Vector4(1f, 1f, 1f, 1f);
        var textDisabled = new Vector4(0.45f, 0.45f, 0.45f, 1f);

        for (var i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine(0, spacing);

            var pos = ImGui.GetCursorScreenPos();
            var size = new Vector2(btnW, btnH);
            var isActive = spawnMode == modes[i];
            var isEnabled = enabled[i];

            ImGui.InvisibleButton($"##spawn_mode_{i}", size);
            var hovered = ImGui.IsItemHovered() && isEnabled;
            var clicked = ImGui.IsItemClicked() && isEnabled;

            var bg = !isEnabled ? bgNormal with { W = 0.35f }
                : isActive ? bgActive
                : hovered ? bgHover
                : bgNormal;
            var txt = !isEnabled ? textDisabled : isActive ? textActive : textNormal;

            dl.AddRectFilled(pos, pos + size, ImGui.GetColorU32(bg), rounding);
            if (!isActive)
                dl.AddRect(pos, pos + size, ImGui.GetColorU32(border with { W = hovered ? 0.9f : 0.4f }), rounding);

            ImGui.PushFont(UiBuilder.IconFont);
            var iconStr = icons[i].ToIconString();
            var iconSize = ImGui.CalcTextSize(iconStr);
            ImGui.PopFont();
            var labelSize = ImGui.CalcTextSize(labels[i]);

            var iconX = pos.X + (size.X - iconSize.X) / 2f;
            var iconY = pos.Y + 6f;
            var labelX = pos.X + (size.X - labelSize.X) / 2f;
            var labelY = pos.Y + size.Y - labelSize.Y - 6f;

            ImGui.PushFont(UiBuilder.IconFont);
            dl.AddText(new Vector2(iconX, iconY), ImGui.GetColorU32(txt), iconStr);
            ImGui.PopFont();
            dl.AddText(new Vector2(labelX, labelY), ImGui.GetColorU32(txt), labels[i]);

            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked) spawnMode = modes[i];

            if (!isEnabled && ImGui.IsItemHovered() && !string.IsNullOrEmpty(disabledTooltips[i]))
                ImGui.SetTooltip(disabledTooltips[i]);
        }
    }

    private void DrawAnamnesisMode()
    {
        ImGui.BeginDisabled(!IsNpcNameValid);
        if (ImGui.Button(Loc.Get("Npc.SpawnDefault") + "##spawn_default"))
        {
            var appearance = NpcAppearance.Default();
            if (!string.IsNullOrWhiteSpace(npcNewName)) appearance.Name = npcNewName.Trim();
            TrySpawn(appearance);
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
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

    private void DrawGlamourerMode()
    {

        DrawGlamourerCreator();
    }

    private void DrawMcdfMode()
    {
        ImGui.TextUnformatted(Loc.Get("Npc.ImportMcdf"));
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##npc_mcdf_path", ref npcMcdfImportPath, 512);
        ImGui.SameLine();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(FontAwesomeIcon.FolderOpen.ToIconString() + "##browse_mcdf"))
                BrowseFile(".mcdf", picked => npcMcdfImportPath = picked);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.BrowseFile"));
        ImGui.SameLine();
        ImGui.BeginDisabled(!IsNpcNameValid);
        if (ImGui.Button(Loc.Get("Npc.Import") + "##import_mcdf"))
            ImportMcdf();
        ImGui.EndDisabled();
    }

    private void DrawPenumbraCreator()
    {
        if (penumbra == null) return;

        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.Get("Npc.PenumbraSection"));

        if (!penumbra.Available)
        {
            ImGui.TextDisabled(Loc.Get("Npc.PenumbraUnavailable"));
            return;
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(Loc.Get("Npc.PenumbraGroupBindHint"));
        ImGui.PopTextWrapPos();

        (Guid Id, string Name)? effectiveCollection = null;
        var localIdx = (ushort?)Plugin.ObjectTable.LocalPlayer?.ObjectIndex;
        if (localIdx is { } lIdx)
            effectiveCollection = penumbra.GetEffectiveCollectionForObject(lIdx);

        if (effectiveCollection == null)
        {
            ImGui.TextColored(new Vector4(0.85f, 0.65f, 0.3f, 1f), Loc.Get("Npc.PenumbraNoYourself"));
            return;
        }

        if (!penumbraUserOptedOut)
        {
            if (!penumbraGroupBindActive)
            {
                ActivatePenumbraGroupBind(effectiveCollection.Value);
            }
            else if (penumbraBoundCollection != effectiveCollection.Value.Id)
            {

                penumbra.TrySetCollectionForGroup(global::Penumbra.Api.Enums.ApiCollectionType.MaleNonPlayerCharacter, effectiveCollection.Value.Id, out _);
                penumbra.TrySetCollectionForGroup(global::Penumbra.Api.Enums.ApiCollectionType.FemaleNonPlayerCharacter, effectiveCollection.Value.Id, out _);
                penumbraBoundCollection = effectiveCollection.Value.Id;
            }
        }

        ImGui.TextUnformatted(Loc.Get("Npc.PenumbraCurrentCollection"));
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.AccentColor, effectiveCollection.Value.Name);

        if (penumbraGroupBindActive)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 0.85f, 0.4f, 1f), Loc.Get("Npc.PenumbraGroupBindActive"));
        }

        ImGui.Spacing();

        if (penumbraGroupBindActive)
        {
            if (ImGui.Button(Loc.Get("Npc.PenumbraGroupBindDisable") + "##deactivate_group"))
            {
                DeactivatePenumbraGroupBind();
                penumbraUserOptedOut = true;
            }
        }
        else
        {
            if (ImGui.Button(Loc.Get("Npc.PenumbraGroupBindEnable") + "##activate_group"))
            {
                ActivatePenumbraGroupBind(effectiveCollection.Value);
                penumbraUserOptedOut = false;
            }
        }
    }

    private void ActivatePenumbraGroupBind((Guid Id, string Name) collection)
    {
        if (penumbra is not { Available: true }) return;

        penumbraSavedMaleNpc = penumbra.GetCollectionId(global::Penumbra.Api.Enums.ApiCollectionType.MaleNonPlayerCharacter);
        penumbraSavedFemaleNpc = penumbra.GetCollectionId(global::Penumbra.Api.Enums.ApiCollectionType.FemaleNonPlayerCharacter);

        var ok1 = penumbra.TrySetCollectionForGroup(global::Penumbra.Api.Enums.ApiCollectionType.MaleNonPlayerCharacter, collection.Id, out var err1);
        var ok2 = penumbra.TrySetCollectionForGroup(global::Penumbra.Api.Enums.ApiCollectionType.FemaleNonPlayerCharacter, collection.Id, out var err2);

        if (ok1 && ok2)
        {
            penumbraGroupBindActive = true;
            penumbraBoundCollection = collection.Id;
            npcLastInfo = string.Format(Loc.Get("Npc.PenumbraGroupBindActivatedFmt"), collection.Name);
            npcLastError = null;
        }
        else
        {
            npcLastError = err1 ?? err2 ?? Loc.Get("Npc.UnknownError");
            npcLastInfo = null;
            DeactivatePenumbraGroupBind();
        }
    }

    private void DeactivatePenumbraGroupBind()
    {
        if (penumbra is not { Available: true }) return;

        penumbra.TrySetCollectionForGroup(global::Penumbra.Api.Enums.ApiCollectionType.MaleNonPlayerCharacter, penumbraSavedMaleNpc, out _);
        penumbra.TrySetCollectionForGroup(global::Penumbra.Api.Enums.ApiCollectionType.FemaleNonPlayerCharacter, penumbraSavedFemaleNpc, out _);

        penumbraSavedMaleNpc = null;
        penumbraSavedFemaleNpc = null;
        penumbraBoundCollection = null;
        penumbraGroupBindActive = false;
        npcLastInfo = Loc.Get("Npc.PenumbraGroupBindDeactivated");
        npcLastError = null;
    }

    private void DrawGlamourerCreator()
    {
        if (glamourer == null) return;
        if (!glamourer.Available)
        {

            ImGui.TextDisabled(Loc.Get("Npc.GlamourerUnavailable"));
            return;
        }

        if (glamourerDesigns.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Npc.GlamourerNoDesigns"));
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.Get("Npc.GlamourerRefresh") + "##refresh_designs"))
                RefreshGlamourerDesigns();
            return;
        }

        var preview = glamourerSelectedIndex >= 0 && glamourerSelectedIndex < glamourerDesigns.Count
            ? glamourerDesigns[glamourerSelectedIndex].Name
            : Loc.Get("Npc.GlamourerSelectPrompt");

        ImGui.SetNextItemWidth(260f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##npc_glamourer_design", preview))
        {

            if (ImGui.IsWindowAppearing())
            {
                glamourerSearchFilter = string.Empty;
                ImGui.SetKeyboardFocusHere();
            }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##glam_search", Loc.Get("Npc.SearchPlaceholder"), ref glamourerSearchFilter, 64);

            ImGui.Separator();

            // Match insensible à la casse, substring sur le nom du design.
            var filter = glamourerSearchFilter;
            var hasFilter = !string.IsNullOrEmpty(filter);

            for (var i = 0; i < glamourerDesigns.Count; i++)
            {
                var entry = glamourerDesigns[i];
                if (hasFilter && entry.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var selected = i == glamourerSelectedIndex;
                if (ImGui.Selectable(entry.Name + "##design_" + i, selected))
                    glamourerSelectedIndex = i;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(FontAwesomeIcon.SyncAlt.ToIconString() + "##refresh_designs"))
                RefreshGlamourerDesigns();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.GlamourerRefresh"));

        var canSpawn = glamourerSelectedIndex >= 0 && glamourerSelectedIndex < glamourerDesigns.Count
            && IsNpcNameValid;
        if (!canSpawn) ImGui.BeginDisabled();
        if (ImGui.Button(Loc.Get("Npc.SpawnWithDesign") + "##spawn_glam"))
        {
            var design = glamourerDesigns[glamourerSelectedIndex];
            var appearance = NpcAppearance.Default();
            appearance.Name = string.IsNullOrWhiteSpace(npcNewName) ? design.Name : npcNewName.Trim();
            TrySpawnWithGlamourer(appearance, design);
        }
        if (!canSpawn) ImGui.EndDisabled();
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
                    npc.TeleportToLocalPlayer();
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

            if (glamourer is { Available: true } && glamourerDesigns.Count > 0
                && glamourerSelectedIndex >= 0 && glamourerSelectedIndex < glamourerDesigns.Count)
            {
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button(FontAwesomeIcon.Magic.ToIconString() + "##apply_glam"))
                    {
                        var design = glamourerDesigns[glamourerSelectedIndex];
                        if (glamourer.TryApplyDesignByName(design.Id, npc.IdentifierName, out var glamErr))
                        {
                            npcLastInfo = string.Format(Loc.Get("Npc.GlamourerAppliedFmt"), design.Name, npc.DisplayName);
                            npcLastError = null;
                        }
                        else
                        {
                            npcLastError = glamErr ?? Loc.Get("Npc.UnknownError");
                            npcLastInfo = null;
                        }
                    }
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Npc.GlamourerApplyTooltip"));
                ImGui.SameLine();
            }

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

    private void TrySpawn(NpcAppearance appearance)
    {
        if (npcManager == null) return;
        if (npcManager.TrySpawn(appearance, out var instance, out var error))
        {
            npcSelected = instance;
            npcLastInfo = string.Format(Loc.Get("Npc.SpawnedFmt"), instance!.DisplayName);
            npcLastError = null;
        }
        else
        {
            npcLastError = error ?? Loc.Get("Npc.UnknownError");
            npcLastInfo = null;
        }
    }

    private void TrySpawnWithGlamourer(NpcAppearance appearance, MasterEvent.API.Glamourer.DesignEntry design)
    {
        if (npcManager == null || glamourer == null) return;
        if (!npcManager.TrySpawn(appearance, out var instance, out var error))
        {
            npcLastError = error ?? Loc.Get("Npc.UnknownError");
            npcLastInfo = null;
            return;
        }

        npcSelected = instance;
        var captured = instance!;
        var api = glamourer;
        var penApi = penumbra;
        captured.Drawn += () =>
        {
            if (penApi is { Available: true } && captured.GameObjectIndex is { } i0)
                penApi.LogResolvedPaths(i0, "draw-initial");

            Plugin.Framework.RunOnTick(() =>
            {
                if (api.TryApplyDesignByName(design.Id, captured.IdentifierName, out var glamErr))
                {
                    npcLastInfo = string.Format(Loc.Get("Npc.SpawnedWithDesignFmt"), captured.DisplayName, design.Name);
                    npcLastError = null;
                }
                else
                {
                    npcLastError = glamErr ?? Loc.Get("Npc.UnknownError");
                    npcLastInfo = null;
                }

                Plugin.Framework.RunOnTick(() =>
                {
                    if (penApi is { Available: true } && captured.GameObjectIndex is { } i2)
                    {
                        penApi.Redraw(i2);
                        Plugin.Framework.RunOnTick(() =>
                        {
                            if (captured.GameObjectIndex is { } i3)
                                penApi.LogResolvedPaths(i3, "after-glam-redraw");
                        }, delayTicks: 30);
                    }
                }, delayTicks: 30);
            }, delayTicks: 30);
        };
        npcLastInfo = string.Format(Loc.Get("Npc.SpawnedFmt"), captured.DisplayName);
        npcLastError = null;
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

    private void ImportMcdf()
    {
        if (glamourer is not { Available: true })
        {
            npcLastError = Loc.Get("Glamourer.Unavailable");
            npcLastInfo = null;
            return;
        }

        var path = npcMcdfImportPath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            npcLastError = Loc.Get("Npc.ImportPathInvalid");
            npcLastInfo = null;
            return;
        }

        Models.Mcdf.MareCharaFileHeader header;
        try
        {
            header = Models.Mcdf.MareCharaFileHeader.LoadFromFile(path);
        }
        catch (Exception ex)
        {
            npcLastError = string.Format(Loc.Get("Npc.McdfParseErrorFmt"), ex.Message);
            npcLastInfo = null;
            return;
        }

        if (string.IsNullOrEmpty(header.CharaFileData.GlamourerData))
        {
            npcLastError = Loc.Get("Npc.McdfNoGlamourer");
            npcLastInfo = null;
            return;
        }

        McdfSession? session = null;
        var hasMods = header.CharaFileData.Files.Count > 0
            || header.CharaFileData.FileSwaps.Count > 0
            || !string.IsNullOrEmpty(header.CharaFileData.ManipulationData);

        if (penumbra is { Available: true } && hasMods)
        {
            session = TryPrepareMcdfSession(header, path, out var setupErr);
            if (session == null && !string.IsNullOrEmpty(setupErr))
                Plugin.Log.Warning($"[MasterEvent] MCDF Penumbra setup partiel : {setupErr}");
        }

        var appearance = NpcAppearance.Default();
        appearance.Name = string.IsNullOrWhiteSpace(npcNewName)
            ? Path.GetFileNameWithoutExtension(path)
            : npcNewName.Trim();

        if (npcManager == null) return;
        if (!npcManager.TrySpawn(appearance, out var instance, out var spawnErr))
        {
            npcLastError = spawnErr ?? Loc.Get("Npc.UnknownError");
            npcLastInfo = null;
            // Cleanup la session si on en avait préparé une.
            if (session != null) DisposeMcdfSession(session);
            return;
        }

        npcSelected = instance;
        var captured = instance!;
        var glamApi = glamourer;
        var penApi = penumbra;
        var glamData = header.CharaFileData.GlamourerData;
        var displayName = captured.DisplayName;
        var capturedSession = session;

        // Track la session pour cleanup au despawn (cf. OnNpcDespawning).
        if (capturedSession != null)
            mcdfSessions[captured] = capturedSession;

        captured.Drawn += () =>
        {
            Plugin.Framework.RunOnTick(() =>
            {
                var idx = captured.GameObjectIndex;
                if (idx == null)
                {
                    npcLastError = Loc.Get("Npc.UnknownError");
                    npcLastInfo = null;
                    return;
                }


                if (capturedSession != null && penApi != null)
                {
                    captured.RunWithPlayerKind(() =>
                    {
                        penApi.TryAssignTempCollectionToActor(capturedSession.TempCollection, idx.Value, out _);
                    });
                }

                if (glamApi.TryApplyStateBase64(glamData, idx.Value, out var applyErr))
                {
                    npcLastInfo = string.Format(
                        capturedSession != null ? Loc.Get("Npc.McdfImportedWithModsFmt") : Loc.Get("Npc.McdfImportedFmt"),
                        displayName);
                    npcLastError = null;
                }
                else
                {
                    npcLastError = applyErr ?? Loc.Get("Npc.UnknownError");
                    npcLastInfo = null;
                }
            }, delayTicks: 30);
        };

        npcLastInfo = string.Format(Loc.Get("Npc.SpawnedFmt"), captured.DisplayName);
        npcLastError = null;
    }

    // Prépare une session MCDF
    private McdfSession? TryPrepareMcdfSession(Models.Mcdf.MareCharaFileHeader header, string sourcePath, out string? error)
    {
        error = null;
        if (penumbra is not { Available: true })
        {
            error = "Penumbra indisponible.";
            return null;
        }

        var extractDir = Path.Combine(Path.GetTempPath(), "MasterEvent_mcdf_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(extractDir);
        }
        catch (Exception ex)
        {
            error = $"Création du dossier temporaire échouée : {ex.Message}";
            return null;
        }

        var extractedFiles = new List<string>();
        Dictionary<string, string> modPaths;
        try
        {
            modPaths = header.ExtractFilesTo(sourcePath, extractDir, extractedFiles);
        }
        catch (Exception ex)
        {
            error = $"Extraction des fichiers MCDF échouée : {ex.Message}";
            try { Directory.Delete(extractDir, recursive: true); } catch { /* best-effort */ }
            return null;
        }

        var sessionTag = "MasterEvent_Mcdf_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var collId = penumbra.TryCreateTempCollection(sessionTag, sessionTag, out var createErr);
        if (collId == Guid.Empty)
        {
            error = createErr;
            try { Directory.Delete(extractDir, recursive: true); } catch { /* best-effort */ }
            return null;
        }

        if (modPaths.Count > 0)
            penumbra.TryAddTempMod(sessionTag + "_Files", collId, modPaths, string.Empty, 0, out _);

        if (!string.IsNullOrEmpty(header.CharaFileData.ManipulationData))
        {
            var emptyPaths = new Dictionary<string, string>(0, StringComparer.Ordinal);
            penumbra.TryAddTempMod(sessionTag + "_Meta", collId, emptyPaths, header.CharaFileData.ManipulationData, 0, out _);
        }

        return new McdfSession
        {
            TempCollection = collId,
            ExtractDir = extractDir,
            ExtractedFiles = extractedFiles,
        };
    }

    // Détruit une session MCDF
    private void DisposeMcdfSession(McdfSession session)
    {
        if (penumbra is { Available: true })
            penumbra.TryDeleteTempCollection(session.TempCollection);

        foreach (var f in session.ExtractedFiles)
        {
            try { File.Delete(f); } catch { /* best-effort */ }
        }
        try { Directory.Delete(session.ExtractDir, recursive: true); } catch { /* best-effort */ }
    }

    // Cleanup toutes les sessions MCDF actives.
    private void CleanupAllMcdfSessions()
    {
        foreach (var session in mcdfSessions.Values)
            DisposeMcdfSession(session);
        mcdfSessions.Clear();
    }
}
