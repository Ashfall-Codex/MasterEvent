using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Communication;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
    private int CountPlacedMarkers()
    {
        var count = 0;
        for (var i = 0; i < Constants.WaymarkCount; i++)
            if (session.CurrentMarkers.Markers[i].HasData) count++;
        return count;
    }

    /// <summary>
    /// Jet libre du MJ, pour une réaction de figurant ou un test hors décor. Sans lui, le
    /// seul moyen de lancer un dé côté MJ était de passer par un marqueur, ou d'ouvrir la
    /// fenêtre joueur. Les tuiles sont celles du modèle actif, avec leur valeur par défaut.
    /// </summary>
    private void DrawGmFreeRoll()
    {
        ImGui.TextColored(MasterEventTheme.AccentText, Loc.Get("Dice.GmFreeRoll"));
        ImGuiHelpers.ScaledDummy(2f);

        var rollerName = Loc.Get("Gm.Title");
        var availWidth = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        const int columns = 3;
        var tileSize = (availWidth - spacing * (columns - 1)) / columns;
        var tileH = tileSize * 0.62f;

        DiceControls.DrawDiceTile(Loc.Get("Dice.NoStat"), null, "gm_roll_simple", tileSize, tileH,
            () => session.RollDiceRaw(rollerName, null, 0));

        var definitions = session.ActiveTemplate?.StatDefinitions;
        if (definitions == null) return;

        var idx = 1;
        foreach (var definition in definitions)
        {
            if (idx % columns != 0)
                ImGui.SameLine();

            // RollDiceRaw attend des valeurs, pas des définitions : la valeur par défaut
            // du modèle sert de modificateur, ce qui est le comportement voulu pour un figurant.
            var stats = new List<StatValue>
            {
                new() { Id = definition.Id, Name = definition.Name, Modifier = definition.DefaultValue },
            };
            var statId = definition.Id;
            var modStr = StatDisplay.Stat(definition.DefaultValue);

            DiceControls.DrawDiceTile(definition.Name, modStr, "gm_roll_" + statId, tileSize, tileH,
                () => session.RollDiceRaw(rollerName, stats, 0, statId));
            idx++;
        }
    }
}
