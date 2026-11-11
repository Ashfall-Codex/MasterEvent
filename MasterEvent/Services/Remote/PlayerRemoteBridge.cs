using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text.Json;
using MasterEvent.Communication.Remote;
using MasterEvent.Localization;

namespace MasterEvent.Services.Remote;

// Pont entre le thread du jeu et le serveur local de la page joueur, sur le modèle de GmRemoteBridge, sans
// notes. Il tourne à côté de celui du MJ, avec ses propres fichiers, son lien et ses sessions. Toutes les
// méthodes s'appellent depuis le thread framework : PlayerSnapshotBuilder et PlayerCommandRunner l'exigent.
public sealed class PlayerRemoteBridge : IDisposable
{
    private const string LogPrefix = "[MasterEvent] Second écran joueur : ";
    private const long StateIntervalMs = 250;
    private const long PortraitsIntervalMs = 1000;
    // Un chargement de zone ne coupe pas l'écran : seule une déconnexion qui dure le fait.
    private const long RevokeGraceMs = 5000;
    // Moins que les 5 s au bout desquelles la page abandonne l'attente : ce qu'elle croit perdu ne s'exécute pas ensuite.
    private const long CommandLifetimeMs = 2500;
    private const int MaxCommandsPerTick = 4;
    private const double CommandsPerSecond = 6;

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

    // app.css est la feuille de la page MJ, partagée : player.css n'y ajoute que ce qui est propre au joueur.
    private static readonly (string Path, string Resource, string ContentType)[] WebResources =
    [
        ("/", "MasterEvent.Resources.Web.player.html", "text/html; charset=utf-8"),
        ("/player.js", "MasterEvent.Resources.Web.player.js", "text/javascript; charset=utf-8"),
        ("/player.css", "MasterEvent.Resources.Web.player.css", "text/css; charset=utf-8"),
        ("/app.css", "MasterEvent.Resources.Web.app.css", "text/css; charset=utf-8"),
    ];

    private readonly PlayerSnapshotBuilder snapshots;
    private readonly PlayerCommandRunner commands;
    private readonly List<(long Connection, string Id, RemoteOutcome Outcome)> answers = [];
    private readonly RemotePortraitFeed portraitFeed = new();
    private Dictionary<string, WebFile>? files;
    private LocalGmServer? server;
    private string? helloLanguage;
    // Dernier état publié, sérialisé avec Seq à zéro : seul un vrai changement de la vue compte.
    private byte[]? lastState;
    private RemotePlayerState? latest;
    private long seq;
    private long nextStateAt;
    private long nextPortraitsAt;
    private int lastSessionCount;
    private bool buildFailed;
    private long disallowedSince;
    private long noticedSessions;
    private double commandTokens = CommandsPerSecond;
    private long commandTokensAt;

    public PlayerRemoteBridge(PlayerSnapshotBuilder snapshots, PlayerCommandRunner commands)
    {
        this.snapshots = snapshots;
        this.commands = commands;
    }

    public bool IsRunning => server is { IsListening: true };

    public int SessionCount => server is { IsListening: true } running ? running.SessionCount : 0;

    // Créé après le pont : sans lui, aucune image ne part.
    public UmbraPortraitCache? Portraits { get; set; }

    // Renvoie un lien à usage unique, ou null si l'écoute locale n'a pas pu démarrer (cause au journal).
    // Chaque appel émet un nouveau lien et invalide le précédent.
    public string? Open()
    {
        if (EnsureServer() is not { } running) return null;

        EnsureHello(running);
        // La page qui ouvrira ce lien reçoit d'abord l'état gardé par le serveur : il doit être à jour.
        PublishState(running);
        return running.IssueTicketUrl();
    }

    // allowed : un personnage est connecté. Sinon, passé un court délai, les pages reçoivent bye « ended »
    // et l'écoute s'arrête. Pendant ce délai, les commandes reçoivent « forbidden ».
    public void Tick(bool allowed)
    {
        if (server is not { IsListening: true } running) return;

        var now = Environment.TickCount64;
        if (allowed)
        {
            disallowedSince = 0;
        }
        else
        {
            if (disallowedSince == 0) disallowedSince = now;
            if (now - disallowedSince >= RevokeGraceMs)
            {
                Plugin.Log.Information($"{LogPrefix}coupé, plus de personnage connecté");
                Shutdown(LocalGmServer.Ended);
                return;
            }
        }

        // Avant le test des sessions : une page qui se connecte reçoit le hello avant de s'authentifier.
        EnsureHello(running);
        NoticeNewSessions(running);

        var count = running.SessionCount;
        var arrived = count > 0 && lastSessionCount == 0;
        lastSessionCount = count;

        // Avant la publication cadencée : une commande voit l'état le plus frais, et son effet part aussitôt.
        RunCommands(running, allowed, now);
        if (count == 0) return;

        if (arrived || now >= nextStateAt)
        {
            nextStateAt = now + StateIntervalMs;
            PublishState(running);
        }
        if (Portraits is { } cache && latest is { } state && (arrived || now >= nextPortraitsAt))
        {
            nextPortraitsAt = now + PortraitsIntervalMs;
            PublishPortraits(running, cache, state);
        }
    }

    // « Couper le second écran » : les pages reçoivent bye « revoked », puis l'écoute s'arrête.
    public void Stop() => Shutdown(LocalGmServer.Revoked);

    // Déconnexion du personnage : bye « ended » sans attendre, la vue n'a plus de joueur à montrer.
    public void End()
    {
        if (server is null) return;
        Plugin.Log.Information($"{LogPrefix}coupé, personnage déconnecté");
        Shutdown(LocalGmServer.Ended);
    }

    private void Shutdown(string reason)
    {
        if (server is not { } running) return;
        running.RevokeAll(reason);
        running.Dispose();
        server = null;
        ResetPublications();
    }

    public void Dispose()
    {
        server?.Dispose();
        server = null;
    }

    private LocalGmServer? EnsureServer()
    {
        // Une boucle d'accept arrêtée ne repartira pas : on recrée le serveur, avec un nouveau port.
        if (server is { IsListening: false } dead)
        {
            dead.Dispose();
            server = null;
            ResetPublications();
        }
        if (server is not null) return server;

        files ??= LoadFiles();
        if (files is null) return null;

        try
        {
            server = new LocalGmServer(files);
        }
        catch (SocketException ex)
        {
            Plugin.Log.Error($"{LogPrefix}écoute impossible sur 127.0.0.1 ({ex.SocketErrorCode}) : {ex.Message}");
            return null;
        }

        ResetPublications();
        return server;
    }

    // Le serveur neuf ne garde rien : tout repart à la prochaine publication.
    private void ResetPublications()
    {
        helloLanguage = null;
        lastState = null;
        lastSessionCount = 0;
        nextStateAt = 0;
        nextPortraitsAt = 0;
        portraitFeed.Reset();
        disallowedSince = 0;
        noticedSessions = 0;
    }

    // Le jeton d'une session donne le droit de lancer les dés du joueur : il doit savoir qu'une page vient de l'obtenir.
    private void NoticeNewSessions(LocalGmServer running)
    {
        var opened = running.SessionsOpened;
        if (opened == noticedSessions) return;
        noticedSessions = opened;
        Plugin.ChatGui.Print(Loc.Get("PlayerRemote.SessionOpened"));
    }

    private void RunCommands(LocalGmServer running, bool allowed, long now)
    {
        var changed = false;
        var taken = 0;
        while (taken < MaxCommandsPerTick && running.TryTakeCommand(out var inbound))
        {
            if (!running.IsLive(inbound.ConnectionId)) continue;
            taken++;
            var outcome = !allowed ? RemoteOutcome.Fail(RemoteErrors.Forbidden)
                : now - inbound.ReceivedAt > CommandLifetimeMs ? RemoteOutcome.Fail(RemoteErrors.Expired)
                : !TakeCommandToken(now) ? RemoteOutcome.Fail(RemoteErrors.Busy)
                : commands.Run(inbound.Command, now);
            changed |= outcome.Changed;
            answers.Add((inbound.ConnectionId, inbound.Command.Id, outcome));
        }
        if (answers.Count == 0) return;

        // Sans attendre la cadence : la réponse porte le seq de l'état qui contient déjà l'effet.
        if (changed)
        {
            PublishState(running);
            nextStateAt = now + StateIntervalMs;
        }
        foreach (var (connection, id, outcome) in answers)
            running.Acknowledge(connection, new RemoteAck(id, outcome.Ok, outcome.Error, outcome.Changed, seq));
        answers.Clear();
    }

    // Seau de jetons partagé par toutes les pages, rechargé en continu.
    private bool TakeCommandToken(long now)
    {
        commandTokens = Math.Min(CommandsPerSecond, commandTokens + (now - commandTokensAt) * CommandsPerSecond / 1000);
        commandTokensAt = now;
        if (commandTokens < 1) return false;
        commandTokens--;
        return true;
    }

    private void EnsureHello(LocalGmServer running)
    {
        var language = Loc.CurrentLanguage;
        if (language == helloLanguage) return;

        var strings = new Dictionary<string, string>(Loc.GetAllWithPrefix("PlayerWeb."), StringComparer.Ordinal);
        foreach (var (key, value) in Loc.GetAllWithPrefix("PlayerView.")) strings[key] = value;
        foreach (var key in GameStrings) strings[key] = Loc.Get(key);
        var hello = new RemoteHello(RemoteView.Version, Constants.PluginVersion, language, false, strings);
        running.SetHello(JsonSerializer.Serialize(hello, RemoteView.Json));
        helloLanguage = language;
    }

    private void PublishState(LocalGmServer running)
    {
        RemotePlayerState snapshot;
        byte[] comparable;
        try
        {
            snapshot = snapshots.Build(0);
            comparable = JsonSerializer.SerializeToUtf8Bytes(snapshot, RemoteView.Json);
        }
        catch (Exception ex)
        {
            // Appelé à chaque tick : une erreur qui se répète ne doit laisser qu'une trace.
            if (!buildFailed) Plugin.Log.Error($"{LogPrefix}instantané impossible : {ex}");
            buildFailed = true;
            return;
        }
        buildFailed = false;
        latest = snapshot;

        if (lastState is not null && comparable.AsSpan().SequenceEqual(lastState)) return;
        lastState = comparable;
        seq++;
        running.PublishState(JsonSerializer.SerializeToUtf8Bytes(snapshot with { Seq = seq }, RemoteView.Json), seq);
    }

    // Ceux que la page montre et eux seuls : soi, puis ses coéquipiers.
    private void PublishPortraits(LocalGmServer running, UmbraPortraitCache cache, RemotePlayerState state)
    {
        var players = new List<(string Hash, string Name)>(state.Players.Count + 1);
        if (state.Me is { } me) players.Add((me.Hash, me.Name));
        foreach (var mate in state.Players) players.Add((mate.Hash, mate.Name));
        portraitFeed.Publish(running, cache, players, state.Me?.Hash);
    }

    private static Dictionary<string, WebFile>? LoadFiles()
    {
        var assembly = typeof(PlayerRemoteBridge).Assembly;
        var loaded = new Dictionary<string, WebFile>(StringComparer.Ordinal);
        foreach (var (path, resource, contentType) in WebResources)
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                Plugin.Log.Error($"{LogPrefix}ressource {resource} absente du plugin");
                return null;
            }

            var body = new byte[stream.Length];
            stream.ReadExactly(body);
            loaded[path] = new WebFile(contentType, body);
        }
        return loaded;
    }
}
