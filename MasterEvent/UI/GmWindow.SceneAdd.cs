using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services.Npc;
using MasterEvent.UI.Components;
using MasterEvent.Waymarks;

namespace MasterEvent.UI;

// Ajout en un clic : une balise libre se pose au sol, ou aux pieds du MJ avec Maj, et un PNJ
// enregistré ou un monstre épinglé apparaît aussitôt. Les créations complètes gardent leur page.
public sealed partial class GmWindow
{
    private Vector2 sceneAddAnchor;
    private string sceneAddQuery = string.Empty;
    private const int SceneAddRowLimit = 6;

    private void DrawSceneAddPopup(bool npcAvailable)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowPos(new Vector2(sceneAddAnchor.X, sceneAddAnchor.Y + 4f * scale), ImGuiCond.Appearing,
            new Vector2(1f, 0f));
        ImGui.SetNextWindowSize(new Vector2(340f * scale, 0));
        if (!ImGui.BeginPopup("##scene_add_popup")) return;

        ImGui.TextColored(MasterEventTheme.TextStrong, Loc.Get("Scene.AddTitle"));
        if (npcAvailable)
        {
            var count = npcManager!.Count;
            var slots = string.Format(Loc.Get("Scene.SlotsFmt"), count, NpcManager.MaxConcurrentNpcs);
            ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(slots).X);
            ImGui.TextColored(count >= NpcManager.MaxConcurrentNpcs ? MasterEventTheme.WarningColor : MasterEventTheme.MutedTextColor,
                slots);
        }
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        DrawSceneAddWaymarks();
        if (npcAvailable) DrawSceneAddNpcs();

        ImGui.EndPopup();
    }

    private void DrawSceneAddWaymarks()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Terrain.FilterMarkers"));
        ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X);
        ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Scene.AddMarkersHint"));
        ImGui.PopTextWrapPos();
        ImGuiHelpers.ScaledDummy(2f);

        var gap = 4f * scale;
        var size = Math.Min(36f * scale, (ImGui.GetContentRegionAvail().X - gap * (Constants.WaymarkCount - 1)) / Constants.WaymarkCount);
        var dl = ImGui.GetWindowDrawList();
        var rounding = MasterEventTheme.RadiusControl * scale;

        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            if (i > 0) ImGui.SameLine(0, gap);

            var id = (WaymarkId)i;
            var marker = session.CurrentMarkers[id];
            var placed = marker.IsVisible;
            var pos = ImGui.GetCursorScreenPos();
            var clicked = ImGui.InvisibleButton($"##slot_{i}", new Vector2(size, size));
            var hovered = ImGui.IsItemHovered();

            dl.AddRectFilled(pos, pos + new Vector2(size, size),
                ImGui.GetColorU32(hovered ? MasterEventTheme.ThemeFrameBgHovered : MasterEventTheme.ThemeFrameBg), rounding);
            var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(id.ToIconId())).GetWrapOrEmpty();
            var inset = 3f * scale;
            dl.AddImage(wrap.Handle, pos + new Vector2(inset, inset), pos + new Vector2(size - inset, size - inset),
                Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, placed ? 0.3f : 1f)));

            if (hovered)
            {
                var name = string.IsNullOrEmpty(marker.Name) ? Loc.Get("Scene.Unnamed") : marker.Name;
                ImGui.SetTooltip(placed
                    ? string.Format(Loc.Get("Scene.WaymarkPlacedFmt"), id.ToLabel(), name)
                    : string.Format(Loc.Get("Scene.WaymarkFreeFmt"), id.ToLabel()));
            }

            if (!clicked) continue;

            // Une balise déjà posée ouvre sa carte ; une libre se pose, et sa carte s'ouvrira à l'arrivée.
            if (placed)
            {
                SelectScene(SceneSelection.Marker, marker: id, toggle: false);
            }
            else
            {
                FollowWaymarkPlacement(id);
                if (ImGui.GetIO().KeyShift)
                {
                    PlaceWaymarkAtFeet(id);
                }
                else
                {
                    WaymarkManager.EnterPlacementMode(id);
                    ShowTerrainHint(string.Format(Loc.Get("Terrain.PlaceWaymarkFmt"), id.ToLabel()));
                }
            }

            ImGui.CloseCurrentPopup();
        }
    }

    private void PlaceWaymarkAtFeet(WaymarkId id)
    {
        var position = Plugin.ObjectTable.LocalPlayer?.Position;
        if (position is { } p && WaymarkManager.PlaceWaymark(id, p.X, p.Y, p.Z)) return;

        scenePlacingWaymark = null;
        ShowTerrainHint(Loc.Get("Scene.WaymarkBlocked"));
    }

    private void DrawSceneAddNpcs()
    {
        ImGuiHelpers.ScaledDummy(6f);
        SearchField.Draw("scene_add_search", ref sceneAddQuery, Loc.Get("Scene.SearchHint"), ImGui.GetContentRegionAvail().X);
        ImGuiHelpers.ScaledDummy(2f);

        var query = sceneAddQuery.Trim();
        bool Matches(string name) => query.Length == 0 || name.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        var full = npcManager!.Count >= NpcManager.MaxConcurrentNpcs;
        var npcs = NpcPresetNames(creatures: false).Where(Matches).Take(SceneAddRowLimit).ToList();
        var savedCreatures = NpcPresetNames(creatures: true).Where(Matches).ToList();
        var favorites = configuration.FavoriteCreatures.Where(f => Matches(f.Name))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(0, SceneAddRowLimit - savedCreatures.Count))
            .ToList();

        if (npcs.Count > 0)
        {
            ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Npc.Presets"));
            foreach (var name in npcs)
                DrawSceneAddRow("npc_" + name, FontAwesomeIcon.UserFriends, name, full, () => SpawnPresetNamed(name));
        }

        if (savedCreatures.Count + favorites.Count > 0)
        {
            if (npcs.Count > 0) ImGuiHelpers.ScaledDummy(4f);
            ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Scene.FavoriteCreatures"));
            foreach (var name in savedCreatures.Take(SceneAddRowLimit))
                DrawSceneAddRow("saved_" + name, FontAwesomeIcon.Dragon, name, full, () => SpawnPresetNamed(name));
            foreach (var favorite in favorites)
                DrawSceneAddRow("fav_" + favorite.BNpcBaseId + "_" + favorite.Name, FontAwesomeIcon.Dragon, favorite.Name, full,
                    () => SpawnFavoriteCreature(favorite));
        }

        if (npcs.Count + savedCreatures.Count + favorites.Count == 0)
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get(query.Length > 0 ? "Scene.NoMatch" : "Scene.AddEmpty"));

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        DrawSceneAddPageLink(Loc.Get("Scene.NewNpc") + "##scene_new_npc", TerrainPage.NewNpc, full);
        ImGui.SameLine();
        DrawSceneAddPageLink(Loc.Get("Scene.FindCreature") + "##scene_find_creature", TerrainPage.NewCreature, full);
    }

    private void DrawSceneAddPageLink(string label, TerrainPage page, bool full)
    {
        bool clicked;
        using (ImRaii.Disabled(full))
            clicked = Buttons.Tinted(label);
        if (full && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.Get("Terrain.NpcLimit"));

        if (!clicked) return;
        OpenTerrainPage(page);
        ImGui.CloseCurrentPopup();
    }

    private void DrawSceneAddRow(string id, FontAwesomeIcon icon, string name, bool full, Action spawn)
    {
        ImGui.PushID(id);

        var spawnLabel = Loc.Get("Npc.PresetSpawn");
        var buttonWidth = Buttons.TextWidth(spawnLabel);
        var rowEnd = ImGui.GetContentRegionMax().X;

        ImGui.AlignTextToFramePadding();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.NpcColor, icon.ToIconString());
        ImGui.SameLine();
        ImGui.TextUnformatted(FitText(name, rowEnd - ImGui.GetCursorPosX() - buttonWidth - ImGui.GetStyle().ItemSpacing.X));

        ImGui.SameLine(rowEnd - buttonWidth);
        bool clicked;
        using (ImRaii.Disabled(full))
            clicked = Buttons.Tinted(spawnLabel + "##spawn");
        if (full && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.Get("Terrain.NpcLimit"));

        if (clicked)
        {
            spawn();
            ImGui.CloseCurrentPopup();
        }

        ImGui.PopID();
    }

    private void OpenTerrainPage(TerrainPage page)
    {
        terrainPage = page;
        terrainSpawnSerialAtEntry = terrainSpawnSerial;
    }

    // Même apparition que la page « Nouveau monstre », sans toucher à ce qu'elle a en cours.
    private void SpawnFavoriteCreature(FavoriteCreature favorite)
    {
        var appearance = NpcAppearance.Default();
        appearance.Name = favorite.Name;
        appearance.Creature = FromFavorite(favorite);

        if (TrySpawn(appearance) is not { } npc) return;

        npc.Rename(appearance.Name);
        npc.Attitude = Attitude.Hostile;
        npcManager!.NotifyChanged();
        RememberRecentCreature(appearance.Creature, appearance.Name);
    }
}
