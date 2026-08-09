using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MasterEvent.Models;

namespace MasterEvent.Services;

public sealed class CloudSyncService : IDisposable
{
    private const string KindTemplate = "template";
    private const string KindSheet = "sheet";
    private const string KindNote = "note";

    private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Configuration configuration;
    private readonly SaveManager saveManager;
    private readonly TemplateManager templateManager;
    private readonly NotesStore notesStore;

    // Les push sont regroupés : enchaîner dix sauvegardes de fiche ne déclenche qu'un envoi.
    private readonly Dictionary<(string kind, string name), DateTime> pendingPushes = new();
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private CancellationTokenSource? cts;
    private DateTime lastPullAttempt = DateTime.MinValue;

    // Ses propres SaveManager/TemplateManager : ces classes ne portent aucun état, seulement
    // des chemins. Écrire par ce biais lors d'un pull évite de repasser par SessionManager,
    // donc de re-déclencher un push pour un contenu qui vient justement du serveur.
    // Le NotesStore, lui, est PARTAGÉ avec la fenêtre de notes, contrairement aux deux managers
    // ci-dessus : la fenêtre garde le texte en mémoire, un second exemplaire lui masquerait ce
    // qui arrive du coffre. `ApplyRemote` ne marque rien comme modifié, donc aucune boucle.
    public CloudSyncService(Configuration configuration, string pluginConfigDir, NotesStore notesStore)
    {
        this.configuration = configuration;
        this.notesStore = notesStore;
        saveManager = new SaveManager(pluginConfigDir);
        templateManager = new TemplateManager(pluginConfigDir);
    }

    /// Vrai quand le plugin dispose d'un compte cloud et que l'utilisateur n'a pas coupé la synchro.
    public bool IsActive =>
        configuration.CloudSyncEnabled
        && !string.IsNullOrEmpty(configuration.MasterEventAccountId)
        && configuration.IsRgpdConsentValid;

    public string? LastError { get; private set; }
    public bool IsBusy { get; private set; }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
        syncGate.Dispose();
    }

    private string BaseUrl => configuration.RelayServerUrl
        .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
        .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
        .TrimEnd('/');

    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        request.Headers.Add("X-Leader-Token", configuration.EnsureLeaderToken());
        return request;
    }

    /// Message d'erreur lisible.
    private static string DescribeFailure(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.NotFound => Localization.Loc.Get("Cloud.Error.RelayTooOld"),
        System.Net.HttpStatusCode.Unauthorized => Localization.Loc.Get("Cloud.Error.Unauthorized"),
        System.Net.HttpStatusCode.TooManyRequests => Localization.Loc.Get("Cloud.Error.RateLimited"),
        System.Net.HttpStatusCode.InsufficientStorage => Localization.Loc.Get("Cloud.Error.Quota"),
        _ => $"HTTP {(int)status}",
    };

    // ─── Compte ──────────────────────────────────────────────────────────────

    public sealed record AccountInfo(string Identifier, string? Alias);
    private sealed record RegisterResponse(string? identifier, string? alias, long createdAt);

    /// Crée le compte cloud s'il n'existe pas encore et mémorise son identifiant.
    public async Task<AccountInfo?> EnsureAccountAsync(string? alias, CancellationToken token)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Post, "/api/account/register");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { alias }), Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastError = DescribeFailure(response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<RegisterResponse>(body);
            if (dto?.identifier is null)
            {
                LastError = "Réponse invalide du relay.";
                return null;
            }

            if (!string.Equals(configuration.MasterEventAccountId, dto.identifier, StringComparison.Ordinal))
            {
                configuration.MasterEventAccountId = dto.identifier;
                configuration.Save();
            }

            LastError = null;
            return new AccountInfo(dto.identifier, dto.alias);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Plugin.Log.Warning($"[CloudSync] Enregistrement du compte impossible : {ex.Message}");
            return null;
        }
    }

    public sealed record LinkCodeResult(string Code, DateTimeOffset ExpiresAt, string? Identifier);
    private sealed record LinkCodeResponse(string? code, DateTimeOffset expiresAt, string? identifier);

    /// Demande au relay un code de liaison Ashfall Connect. C'est le relay qui parle à Connect :
    /// le plugin ne détient aucun secret inter-services.
    public async Task<LinkCodeResult?> GenerateLinkCodeAsync(string? alias, CancellationToken token)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Post, "/api/connect/generate-link-code");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { alias }), Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                LastError = Localization.Loc.Get("Cloud.Error.ConnectDisabled");
                return null;
            }
            if (response.StatusCode == System.Net.HttpStatusCode.BadGateway)
            {
                LastError = Localization.Loc.Get("Cloud.Error.ConnectUnreachable");
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                LastError = DescribeFailure(response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<LinkCodeResponse>(body);
            if (dto?.code is null)
            {
                LastError = "Réponse invalide du relay.";
                return null;
            }

            if (!string.IsNullOrEmpty(dto.identifier)
                && !string.Equals(configuration.MasterEventAccountId, dto.identifier, StringComparison.Ordinal))
            {
                configuration.MasterEventAccountId = dto.identifier;
                configuration.Save();
            }

            LastError = null;
            return new LinkCodeResult(dto.code, dto.expiresAt, dto.identifier);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    public sealed record LinkStatus(string Status, string? LinkedTo);
    private sealed record LinkStatusResponse(string? status, string? linkedTo);

    public async Task<LinkStatus?> GetLinkStatusAsync(string code, CancellationToken token)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Get, $"/api/connect/link-status/{Uri.EscapeDataString(code)}");
            using var response = await httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<LinkStatusResponse>(body);
            return dto?.status is null ? null : new LinkStatus(dto.status, dto.linkedTo);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    public sealed record CloudStatus(bool ConnectEnabled, string? Identifier, bool Linked, string? Level);
    private sealed record MyStatusResponse(bool connectEnabled, string? identifier, bool linked, string? level);

    public async Task<CloudStatus?> GetStatusAsync(CancellationToken token)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Get, "/api/connect/my-status");
            using var response = await httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<MyStatusResponse>(body);
            return dto is null ? null : new CloudStatus(dto.connectEnabled, dto.identifier, dto.linked, dto.level);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    // ─── Synchronisation ─────────────────────────────────────────────────────

    private sealed class RemoteDocument
    {
        public string id { get; set; } = string.Empty;
        public string kind { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public JsonElement? data { get; set; }
        public int version { get; set; }
        public long updatedAt { get; set; }
        public bool deleted { get; set; }
    }

    private sealed class ListResponse
    {
        public long serverTime { get; set; }
        public List<RemoteDocument> documents { get; set; } = new();
    }

    /// Appelé après chaque sauvegarde locale : marque l'élément à pousser au prochain tick.
    public void QueuePush(string kind, string name)
    {
        if (!IsActive || string.IsNullOrWhiteSpace(name)) return;
        lock (pendingPushes)
            pendingPushes[(kind, name)] = DateTime.UtcNow;
    }

    public void QueueSheetPush(string name) => QueuePush(KindSheet, name);
    public void QueueTemplatePush(string name) => QueuePush(KindTemplate, name);

    /// Le bloc-notes est un document unique : son nom est fixe, pas besoin de le passer.
    public void QueueNotePush() => QueuePush(KindNote, NotesDocument.DefaultName);

    /// Signale une suppression locale : le tombstone distant empêchera l'élément de revenir
    /// au prochain pull.
    public void QueueDelete(string kind, string name)
    {
        if (!IsActive) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var request = BuildRequest(HttpMethod.Delete,
                    $"/api/cloud/documents/{kind}/{Uri.EscapeDataString(name)}");
                using var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                    Plugin.Log.Warning($"[CloudSync] Suppression distante refusée : HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[CloudSync] Suppression distante impossible : {ex.Message}");
            }
        });
    }

    /// Tick appelé par le plugin : pousse ce qui est en attente puis récupère les nouveautés.
    /// Les deux opérations sont espacées (`pullIntervalMinutes`) pour ne pas marteler le relay.
    public void Tick(int pullIntervalMinutes = 5)
    {
        if (!IsActive || IsBusy) return;

        var hasPending = false;
        lock (pendingPushes) hasPending = pendingPushes.Count > 0;

        var pullDue = (DateTime.UtcNow - lastPullAttempt).TotalMinutes >= pullIntervalMinutes;
        if (!hasPending && !pullDue) return;

        _ = SyncAsync(pull: pullDue);
    }

    /// Synchronisation complète, déclenchée aussi par le bouton « Synchroniser maintenant ».
    public async Task<bool> SyncAsync(bool pull = true)
    {
        if (!IsActive) return false;
        if (!await syncGate.WaitAsync(0).ConfigureAwait(false)) return false;

        IsBusy = true;
        cts?.Dispose();
        cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        try
        {
            await PushPendingAsync(cts.Token).ConfigureAwait(false);
            if (pull)
            {
                lastPullAttempt = DateTime.UtcNow;
                await PullAsync(cts.Token).ConfigureAwait(false);
            }
            LastError = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = Localization.Loc.Get("Cloud.Error.Timeout");
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Plugin.Log.Warning($"[CloudSync] Synchronisation échouée : {ex.Message}");
            return false;
        }
        finally
        {
            IsBusy = false;
            syncGate.Release();
        }
    }

    /// Envoie tout ce que le plugin a modifié depuis le dernier passage.
    private async Task PushPendingAsync(CancellationToken token)
    {
        List<(string kind, string name)> batch;
        lock (pendingPushes)
        {
            batch = pendingPushes.Keys.ToList();
            pendingPushes.Clear();
        }

        foreach (var (kind, name) in batch)
        {
            object? payload = kind switch
            {
                KindTemplate => templateManager.LoadTemplate(name),
                KindNote => notesStore.Snapshot(),
                _ => saveManager.LoadSheet(name),
            };

            // Élément disparu entre la mise en file et l'envoi : la suppression a son propre chemin.
            if (payload is null) continue;

            var json = JsonSerializer.Serialize(payload, JsonFileStore.DefaultOptions);
            if (ContainsSecretLikePattern(json))
            {
                Plugin.Log.Error($"[CloudSync] Garde-fou : contenu suspect dans '{name}', envoi abandonné.");
                continue;
            }

            using var request = BuildRequest(HttpMethod.Put,
                $"/api/cloud/documents/{kind}/{Uri.EscapeDataString(name)}");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                Plugin.Log.Warning($"[CloudSync] Envoi de '{name}' refusé : HTTP {(int)response.StatusCode}");
        }
    }

    /// Récupère les documents modifiés côté serveur depuis le dernier passage et les applique
    /// en local. Un tombstone supprime le fichier correspondant.
    private async Task PullAsync(CancellationToken token)
    {
        var since = configuration.CloudLastSyncAt;
        using var request = BuildRequest(HttpMethod.Get, $"/api/cloud/documents?since={since}");
        using var response = await httpClient.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LastError = DescribeFailure(response.StatusCode);
            return;
        }

        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var payload = JsonSerializer.Deserialize<ListResponse>(body);
        if (payload is null) return;

        var applied = 0;
        foreach (var doc in payload.documents)
        {
            if (doc.deleted)
            {
                if (doc.kind == KindTemplate) templateManager.DeleteTemplate(doc.name);
                // Un bloc-notes supprimé depuis le site se vide en jeu : il n'y a pas de fichier
                // à effacer, la fenêtre doit juste refléter l'état du coffre.
                else if (doc.kind == KindNote) notesStore.ApplyRemote(new NotesDocument());
                else saveManager.DeleteSheet(doc.name);
                applied++;
                continue;
            }

            if (doc.data is not { } data) continue;
            var raw = data.GetRawText();

            try
            {
                if (doc.kind == KindTemplate)
                {
                    var template = JsonSerializer.Deserialize<EventTemplate>(raw);
                    if (template is null) continue;
                    // Le nom du document fait autorité : c'est lui qui a servi de clé côté serveur.
                    template.Name = doc.name;
                    templateManager.SaveTemplate(template);
                }
                else if (doc.kind == KindNote)
                {
                    var note = JsonSerializer.Deserialize<NotesDocument>(raw);
                    if (note is null) continue;
                    notesStore.ApplyRemote(note);
                }
                else
                {
                    var sheet = JsonSerializer.Deserialize<PlayerSheet>(raw);
                    if (sheet is null) continue;
                    sheet.Name = doc.name;
                    saveManager.SaveSheet(sheet);
                }
                applied++;
            }
            catch (JsonException ex)
            {
                Plugin.Log.Warning($"[CloudSync] Document '{doc.name}' illisible, ignoré : {ex.Message}");
            }
        }

        // On mémorise l'horloge du serveur, pas la nôtre : les deux peuvent diverger.
        configuration.CloudLastSyncAt = payload.serverTime;
        configuration.Save();

        if (applied > 0)
            Plugin.Log.Information($"[CloudSync] {applied} élément(s) récupéré(s) depuis le cloud.");
    }

    /// Premier envoi complet après la liaison d'un compte : tout ce qui existe en local part
    /// vers le coffre, sinon le site s'ouvrirait sur une page vide.
    public async Task<int> PushEverythingAsync(CancellationToken token)
    {
        if (!IsActive) return 0;

        foreach (var name in templateManager.GetTemplateNames()) QueuePush(KindTemplate, name);
        foreach (var name in saveManager.GetSheetNames()) QueuePush(KindSheet, name);
        QueueNotePush();

        int count;
        lock (pendingPushes) count = pendingPushes.Count;

        await SyncAsync(pull: true).ConfigureAwait(false);
        return count;
    }

    private static readonly Regex Sha256Pattern =
        new(@"\b[0-9a-fA-F]{64}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    /// Garde-fou repris d'UmbraSync : rien qui ressemble à un secret ne doit quitter le plugin,
    /// même si une évolution des modèles venait à en introduire par inadvertance.
    private static bool ContainsSecretLikePattern(string json)
    {
        if (Sha256Pattern.IsMatch(json)) return true;

        var lowered = json.ToLowerInvariant();
        string[] forbidden = ["\"secretkey\"", "\"leadertoken\"", "\"password\"", "\"jwt\"", "\"bearer\""];
        foreach (var word in forbidden)
            if (lowered.Contains(word, StringComparison.Ordinal)) return true;

        return false;
    }
}
