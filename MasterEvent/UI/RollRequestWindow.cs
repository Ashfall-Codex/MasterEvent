using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;

namespace MasterEvent.UI;

public sealed class RollRequestWindow : MasterEventWindowBase
{
    private readonly SessionManager session;

    private string? playerHash;
    private string playerName = string.Empty;
    private string? selectedStatId;
    private int threshold;
    private bool thresholdInitialized;

    public RollRequestWindow(SessionManager session)
        : base($"{Loc.Get("RollRequest.WindowTitle")}###MasterEventRollRequest",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.session = session;
    }

    public void Open(PlayerData player)
    {
        if (playerHash != player.Hash)
        {
            playerHash = player.Hash;
            selectedStatId = player.Stats?.FirstOrDefault()?.Id;
        }

        playerName = player.Name;
        if (!thresholdInitialized)
        {
            threshold = System.Math.Max(1, session.DiceMax / 2);
            thresholdInitialized = true;
        }

        if (IsTargetMode) PrefillFromStat(player);

        IsOpen = true;
        BringToFront();
    }

    private bool IsTargetMode => session.ActiveTemplate is { StatResolution: StatResolution.Target };
    private void PrefillFromStat(PlayerData player)
    {
        var stat = player.Stats?.FirstOrDefault(s => s.Id == selectedStatId);
        if (stat != null) threshold = stat.Modifier + player.TempModifier;
    }

    protected override void DrawContents()
    {
        var player = session.PartyMembers.FirstOrDefault(p => p.Hash == playerHash);
        if (player == null)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheet.PlayerGone"));
            return;
        }

        ImGui.TextColored(MasterEventTheme.AccentColor, string.Format(Loc.Get("RollRequest.For"), playerName));
        ImGuiHelpers.ScaledDummy(4f);

        var fieldWidth = 220f * ImGuiHelpers.GlobalScale;
        var stats = player.Stats ?? [];
        var selected = stats.FirstOrDefault(s => s.Id == selectedStatId);

        ImGui.TextUnformatted(Loc.Get("RollRequest.Stat"));
        ImGui.SetNextItemWidth(fieldWidth);
        if (ImGui.BeginCombo("##roll_request_stat", selected?.Name ?? Loc.Get("Dice.NoStat")))
        {
            if (ImGui.Selectable(Loc.Get("Dice.NoStat"), selected == null))
                selectedStatId = null;

            foreach (var stat in stats)
            {
                var modStr = stat.Modifier >= 0 ? $"+{stat.Modifier}" : stat.Modifier.ToString();
                if (ImGui.Selectable($"{stat.Name} ({modStr})##{stat.Id}", stat.Id == selectedStatId))
                {
                    selectedStatId = stat.Id;
                    if (IsTargetMode) PrefillFromStat(player);
                }
            }

            ImGui.EndCombo();
        }

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.TextUnformatted(Loc.Get("RollRequest.Threshold"));
        ImGui.SetNextItemWidth(fieldWidth);
        ImGui.InputInt("##roll_request_threshold", ref threshold);

        var hintKey = IsTargetMode
            ? "RollRequest.HintTarget"
            : session.ActiveTemplate?.RollLowerIsBetter == true ? "RollRequest.HintLower" : "RollRequest.HintHigher";
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + fieldWidth);
        ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get(hintKey));
        ImGui.PopTextWrapPos();

        ImGuiHelpers.ScaledDummy(6f);

        var connected = session.IsConnected;
        using (ImRaii.Disabled(!connected))
        {
            if (ImGui.Button(Loc.Get("RollRequest.Send")))
            {
                session.RequestRoll(player, stats.FirstOrDefault(s => s.Id == selectedStatId), threshold);
                IsOpen = false;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(Loc.Get("Gm.Cancel")))
            IsOpen = false;

        if (!connected)
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("RollRequest.Offline"));
    }
}
