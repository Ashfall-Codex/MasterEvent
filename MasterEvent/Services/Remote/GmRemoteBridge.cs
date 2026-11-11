using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text.Json;
using MasterEvent.Communication.Remote;
using MasterEvent.Localization;

namespace MasterEvent.Services.Remote;

// Pont entre le thread du jeu et le serveur local du second écran. Toutes les méthodes s'appellent
// depuis le thread framework (tick, fenêtre ImGui) : GmSnapshotBuilder et RemoteCommandRunner l'exigent.
public sealed class GmRemoteBridge : IDisposable
{
    private const string LogPrefix = "[MasterEvent] Second écran : ";
    private const long StateIntervalMs = 250;
    private const long NotesIntervalMs = 1000;
    // Un rôle ou un lobby qui vacille (reconnexion, passation en cours) ne coupe pas l'écran aussitôt.
    private const long RevokeGraceMs = 5000;
    // Moins que les 5 s au bout desquelles la page abandonne l'attente : ce qu'elle croit perdu ne s'exécute
    // pas ensuite, et un écart de PV ou un jet recommencé ne s'applique pas deux fois.
    private const long CommandLifetimeMs = 2500;
    private const int MaxCommandsPerTick = 4;
    // Le relais ignore sans prévenir ce qui dépasse 30 trames par seconde et par client, trafic du MJ compris ;
    // une commande en coûte jusqu'à 8 (refus d'un sous-groupe).
    private const double CommandsPerSecond = 6;

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

    private static readonly (string Path, string Resource, string ContentType)[] WebResources =
    [
        ("/", "MasterEvent.Resources.Web.index.html", "text/html; charset=utf-8"),
        ("/app.js", "MasterEvent.Resources.Web.app.js", "text/javascript; charset=utf-8"),
        ("/app.css", "MasterEvent.Resources.Web.app.css", "text/css; charset=utf-8"),
    ];

    private readonly GmSnapshotBuilder snapshots;
    private readonly NotesStore notes;
    private readonly Func<bool> privateByDefault;
    private readonly RemoteCommandRunner commands;
    private readonly List<(long Connection, string Id, RemoteOutcome Outcome)> answers = [];
    private Dictionary<string, WebFile>? files;
    private LocalGmServer? server;
    private string? helloLanguage;
    private bool helloPrivate;
    // Dernier état publié, sérialisé avec Seq à zéro : seul un vrai changement de la scène compte.
    private byte[]? lastState;
    private string? lastNotes;
    private long seq;
    private long nextStateAt;
    private long nextNotesAt;
    private int lastSessionCount;
    private bool buildFailed;
    private long disallowedSince;
    private long noticedSessions;
    private double commandTokens = CommandsPerSecond;
    private long commandTokensAt;

    public GmRemoteBridge(GmSnapshotBuilder snapshots, NotesStore notes, Func<bool> privateByDefault,
        RemoteCommandRunner commands)
    {
        this.snapshots = snapshots;
        this.notes = notes;
        this.privateByDefault = privateByDefault;
        this.commands = commands;
    }

    public bool IsRunning => server is { IsListening: true };

    public int SessionCount => server is { IsListening: true } running ? running.SessionCount : 0;

    // Renvoie un lien à usage unique, ou null si l'écoute locale n'a pas pu démarrer (cause au journal).
    // Chaque appel émet un nouveau lien et invalide le précédent.
    public string? Open()
    {
        if (EnsureServer() is not { } running) return null;

        EnsureHello(running);
        // La page qui ouvrira ce lien reçoit d'abord l'état gardé par le serveur : il doit être à jour.
        PublishState(running);
        PublishNotes(running);
        return running.IssueTicketUrl();
    }

    // allowed : le MJ a encore droit au second écran (lobby actif, MJ ou co-MJ). Sinon, passé un court
    // délai, les pages reçoivent bye « ended » et l'écoute s'arrête : rien ne reste ouvert sans bouton pour l'arrêter.
    // Pendant ce délai, les commandes reçoivent « forbidden ».
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
                Plugin.Log.Information($"{LogPrefix}coupé, plus de lobby ou plus de rôle de MJ");
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
        if (arrived || now >= nextNotesAt)
        {
            nextNotesAt = now + NotesIntervalMs;
            PublishNotes(running);
        }
    }

    // « Couper » : les pages reçoivent bye « revoked », puis l'écoute s'arrête.
    public void Stop() => Shutdown(LocalGmServer.Revoked);

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
        lastNotes = null;
        lastSessionCount = 0;
        nextStateAt = 0;
        nextNotesAt = 0;
        disallowedSince = 0;
        noticedSessions = 0;
    }

    // Le jeton d'une session donne le droit d'agir sur la scène : le MJ doit savoir qu'une page vient de l'obtenir.
    private void NoticeNewSessions(LocalGmServer running)
    {
        var opened = running.SessionsOpened;
        if (opened == noticedSessions) return;
        noticedSessions = opened;
        Plugin.ChatGui.Print(Loc.Get("RemoteView.SessionOpened"));
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
        var hidePrivate = privateByDefault();
        if (language == helloLanguage && hidePrivate == helloPrivate) return;

        var strings = new Dictionary<string, string>(Loc.GetAllWithPrefix("Web."), StringComparer.Ordinal);
        foreach (var key in GameStrings) strings[key] = Loc.Get(key);
        var hello = new RemoteHello(RemoteView.Version, Constants.PluginVersion, language, hidePrivate, strings);
        running.SetHello(JsonSerializer.Serialize(hello, RemoteView.Json));
        helloLanguage = language;
        helloPrivate = hidePrivate;
    }

    private void PublishState(LocalGmServer running)
    {
        RemoteState snapshot;
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

        if (lastState is not null && comparable.AsSpan().SequenceEqual(lastState)) return;
        lastState = comparable;
        seq++;
        running.PublishState(JsonSerializer.SerializeToUtf8Bytes(snapshot with { Seq = seq }, RemoteView.Json), seq);
    }

    private void PublishNotes(LocalGmServer running)
    {
        var text = notes.Text;
        if (text == lastNotes) return;
        lastNotes = text;
        running.PublishNotes(JsonSerializer.Serialize(new RemoteNotes(text), RemoteView.Json));
    }

    private static Dictionary<string, WebFile>? LoadFiles()
    {
        var assembly = typeof(GmRemoteBridge).Assembly;
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
