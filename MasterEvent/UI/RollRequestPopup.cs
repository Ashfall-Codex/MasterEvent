using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Demande de jet du MJ, au centre de l'écran : le joueur la voit même vue joueur fermée, et y
// répond sans la chercher. Elle disparaît dès qu'il lance ou ignore, ici ou dans sa vue joueur.
public sealed class RollRequestPopup(SessionManager session)
{
    public void Draw()
    {
        if (session.PendingRollRequest is not { } request) return;

        // Au centre : les barres de vie flottantes se dessinent sous les fenêtres, elles ne la masquent pas.
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Always, new Vector2(0.5f, 0.5f));

        MasterEventTheme.PushTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18f, 14f) * ImGuiHelpers.GlobalScale);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                                       | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse
                                       | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                                       | ImGuiWindowFlags.NoDocking;
        if (ImGui.Begin("##roll_request_popup", flags))
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.AccentText, FontAwesomeIcon.Dice.ToIconString());
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.TextStrong,
                string.Format(Loc.Get("RollRequest.Banner"), request.StatName, request.Threshold));

            ImGuiHelpers.ScaledDummy(6f);

            // Répondre est ce que le MJ attend : « Lancer » plein, « Ignorer » discret à côté.
            var rollLabel = Loc.Get("RollRequest.Roll");
            var dismissLabel = Loc.Get("RollRequest.Dismiss");
            var buttonsWidth = Buttons.PrimaryWidth(rollLabel) + ImGui.GetStyle().ItemSpacing.X
                               + Buttons.TextWidth(dismissLabel);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX()
                                + (ImGui.GetContentRegionAvail().X - buttonsWidth) / 2f);

            if (Buttons.Primary(rollLabel + "##roll_popup_answer", Buttons.PrimaryWidth(rollLabel)))
                session.AnswerRollRequest();
            ImGui.SameLine();
            if (Buttons.Quiet(dismissLabel + "##roll_popup_dismiss"))
                session.DismissRollRequest();
        }
        ImGui.End();
        ImGui.PopStyleVar();
        MasterEventTheme.PopTheme();
    }
}
