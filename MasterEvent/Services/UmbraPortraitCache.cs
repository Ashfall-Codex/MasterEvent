using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Textures.TextureWraps;
using MasterEvent.Models;

namespace MasterEvent.Services;

public sealed class UmbraPortraitCache : IDisposable
{
    private static readonly TimeSpan UnusedLifetime = TimeSpan.FromMinutes(5);
    // UmbraSync se connecte quelques secondes après le personnage et ne déclare une paire visible
    // qu'après ses propres scans : un refus n'est souvent que provisoire.
    private static readonly TimeSpan MissingRetryDelay = TimeSpan.FromSeconds(2.5);
    // Chaque essai relit l'image chez UmbraSync, et une image illisible le restera sans doute.
    private static readonly TimeSpan DecodeRetryDelay = TimeSpan.FromSeconds(30);

    // Le premier portrait lu d'un profil est décodé chez UmbraSync sur notre thread : une foule qui
    // devient visible d'un coup se répartit sur quelques images.
    private const int RequestsPerFrame = 2;

    // Identifiant que le jeu donne à un objet absent, et que le menu contextuel transmet tel quel.
    private const uint InvalidObjectId = 0xE0000000;

    // Le second écran envoie l'image entière dans une data URI, à chaque page et à chaque changement.
    private const int MaxImageBytes = 1024 * 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static readonly Dictionary<string, uint> NameIndex = new(StringComparer.Ordinal);
    private static DateTime nameIndexFrame = DateTime.MinValue;

    private readonly UmbraProfileIpc profiles;
    private readonly Dictionary<uint, Entry> entries = new();
    private DateTime requestFrame = DateTime.MinValue;
    private int requestsThisFrame;
    private int imageVersions;

    public UmbraPortraitCache(UmbraProfileIpc profiles)
    {
        this.profiles = profiles;
        Plugin.ClientState.Logout += OnLogout;
    }

    private sealed class Entry
    {
        public IDalamudTextureWrap? Texture;
        public Task<IDalamudTextureWrap>? Loading;
        public DateTime LastUsed = DateTime.UtcNow;
        public DateTime NextAttempt = DateTime.MinValue;
        public string? Reported;
        public byte[]? Image;
        public string? ImageMime;
        public int ImageVersion;
    }

    public static bool IsValidObjectId(uint objectId)
        => objectId is not (0 or InvalidObjectId or uint.MaxValue);

    public IDalamudTextureWrap? Get(uint objectId)
    {
        if (!IsValidObjectId(objectId)) return null;

        if (!entries.TryGetValue(objectId, out var entry))
        {
            entry = new Entry();
            entries[objectId] = entry;
        }

        var now = DateTime.UtcNow;
        entry.LastUsed = now;
        if (entry.Texture != null) return entry.Texture;

        if (entry.Loading is { } loading)
        {
            if (!loading.IsCompleted) return null;

            entry.Loading = null;
            if (loading.IsCompletedSuccessfully)
            {
                entry.Texture = loading.Result;
                entry.Reported = null;
                return entry.Texture;
            }

            Report(objectId, entry, "decode",
                $"image illisible ({loading.Exception?.GetBaseException().Message ?? "annulé"})");
            entry.NextAttempt = now + DecodeRetryDelay;
            return null;
        }

        if (now < entry.NextAttempt || !TakeRequest()) return null;
        entry.NextAttempt = now + MissingRetryDelay;

        var result = profiles.GetPortrait(objectId);
        if (result.Bytes == null)
        {
            Report(objectId, entry, result.Status.ToString(), Describe(result));
            return null;
        }

        KeepImage(entry, result.Bytes);
        try
        {
            entry.Loading = Plugin.TextureProvider.CreateFromImageAsync(result.Bytes);
        }
        // Un décodage raté arrive par la tâche, traitée plus haut. L'appel lui-même ne lève que si le service
        // de textures est libéré (ObjectDisposedException) ou pas encore prêt.
        catch (InvalidOperationException ex)
        {
            Report(objectId, entry, "decode", $"image illisible ({ex.Message})");
            entry.NextAttempt = now + DecodeRetryDelay;
        }

        return null;
    }

    private bool TakeRequest()
    {
        var frame = Plugin.Framework.LastUpdateUTC;
        if (frame != requestFrame)
        {
            requestFrame = frame;
            requestsThisFrame = 0;
        }

        if (requestsThisFrame >= RequestsPerFrame) return false;
        requestsThisFrame++;
        return true;
    }

    // Sans appel IPC : les octets du dernier portrait qu'un Get a reçu, avant même leur décodage en texture.
    public bool TryGetImage(uint objectId, [NotNullWhen(true)] out byte[]? bytes, [NotNullWhen(true)] out string? mime,
        out int version)
    {
        if (entries.TryGetValue(objectId, out var entry) && entry is { Image: { } image, ImageMime: { } type })
        {
            bytes = image;
            mime = type;
            version = entry.ImageVersion;
            return true;
        }

        bytes = null;
        mime = null;
        version = 0;
        return false;
    }

    public IDalamudTextureWrap? GetForPlayer(PlayerData player, string? localPlayerHash)
        => Get(ObjectIdFor(player.Hash, player.Name, localPlayerHash));

    // Soi par son propre personnage : le nom que porte la fiche n'est pas forcément celui de la table.
    public static uint ObjectIdFor(string hash, string name, string? localPlayerHash)
    {
        if (!string.IsNullOrEmpty(localPlayerHash) && hash == localPlayerHash
            && Plugin.ObjectTable.LocalPlayer is { } local)
            return local.EntityId;

        return ResolveObjectId(name);
    }

    public static uint ResolveObjectId(string playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return 0;

        // Une table par tick du jeu : chaque ligne de joueur la consulte à chaque image.
        var frame = Plugin.Framework.LastUpdateUTC;
        if (frame != nameIndexFrame)
        {
            nameIndexFrame = frame;
            NameIndex.Clear();
            foreach (var obj in Plugin.ObjectTable.PlayerObjects)
            {
                if (obj is IPlayerCharacter pc)
                    NameIndex.TryAdd(pc.Name.TextValue, pc.EntityId);
            }
        }

        return NameIndex.GetValueOrDefault(playerName);
    }

    public static (Vector2 Uv0, Vector2 Uv1) CoverUv(float textureWidth, float textureHeight,
        float targetWidth, float targetHeight)
    {
        if (textureWidth <= 0f || textureHeight <= 0f || targetWidth <= 0f || targetHeight <= 0f)
            return (Vector2.Zero, Vector2.One);

        var textureRatio = textureWidth / textureHeight;
        var targetRatio = targetWidth / targetHeight;

        if (Math.Abs(textureRatio - targetRatio) < 0.001f)
            return (Vector2.Zero, Vector2.One);

        if (textureRatio > targetRatio)
        {
            var margin = (1f - targetRatio / textureRatio) / 2f;
            return (new Vector2(margin, 0f), new Vector2(1f - margin, 1f));
        }

        var marginY = (1f - textureRatio / targetRatio) / 2f;
        return (new Vector2(0f, marginY), new Vector2(1f, 1f - marginY));
    }

    public void PruneUnused()
    {
        if (entries.Count == 0) return;

        var cutoff = DateTime.UtcNow - UnusedLifetime;
        foreach (var (id, entry) in entries.Where(e => e.Value.LastUsed < cutoff).ToList())
        {
            Release(entry);
            entries.Remove(id);
        }
    }

    public void Dispose()
    {
        Plugin.ClientState.Logout -= OnLogout;
        foreach (var entry in entries.Values)
            Release(entry);
        entries.Clear();
    }

    // Un refus valait pour la connexion d'UmbraSync qui s'achève : on repart de zéro au prochain personnage.
    private void OnLogout(int type, int code)
    {
        foreach (var (id, entry) in entries.Where(e => e.Value.Texture == null).ToList())
        {
            Release(entry);
            entries.Remove(id);
        }
    }

    private void KeepImage(Entry entry, byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        var mime = ImageMime(span);
        if (mime == null)
        {
            entry.Image = null;
            entry.ImageMime = null;
            return;
        }

        // UmbraSync peut rendre un nouveau tableau aux mêmes octets : la page ne recharge que ce qui a changé.
        if (entry.Image != null && span.SequenceEqual(entry.Image)) return;
        entry.Image = bytes;
        entry.ImageMime = mime;
        entry.ImageVersion = ++imageVersions;
    }

    // PNG et JPEG seulement, les deux formats qu'UmbraSync accepte : rien d'autre n'atteint la page.
    private static string? ImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxImageBytes) return null;
        if (bytes.StartsWith(PngSignature)) return "image/png";
        return bytes.StartsWith(JpegSignature) ? "image/jpeg" : null;
    }

    private static void Release(Entry entry)
    {
        entry.Texture?.Dispose();
        entry.Texture = null;
        entry.Image = null;
        entry.ImageMime = null;

        // Un décodage en cours finirait sans propriétaire : sa texture est libérée à l'arrivée.
        if (entry.Loading is { } loading) _ = DisposeWhenLoadedAsync(loading);
        entry.Loading = null;
    }

    private static async Task DisposeWhenLoadedAsync(Task<IDalamudTextureWrap> loading)
    {
        // Sur le pool de threads, jamais pendant Release ; un échec est seulement marqué comme observé.
        await ((Task)loading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
        if (loading.IsCompletedSuccessfully) (await loading.ConfigureAwait(false)).Dispose();
        else _ = loading.Exception;
    }

    private static void Report(uint objectId, Entry entry, string state, string reason)
    {
        if (entry.Reported == state) return;
        entry.Reported = state;

        var name = Plugin.ObjectTable.SearchByEntityId(objectId)?.Name.TextValue;
        var who = string.IsNullOrEmpty(name) ? $"{objectId:X8}" : $"{name} ({objectId:X8})";
        Plugin.Log.Debug($"[MasterEvent] Portrait Umbra absent pour {who} : {reason}");
    }

    private static string Describe(UmbraPortraitResult result) => result.Status switch
    {
        UmbraPortraitStatus.Unavailable => "UmbraSync absent ou d'une autre version",
        UmbraPortraitStatus.NotPaired => "non appairé, en pause ou pas encore visible pour UmbraSync",
        UmbraPortraitStatus.Empty => "portrait vide ou profil encore en chargement",
        _ => $"appel IPC en échec ({result.Error})",
    };
}
