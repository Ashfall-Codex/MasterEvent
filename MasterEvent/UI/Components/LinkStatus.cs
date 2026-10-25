using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using MasterEvent.Communication;
using MasterEvent.Localization;

namespace MasterEvent.UI.Components;

// État de la connexion, dit par une icône, une couleur et une phrase : la couleur seule ne
// suffit pas à qui la distingue mal.
public static class LinkStatus
{
    public static void Draw(RelayLinkState state, int playerCount)
    {
        var (icon, color, text) = Describe(state, playerCount);

        ImGui.AlignTextToFramePadding();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(color, icon.ToIconString());
        ImGui.SameLine();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    public static (FontAwesomeIcon Icon, System.Numerics.Vector4 Color, string Text) Describe(
        RelayLinkState state, int playerCount) => state switch
    {
        RelayLinkState.Offline => (FontAwesomeIcon.Unlink, MasterEventTheme.DangerColor, Loc.Get("Link.Offline")),
        RelayLinkState.Reconnecting => (FontAwesomeIcon.SyncAlt, MasterEventTheme.WarningColor, Loc.Get("Link.Reconnecting")),
        RelayLinkState.ServerOnly => (FontAwesomeIcon.Cloud, MasterEventTheme.MutedTextColor, Loc.Get("Link.ServerOnly")),
        _ => (FontAwesomeIcon.CheckCircle, MasterEventTheme.SuccessColor, playerCount == 1
            ? Loc.Get("Link.InSessionOne")
            : string.Format(Loc.Get("Link.InSession"), playerCount)),
    };
}
