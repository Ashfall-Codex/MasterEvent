using System.Collections.Generic;
using MasterEvent.Communication.Remote;

namespace MasterEvent.Services.Remote;

// Pont entre le thread du jeu et le serveur local de la page joueur, sur le modèle de GmRemoteBridge, sans
// notes. Il tourne à côté de celui du MJ, avec ses propres fichiers, son lien et ses sessions. Toutes les
// méthodes s'appellent depuis le thread framework : PlayerSnapshotBuilder et PlayerCommandRunner l'exigent.
// Le droit que Tick reçoit : un personnage est connecté.
public sealed class PlayerRemoteBridge : RemoteBridgeBase<RemotePlayerState>
{
    // Textes du jeu que la page reprend sous leur propre nom, en plus des clés PlayerWeb. et PlayerView.
    private static readonly string[] GameStrings =
    [
        "Tactical.EndMyTurn", "Tactical.EndGroupTurn", "Tactical.MovementValue",
        "RollRequest.Banner", "RollRequest.HintLower", "RollRequest.Dismiss",
        "Turns.Round", "Turns.Progress", "Turns.GroupUnnamed", "Turns.SubtitleFmt", "Turns.InitRoll",
        "Scene.OnField", "Scene.TurnOrder", "Scene.TheirTurn", "Scene.LobbyFmt", "Scene.OnlineFmt", "Sidebar.Scene",
        "Group.Players", "Group.CoGm", "Group.Connected", "Group.Disconnected", "Group.Gm",
        "Terrain.EmptyTitle", "Attitude.Hostile", "Attitude.Neutral", "Attitude.Friendly",
        "Marker.Boss", "Marker.Hp", "Marker.Mp", "Marker.Shield",
        "Dice.NoStat", "Dice.Success", "Dice.Failure", "Dice.History", "Dice.NoHistory",
        "StatGrid.RollFmt", "StatGrid.RollBonusFmt", "Models.StatsFilter", "Sheets.NoStatMatch", "Sheets.NoneLoaded",
        "Player.NoTemplate", "Player.NoStats", "Link.InSession", "Link.InSessionOne",
        "Gm.LinkServer", "Gm.LinkReconnecting", "Gm.Local", "Lobby.JoinTitle", "Search.Clear",
    ];

    private const string ModulePrefix = "MasterEvent.Resources.WebModules.Player.";

    // app.css est la feuille de la page MJ, partagée : player.css n'y ajoute que ce qui est propre au joueur.
    private static readonly RemotePageSpec Page = new()
    {
        LogPrefix = "[MasterEvent] Second écran joueur : ",
        Files =
        [
            ("/", "MasterEvent.Resources.Web.player.html", RemoteWebFiles.Html),
            ("/player/main.js", ModulePrefix + "main.js", RemoteWebFiles.JavaScript),
            ("/player.css", "MasterEvent.Resources.Web.player.css", RemoteWebFiles.Css),
            ("/app.css", "MasterEvent.Resources.Web.app.css", RemoteWebFiles.Css),
        ],
        ModulePrefix = ModulePrefix,
        ModulePath = "/player/",
        HelloPrefixes = ["PlayerWeb.", "PlayerView."],
        GameStrings = GameStrings,
        SessionOpenedKey = "PlayerRemote.SessionOpened",
        RevokedReason = "plus de personnage connecté",
    };

    private readonly PlayerSnapshotBuilder snapshots;
    private readonly PlayerCommandRunner commands;

    public PlayerRemoteBridge(PlayerSnapshotBuilder snapshots, PlayerCommandRunner commands) : base(Page)
    {
        this.snapshots = snapshots;
        this.commands = commands;
    }

    // Déconnexion du personnage : bye « ended » sans attendre, la vue n'a plus de joueur à montrer.
    public void End() => End("personnage déconnecté");

    protected override RemotePlayerState BuildSnapshot() => snapshots.Build(0);

    protected override RemotePlayerState WithSeq(RemotePlayerState snapshot, long seq) => snapshot with { Seq = seq };

    protected override RemoteOutcome Run(RemoteCommand command, long now) => commands.Run(command, now);

    // Ceux que la page montre et eux seuls : soi, puis ses coéquipiers.
    protected override (IEnumerable<(string Hash, string Name)> Players, string? LocalHash) PortraitsOf(
        RemotePlayerState state)
    {
        var players = new List<(string Hash, string Name)>(state.Players.Count + 1);
        if (state.Me is { } me) players.Add((me.Hash, me.Name));
        foreach (var mate in state.Players) players.Add((mate.Hash, mate.Name));
        return (players, state.Me?.Hash);
    }
}
