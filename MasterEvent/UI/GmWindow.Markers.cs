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

    // Cartes des marqueurs posés, dans l'ordre des balises.
    private void DrawMarkerCards()
    {
        var first = true;
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var waymarkId = (WaymarkId)i;
            var marker = session.CurrentMarkers[waymarkId];

            if (!marker.HasData)
                continue;

            if (!first)
                ImGui.Spacing();

            first = false;
            MarkerCard.DrawEdit(waymarkId, marker,
                // La consigne s'affiche dans la vue, là où le MJ regarde, plutôt que dans le chat.
                onPlace: () => ShowTerrainHint(string.Format(Loc.Get("Terrain.PlaceWaymarkFmt"), waymarkId.ToLabel())),
                onClear: () => session.ClearMarker(waymarkId),
                onMove: () => session.MoveMarker(waymarkId),
                onRoll: statId => session.RollDiceWithStat(waymarkId, statId),
                hpMode: session.HpMode,
                showShield: session.ShowShield,
                showMpBar: session.ShowMpBar,
                mpMode: session.MpMode);
        }
    }

    // En-tête commun des vues : l'état de la séance en sous-titre, et à droite l'icône d'état,
    // l'historique des jets et le renvoi aux joueurs.
    private void DrawHeader()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var (statusIcon, _, _) = LinkStatus.Describe(session.LinkState, session.ConnectedPlayerCount);
        float statusWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            statusWidth = ImGui.CalcTextSize(statusIcon.ToIconString()).X;
        var trailingWidth = statusWidth + spacing
                            + Buttons.IconWidth(FontAwesomeIcon.Dice) + spacing
                            + Buttons.IconWidth(FontAwesomeIcon.PaperPlane);

        PageHeader.Draw(FontAwesomeIcon.MapMarkerAlt, Loc.Get("Terrain.Title"), TerrainSubtitle(),
            DrawHeaderActions, trailingWidth);
    }

    // Formulation courte de l'état, la phrase complète de LinkStatus passant en infobulle.
    private string TerrainSubtitle()
    {
        var status = session.LinkState switch
        {
            RelayLinkState.InSession => session.ConnectedPlayerCount == 1
                ? Loc.Get("Link.InSessionOne")
                : string.Format(Loc.Get("Link.InSession"), session.ConnectedPlayerCount),
            RelayLinkState.ServerOnly => Loc.Get("Gm.LinkServer"),
            RelayLinkState.Reconnecting => Loc.Get("Gm.LinkReconnecting"),
            _ => Loc.Get("Gm.Local"),
        };

        return session.ActiveTemplate is { } template
            ? $"{status} · {Loc.Get("Player.CurrentTemplate")} {template.Name}"
            : status;
    }

    private void DrawHeaderActions()
    {
        // L'icône répète l'état en couleur ; la couleur seule ne suffirait pas, le sous-titre le dit.
        var (statusIcon, statusColor, statusText) = LinkStatus.Describe(session.LinkState, session.ConnectedPlayerCount);
        ImGui.AlignTextToFramePadding();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(statusColor, statusIcon.ToIconString());
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
            ImGui.TextUnformatted(statusText);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.SameLine();

        if (Buttons.QuietIcon(FontAwesomeIcon.Dice, "roll_history", Loc.Get("Dice.Title")))
            ImGui.OpenPopup("##roll_history_popup");

        ImGui.SetNextWindowSize(new Vector2(320f * ImGuiHelpers.GlobalScale, 0));
        if (ImGui.BeginPopup("##roll_history_popup"))
        {
            DrawGmFreeRoll();
            DiceControls.DrawRollHistory(session, maxEntries: 20, showClearButton: true);
            ImGui.EndPopup();
        }
        ImGui.SameLine();

        if (Buttons.QuietIcon(FontAwesomeIcon.PaperPlane, "resend_markers", Loc.Get("Gm.UpdateTooltip")))
        {
            session.SyncWaymarks();
            session.BroadcastUpdate();
        }
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
