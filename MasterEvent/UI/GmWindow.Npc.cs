using System;
using System.IO;
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

    public void SetNpcManager(NpcManager manager)
    {
        npcManager = manager;
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

    private void DrawNpcCreator()
    {
        ImGui.TextUnformatted(Loc.Get("Npc.NewName"));
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##npc_new_name", ref npcNewName, 30);

        if (ImGui.Button(Loc.Get("Npc.SpawnDefault") + "##spawn_default"))
        {
            var appearance = NpcAppearance.Default();
            if (!string.IsNullOrWhiteSpace(npcNewName)) appearance.Name = npcNewName.Trim();
            TrySpawn(appearance);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.Get("Npc.ImportAnamnesis"));
        ImGui.SetNextItemWidth(320f * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##npc_import_path", ref npcImportPath, 512);
        ImGui.SameLine();
        if (ImGui.Button(Loc.Get("Npc.Import") + "##import_anam"))
        {
            ImportAnamnesis();
        }
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
