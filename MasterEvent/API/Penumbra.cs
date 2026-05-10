using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using global::Penumbra.Api.Enums;
using global::Penumbra.Api.Helpers;
using global::Penumbra.Api.IpcSubscribers;

namespace MasterEvent.API;

public sealed class Penumbra : IDisposable
{
    private readonly IPluginLog log;

    private readonly ApiVersion apiVersion;
    private readonly GetEnabledState enabledState;
    private readonly GetCollections getCollections;
    private readonly GetCollection getCollection;
    private readonly SetCollection setCollection;
    private readonly GetCollectionForObject getCollectionForObject;
    private readonly CreateTemporaryCollection createTempCollection;
    private readonly DeleteTemporaryCollection deleteTempCollection;
    private readonly AssignTemporaryCollection assignTempCollection;
    private readonly AddTemporaryMod addTemporaryMod;
    private readonly GetGameObjectResourcePaths getResourcePaths;
    private readonly RedrawObject redrawObject;

    private readonly EventSubscriber initializedSub;
    private readonly EventSubscriber disposedSub;

    public bool Available { get; private set; }
    public (int Breaking, int Features) Version { get; private set; }

    public event Action? OnAvailabilityChanged;

    public Penumbra(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;

        apiVersion = new ApiVersion(pi);
        enabledState = new GetEnabledState(pi);
        getCollections = new GetCollections(pi);
        getCollection = new GetCollection(pi);
        setCollection = new SetCollection(pi);
        getCollectionForObject = new GetCollectionForObject(pi);
        createTempCollection = new CreateTemporaryCollection(pi);
        deleteTempCollection = new DeleteTemporaryCollection(pi);
        assignTempCollection = new AssignTemporaryCollection(pi);
        addTemporaryMod = new AddTemporaryMod(pi);
        getResourcePaths = new GetGameObjectResourcePaths(pi);
        redrawObject = new RedrawObject(pi);

        // Penumbra suit le même pattern d'événements que Glamourer.
        initializedSub = Initialized.Subscriber(pi, OnPenumbraInitialized);
        disposedSub = Disposed.Subscriber(pi, OnPenumbraDisposed);
        initializedSub.Enable();
        disposedSub.Enable();

        RefreshAvailability();
    }

    private void OnPenumbraInitialized() => RefreshAvailability();

    private void OnPenumbraDisposed()
    {
        if (!Available) return;
        Available = false;
        Version = (0, 0);
        log.Info("[MasterEvent] Penumbra s'est déchargé : pont désactivé.");
        OnAvailabilityChanged?.Invoke();
    }

    public void RefreshAvailability()
    {
        var prev = Available;
        var newAvailable = false;
        var newVersion = (Breaking: 0, Features: 0);
        try
        {
            newVersion = apiVersion.Invoke();
            newAvailable = newVersion.Breaking >= 5 && enabledState.Invoke();
        }
        catch
        {
            // Plugin non chargé ou IPC pas encore enregistré : silencieux.
        }

        Available = newAvailable;
        Version = newVersion;
        if (Available != prev)
        {
            if (Available)
                log.Info($"[MasterEvent] Penumbra détecté (API v{newVersion.Breaking}.{newVersion.Features}).");
            OnAvailabilityChanged?.Invoke();
        }
    }

    public Guid TrySyncFromLocalPlayer(ushort pnjObjectIndex, ushort localPlayerObjectIndex,
        string identityTag, out string? error)
    {
        error = null;
        if (!Available)
        {
            error = "Penumbra indisponible.";
            return Guid.Empty;
        }

        // créer la temp collection. Penumbra retourne son GUID via out.
        Guid tempCollId;
        try
        {
            var ec = createTempCollection.Invoke(identityTag, identityTag, out tempCollId);
            log.Info($"[MasterEvent] Penumbra.CreateTemporaryCollection('{identityTag}') → {ec} ({tempCollId})");
            if (ec != PenumbraApiEc.Success || tempCollId == Guid.Empty)
            {
                error = $"Création de temp collection refusée (code {ec}).";
                return Guid.Empty;
            }
        }
        catch (Exception ex)
        {
            error = $"Erreur Penumbra (CreateTemporaryCollection) : {ex.Message}";
            log.Warning($"[MasterEvent] {error}");
            return Guid.Empty;
        }

        Dictionary<string, string> modPaths;
        try
        {
            var arr = getResourcePaths.Invoke(localPlayerObjectIndex);
            var playerPaths = arr is { Length: > 0 } ? arr[0] : null;
            if (playerPaths == null || playerPaths.Count == 0)
            {
                log.Info($"[MasterEvent] Penumbra : aucun resource path pour le joueur local (idx={localPlayerObjectIndex}). Temp collection laissée vide.");
                modPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            else
            {
                modPaths = new Dictionary<string, string>(playerPaths.Count, StringComparer.Ordinal);
                foreach (var kv in playerPaths)
                {
                    var resolved = kv.Value.FirstOrDefault();
                    if (!string.IsNullOrEmpty(resolved))
                        modPaths[kv.Key] = resolved;
                }
            }
        }
        catch (Exception ex)
        {
            error = $"Erreur Penumbra (GetGameObjectResourcePaths) : {ex.Message}";
            log.Warning($"[MasterEvent] {error}");
            // Cleanup : on a déjà créé la collection
            TryDeleteTempCollection(tempCollId);
            return Guid.Empty;
        }


        if (modPaths.Count > 0)
        {
            try
            {
                var ec = addTemporaryMod.Invoke("MasterEventFiles", tempCollId, modPaths,
                    manipString: string.Empty, priority: 0);
                log.Info($"[MasterEvent] Penumbra.AddTemporaryMod({modPaths.Count} paths) → {ec}");
                if (ec != PenumbraApiEc.Success)
                    log.Warning($"[MasterEvent] Penumbra.AddTemporaryMod a renvoyé {ec}, la temp collection sera assignée vide.");
            }
            catch (Exception ex)
            {
                log.Warning($"[MasterEvent] Penumbra.AddTemporaryMod a levé : {ex.Message}");
            }
        }

        // Étape 4 : assigner la temp collection au PNJ.
        try
        {
            var ec = assignTempCollection.Invoke(tempCollId, pnjObjectIndex, forceAssignment: true);
            log.Info($"[MasterEvent] Penumbra.AssignTemporaryCollection(coll={tempCollId}, idx={pnjObjectIndex}) → {ec}");
            if (ec != PenumbraApiEc.Success)
            {
                error = $"Penumbra a refusé l'assignation (code {ec}).";
                TryDeleteTempCollection(tempCollId);
                return Guid.Empty;
            }
        }
        catch (Exception ex)
        {
            error = $"Erreur Penumbra (AssignTemporaryCollection) : {ex.Message}";
            log.Warning($"[MasterEvent] {error}");
            TryDeleteTempCollection(tempCollId);
            return Guid.Empty;
        }

        return tempCollId;
    }

    // Crée une temp collection Penumbra par identité+nom.
    public Guid TryCreateTempCollection(string identity, string name, out string? error)
    {
        error = null;
        if (!Available)
        {
            error = "Penumbra indisponible.";
            return Guid.Empty;
        }
        try
        {
            var ec = createTempCollection.Invoke(identity, name, out var collId);
            log.Info($"[MasterEvent] Penumbra.CreateTemporaryCollection('{identity}', '{name}') → {ec} ({collId})");
            if (ec != PenumbraApiEc.Success || collId == Guid.Empty)
            {
                error = $"Penumbra a refusé la création (code {ec}).";
                return Guid.Empty;
            }
            return collId;
        }
        catch (Exception ex)
        {
            error = $"Erreur Penumbra : {ex.Message}";
            log.Warning($"[MasterEvent] Penumbra.CreateTemporaryCollection a levé : {ex}");
            return Guid.Empty;
        }
    }

    // Ajoute un set de paires (gamePath → fichierLocal)
    public bool TryAddTempMod(string tag, Guid collId, Dictionary<string, string> paths, string manipString, int priority, out string? error)
    {
        error = null;
        if (!Available)
        {
            error = "Penumbra indisponible.";
            return false;
        }
        try
        {
            var ec = addTemporaryMod.Invoke(tag, collId, paths, manipString, priority);
            log.Info($"[MasterEvent] Penumbra.AddTemporaryMod(tag='{tag}', coll={collId}, {paths.Count} paths) → {ec}");
            if (ec != PenumbraApiEc.Success)
            {
                error = $"Penumbra a refusé l'ajout (code {ec}).";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Erreur Penumbra : {ex.Message}";
            log.Warning($"[MasterEvent] Penumbra.AddTemporaryMod a levé : {ex}");
            return false;
        }
    }

    // Lie une temp collection à un acteur ciblé par son index global.
    public bool TryAssignTempCollectionToActor(Guid tempCollId, ushort objectIndex, out string? error)
    {
        error = null;
        if (!Available)
        {
            error = "Penumbra indisponible.";
            return false;
        }
        try
        {
            var ec = assignTempCollection.Invoke(tempCollId, objectIndex, forceAssignment: true);
            log.Info($"[MasterEvent] Penumbra.AssignTemporaryCollection(coll={tempCollId}, idx={objectIndex}) → {ec}");
            if (ec != PenumbraApiEc.Success)
            {
                error = $"Penumbra a refusé l'assignation (code {ec}).";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Erreur Penumbra : {ex.Message}";
            log.Warning($"[MasterEvent] Penumbra.AssignTemporaryCollection a levé : {ex}");
            return false;
        }
    }

    // Cleanup d'une temp collection MasterEvent.
    public void TryDeleteTempCollection(Guid tempCollId)
    {
        if (!Available || tempCollId == Guid.Empty) return;
        try
        {
            var ec = deleteTempCollection.Invoke(tempCollId);
            log.Info($"[MasterEvent] Penumbra.DeleteTemporaryCollection({tempCollId}) → {ec}");
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.DeleteTemporaryCollection a levé : {ex}");
        }
    }

    public void LogResolvedCollection(ushort objectIndex, string contextLabel)
    {
        if (!Available) return;
        try
        {
            var (objectValid, individualSet, eff) = getCollectionForObject.Invoke(objectIndex);
            log.Info(
                $"[MasterEvent] Penumbra.GetCollectionForObject({contextLabel}, idx={objectIndex}) → "
                + $"objectValid={objectValid}, individualSet={individualSet}, "
                + $"effective='{eff.Name}' ({eff.Id})");
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.GetCollectionForObject a levé : {ex.Message}");
        }
    }

    public void LogResolvedPaths(ushort objectIndex, string contextLabel)
    {
        if (!Available) return;
        try
        {
            var arr = getResourcePaths.Invoke(objectIndex);
            var paths = arr is { Length: > 0 } ? arr[0] : null;
            if (paths == null)
            {
                log.Info($"[MasterEvent] Penumbra.GetGameObjectResourcePaths({contextLabel}, idx={objectIndex}) → null (acteur introuvable côté Penumbra)");
                return;
            }
            var modded = 0;
            foreach (var kv in paths)
            {
                foreach (var resolved in kv.Value)
                    if (!string.Equals(resolved, kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        modded++;
                        break;
                    }
            }
            log.Info($"[MasterEvent] Penumbra.GetGameObjectResourcePaths({contextLabel}, idx={objectIndex}) → {paths.Count} paths total, {modded} moddés.");
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.GetGameObjectResourcePaths a levé : {ex.Message}");
        }
    }

    // Force un redraw du PNJ par index global.
    public void Redraw(ushort objectIndex)
    {
        if (!Available) return;
        try
        {
            redrawObject.Invoke(objectIndex, RedrawType.Redraw);
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.RedrawObject a levé : {ex}");
        }
    }

    public IReadOnlyList<CollectionEntry> ListCollections()
    {
        if (!Available) return Array.Empty<CollectionEntry>();
        try
        {
            var dict = getCollections.Invoke();
            return dict
                .Select(kv => new CollectionEntry(kv.Key, kv.Value))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.GetCollections a échoué : {ex.Message}");
            return Array.Empty<CollectionEntry>();
        }
    }

    public Guid? GetCollectionId(ApiCollectionType type)
    {
        if (!Available) return null;
        try
        {
            var coll = getCollection.Invoke(type);
            return coll?.Id;
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.GetCollection({type}) a levé : {ex.Message}");
            return null;
        }
    }

    public (Guid Id, string Name)? GetCollectionInfo(ApiCollectionType type)
    {
        if (!Available) return null;
        try
        {
            var coll = getCollection.Invoke(type);
            return coll;
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.GetCollection({type}) a levé : {ex.Message}");
            return null;
        }
    }

    public (Guid Id, string Name)? GetEffectiveCollectionForObject(ushort objectIndex)
    {
        if (!Available) return null;
        try
        {
            var (objectValid, _, effective) = getCollectionForObject.Invoke(objectIndex);
            if (!objectValid) return null;
            return (effective.Id, effective.Name);
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.GetCollectionForObject({objectIndex}) a levé : {ex.Message}");
            return null;
        }
    }

    public bool TrySetCollectionForGroup(ApiCollectionType type, Guid? collectionId, out string? error)
    {
        error = null;
        if (!Available)
        {
            error = "Penumbra indisponible.";
            return false;
        }

        try
        {
            var (ec, _) = setCollection.Invoke(type, collectionId,
                allowCreateNew: true, allowDelete: true);
            log.Info($"[MasterEvent] Penumbra.SetCollection({type}, {collectionId?.ToString() ?? "null"}) → {ec}");
            if (ec != PenumbraApiEc.Success && ec != PenumbraApiEc.NothingChanged)
            {
                error = $"Penumbra a refusé l'assignation (code {ec}).";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Erreur Penumbra : {ex.Message}";
            log.Warning($"[MasterEvent] Penumbra.SetCollection a levé : {ex}");
            return false;
        }
    }

    public void Dispose()
    {
        initializedSub.Dispose();
        disposedSub.Dispose();
    }

    public sealed record CollectionEntry(Guid Id, string Name);
}
