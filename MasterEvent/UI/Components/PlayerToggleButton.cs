using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Services;

namespace MasterEvent.UI.Components;

public sealed class PlayerToggleButton(SessionManager session, Configuration configuration)
{

    private const float DefaultMarginX = 24f;

    private bool dragging;
    private Vector2 position;

    public PlayerWindow? PlayerWindowRef { get; set; }

    public Func<bool>? IsInSession { get; set; }

    public void Draw()
    {
        if (!configuration.ShowPlayerToggleButton) return;
        if (PlayerWindowRef is not { } playerWindow) return;

        if (session.IsGm) return;
        if (IsInSession?.Invoke() != true) return;

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

        // NoMove : le déplacement est géré à la main depuis le bouton lui-même (voir plus bas),
        // sinon il faudrait viser le liseré autour du bouton pour l'attraper.
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoMove;

        if (ImGui.Begin("##MasterEventPlayerToggle", flags))
        {
            var isOpen = playerWindow.IsOpen;
            var icon = (isOpen ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash).ToIconString();
            var size = 30f * ImGuiHelpers.GlobalScale;

            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.Button(icon + "##me_player_toggle", new Vector2(size, size));

            // Le bouton fait aussi office de poignée : cliquer-glisser déplace, clic simple
            // bascule. La valeur de retour de Button() est ignorée, car elle se déclenche aussi
            // au relâchement d'un glisser.
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                dragging = true;
                position += ImGui.GetIO().MouseDelta;
                // Appliqué aussi tout de suite, sinon le déplacement accuserait une frame de
                // retard sur la souris.
                ImGui.SetWindowPos(position);
            }

            if (ImGui.IsItemDeactivated())
            {
                if (dragging)
                {
                    // Bornage sur la taille réelle de la fenêtre, marges comprises : un bouton
                    // relâché hors du viewport deviendrait irrécupérable à la souris.
                    var windowSize = ImGui.GetWindowSize();
                    position = new Vector2(
                        Math.Clamp(position.X, viewport.WorkPos.X,
                            viewport.WorkPos.X + viewport.WorkSize.X - windowSize.X),
                        Math.Clamp(position.Y, viewport.WorkPos.Y,
                            viewport.WorkPos.Y + viewport.WorkSize.Y - windowSize.Y));

                    configuration.PlayerToggleButtonX = position.X;
                    configuration.PlayerToggleButtonY = position.Y;
                    configuration.Save();
                }
                else
                {
                    playerWindow.IsOpen = !isOpen;
                }

                dragging = false;
            }

            if (ImGui.IsItemHovered() && !dragging)
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get(isOpen ? "Player.ToggleHide" : "Player.ToggleShow"));
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), Loc.Get("Player.ToggleDragHint"));
                ImGui.EndTooltip();
            }
        }
        ImGui.End();
    }
}
