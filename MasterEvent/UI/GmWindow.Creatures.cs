using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services.Npc;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Page « Nouveau monstre » : un animal ou un monstre du jeu, choisi dans le catalogue ou copié sur
// la cible, apparaît comme un PNJ, avec les mêmes barres, stats et jets.
public sealed partial class GmWindow
{
    // Au-delà, la liste ne se parcourt plus : mieux vaut préciser la recherche.
    private const int CreatureResultLimit = 50;
    private const int CreatureRecentLimit = 5;

    private string creatureQuery = string.Empty;
    private string creatureName = string.Empty;
    private NpcAppearance.CreatureLook? creaturePick;
    private string? creatureError;

    private string? creatureResultsQuery;
    private List<CreatureCatalog.Entry> creatureResults = [];

    // Derniers monstres apparus, le plus récent en tête. Gardés le temps de la session de jeu.
    private readonly List<(NpcAppearance.CreatureLook Look, string Name)> creatureRecents = [];

    private void DrawNewCreaturePage()
    {
        if (DrawEditorBreadcrumb(Loc.Get("Terrain.NewCreature"), null, false, Loc.Get("Terrain.Title")))
        {
            terrainPage = TerrainPage.List;
            return;
        }

        if (ImGui.BeginChild("##new_creature_scroll", new Vector2(0, -SpawnBarHeight)))
        {
            ImGuiHelpers.ScaledDummy(4f);
            DrawNpcNotices();

            LayoutControls.BeginCard(Loc.Get("Creature.PickTitle"), FontAwesomeIcon.Dragon);
            DrawCreaturePicker();
            LayoutControls.EndCard();

            // Monstres enregistrés avec leurs stats : comme les favoris, seulement sans recherche,
            // et seulement s'il y en a.
            if (string.IsNullOrWhiteSpace(creatureQuery) && NpcPresetNames(creatures: true) is { Count: > 0 } saved)
            {
                LayoutControls.BeginCard(Loc.Get("Creature.Saved"), FontAwesomeIcon.BookOpen);
                DrawNpcPresets(creatures: true, saved);
                LayoutControls.EndCard();
            }
        }
        ImGui.EndChild();

        DrawCreatureSpawnBar();
    }

    private void DrawCreaturePicker()
    {
        var width = LayoutControls.CardContentWidth;
        SearchField.Draw("creature_query", ref creatureQuery, Loc.Get("Creature.SearchHint"), width);

        if (string.IsNullOrWhiteSpace(creatureQuery))
        {
            // Sans recherche, pas de liste imposée : seulement ce que le MJ a épinglé ou déjà utilisé.
            DrawCreatureFavorites(width);
            DrawCreatureRecents(width);
        }
        else if (CreatureCatalog.Entries == null)
        {
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Creature.Loading"));
        }
        else
        {
            // Recherche refaite à la frappe seulement : le catalogue compte des milliers de lignes.
            if (creatureResultsQuery != creatureQuery)
            {
                creatureResultsQuery = creatureQuery;
                creatureResults = CreatureCatalog.Search(creatureQuery).ToList();
            }

            DrawCreatureResults(width);
        }

        ImGuiHelpers.ScaledDummy(2f);
        if (QuietButton(Loc.Get("Creature.CopyTarget") + "##copy_target"))
        {
            if (CreatureCatalog.TryCaptureTarget(out var look, out var name))
                PickCreature(look!, name ?? string.Empty);
            else
                creatureError = Loc.Get("Creature.CopyTargetFailed");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Creature.CopyTargetHint"));

        if (creatureError != null)
            ImGui.TextColored(MasterEventTheme.DangerColor, creatureError);
    }

    private void DrawCreatureResults(float width)
    {
        if (creatureResults.Count == 0)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Creature.NoResult"));
            return;
        }

        var shown = Math.Min(creatureResults.Count, CreatureResultLimit);
        DrawCreatureRows("##creature_results", width, shown,
            i => (CreatureCatalog.ToLook(creatureResults[i]), creatureResults[i].Name));

        if (creatureResults.Count > shown)
            ImGui.TextColored(MasterEventTheme.MutedTextColor,
                string.Format(Loc.Get("Creature.MoreFmt"), creatureResults.Count - shown));
    }

    private void DrawCreatureFavorites(float width)
    {
        var favorites = configuration.FavoriteCreatures;
        if (favorites.Count == 0)
        {
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Creature.FavoritesHint"));
            return;
        }

        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Creature.Favorites"));
        // Copie : désépingler une ligne modifie la liste pendant le parcours.
        var sorted = favorites.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        DrawCreatureRows("##creature_favorites", width, sorted.Count, i => (FromFavorite(sorted[i]), sorted[i].Name));
    }

    // Les récents déjà épinglés figurent dans les favoris juste au-dessus : inutile de les répéter.
    private void DrawCreatureRecents(float width)
    {
        var recents = creatureRecents.Where(r => !IsFavorite(r.Look, r.Name)).ToList();
        if (recents.Count == 0) return;

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Creature.Recent"));
        DrawCreatureRows("##creature_recents", width, recents.Count, i => (CopyLook(recents[i].Look), recents[i].Name));
    }

    private void RememberRecentCreature(NpcAppearance.CreatureLook look, string name)
    {
        creatureRecents.RemoveAll(r => r.Look.BNpcBaseId == look.BNpcBaseId && r.Name == name);
        // Copie : le modèle choisi part avec le PNJ apparu, qui peut le modifier.
        creatureRecents.Insert(0, (CopyLook(look), name));
        if (creatureRecents.Count > CreatureRecentLimit)
            creatureRecents.RemoveRange(CreatureRecentLimit, creatureRecents.Count - CreatureRecentLimit);
    }

    // Une ligne par monstre : le nom à sélectionner, son modèle en discret, et l'épingle à droite.
    private void DrawCreatureRows(string id, float width, int count,
        Func<int, (NpcAppearance.CreatureLook Look, string Name)> row)
    {
        var rows = Math.Min(count, 10);
        var height = rows * ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2f;
        if (!ImGui.BeginChild(id, new Vector2(width, height), true))
        {
            ImGui.EndChild();
            return;
        }

        var pinWidth = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        for (var i = 0; i < count; i++)
        {
            var (look, name) = row(i);
            ImGui.PushID(i);

            var selected = creaturePick?.BNpcBaseId == look.BNpcBaseId && creatureName == name;
            var rowWidth = ImGui.GetContentRegionAvail().X - pinWidth - spacing;
            if (ImGui.Selectable(name + "##pick", selected, ImGuiSelectableFlags.None,
                    new Vector2(rowWidth, ImGui.GetFrameHeight())))
                PickCreature(look, name);

            // Le modèle distingue deux monstres de même nom.
            var model = string.Format(Loc.Get("Creature.ModelFmt"), look.ModelCharaId);
            var max = ImGui.GetItemRectMax();
            var modelSize = ImGui.CalcTextSize(model);
            ImGui.GetWindowDrawList().AddText(
                new Vector2(max.X - modelSize.X - spacing, max.Y - (ImGui.GetFrameHeight() + modelSize.Y) / 2f),
                ImGui.GetColorU32(MasterEventTheme.TextDim), model);

            ImGui.SameLine();
            DrawCreaturePin(look, name);
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    private void DrawCreaturePin(NpcAppearance.CreatureLook look, string name)
    {
        var pinned = IsFavorite(look, name);
        ImGui.PushStyleColor(ImGuiCol.Text, pinned ? MasterEventTheme.AccentText : MasterEventTheme.TextDim);
        var clicked = QuietIconButton(FontAwesomeIcon.Thumbtack, "pin",
            Loc.Get(pinned ? "Creature.Unpin" : "Creature.Pin"));
        ImGui.PopStyleColor();
        if (!clicked) return;

        if (pinned)
            configuration.FavoriteCreatures.RemoveAll(f => f.BNpcBaseId == look.BNpcBaseId && f.Name == name);
        else
            configuration.FavoriteCreatures.Add(new FavoriteCreature
            {
                Name = name,
                BNpcBaseId = look.BNpcBaseId,
                BNpcNameId = look.BNpcNameId,
                ModelCharaId = look.ModelCharaId,
                Scale = look.Scale,
            });
        configuration.Save();
    }

    private bool IsFavorite(NpcAppearance.CreatureLook look, string name)
        => configuration.FavoriteCreatures.Any(f => f.BNpcBaseId == look.BNpcBaseId && f.Name == name);

    private static NpcAppearance.CreatureLook CopyLook(NpcAppearance.CreatureLook look) => new()
    {
        BNpcBaseId = look.BNpcBaseId,
        BNpcNameId = look.BNpcNameId,
        ModelCharaId = look.ModelCharaId,
        Scale = look.Scale,
    };

    private static NpcAppearance.CreatureLook FromFavorite(FavoriteCreature favorite) => new()
    {
        BNpcBaseId = favorite.BNpcBaseId,
        BNpcNameId = favorite.BNpcNameId,
        ModelCharaId = favorite.ModelCharaId,
        Scale = favorite.Scale,
    };

    private void PickCreature(NpcAppearance.CreatureLook look, string name)
    {
        creaturePick = look;
        creatureName = name;
        creatureError = null;
    }

    // Barre fixe : « Annuler » discret, le nom affiché, puis l'unique action principale à droite.
    private void DrawCreatureSpawnBar()
    {
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        var style = ImGui.GetStyle();
        var spawnLabel = Loc.Get("Creature.Spawn");
        var height = ImGui.GetFrameHeight() * 1.2f;
        var spawnWidth = Buttons.PrimaryWidth(spawnLabel);

        if (Buttons.Quiet(Loc.Get("Gm.Cancel") + "##creature_cancel", 0f, height))
        {
            terrainPage = TerrainPage.List;
            return;
        }
        ImGui.SameLine();

        // Le monstre choisi (y compris copié sur la cible) s'épingle d'ici, sous le nom saisi.
        if (creaturePick != null && !string.IsNullOrWhiteSpace(creatureName))
        {
            DrawCreaturePin(creaturePick, creatureName.Trim());
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(Math.Max(ImGui.GetContentRegionAvail().X - spawnWidth - style.ItemSpacing.X,
            80f * ImGuiHelpers.GlobalScale));
        var submitted = ImGui.InputTextWithHint("##creature_name", Loc.Get("Creature.NameHint"), ref creatureName, 32,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();

        var full = npcManager!.Count >= NpcManager.MaxConcurrentNpcs;
        var ready = creaturePick != null && !string.IsNullOrWhiteSpace(creatureName) && !full;
        bool clicked;
        using (ImRaii.Disabled(!ready))
            clicked = Buttons.Primary(spawnLabel + "##spawn_creature", spawnWidth, height);
        if (full && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.Get("Terrain.NpcLimit"));

        if (ready && (clicked || submitted))
            SpawnCreature();
    }

    private void SpawnCreature()
    {
        var appearance = NpcAppearance.Default();
        appearance.Name = creatureName.Trim();
        appearance.Creature = creaturePick;

        if (TrySpawn(appearance) is not { } npc) return;

        // Un monstre arrive en adversaire ; le MJ change l'attitude depuis sa carte s'il le faut.
        npc.Rename(appearance.Name);
        npc.Attitude = Attitude.Hostile;
        npcManager!.NotifyChanged();
        RememberRecentCreature(creaturePick!, appearance.Name);

        creaturePick = null;
        creatureName = string.Empty;
        creatureQuery = string.Empty;
    }
}
