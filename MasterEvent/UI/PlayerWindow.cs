using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
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

        var state = session.CurrentTurnState;
        var turnsActive = state is { IsActive: true } && state.Entries.Count > 0;

        // Show local player's HP card only when turn tracking is NOT active
        // (when active, the player is drawn inline in the initiative order)
        if (!turnsActive)
        {
            DrawLocalPlayerCard();
        }

        if (turnsActive)
        {
            DrawCombinedTurnView(state!);
        }
        else
        {
            DrawPlainMarkerList();
        }
    }

    private void DrawLocalPlayerCard()
    {
        var localHash = Plugin.GeneratePlayerHash(playerState.ContentId);
        var localPlayer = session.PartyMembers.FirstOrDefault(p => p.Hash == localHash);
        if (localPlayer == null) return;

        var cardWidth = ImGui.GetContentRegionAvail().X;
        var extraRows = 0;
        if (session.ShowMpBar) extraRows++;
        if (localPlayer.Counters != null) extraRows += localPlayer.Counters.Count;
        var cardHeight = ImGui.GetFrameHeightWithSpacing() * (2 + extraRows) + ImGui.GetStyle().WindowPadding.Y * 2;

        var playerBlue = new Vector4(0.227f, 0.604f, 1f, 0.8f);
        ImGui.PushStyleColor(ImGuiCol.Border, playerBlue);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);

        if (ImGui.BeginChild("##player_hp_card", new Vector2(cardWidth, cardHeight), true))
        {
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

            HpBar.Draw(localPlayer.Hp, Attitude.Neutral, ImGui.GetContentRegionAvail().X,
                session.HpMode, hpMax: localPlayer.HpMax,
                shield: session.ShowShield ? localPlayer.Shield : 0);

            if (session.ShowMpBar)
                HpBar.DrawMpBar(localPlayer.Mp, ImGui.GetContentRegionAvail().X, session.MpMode, localPlayer.MpMax);

            if (localPlayer.Counters != null)
            {
                foreach (var counter in localPlayer.Counters)
                    CounterBar.Draw(counter, ImGui.GetContentRegionAvail().X);
            }
        }
        ImGui.EndChild();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();

        ImGuiHelpers.ScaledDummy(4f);
    }

    private void DrawCombinedTurnView(TurnState state)
    {
        var roundText = string.Format(Loc.Get("Turns.Round"), state.Round);
        ImGui.TextColored(MasterEventTheme.AccentColor, roundText);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"(d{state.DiceMax})");

        var actedCount = state.Entries.Count(e => e.HasActed);
        var progressText = string.Format(Loc.Get("Turns.Progress"), actedCount, state.Entries.Count);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), progressText);

        ImGuiHelpers.ScaledDummy(4f);

        var nextIndex = -1;
        for (var j = 0; j < state.Entries.Count; j++)
        {
            if (!state.Entries[j].HasActed) { nextIndex = j; break; }
        }

        for (var i = 0; i < state.Entries.Count; i++)
        {
            var entry = state.Entries[i];
            var isNext = i == nextIndex;
            var indicator = GetTurnIndicator(entry, isNext);

            if (entry.HasActed)
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.45f);

            if (entry.IsMarker && entry.WaymarkIndex.HasValue)
            {
                var waymarkId = (WaymarkId)entry.WaymarkIndex.Value;
                var marker = session.CurrentMarkers[waymarkId];

                if (!marker.IsVisible || string.IsNullOrEmpty(marker.Name))
                {
                    DrawTurnLine(entry, isNext);
                }
                else
                {
                    MarkerCard.DrawReadOnly(waymarkId, marker, session.HpMode,
                        session.ShowShield, session.ShowMpBar, session.MpMode,
                        turnIndicator: indicator, initiative: entry.Initiative);
                }
            }
            else
            {
                DrawPlayerTurnCard(i, entry, isNext);
            }

            if (entry.HasActed)
                ImGui.PopStyleVar();

            ImGui.Spacing();
        }

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

    private static string? GetTurnIndicator(TurnEntry entry, bool isNext)
    {
        if (entry.HasActed) return "\u2713"; // ✓
        if (isNext) return ">";
        return null;
    }

    private void DrawPlayerTurnCard(int index, TurnEntry entry, bool isNext)
    {
        var indicator = GetTurnIndicator(entry, isNext);
        var player = session.PartyMembers.FirstOrDefault(p => p.Hash == entry.PlayerHash);
        var hp = player?.Hp ?? 100;

        var playerBlue = new Vector4(0.227f, 0.604f, 1f, 0.8f);
        ImGui.PushStyleColor(ImGuiCol.Border, playerBlue);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);

        var cardWidth = ImGui.GetContentRegionAvail().X;
        var extraRows = 0;
        if (session.ShowMpBar) extraRows++;
        if (player?.Counters != null) extraRows += player.Counters.Count;
        var cardHeight = ImGui.GetFrameHeightWithSpacing() * (2 + extraRows) + ImGui.GetStyle().WindowPadding.Y * 2;
        if (ImGui.BeginChild($"##pturn_{index}", new Vector2(cardWidth, cardHeight), true))
        {
            // Turn indicator
            if (indicator != null)
            {
                var indicatorColor = indicator == ">"
                    ? MasterEventTheme.AccentColor
                    : new Vector4(0.4f, 0.4f, 0.4f, 1f);
                ImGui.TextColored(indicatorColor, indicator);
                ImGui.SameLine();
            }

            // User icon
            var userIcon = FontAwesomeIcon.User.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                ImGui.TextColored(playerBlue, userIcon);
            }
            ImGui.SameLine();

            // Center name
            var nameWidth = ImGui.CalcTextSize(entry.Name).X;
            var nameX = (cardWidth - nameWidth) / 2f;
            var minX = ImGui.GetCursorPosX();
            if (nameX > minX)
                ImGui.SetCursorPosX(nameX);
            ImGui.TextUnformatted(entry.Name);

            // Initiative on the right
            var initText = $"[{entry.Initiative}]";
            var initW = ImGui.CalcTextSize(initText).X;
            ImGui.SameLine(cardWidth - initW - ImGui.GetStyle().WindowPadding.X);
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), initText);

            // HP bar
            HpBar.Draw(hp, Attitude.Neutral, ImGui.GetContentRegionAvail().X, session.HpMode,
                hpMax: player?.HpMax ?? 100,
                shield: session.ShowShield ? (player?.Shield ?? 0) : 0);

            // EP bar
            if (session.ShowMpBar)
                HpBar.DrawMpBar(player?.Mp ?? 100, ImGui.GetContentRegionAvail().X, session.MpMode, player?.MpMax ?? 100);

            // Counters
            if (player?.Counters != null)
            {
                foreach (var counter in player.Counters)
                    CounterBar.Draw(counter, ImGui.GetContentRegionAvail().X);
            }
        }
        ImGui.EndChild();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private void DrawTurnLine(TurnEntry entry, bool isNext)
    {
        var indicator = GetTurnIndicator(entry, isNext);
        if (indicator != null)
        {
            var indicatorColor = entry.HasActed
                ? new Vector4(0.5f, 0.5f, 0.5f, 1f)
                : MasterEventTheme.AccentColor;
            ImGui.TextColored(indicatorColor, indicator);
        }
        else
        {
            ImGui.TextUnformatted("  ");
        }
        ImGui.SameLine();

        var iconSize = ImGui.GetFrameHeight();
        if (entry.IsMarker && entry.WaymarkIndex.HasValue)
        {
            var waymarkId = (WaymarkId)entry.WaymarkIndex.Value;
            var iconId = waymarkId.ToIconId();
            var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));
        }
        else
        {
            var userIcon = FontAwesomeIcon.User.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                ImGui.TextColored(new Vector4(0.227f, 0.604f, 1f, 0.8f), userIcon);
            }
        }
        ImGui.SameLine();

        Vector4 nameColor;
        if (entry.HasActed)
            nameColor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
        else if (isNext)
            nameColor = MasterEventTheme.AccentColor;
        else
            nameColor = new Vector4(1f, 1f, 1f, 1f);
        ImGui.TextColored(nameColor, entry.Name);

        ImGui.SameLine();
        var initText = $"[{entry.Initiative}]";
        var initWidth = ImGui.CalcTextSize(initText).X;
        var initPos = ImGui.GetContentRegionMax().X - initWidth;
        if (initPos > ImGui.GetCursorPosX())
            ImGui.SameLine(initPos);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), initText);
    }

    private void DrawPlainMarkerList()
    {
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
            if (!session.IsConnected)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), Loc.Get("Player.Disconnected"));
            }
            else
            {
                ImGui.TextColored(MasterEventTheme.AttitudeNeutral, Loc.Get("Player.Waiting"));
            }
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
