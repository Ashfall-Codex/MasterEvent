using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MasterEvent.Communication.Remote;

namespace MasterEvent.Services.Remote;

// Pont entre le thread du jeu et le serveur local du second écran. Toutes les méthodes s'appellent
// depuis le thread framework (tick, fenêtre ImGui) : GmSnapshotBuilder et RemoteCommandRunner l'exigent.
// Le droit que Tick reçoit : lobby actif, MJ ou co-MJ.
public sealed class GmRemoteBridge : RemoteBridgeBase<RemoteState>
{
    private const long NotesIntervalMs = 1000;

    // Textes du jeu que la page reprend sous leur propre nom, pour garder le vocabulaire de l'onglet Scène.
    private static readonly string[] GameStrings =
    [
        "Turns.StartTooltip", "Turns.NextRoundTooltip", "Scene.EndTurnTooltip", "Turns.RollFor",
        "Scene.HpDown", "Scene.HpUp", "Scene.HpDownPct", "Scene.HpUpPct", "Scene.CloseDetail",
        "Marker.Roll", "Marker.RollDisabled", "Marker.Shield", "Dice.NoStat", "Dice.SelectStat",
        "Lobby.Admit", "Lobby.Deny", "Group.AskRoll",
        "RollRequest.WindowTitle", "RollRequest.For", "RollRequest.Stat", "RollRequest.Threshold", "RollRequest.Send",
        "RollRequest.HintTarget", "RollRequest.HintLower", "RollRequest.HintHigher",
        "Gm.AnnounceButton", "Gm.AnnounceTooltip", "Gm.AnnouncePopupTitle", "Gm.AnnouncePopupHint", "Gm.AnnounceSend",
        "Gm.Cancel",
    ];

    private const string ModulePrefix = "MasterEvent.Resources.WebModules.Gm.";

    private static readonly RemotePageSpec Page = new()
    {
        LogPrefix = "[MasterEvent] Second écran : ",
        Files =
        [
            ("/", "MasterEvent.Resources.Web.index.html", RemoteWebFiles.Html),
            ("/app.css", "MasterEvent.Resources.Web.app.css", RemoteWebFiles.Css),
            ("/gm/main.js", ModulePrefix + "main.js", RemoteWebFiles.JavaScript),
        ],
        ModulePrefix = ModulePrefix,
        ModulePath = "/gm/",
        HelloPrefixes = ["Web."],
        GameStrings = GameStrings,
        SessionOpenedKey = "RemoteView.SessionOpened",
        RevokedReason = "plus de lobby ou plus de rôle de MJ",
    };

    private readonly GmSnapshotBuilder snapshots;
    private readonly NotesStore notes;
    private readonly Func<bool> privateByDefault;
    private readonly RemoteCommandRunner commands;
    private string? lastNotes;
    private long nextNotesAt;

    public GmRemoteBridge(GmSnapshotBuilder snapshots, NotesStore notes, Func<bool> privateByDefault,
        RemoteCommandRunner commands) : base(Page)
    {
        this.snapshots = snapshots;
        this.notes = notes;
        this.privateByDefault = privateByDefault;
        this.commands = commands;
    }

    protected override RemoteState BuildSnapshot() => snapshots.Build(0);

    protected override RemoteState WithSeq(RemoteState snapshot, long seq) => snapshot with { Seq = seq };

    protected override RemoteOutcome Run(RemoteCommand command, long now) => commands.Run(command, now);

    // Sans l'empreinte locale, que le pont ne connaît pas : le MJ qui joue se résout par son nom, celui de son personnage.
    protected override (IEnumerable<(string Hash, string Name)> Players, string? LocalHash) PortraitsOf(RemoteState state)
        => (state.Party.Select(p => (p.Hash, p.Name)), null);

    protected override bool HidePrivate() => privateByDefault();

    protected override void PublishOnOpen(LocalGmServer running) => PublishNotes(running);

    protected override void PublishOnTick(LocalGmServer running, long now, bool arrived)
    {
        if (arrived || now >= nextNotesAt)
        {
            nextNotesAt = now + NotesIntervalMs;
            PublishNotes(running);
        }
    }

    protected override void ResetOwnPublications()
    {
        lastNotes = null;
        nextNotesAt = 0;
    }

    private void PublishNotes(LocalGmServer running)
    {
        var text = notes.Text;
        if (text == lastNotes) return;
        lastNotes = text;
        running.PublishNotes(JsonSerializer.Serialize(new RemoteNotes(text), RemoteView.Json));
    }
}
