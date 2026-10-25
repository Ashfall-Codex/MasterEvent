using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Services.Npc;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Onglet « Terrain » : marqueurs et PNJ réunis. Les deux portent les mêmes données de jeu (PV, stats,
// jets) ; les séparer obligeait à changer d'onglet pour suivre une scène.
public sealed partial class GmWindow
{
    private enum TerrainFilter { All, Markers, Npcs, Creatures }
    private enum TerrainPage { List, NewNpc, NewCreature }

    private TerrainFilter terrainFilter;
    private TerrainPage terrainPage;

    // Incrémenté à chaque PNJ créé : la page « Nouveau PNJ » se referme dès qu'il change.
    private int terrainSpawnSerial;
    private int terrainSpawnSerialAtEntry;

    private void DrawTerrainContent()
    {
        var gmAccess = HasGmAccess();

        if (!session.CanEdit && !gmAccess)
        {
            var avail = ImGui.GetContentRegionAvail();
            var text = Loc.Get("Gm.PlayerViewLocked");
            var textSz = ImGui.CalcTextSize(text);
            ImGui.SetCursorPos(new Vector2(
                ImGui.GetCursorPosX() + (avail.X - textSz.X) / 2f,
                ImGui.GetCursorPosY() + (avail.Y - textSz.Y) / 2f));
            ImGui.TextColored(MasterEventTheme.TextDim, text);
            return;
        }

        npcManager?.PruneDead();

        if (terrainPage != TerrainPage.List)
        {
            // Le PNJ créé (ou des droits perdus) referme la page et ramène à la liste.
            if (gmAccess && npcManager != null && terrainSpawnSerial == terrainSpawnSerialAtEntry)
            {
                if (terrainPage == TerrainPage.NewNpc) DrawNewNpcPage();
                else DrawNewCreaturePage();
                return;
            }

            terrainPage = TerrainPage.List;
        }

        DrawHeader();

        if (ImGui.BeginChild("##terrain_scroll", Vector2.Zero))
            DrawTerrainList(gmAccess && npcManager != null);
        ImGui.EndChild();
    }

    private void DrawTerrainList(bool npcAvailable)
    {
        ImGuiHelpers.ScaledDummy(4f);
        DrawNpcNotices();

        var markerCount = CountPlacedMarkers();
        var creatureCount = npcAvailable ? npcManager!.Instances.Count(n => n.IsCreature) : 0;
        var npcCount = npcAvailable ? npcManager!.Count - creatureCount : 0;

        if (!npcAvailable) terrainFilter = TerrainFilter.Markers;

        DrawTerrainToolbar(npcAvailable);
        ImGuiHelpers.ScaledDummy(8f);

        if (markerCount == 0 && npcCount == 0 && creatureCount == 0)
        {
            LayoutControls.DrawEmptyState(FontAwesomeIcon.MapMarkerAlt,
                Loc.Get("Terrain.EmptyTitle"), Loc.Get("Terrain.EmptyHint"));
            return;
        }

        var showMarkers = terrainFilter is TerrainFilter.All or TerrainFilter.Markers;
        var showNpcs = npcAvailable && terrainFilter is TerrainFilter.All or TerrainFilter.Npcs;
        // Sous « Tous », la section des monstres n'apparaît que s'il y en a sur le terrain.
        var showCreatures = npcAvailable && (terrainFilter == TerrainFilter.Creatures
                                             || (terrainFilter == TerrainFilter.All && creatureCount > 0));

        if (showMarkers)
        {
            if (markerCount > 0)
            {
                DrawTerrainSectionTitle(string.Format(Loc.Get("Terrain.MarkersFmt"), markerCount));
                DrawMarkerCards();
            }
            else if (terrainFilter == TerrainFilter.Markers)
            {
                ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Gm.NoMarkers"));
            }
        }

        if (showNpcs)
        {
            if (showMarkers && markerCount > 0) ImGuiHelpers.ScaledDummy(8f);

            DrawTerrainSectionTitle(string.Format(Loc.Get("Terrain.NpcsFmt"), npcCount, NpcManager.MaxConcurrentNpcs));
            DrawNpcList(creatures: false);
        }

        if (showCreatures)
        {
            if (showMarkers || showNpcs) ImGuiHelpers.ScaledDummy(8f);

            DrawTerrainSectionTitle(string.Format(Loc.Get("Terrain.CreaturesFmt"), creatureCount));
            DrawNpcList(creatures: true);
        }
    }

    private static void DrawTerrainSectionTitle(string text)
    {
        ImGui.TextColored(MasterEventTheme.TextSecondary, text);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);
    }

    // Barre d'outils : le filtre à gauche, l'unique action principale (ajouter) à droite.
    private void DrawTerrainToolbar(bool npcAvailable)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var labels = npcAvailable
            ? new[]
            {
                Loc.Get("Terrain.FilterAll"),
                Loc.Get("Terrain.FilterMarkers"),
                Loc.Get("Terrain.FilterNpcs"),
                Loc.Get("Terrain.FilterCreatures"),
            }
            : null;

        var addLabel = "+  " + Loc.Get(npcAvailable ? "Terrain.Add" : "Gm.AddMarker");
        var (selected, clicked, addMax) = DrawSegmentedToolbar("terrain_bar", labels, (int)terrainFilter, addLabel,
            leftText: npcAvailable ? null : Loc.Get("Terrain.FilterMarkers"));
        terrainFilter = (TerrainFilter)selected;

        if (!npcAvailable)
        {
            if (clicked) OpenFieldMarkerAgent();
            return;
        }

        if (clicked) ImGui.OpenPopup("##terrain_add_menu");
        DrawTerrainAddMenu(addMax, scale);
    }

    // Menu d'ajout : ancré sous le bouton, aligné à droite, avec le décompte des PNJ en regard.
    private void DrawTerrainAddMenu(Vector2 buttonMax, float scale)
    {
        var menuWidth = 210f * scale;
        var rowHeight = ImGui.GetFrameHeight() * 1.15f;

        ImGui.SetNextWindowPos(new Vector2(buttonMax.X, buttonMax.Y + 4f * scale), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowSize(new Vector2(menuWidth, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f * scale, 8f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f * scale, 4f * scale));

        if (ImGui.BeginPopup("##terrain_add_menu"))
        {
            if (ImGui.Selectable(Loc.Get("Terrain.AddMarker") + "##add_marker", false, ImGuiSelectableFlags.None,
                    new Vector2(0, rowHeight)))
                OpenFieldMarkerAgent();

            // PNJ et monstres partagent les mêmes emplacements : même décompte sur les deux lignes.
            DrawTerrainAddNpcRow(Loc.Get("Terrain.AddNpc") + "##add_npc", TerrainPage.NewNpc, rowHeight, scale);
            DrawTerrainAddNpcRow(Loc.Get("Terrain.AddCreature") + "##add_creature", TerrainPage.NewCreature, rowHeight, scale);

            ImGui.EndPopup();
        }

        ImGui.PopStyleVar(2);
    }

    private void DrawTerrainAddNpcRow(string label, TerrainPage page, float rowHeight, float scale)
    {
        var count = npcManager!.Count;
        var full = count >= NpcManager.MaxConcurrentNpcs;
        ImGui.BeginDisabled(full);
        if (ImGui.Selectable(label, false, ImGuiSelectableFlags.None, new Vector2(0, rowHeight)))
        {
            terrainPage = page;
            terrainSpawnSerialAtEntry = terrainSpawnSerial;
        }
        ImGui.EndDisabled();

        // Décompte à droite de la ligne, en discret ; le motif de blocage en infobulle.
        var itemMin = ImGui.GetItemRectMin();
        var itemMax = ImGui.GetItemRectMax();
        var detail = $"{count}/{NpcManager.MaxConcurrentNpcs}";
        var detailSize = ImGui.CalcTextSize(detail);
        ImGui.GetWindowDrawList().AddText(
            new Vector2(itemMax.X - detailSize.X - 6f * scale, itemMin.Y + (itemMax.Y - itemMin.Y - detailSize.Y) / 2f),
            ImGui.GetColorU32(full ? MasterEventTheme.WarningColor : MasterEventTheme.TextDim), detail);
        if (full && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.Get("Terrain.NpcLimit"));
    }

    // Hauteur réservée à la barre d'action fixe des pages « Nouveau PNJ » et « Nouveau monstre ».
    private static float SpawnBarHeight
        => ImGui.GetFrameHeight() * 1.2f + ImGui.GetStyle().ItemSpacing.Y * 3f;

    // Création d'un PNJ en pleine page : la source se choisit en haut, l'unique action « Faire
    // apparaître » reste dans la barre fixe du bas, avec le nom qu'elle utilisera.
    private void DrawNewNpcPage()
    {
        if (DrawEditorBreadcrumb(Loc.Get("Terrain.NewNpc"), null, false, Loc.Get("Terrain.Title")))
        {
            terrainPage = TerrainPage.List;
            return;
        }

        if (ImGui.BeginChild("##new_npc_scroll", new Vector2(0, -SpawnBarHeight)))
        {
            ImGuiHelpers.ScaledDummy(4f);
            LayoutControls.DrawNotice(Loc.Get("Npc.Warning"), MasterEventTheme.WarningColor);
            ImGuiHelpers.ScaledDummy(4f);
            DrawNpcNotices();

            LayoutControls.BeginCard(Loc.Get("Npc.CreateTitle"), FontAwesomeIcon.UserPlus);
            DrawNpcCreator();
            LayoutControls.EndCard();

            LayoutControls.BeginCard(Loc.Get("Npc.Presets"), FontAwesomeIcon.BookOpen);
            DrawNpcPresets(creatures: false);
            LayoutControls.EndCard();
        }
        ImGui.EndChild();

        DrawNpcSpawnBar();
    }
}
