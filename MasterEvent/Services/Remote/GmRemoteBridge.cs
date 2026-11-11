using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
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
    private const long PortraitsIntervalMs = 1000;
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

    private const string ModulePrefix = "MasterEvent.Resources.WebModules.Gm.";

    // En plus des modules de ModulePrefix, servis sous /gm/ : main.js y figure pour que son absence empêche l'ouverture.
    private static readonly (string Path, string Resource, string ContentType)[] WebResources =
    [
        ("/", "MasterEvent.Resources.Web.index.html", RemoteWebFiles.Html),
        ("/app.css", "MasterEvent.Resources.Web.app.css", RemoteWebFiles.Css),
        ("/gm/main.js", ModulePrefix + "main.js", RemoteWebFiles.JavaScript),
    ];

    private readonly GmSnapshotBuilder snapshots;
    private readonly NotesStore notes;
    private readonly Func<bool> privateByDefault;
    private readonly RemoteCommandRunner commands;
    private readonly List<(long Connection, string Id, RemoteOutcome Outcome)> answers = [];
    private readonly RemotePortraitFeed portraitFeed = new();
    private Dictionary<string, WebFile>? files;
    private LocalGmServer? server;
    private string? helloLanguage;
    private bool helloPrivate;
    // Dernier état publié, sérialisé avec Seq à zéro : seul un vrai changement de la scène compte.
    private byte[]? lastState;
    private RemoteState? latest;
    private string? lastNotes;
    private long seq;
    private long nextStateAt;
    private long nextNotesAt;
    private long nextPortraitsAt;
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
        if (Portraits is { } cache && latest is { } state && (arrived || now >= nextPortraitsAt))
        {
            nextPortraitsAt = now + PortraitsIntervalMs;
            // Sans l'empreinte locale, que le pont ne connaît pas : le MJ qui joue se résout par son nom, celui de son personnage.
            portraitFeed.Publish(running, cache, state.Party.Select(p => (p.Hash, p.Name)), null);
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

        files ??= RemoteWebFiles.Load(LogPrefix, WebResources, ModulePrefix, "/gm/");
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
        nextPortraitsAt = 0;
        portraitFeed.Reset();
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
            var outcome = Execute(inbound, allowed, now);
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

    // Le jeton n'est pris que pour une commande qui s'exécuterait : un refus ne coûte rien au débit.
    private RemoteOutcome Execute(InboundCommand inbound, bool allowed, long now)
    {
        if (!allowed) return RemoteOutcome.Fail(RemoteErrors.Forbidden);
        if (now - inbound.ReceivedAt > CommandLifetimeMs) return RemoteOutcome.Fail(RemoteErrors.Expired);
        if (!TakeCommandToken(now)) return RemoteOutcome.Fail(RemoteErrors.Busy);
        return commands.Run(inbound.Command, now);
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
        if (BuildState() is not { } built) return;
        var (snapshot, comparable) = built;
        latest = snapshot;

        if (lastState is not null && comparable.AsSpan().SequenceEqual(lastState)) return;
        lastState = comparable;
        seq++;
        running.PublishState(JsonSerializer.SerializeToUtf8Bytes(snapshot with { Seq = seq }, RemoteView.Json), seq);
    }

    // Null si l'instantané échoue. Ce que l'état reçu du relais peut provoquer : marqueur ou entrée de tour
    // hors bornes, collection modifiée, texte UTF-16 invalide que le sérialiseur refuse.
    private (RemoteState Snapshot, byte[] Comparable)? BuildState()
    {
        try
        {
            var snapshot = snapshots.Build(0);
            var comparable = JsonSerializer.SerializeToUtf8Bytes(snapshot, RemoteView.Json);
            buildFailed = false;
            return (snapshot, comparable);
        }
        catch (InvalidOperationException ex) { ReportBuildFailure(ex); }
        catch (ArgumentException ex) { ReportBuildFailure(ex); }
        catch (IndexOutOfRangeException ex) { ReportBuildFailure(ex); }
        return null;
    }

    // Appelé à chaque tick : une erreur qui se répète ne doit laisser qu'une trace.
    private void ReportBuildFailure(Exception ex)
    {
        if (!buildFailed) Plugin.Log.Error($"{LogPrefix}instantané impossible : {ex}");
        buildFailed = true;
    }

    private void PublishNotes(LocalGmServer running)
    {
        var text = notes.Text;
        if (text == lastNotes) return;
        lastNotes = text;
        running.PublishNotes(JsonSerializer.Serialize(new RemoteNotes(text), RemoteView.Json));
    }
}

// Fichiers servis par les deux ponts : ceux qu'une page nomme un à un, puis tous les modules embarqués sous un
// préfixe, que la page importe entre eux par chemin relatif.
internal static class RemoteWebFiles
{
    public const string Html = "text/html; charset=utf-8";
    public const string Css = "text/css; charset=utf-8";
    public const string JavaScript = "text/javascript; charset=utf-8";

    // Null si une ressource nommée manque (cause au journal) : la page ne s'ouvrirait qu'à moitié.
    public static Dictionary<string, WebFile>? Load(string logPrefix,
        IEnumerable<(string Path, string Resource, string ContentType)> named, string modulePrefix, string modulePath)
    {
        var assembly = typeof(RemoteWebFiles).Assembly;
        var loaded = new Dictionary<string, WebFile>(StringComparer.Ordinal);
        foreach (var (path, resource, contentType) in named)
        {
            if (Read(assembly, resource) is not { } body)
            {
                Plugin.Log.Error($"{logPrefix}ressource {resource} absente du plugin");
                return null;
            }
            loaded[path] = new WebFile(contentType, body);
        }

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (resource.StartsWith(modulePrefix, StringComparison.Ordinal) && Read(assembly, resource) is { } body)
                loaded[modulePath + resource[modulePrefix.Length..]] = new WebFile(JavaScript, body);
        }
        return loaded;
    }

    private static byte[]? Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;

        var body = new byte[stream.Length];
        stream.ReadExactly(body);
        return body;
    }
}

// Plugin vers page, après les notes : portrait UmbraSync de chaque joueur montré qui en a un, sous son
// empreinte (RemotePlayer.Hash, RemotePlayerSheet.Hash ou RemotePlayerMate.Hash), en data URI PNG ou JPEG.
// Liste complète à chaque envoi ; un joueur absent n'a pas (ou plus) d'image. Rien d'autre ne part : ni nom,
// ni UID UmbraSync, ni objectId.
public sealed record RemotePortraits(IReadOnlyDictionary<string, string> Items)
{
    public string Type => "portraits";
}

// Portraits des joueurs d'une page, pour les deux ponts. Thread du jeu : le cache et la table des objets l'exigent.
internal sealed class RemotePortraitFeed
{
    private readonly Dictionary<string, int> published = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> versions = new(StringComparer.Ordinal);
    private readonly List<(string Hash, byte[] Bytes, string Mime)> images = [];
    private readonly List<(string Hash, string Name)> order = [];
    private int rotation;

    // Serveur neuf : il ne garde aucune image.
    public void Reset() => published.Clear();

    // localHash : le joueur local, résolu par son propre personnage comme dans les vues en jeu.
    public void Publish(LocalGmServer server, UmbraPortraitCache cache, IEnumerable<(string Hash, string Name)> players,
        string? localHash)
    {
        versions.Clear();
        images.Clear();
        order.Clear();
        order.AddRange(players);
        // Le cache n'accepte que deux essais par image : sans rotation, les joueurs sans portrait en tête
        // de liste reprendraient tout le budget à chaque passage et ceux de la fin n'auraient jamais le leur.
        var start = order.Count == 0 ? 0 : rotation = (rotation + 1) % order.Count;
        for (var i = 0; i < order.Count; i++)
        {
            var (hash, name) = order[(start + i) % order.Count];
            if (string.IsNullOrEmpty(hash) || versions.ContainsKey(hash)) continue;
            var objectId = UmbraPortraitCache.ObjectIdFor(hash, name, localHash);
            // Lance ou poursuit la récupération, au rythme que le cache s'impose déjà pour les vues en jeu.
            cache.Get(objectId);
            if (!cache.TryGetImage(objectId, out var bytes, out var mime, out var version)) continue;
            versions[hash] = version;
            images.Add((hash, bytes, mime));
        }

        if (versions.Count == published.Count
            && versions.All(v => published.TryGetValue(v.Key, out var sent) && sent == v.Value)) return;

        var items = new Dictionary<string, string>(images.Count, StringComparer.Ordinal);
        foreach (var (hash, bytes, mime) in images)
            items[hash] = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        server.PublishPortraits(JsonSerializer.Serialize(new RemotePortraits(items), RemoteView.Json));

        published.Clear();
        foreach (var (hash, version) in versions) published[hash] = version;
    }
}
