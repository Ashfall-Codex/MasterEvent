using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using MasterEvent.Localization;
using MasterEvent.Services;

namespace MasterEvent.UI.Components;

public static class LobbyJoinControls
{
    private const int MaxInputLength = 64;

    public static void Draw(string id, ref string input, Action<string> onJoin)
    {
        var style = ImGui.GetStyle();
        var joinLabel = Loc.Get("Lobby.Join");
        var joinWidth = ImGui.CalcTextSize(joinLabel).X + style.FramePadding.X * 4f;
        var pasteWidth = ImGui.GetFrameHeight();
        var inputWidth = ImGui.GetContentRegionAvail().X - joinWidth - pasteWidth - style.ItemSpacing.X * 2f;

        ImGui.SetNextItemWidth(Math.Max(inputWidth, ImGui.GetFrameHeight() * 4f));
        var submitted = ImGui.InputTextWithHint("##" + id, Loc.Get("Lobby.InputHint"), ref input, MaxInputLength,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        if (DrawPasteButton(id))
            input = ImGui.GetClipboardText() ?? string.Empty;

        var valid = SessionManager.TryParseLobbyCode(input, out var code);

        ImGui.SameLine();
        bool clicked;
        using (ImRaii.Disabled(!valid))
            clicked = Buttons.Primary(joinLabel + "##" + id + "_join", joinWidth);

        if (valid && (submitted || clicked))
        {
            onJoin(code);
            input = string.Empty;
            return;
        }

        if (input.Trim().Length == 0) return;

        if (valid && !string.Equals(input.Trim(), code, StringComparison.OrdinalIgnoreCase))
            ImGui.TextColored(MasterEventTheme.MutedTextColor, string.Format(Loc.Get("Lobby.CodeFoundFmt"), code));
        else if (!valid)
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Lobby.InvalidCode"));
    }

    private static bool DrawPasteButton(string id)
    {
        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            clicked = ImGui.Button(FontAwesomeIcon.Paste.ToIconString() + "##" + id + "_paste",
                new System.Numerics.Vector2(ImGui.GetFrameHeight(), 0f));

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Lobby.Paste"));
        return clicked;
    }
}
