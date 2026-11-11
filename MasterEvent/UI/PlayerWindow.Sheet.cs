using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Carte « Ma fiche » : toujours à la même place, tout en lecture seule (le MJ tient les jauges),
// sauf les tuiles de stats, qui lancent le dé.
public sealed partial class PlayerWindow
{
    private const string SheetMenuId = "##player_sheet_more";
    private const string SheetPickId = "##player_sheet_pick";

    private string statQuery = string.Empty;

    // Mêmes teintes que le bandeau tactique pour le déplacement, et que l'historique pour le verdict.
    private static readonly Vector4 MovementColor = new(0.45f, 0.75f, 0.95f, 1f);
    private static readonly Vector4 MovementTrack = new(0.15f, 0.15f, 0.18f, 0.9f);
    private static readonly Vector4 VerdictSuccess = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 VerdictFailure = new(0.9f, 0.4f, 0.4f, 1f);

    private void DrawSheetCard()
    {
        var local = LocalPlayer;
        LayoutControls.BeginCard(local?.Name ?? Loc.Get("PlayerView.MySheet"), FontAwesomeIcon.User, null, () =>
        {
            if (Buttons.QuietIcon(FontAwesomeIcon.EllipsisH, "player_sheet_more", Loc.Get("Sheets.More")))
                ImGui.OpenPopup(SheetMenuId);
        }, Buttons.IconWidth(FontAwesomeIcon.EllipsisH));

        if (local == null)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.NoneLoaded"));
        }
        else
        {
            var myTurn = combatShown && session.IsLocalPlayerTurn;
            DrawSheetIdentity(myTurn);
            DrawSheetGauges(local);
            DrawTempModifierPill(local.TempModifier, local.TempModTurns);
            if (myTurn) DrawSheetMovement(local);
            DrawSheetStats(local);
            DrawSheetLastRoll();
        }

        LayoutControls.EndCard();
        DrawSheetMenus();
    }

    private void DrawSheetIdentity(bool myTurn)
    {
        DrawOwnPortrait();
        if (myTurn)
        {
            Pill.Item(Loc.Get("PlayerView.YourTurnPill"), MasterEventTheme.PendingColor);
            ImGui.SameLine();
        }

        ImGui.AlignTextToFramePadding();
        if (session.LoadedSheetName is { } sheetName)
        {
            var subtitle = string.Format(Loc.Get("PlayerView.SheetNameFmt"), sheetName);
            if (session.ActiveTemplate is { } template) subtitle += " · " + template.Name;
            ImGui.TextColored(MasterEventTheme.TextDim, TextFit.Fit(subtitle, LayoutControls.RemainingCardWidth));
            return;
        }

        ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.NoneLoaded"));
        ImGui.SameLine();
        if (Buttons.Tinted(Loc.Get("PlayerView.ChooseSheet") + "##player_choose_sheet"))
            ImGui.OpenPopup(SheetPickId);
    }

    // Portrait UmbraSync, carré d'une hauteur de cadre ; l'icône du joueur quand le plugin n'en fournit pas.
    private void DrawOwnPortrait()
    {
        var texture = UmbraPortraits?.Get(Plugin.ObjectTable.LocalPlayer?.EntityId ?? 0);
        var side = ImGui.GetFrameHeight();
        ActorRow.DrawAvatar(ImGui.GetWindowDrawList(), texture, ImGui.GetCursorScreenPos(), side,
            MasterEventTheme.PlayerColor);
        ImGui.Dummy(new Vector2(side, side));
        ImGui.SameLine();
    }

    private void DrawSheetGauges(PlayerData local)
    {
        var width = LayoutControls.CardContentWidth;
        HpBar.Draw(local.Hp, MasterEventTheme.PlayerColor, width, session.HpMode, local.HpMax,
            shield: session.ShowShield ? local.Shield : 0);
        if (session.ShowMpBar)
            HpBar.DrawMpBar(local.Mp, width, session.MpMode, local.MpMax);
        if (local.Counters == null) return;
        foreach (var counter in local.Counters)
            CounterBar.Draw(counter, width);
    }

    // Partagée avec le détail d'un marqueur.
    private static void DrawTempModifierPill(int modifier, int rounds)
    {
        if (modifier == 0) return;

        var key = modifier > 0 ? "PlayerView.TempBonusFmt" : "PlayerView.TempMalusFmt";
        var text = string.Format(Loc.Get(key), StatDisplay.Signed(modifier));
        // Le bonus se décompte par manche : NextRound le fait baisser.
        if (rounds > 0)
        {
            text += " · " + (rounds == 1
                ? Loc.Get("PlayerView.TempRoundsLeftOne")
                : string.Format(Loc.Get("PlayerView.TempRoundsLeftFmt"), rounds));
        }
        Pill.Item(text, MasterEventTheme.TempModifierColor);
    }

    // Quota du tour en cours, le même que le bandeau tactique : en avertissement sous un quart, en danger à zéro.
    private static void DrawSheetMovement(PlayerData local)
    {
        if (local.MoveMax <= 0f) return;

        var ratio = Math.Clamp(local.MoveLeft / local.MoveMax, 0f, 1f);
        var color = ratio <= 0f ? MasterEventTheme.DangerColor
            : ratio <= 0.25f ? MasterEventTheme.WarningColor
            : MovementColor;

        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("PlayerView.Movement"));
        ImGui.SameLine();
        ImGui.TextColored(color, string.Format(Loc.Get("Tactical.MovementValue"), local.MoveLeft, local.MoveMax));

        var width = LayoutControls.CardContentWidth;
        var height = 4f * ImGuiHelpers.GlobalScale;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.GetColorU32(MovementTrack), height / 2f);
        if (ratio > 0f)
            dl.AddRectFilled(pos, pos + new Vector2(width * ratio, height), ImGui.GetColorU32(color), height / 2f);
        ImGui.Dummy(new Vector2(width, height));
    }

    private void DrawSheetStats(PlayerData local)
    {
        if (local.Stats is not { Count: > 0 } stats)
        {
            ImGui.TextColored(MasterEventTheme.TextDim,
                Loc.Get(session.ActiveTemplate == null ? "Player.NoTemplate" : "Player.NoStats"));
            return;
        }

        // En mode cible, le seuil du MJ remplace la valeur de la stat : la surligner tromperait.
        var request = IsInRoom(viewState) ? session.PendingRollRequest : null;
        var targetMode = session.ActiveTemplate is { StatResolution: StatResolution.Target };
        var highlighted = request != null && !targetMode ? request.StatId : null;
        var highlightTooltip = request != null
            ? string.Format(Loc.Get("PlayerView.RequestedTileFmt"), request.Threshold)
            : null;

        ImGui.PushID("player_stats");
        if (stats.Count > VitalsControls.StatFilterThreshold)
        {
            SearchField.Draw("stat_search", ref statQuery, Loc.Get("Models.StatsFilter"), LayoutControls.CardContentWidth);
            ImGuiHelpers.ScaledDummy(2f);
        }
        else
        {
            // Modèle ou fiche plus court : sans champ pour l'effacer, une saisie restante masquerait des tuiles.
            statQuery = string.Empty;
        }

        // La stat que le MJ attend reste visible, quoi qu'on cherche.
        var shown = stats.Where(s => s.Id == request?.StatId || VitalsControls.Matches(s, statQuery)).ToList();
        if (shown.Count == 0)
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.NoStatMatch"));
        else
            StatGrid.Draw(shown, LayoutControls.CardContentWidth,
                stat => RollOnce(() => session.RollDiceForPlayer(session.LocalPlayerHash, stat.Id)),
                highlighted, highlightTooltip, local.TempModifier);
        ImGui.PopID();
    }

    // Posé à la fin de l'animation du dé, comme la ligne de chat : jamais sous le dé qui roule encore.
    private void DrawSheetLastRoll()
    {
        if (session.LastOwnRoll is not { } roll) return;

        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("PlayerView.LastRoll"));
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.TextStrong, roll.Total.ToString());

        var parts = new List<string>();
        if (roll.StatName != null)
        {
            var statValue = roll.StatModifier ?? roll.Modifier - (roll.TempModifier ?? 0);
            parts.Add($"{roll.StatName} {StatDisplay.Stat(statValue)}");
        }
        if (roll.TempModifier is { } temp)
            parts.Add(string.Format(Loc.Get("PlayerView.BonusFmt"), StatDisplay.Signed(temp)));

        var detail = $"{roll.RawRoll} / {roll.DiceMax}";
        if (parts.Count > 0) detail += " · " + string.Join(", ", parts);
        SameLineIfFits(detail);
        ImGui.TextColored(MasterEventTheme.MutedTextColor, detail);

        if (roll is not { Target: { } target, Success: { } success }) return;

        var verdict = string.Format(Loc.Get(success ? "Dice.Success" : "Dice.Failure"), target);
        SameLineIfFits(verdict);
        ImGui.TextColored(success ? VerdictSuccess : VerdictFailure, verdict);
    }

    private static void SameLineIfFits(string text)
    {
        ImGui.SameLine();
        if (ImGui.CalcTextSize(text).X > LayoutControls.RemainingCardWidth)
            ImGui.NewLine();
    }

    private void DrawSheetMenus()
    {
        if (ImGui.BeginPopup(SheetMenuId))
        {
            if (ImGui.BeginMenu(Loc.Get("PlayerView.ChangeSheet")))
            {
                DrawSheetChoices();
                ImGui.EndMenu();
            }
            if (openSheets != null && ImGui.Selectable(Loc.Get("PlayerView.ManageSheets")))
                openSheets();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup(SheetPickId))
        {
            if (!DrawSheetChoices() && openSheets != null && ImGui.Selectable(Loc.Get("PlayerView.ManageSheets")))
                openSheets();
            ImGui.EndPopup();
        }
    }

    // Les fiches du modèle actif seulement, comme l'ancien choix de la vue. Vrai s'il y en a au moins une.
    private bool DrawSheetChoices()
    {
        var template = session.ActiveTemplate?.Name;
        var current = session.LoadedSheetName ?? selectedSheetName;
        var any = false;

        foreach (var name in session.GetPlayerSheetNames())
        {
            var sheet = session.LoadPlayerSheet(name);
            if (sheet == null || (template != null && sheet.TemplateName != template)) continue;

            any = true;
            if (ImGui.Selectable(name, name == current))
            {
                selectedSheetName = name;
                session.ApplyPlayerSheet(sheet);
            }
        }

        if (!any)
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Player.NoProfiles"));

        // ApplyPlayerSheet réécrit aussi PV et PE : mieux vaut le savoir avant de cliquer en plein combat.
        ImGui.Separator();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetFontSize() * 18f);
        ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("PlayerView.ChangeSheetHint"));
        ImGui.PopTextWrapPos();
        return any;
    }
}
