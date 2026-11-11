using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using MasterEvent.Localization;
using MasterEvent.Services.Remote;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Second écran : la vue MJ dans un navigateur du même PC, qui peut aussi commander la scène.
public sealed partial class GmWindow
{
    private const string RemoteViewPopupId = "##remote_view_popup";

    private DateTime remoteLinkCopiedAt = DateTime.MinValue;
    private bool remoteOpenFailed;

    public GmRemoteBridge? RemoteBridge { get; set; }

    private void DrawRemoteViewButton(bool gmAccess)
    {
        // Hors de ces conditions, GmRemoteBridge.Tick coupe lui-même ce qui reste ouvert.
        if (RemoteBridge is not { } bridge || !session.IsLobbyMode || (!gmAccess && !session.IsPromoted)) return;

        var connected = bridge.SessionCount;
        if (SidebarControls.DrawButton(FontAwesomeIcon.Desktop, "##sidebar_remote_view", connected > 0,
                Loc.Get("RemoteView.Title") + "\n" + RemoteViewStatus(connected), SidebarButtonSize, outlined: true))
        {
            remoteOpenFailed = false;
            ImGui.OpenPopup(RemoteViewPopupId);
        }

        DrawRemoteViewPopup(bridge, connected);
    }

    private void DrawRemoteViewPopup(GmRemoteBridge bridge, int connected)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSize(new Vector2(340f * scale, 0));
        if (!ImGui.BeginPopup(RemoteViewPopupId)) return;

        var width = ImGui.GetContentRegionAvail().X;
        var rowStart = ImGui.GetCursorPosX();

        ImGui.TextColored(MasterEventTheme.AccentText, Loc.Get("RemoteView.Title"));
        ImGui.SameLine();
        ImGui.TextColored(connected > 0 ? MasterEventTheme.SuccessColor : MasterEventTheme.MutedTextColor,
            RemoteViewStatus(connected));
        ImGui.Separator();

        ImGui.PushTextWrapPos(rowStart + width);
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("RemoteView.Hint"));
        ImGui.PopTextWrapPos();
        ImGuiHelpers.ScaledDummy(4f);

        if (Buttons.Primary(Loc.Get("RemoteView.Open") + "##remote_open", width, ImGui.GetFrameHeight() * 1.2f)
            && OpenRemoteLink(bridge) is { } url)
        {
            Util.OpenLink(url);
            ImGui.CloseCurrentPopup();
        }

        if (Buttons.Quiet(Loc.Get("RemoteView.CopyLink") + "##remote_copy") && OpenRemoteLink(bridge) is { } link)
        {
            ImGui.SetClipboardText(link);
            remoteLinkCopiedAt = DateTime.UtcNow;
        }

        if (bridge.IsRunning)
        {
            var stopLabel = Loc.Get("RemoteView.Stop");
            ImGui.SameLine(rowStart + width - Buttons.TextWidth(stopLabel));
            if (Buttons.Destructive(stopLabel + "##remote_stop"))
            {
                bridge.Stop();
                remoteLinkCopiedAt = DateTime.MinValue;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("RemoteView.StopTooltip"));
        }

        ImGuiHelpers.ScaledDummy(2f);
        // Relu par le pont à chaque tick : les onglets ouverts ensuite suivent le réglage.
        var hidePrivate = configuration.RemoteViewHidePrivate;
        if (ToggleSwitch.Draw("##remote_hide_private", Loc.Get("RemoteView.HidePrivate"), ref hidePrivate))
        {
            configuration.RemoteViewHidePrivate = hidePrivate;
            configuration.Save();
        }

        // Le retour s'affiche sous les commandes, pour qu'elles ne bougent pas quand il apparaît.
        ImGui.PushTextWrapPos(rowStart + width);
        if (remoteOpenFailed)
            ImGui.TextColored(MasterEventTheme.DangerColor, Loc.Get("RemoteView.PortError"));
        else if (DateTime.UtcNow - remoteLinkCopiedAt < TimeSpan.FromSeconds(3))
            ImGui.TextColored(MasterEventTheme.SuccessColor, Loc.Get("RemoteView.LinkCopied"));

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("RemoteView.LocalOnly"));
        ImGui.PopTextWrapPos();

        ImGui.EndPopup();
    }

    private string? OpenRemoteLink(GmRemoteBridge bridge)
    {
        var url = bridge.Open();
        remoteOpenFailed = url is null;
        return url;
    }

    private static string RemoteViewStatus(int connected) => connected switch
    {
        0 => Loc.Get("RemoteView.StatusNone"),
        1 => Loc.Get("RemoteView.StatusOne"),
        _ => string.Format(Loc.Get("RemoteView.StatusFmt"), connected),
    };
}
