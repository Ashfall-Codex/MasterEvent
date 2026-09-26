using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.UI.Components;

public sealed class PlayerToggleButton(Configuration configuration)
{
    private const float DefaultMarginX = 24f;
    private const float ButtonSize = 30f;

    private bool dragging;
    private Vector2 position;
    private string? lastHiddenReason = "initialisation";

    // Ouverture du menu principal. Une action plutôt qu'une référence à la fenêtre : le
    // chemin d'ouverture vérifie le consentement RGPD, rafraîchit le rôle et retente la
    // connexion au relais, ce qu'un simple IsOpen contournerait.
    public Action? OnToggleMainWindow { get; set; }

    public PlayerWindow? PlayerWindowRef { get; set; }
    public NotesWindow? NotesWindowRef { get; set; }

    public Func<bool>? IsInSession { get; set; }

    private string? HiddenReason()
    {
        if (!configuration.ShowPlayerToggleButton) return "option désactivée";
        if (PlayerWindowRef == null) return "fenêtre joueur non initialisée";
        if (IsInSession?.Invoke() != true && !configuration.DebugMode) return "hors groupe et hors alliance";

        return null;
    }

    public void Draw()
    {
        var reason = HiddenReason();

        if (reason != lastHiddenReason)
        {
            lastHiddenReason = reason;
            Plugin.Log.Debug(reason == null
                ? "[MasterEvent] Barre flottante : affichée."
                : $"[MasterEvent] Barre flottante masquée — {reason}.");
        }

        if (reason != null) return;
        var playerWindow = PlayerWindowRef!;

        var viewport = ImGui.GetMainViewport();

        if (!dragging)
        {
            position = configuration.PlayerToggleButtonX < 0f || configuration.PlayerToggleButtonY < 0f
                ? new Vector2(
                    viewport.WorkPos.X + DefaultMarginX * ImGuiHelpers.GlobalScale,
                    viewport.WorkPos.Y + viewport.WorkSize.Y * 0.5f)
                : new Vector2(configuration.PlayerToggleButtonX, configuration.PlayerToggleButtonY);
        }

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.55f);


        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoMove;

        if (ImGui.Begin("##MasterEventFloatingBar", flags))
        {
            var playerOpen = playerWindow.IsOpen;

            // Les boutons sont d'abord rassemblés, puis placés : la disposition décide seule
            // de qui reste sur la ligne du précédent, sans la répéter à chaque bouton.
            var buttons = new List<(string Id, FontAwesomeIcon Icon, string Tooltip, Action OnClick)>
            {
                ("##me_player_toggle",
                    playerOpen ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                    Loc.Get(playerOpen ? "Player.ToggleHide" : "Player.ToggleShow"),
                    () => playerWindow.IsOpen = !playerOpen),
            };

            if (NotesWindowRef is { } notesWindow)
            {
                var notesOpen = notesWindow.IsOpen;
                buttons.Add(("##me_notes_toggle",
                    FontAwesomeIcon.StickyNote,
                    Loc.Get(notesOpen ? "Notes.ToggleHide" : "Notes.ToggleShow"),
                    () => notesWindow.IsOpen = !notesOpen));
            }

            // Raccourci vers l'onglet de jet de la vue joueur : lancer un dé sans passer
            // par la fenêtre puis sa barre latérale.
            buttons.Add(("##me_dice_toggle",
                FontAwesomeIcon.Dice,
                Loc.Get("Player.RollDice"),
                playerWindow.ToggleDiceView));

            if (OnToggleMainWindow is { } toggleMain)
            {
                buttons.Add(("##me_main_toggle",
                    FontAwesomeIcon.Bars,
                    Loc.Get("Player.ToggleMainWindow"),
                    toggleMain));
            }

            for (var i = 0; i < buttons.Count; i++)
            {
                var sameLine = configuration.PlayerToggleLayout switch
                {
                    ToggleButtonLayout.Horizontal => i > 0,
                    // Deux colonnes : un bouton sur deux prolonge la ligne précédente.
                    ToggleButtonLayout.Grid => i % 2 == 1,
                    _ => false,
                };
                if (sameLine) ImGui.SameLine();

                var (id, icon, tooltip, onClick) = buttons[i];
                if (DrawDraggableToggle(id, icon, tooltip))
                    onClick();
            }

            if (!dragging) ClampIntoViewport();
        }
        ImGui.End();
    }

    private bool DrawDraggableToggle(string id, FontAwesomeIcon icon, string tooltip)
    {
        var size = ButtonSize * ImGuiHelpers.GlobalScale;

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.Button(icon.ToIconString() + id, new Vector2(size, size));

        if (ImGui.IsItemHovered() && !dragging)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(tooltip);
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Player.ToggleDragHint"));
            ImGui.EndTooltip();
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            dragging = true;
            position += ImGui.GetIO().MouseDelta;
            // Appliqué aussi tout de suite, sinon le déplacement accuserait une frame de retard.
            ImGui.SetWindowPos(position);
            return false;
        }

        if (!ImGui.IsItemDeactivated()) return false;

        if (!dragging) return true;

        CommitPosition();
        dragging = false;
        return false;
    }

    private void CommitPosition()
    {
        ClampIntoViewport(force: true);
    }


    private void ClampIntoViewport(bool force = false)
    {
        var viewport = ImGui.GetMainViewport();
        var windowSize = ImGui.GetWindowSize();

        var clamped = new Vector2(
            Math.Clamp(position.X, viewport.WorkPos.X,
                MathF.Max(viewport.WorkPos.X, viewport.WorkPos.X + viewport.WorkSize.X - windowSize.X)),
            Math.Clamp(position.Y, viewport.WorkPos.Y,
                MathF.Max(viewport.WorkPos.Y, viewport.WorkPos.Y + viewport.WorkSize.Y - windowSize.Y)));

        if (!force && clamped == position) return;

        position = clamped;
        configuration.PlayerToggleButtonX = position.X;
        configuration.PlayerToggleButtonY = position.Y;
        configuration.Save();
    }
}
