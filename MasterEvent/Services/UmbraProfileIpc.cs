using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace MasterEvent.Services;

/// Fiche d'identité RP d'un joueur, telle qu'UmbraSync la publie.
public sealed record UmbraRpProfile
{
    [JsonPropertyName("firstName")] public string? FirstName { get; init; }
    [JsonPropertyName("lastName")] public string? LastName { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("age")] public string? Age { get; init; }
    [JsonPropertyName("race")] public string? Race { get; init; }
    [JsonPropertyName("ethnicity")] public string? Ethnicity { get; init; }
    [JsonPropertyName("height")] public string? Height { get; init; }
    [JsonPropertyName("build")] public string? Build { get; init; }
    [JsonPropertyName("residence")] public string? Residence { get; init; }
    [JsonPropertyName("occupation")] public string? Occupation { get; init; }
    [JsonPropertyName("affiliation")] public string? Affiliation { get; init; }
    [JsonPropertyName("alignment")] public string? Alignment { get; init; }
    [JsonPropertyName("additionalInfo")] public string? AdditionalInfo { get; init; }
    [JsonPropertyName("nameColor")] public string? NameColor { get; init; }
    [JsonPropertyName("isNsfw")] public bool IsNsfw { get; init; }
    [JsonPropertyName("customFields")] public List<UmbraRpField>? CustomFields { get; init; }
}

public sealed record UmbraRpField
{
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;
}


public enum UmbraPortraitStatus
{
    Found,
    Unavailable,
    NotPaired,
    Empty,
    Failed,
}

public readonly record struct UmbraPortraitResult(byte[]? Bytes, UmbraPortraitStatus Status, string? Error = null);

public sealed class UmbraProfileIpc
{
    private const int SupportedVersion = 1;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);
    // Plugin absent, l'appel lève une exception : une réponse par intervalle suffit pour tous les joueurs.
    private static readonly TimeSpan AvailabilityLifetime = TimeSpan.FromSeconds(2);
    private readonly ICallGateSubscriber<int> version;
    private readonly ICallGateSubscriber<uint, bool> isPaired;
    private readonly ICallGateSubscriber<uint, string?> getProfile;
    private readonly ICallGateSubscriber<uint, byte[]?> getPortrait;
    private readonly Dictionary<uint, (DateTime At, UmbraRpProfile? Value)> profileCache = new();
    private DateTime availabilityCheckedAt = DateTime.MinValue;
    private bool available;

    public UmbraProfileIpc(IDalamudPluginInterface pluginInterface)
    {
        version = pluginInterface.GetIpcSubscriber<int>("UmbraSync.Profile.Version");
        isPaired = pluginInterface.GetIpcSubscriber<uint, bool>("UmbraSync.Profile.IsPaired");
        getProfile = pluginInterface.GetIpcSubscriber<uint, string?>("UmbraSync.Profile.GetRpProfile");
        getPortrait = pluginInterface.GetIpcSubscriber<uint, byte[]?>("UmbraSync.Profile.GetRpPortrait");
    }

    /// Vrai si UmbraSync répond et parle la même version que nous.
    public bool IsAvailable
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now - availabilityCheckedAt < AvailabilityLifetime) return available;
            availabilityCheckedAt = now;

            try
            {
                available = version.InvokeFunc() == SupportedVersion;
            }
            catch
            {
                // Plugin absent, point d'entrée non enregistré : cas normal, pas une erreur.
                available = false;
            }

            return available;
        }
    }

    public UmbraRpProfile? GetProfile(uint objectId)
    {
        if (!UmbraPortraitCache.IsValidObjectId(objectId)) return null;
        if (Fresh(profileCache, objectId, out var cached)) return cached.Value;

        UmbraRpProfile? profile = null;
        try
        {
            if (IsAvailable && isPaired.InvokeFunc(objectId))
            {
                var json = getProfile.InvokeFunc(objectId);
                if (!string.IsNullOrEmpty(json))
                    profile = JsonSerializer.Deserialize<UmbraRpProfile>(json);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[MasterEvent] Profil Umbra indisponible : {ex.Message}");
        }

        profileCache[objectId] = (DateTime.UtcNow, profile);
        return profile;
    }

    /// Portrait RP, sans cache ici : UmbraPortraitCache en règle le rythme, garde la texture plutôt que
    /// les octets et journalise une seule fois la raison d'une absence.
    public UmbraPortraitResult GetPortrait(uint objectId)
    {
        if (!UmbraPortraitCache.IsValidObjectId(objectId)) return new UmbraPortraitResult(null, UmbraPortraitStatus.Empty);

        try
        {
            if (!IsAvailable) return new UmbraPortraitResult(null, UmbraPortraitStatus.Unavailable);
            if (!isPaired.InvokeFunc(objectId)) return new UmbraPortraitResult(null, UmbraPortraitStatus.NotPaired);

            // Octets bruts plutôt que base64 : les deux plugins vivent dans le même
            // processus et partagent les types du framework, l'encodage ne servirait
            // qu'à gonfler d'un tiers une image déjà décodée en face.
            var bytes = getPortrait.InvokeFunc(objectId);
            return bytes is { Length: > 0 }
                ? new UmbraPortraitResult(bytes, UmbraPortraitStatus.Found)
                : new UmbraPortraitResult(null, UmbraPortraitStatus.Empty);
        }
        catch (Exception ex)
        {
            return new UmbraPortraitResult(null, UmbraPortraitStatus.Failed, ex.Message);
        }
    }

    private static bool Fresh<T>(Dictionary<uint, (DateTime At, T Value)> cache, uint key, out (DateTime At, T Value) entry)
    {
        if (cache.TryGetValue(key, out entry) && DateTime.UtcNow - entry.At < CacheLifetime)
            return true;

        entry = default;
        return false;
    }
}
