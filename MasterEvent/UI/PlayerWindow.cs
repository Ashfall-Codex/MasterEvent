using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

public sealed class PlayerWindow : MasterEventWindowBase
{
    private readonly SessionManager session;
    private readonly IPlayerState playerState;

    public PlayerWindow(SessionManager session, IPlayerState playerState)
        : base("MasterEvent###MasterEventPlayer", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.session = session;
        this.playerState = playerState;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 0),
        };
    }

    protected override void DrawContents()
    {
        var fontSize = ImGui.GetFontSize() * 0.85f;
        var text = Loc.Get("Player.Title");
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        dl.AddText(ImGui.GetFont(), fontSize, pos, ImGui.GetColorU32(MasterEventTheme.AccentColor), text);
        ImGui.Dummy(new Vector2(0, fontSize + 2f * ImGuiHelpers.GlobalScale));

        // Show local player's HP in a styled card
        var localHash = Plugin.GeneratePlayerHash(playerState.ContentId);
        var localPlayer = session.PartyMembers.FirstOrDefault(p => p.Hash == localHash);
        if (localPlayer != null)
        {
            var cardWidth = ImGui.GetContentRegionAvail().X;
            var cardHeight = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetStyle().WindowPadding.Y * 2;

            var playerBlue = new Vector4(0.227f, 0.604f, 1f, 0.8f); // #3A9AFF
            ImGui.PushStyleColor(ImGuiCol.Border, playerBlue);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);

            if (ImGui.BeginChild("##player_hp_card", new Vector2(cardWidth, cardHeight), true))
            {
                // User icon + name centered
                var userIcon = FontAwesomeIcon.User.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    ImGui.TextColored(playerBlue, userIcon);
                }
                ImGui.SameLine();

                var nameWidth = ImGui.CalcTextSize(localPlayer.Name).X;
                var nameX = (cardWidth - nameWidth) / 2f;
                var minX = ImGui.GetCursorPosX();
                if (nameX > minX)
                    ImGui.SetCursorPosX(nameX);
                ImGui.TextUnformatted(localPlayer.Name);

                // HP bar
                HpBar.Draw(localPlayer.Hp, Attitude.Neutral, ImGui.GetContentRegionAvail().X,
                    session.HpMode);
            }
            ImGui.EndChild();

            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();

            ImGuiHelpers.ScaledDummy(4f);
        }

        var hasVisibleMarkers = false;
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var waymarkId = (WaymarkId)i;
            var marker = session.CurrentMarkers[waymarkId];

            if (!marker.IsVisible || string.IsNullOrEmpty(marker.Name))
                continue;

            hasVisibleMarkers = true;
            MarkerCard.DrawReadOnly(waymarkId, marker, session.HpMode,
                session.ShowShield, session.ShowMpBar, session.MpMode);
            ImGui.Spacing();
        }

        if (!hasVisibleMarkers)
        {
            ImGui.Separator();
            ImGui.TextColored(MasterEventTheme.AttitudeNeutral, Loc.Get("Player.Waiting"));
        }
        else
        {
            ImGuiHelpers.ScaledDummy(4f);
            var btnWidth = ImGui.GetContentRegionAvail().X;
            if (ImGui.Button(Loc.Get("Player.ApplyMarkers"), new Vector2(btnWidth, 0)))
            {
                var placed = session.ApplyWaymarks();
                if (placed > 0)
                    Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.WaymarksApplied"), placed));
                else
                    Plugin.ChatGui.Print(Loc.Get("Chat.WaymarksApplyFailed"));
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Player.ApplyMarkersTooltip"));
                ImGui.EndTooltip();
            }
        }
    }
}
