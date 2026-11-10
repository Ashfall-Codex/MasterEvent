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

// Pages de création du terrain, ouvertes depuis l'ajout de la scène : nouveau PNJ et nouveau monstre.
public sealed partial class GmWindow
{
    private enum TerrainFilter { All, Markers, Npcs, Creatures }
    private enum TerrainPage { List, NewNpc, NewCreature }

    private TerrainFilter terrainFilter;
    private TerrainPage terrainPage;

    // Incrémenté à chaque PNJ créé : la page « Nouveau PNJ » se referme dès qu'il change.
    private int terrainSpawnSerial;
    private int terrainSpawnSerialAtEntry;

    // Hauteur réservée à la barre d'action fixe des pages « Nouveau PNJ » et « Nouveau monstre ».
    private static float SpawnBarHeight
        => ImGui.GetFrameHeight() * 1.2f + ImGui.GetStyle().ItemSpacing.Y * 3f;

    // Création d'un PNJ en pleine page : la source se choisit en haut, l'unique action « Faire
    // apparaître » reste dans la barre fixe du bas, avec le nom qu'elle utilisera.
    private void DrawNewNpcPage()
    {
        if (DrawEditorBreadcrumb(Loc.Get("Terrain.NewNpc"), null, false, Loc.Get("Sidebar.Scene")))
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
