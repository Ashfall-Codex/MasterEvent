using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.UI.Components;
public static class VitalsControls
{
    // Au-delà de ce nombre de stats, un champ de filtre apparaît : en dessous, la liste
    // tient à l'écran et le filtre ne ferait qu'encombrer.
    public const int StatFilterThreshold = 5;

    // Filtre commun à tous les éditeurs ouverts. Deux marqueurs le partageaient déjà ;
    // marqueurs et PNJ le partagent désormais, ce qui reste le comportement attendu
    // puisqu'un seul popup est ouvert à la fois.
    private static string statsFilter = string.Empty;
    public static void DrawStatsEditor(IVitalEntity entity, string id)
    {
        entity.Stats ??= [];

        if (entity.Stats.Count > StatFilterThreshold)
        {
            ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint($"##stats_filter_{id}", Loc.Get("Models.StatsFilter"), ref statsFilter, 64);
            ImGuiHelpers.ScaledDummy(2f);
        }

        for (var i = 0; i < entity.Stats.Count; i++)
        {
            var stat = entity.Stats[i];
            if (!Matches(stat, statsFilter)) continue;

            ImGui.TextUnformatted(stat.Name);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60f * ImGuiHelpers.GlobalScale);
            var modifier = stat.Modifier;
            if (ImGui.InputInt($"##stat_{id}_{i}", ref modifier))
                stat.Modifier = modifier;
        }
    }

    /// Stats retenues par un filtre saisi, dans l'ordre de la fiche.
    public static IEnumerable<StatValue> Filtered(IEnumerable<StatValue> stats, string filter)
        => stats.Where(s => Matches(s, filter));

    private static bool Matches(StatValue stat, string filter)
        => string.IsNullOrEmpty(filter) || stat.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
