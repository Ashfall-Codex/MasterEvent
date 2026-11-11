using System;
using System.Collections.Generic;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Avis sous la barre, deux au plus : ce que le MJ attend d'abord, puis la connexion, son annonce
// et le retour des marqueurs posés.
public sealed partial class PlayerWindow
{
    private const int MaxNotices = 2;
    private static readonly TimeSpan MarkersNoticeLifetime = TimeSpan.FromSeconds(5);

    private string? markersNoticeText;
    private bool markersNoticeFailed;
    private long markersNoticeAt;

    private void DrawNotices()
    {
        var notices = new List<Action>(4);

        if (viewState == PlayerViewState.GmPreview)
            notices.Add(() => LayoutControls.DrawNotice(Loc.Get("PlayerView.PreviewNotice"),
                MasterEventTheme.TextSecondary, FontAwesomeIcon.Eye));

        // Hors salle, la réponse ne partirait pas : la demande attend le retour en séance.
        if (IsInRoom(viewState) && session.PendingRollRequest is { } request)
            notices.Add(() => DrawRollRequestNotice(request));

        if (viewState == PlayerViewState.Reconnecting)
            notices.Add(() => LayoutControls.DrawNotice(Loc.Get("PlayerView.ReconnectingNotice"),
                MasterEventTheme.WarningColor, FontAwesomeIcon.SyncAlt));

        if (IsSessionLike(viewState) && session.LastGmAnnouncement is { } announcement)
            notices.Add(() => DrawAnnouncementNotice(announcement));

        if (MarkersNoticeAlpha() is { } alpha && markersNoticeText is { } markersText)
            notices.Add(() => DrawMarkersNotice(markersText, alpha));

        for (var i = 0; i < notices.Count && i < MaxNotices; i++)
        {
            notices[i]();
            ImGuiHelpers.ScaledDummy(4f);
        }
    }

    private void DrawRollRequestNotice(RollRequest request)
    {
        var text = request.StatId == null && !request.StatMissing
            ? string.Format(Loc.Get("PlayerView.RollRequestNoStatFmt"), request.Threshold)
            : string.Format(Loc.Get("RollRequest.Banner"), request.StatName, request.Threshold);

        var details = new List<string>();
        if (request.StatMissing)
            details.Add(string.Format(Loc.Get("PlayerView.RollMissingStatFmt"), request.StatName));
        // Seuil fixé par le MJ en mode cible : le dé brut s'y compare, la valeur de la stat ne compte plus.
        if (request.StatId != null && session.ActiveTemplate is { StatResolution: StatResolution.Target })
            details.Add(Loc.Get("PlayerView.RollHintTarget"));
        if (session.ActiveTemplate is { RollLowerIsBetter: true })
            details.Add(Loc.Get("RollRequest.HintLower"));

        if (LayoutControls.DrawNotice(text, MasterEventTheme.AccentText, FontAwesomeIcon.Dice,
                new LayoutControls.NoticeAction("roll_dismiss", Label: Loc.Get("RollRequest.Dismiss")),
                details.Count > 0 ? string.Join("\n", details) : null))
            session.DismissRollRequest();
    }

    private void DrawAnnouncementNotice(string announcement)
    {
        if (LayoutControls.DrawNotice(string.Format(Loc.Get("PlayerView.AnnouncementFmt"), announcement),
                MasterEventTheme.AccentText, FontAwesomeIcon.Bullhorn,
                new LayoutControls.NoticeAction("announcement_hide", Icon: FontAwesomeIcon.Times,
                    Tooltip: Loc.Get("PlayerView.HideAnnouncement"))))
            session.DismissGmAnnouncement();
    }

    // Le résultat s'affiche ici plutôt que dans le chat, et s'estompe comme ceux du terrain côté MJ.
    private void ApplyMarkers()
    {
        var placed = session.ApplyWaymarks();
        markersNoticeFailed = placed <= 0;
        markersNoticeText = placed switch
        {
            <= 0 => Loc.Get("Player.MarkersApplyFailed"),
            1 => Loc.Get("Player.MarkersAppliedOne"),
            _ => string.Format(Loc.Get("Player.MarkersAppliedFmt"), placed),
        };
        markersNoticeAt = Environment.TickCount64;
    }

    private float? MarkersNoticeAlpha()
    {
        if (markersNoticeText == null) return null;

        var age = (Environment.TickCount64 - markersNoticeAt) / MarkersNoticeLifetime.TotalMilliseconds;
        if (age >= 1d)
        {
            markersNoticeText = null;
            return null;
        }

        return (float)(1d - age * age);
    }

    private void DrawMarkersNotice(string text, float alpha)
    {
        if (markersNoticeFailed)
            LayoutControls.DrawNotice(text, MasterEventTheme.WarningColor, FontAwesomeIcon.ExclamationTriangle, alpha);
        else
            LayoutControls.DrawNotice(text, MasterEventTheme.SuccessColor, FontAwesomeIcon.CheckCircle, alpha);
    }
}
