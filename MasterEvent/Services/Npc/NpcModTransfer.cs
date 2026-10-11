using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MasterEvent.Models;

namespace MasterEvent.Services.Npc;


public sealed class NpcModTransfer
{
    private const int Attempts = 2;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Configuration configuration;
    private readonly SessionManager session;

    public NpcModTransfer(Configuration configuration, SessionManager session)
    {
        this.configuration = configuration;
        this.session = session;
    }

    public bool CanPublish => session.IsGm && session.IsConnected && !string.IsNullOrEmpty(session.RelayRoomKey);

    public bool CanDownload => session.IsConnected && !string.IsNullOrEmpty(session.RelayRoomKey)
                               && !string.IsNullOrEmpty(session.RoomFileToken);

    private string BaseUrl => configuration.RelayServerUrl
        .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
        .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
        .TrimEnd('/');

    private string UrlFor(string suffix)
        => $"{BaseUrl}/api/files/{Uri.EscapeDataString(session.RelayRoomKey ?? string.Empty)}/{suffix}";

    private HttpRequestMessage Request(HttpMethod method, string suffix, bool asLeader)
    {
        var request = new HttpRequestMessage(method, UrlFor(suffix));
        if (asLeader) request.Headers.Add("X-Leader-Token", configuration.EnsureLeaderToken());
        else request.Headers.Add("X-Room-Token", session.RoomFileToken ?? string.Empty);
        return request;
    }

    private static string Describe(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => "salle ou fichier introuvable sur le relais",
        HttpStatusCode.Forbidden => "accès refusé par le relais",
        HttpStatusCode.TooManyRequests => "trop de requêtes, réessayez plus tard",
        HttpStatusCode.RequestEntityTooLarge => "fichier trop volumineux pour le relais",
        HttpStatusCode.InsufficientStorage => "quota de fichiers de la salle atteint",
        _ => $"HTTP {(int)status}",
    };


    public async Task<string?> PublishAsync(string manifestId, byte[] manifestBytes, NpcModManifest manifest,
        NpcModStore store, Action<long, long>? progress, CancellationToken token)
    {
        if (!CanPublish) return "pas de salle active sur le relais";

        try
        {
            var hashes = manifest.Files.Select(f => f.Hash).Prepend(manifestId).ToList();
            var missing = await FindMissingAsync(hashes, token).ConfigureAwait(false);
            if (missing == null) return "le relais ne répond pas";

            var total = manifest.Files.Where(f => missing.Contains(f.Hash)).Sum(f => f.Size) + manifestBytes.Length;
            long done = 0;

            if (missing.Contains(manifestId))
            {
                var error = await UploadWithRetryAsync(manifestId, () => new ByteArrayContent(manifestBytes), token)
                    .ConfigureAwait(false);
                if (error != null) return error;
            }

            done += manifestBytes.Length;
            progress?.Invoke(done, total);

            foreach (var file in manifest.Files.Where(f => missing.Contains(f.Hash)))
            {
                if (!store.TryGetPath(file.Hash, out var path))
                    return $"fichier {file.Hash[..8]} absent du cache local";

                var error = await UploadWithRetryAsync(file.Hash, () =>
                {
                    var content = new StreamContent(File.OpenRead(path));
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    return content;
                }, token).ConfigureAwait(false);
                if (error != null) return error;

                done += file.Size;
                progress?.Invoke(done, total);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return "annulé";
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[NpcMods] Dépôt échoué : {ex.Message}");
            return ex.Message;
        }
    }

    private async Task<System.Collections.Generic.HashSet<string>?> FindMissingAsync(
        System.Collections.Generic.List<string> hashes, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "check", asLeader: true);
        request.Content = new StringContent(JsonSerializer.Serialize(new { hashes }), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Plugin.Log.Warning($"[NpcMods] Vérification des fichiers refusée : {Describe(response.StatusCode)}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var result = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("missing", out var array))
            foreach (var item in array.EnumerateArray())
                if (item.GetString() is { } hash) result.Add(hash);
        return result;
    }

    private async Task<string?> UploadWithRetryAsync(string hash, Func<HttpContent> content, CancellationToken token)
    {
        string? lastError = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                using var request = Request(HttpMethod.Put, hash, asLeader: true);
                request.Content = content();
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return null;

                lastError = Describe(response.StatusCode);
                // Un refus définitif ne se corrige pas en réessayant.
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.RequestEntityTooLarge
                    or HttpStatusCode.InsufficientStorage or HttpStatusCode.BadRequest) break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(1000 * attempt, token).ConfigureAwait(false);
        }

        return lastError ?? "échec du dépôt";
    }


    public async Task<(byte[]? Bytes, string? Error)> DownloadManifestAsync(string id, CancellationToken token)
    {
        try
        {
            using var request = Request(HttpMethod.Get, id, asLeader: false);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, Describe(response.StatusCode));

            if (response.Content.Headers.ContentLength is > NpcModManifest.MaxManifestBytes)
                return (null, "manifeste démesuré");

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > NpcModManifest.MaxManifestBytes) return (null, "manifeste démesuré");
            }

            return (buffer.ToArray(), null);
        }
        catch (OperationCanceledException)
        {
            return (null, "annulé");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    // Télécharge les fichiers du manifeste absents du cache. Retourne null en cas de succès.
    public async Task<string?> DownloadFilesAsync(NpcModManifest manifest, NpcModStore store,
        Action<long, long>? progress, CancellationToken token)
    {
        var pending = manifest.Files.Where(f => !store.Contains(f.Hash)).ToList();
        var total = pending.Sum(f => f.Size);
        long done = 0;

        foreach (var file in pending)
        {
            var error = await DownloadFileAsync(file, store, token).ConfigureAwait(false);
            if (error != null) return error;

            done += file.Size;
            progress?.Invoke(done, total);
        }

        return null;
    }

    private async Task<string?> DownloadFileAsync(NpcModManifest.ModFile file, NpcModStore store, CancellationToken token)
    {
        string? lastError = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var temp = store.NewTempPath();
            try
            {
                using var request = Request(HttpMethod.Get, file.Hash, asLeader: false);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = Describe(response.StatusCode);
                    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) break;
                    continue;
                }

                await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var dest = File.Create(temp))
                {
                    var buffer = new byte[128 * 1024];
                    long written = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        written += read;
                        // Le manifeste a annoncé la taille : un flux plus long est refusé.
                        if (written > file.Size) throw new InvalidDataException("fichier plus gros qu'annoncé");
                        await dest.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                }

                if (store.TryAdopt(file.Hash, NpcModManifest.ExtensionFor(file), temp)) return null;
                lastError = "empreinte du fichier invalide";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* déjà adopté ou verrouillé */ }
            }
        }

        return lastError ?? "échec du téléchargement";
    }
}
