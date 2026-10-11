using System;
using System.Collections.Generic;
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
    private readonly CreateTemporaryCollection createCollection;
    private readonly DeleteTemporaryCollection deleteCollection;
    private readonly AssignTemporaryCollection assignCollection;
    private readonly AddTemporaryMod addTemporaryMod;
    private readonly GetCollectionForObject collectionForObject;
    private readonly RedrawObject redrawObject;
    private readonly GetGameObjectResourcePaths resourcePaths;
    private readonly GetPlayerMetaManipulations playerMeta;
    private readonly EventSubscriber initializedSub;
    private readonly EventSubscriber disposedSub;

    public bool Available { get; private set; }

    public Penumbra(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        apiVersion = new ApiVersion(pi);
        enabledState = new GetEnabledState(pi);
        createCollection = new CreateTemporaryCollection(pi);
        deleteCollection = new DeleteTemporaryCollection(pi);
        assignCollection = new AssignTemporaryCollection(pi);
        addTemporaryMod = new AddTemporaryMod(pi);
        collectionForObject = new GetCollectionForObject(pi);
        redrawObject = new RedrawObject(pi);
        resourcePaths = new GetGameObjectResourcePaths(pi);
        playerMeta = new GetPlayerMetaManipulations(pi);
        initializedSub = Initialized.Subscriber(pi, Refresh);
        disposedSub = Disposed.Subscriber(pi, () => Available = false);
        initializedSub.Enable();
        disposedSub.Enable();
        Refresh();
    }

    public void Refresh()
    {
        try
        {
            var version = apiVersion.Invoke();
            Available = version.Breaking >= 5 && enabledState.Invoke();
        }
        catch
        {
            Available = false;
        }
    }

    public Guid CreateCollection(string name)
    {
        if (!Available) return Guid.Empty;
        try
        {
            var code = createCollection.Invoke(name, name, out var id);
            if (code == PenumbraApiEc.Success && id != Guid.Empty) return id;
            log.Warning($"[MasterEvent] Penumbra.CreateTemporaryCollection : {code}");
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.CreateTemporaryCollection a levé : {ex.Message}");
        }

        return Guid.Empty;
    }

    public bool AddMod(string tag, Guid collection, Dictionary<string, string> paths, string manipulations)
    {
        try
        {
            var code = addTemporaryMod.Invoke(tag, collection, paths, manipulations, 0);
            if (code == PenumbraApiEc.Success) return true;
            log.Warning($"[MasterEvent] Penumbra.AddTemporaryMod({tag}) : {code}");
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.AddTemporaryMod a levé : {ex.Message}");
        }

        return false;
    }

    public bool Assign(Guid collection, ushort objectIndex)
    {
        try
        {
            var code = assignCollection.Invoke(collection, objectIndex, forceAssignment: true);
            if (code == PenumbraApiEc.Success) return true;
            log.Warning($"[MasterEvent] Penumbra.AssignTemporaryCollection(idx={objectIndex}) : {code}");
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.AssignTemporaryCollection a levé : {ex.Message}");
        }

        return false;
    }

    // Collection effectivement résolue pour l'objet, ou Guid.Empty si l'objet est inconnu.
    public Guid EffectiveCollection(ushort objectIndex)
    {
        try
        {
            var (valid, _, effective) = collectionForObject.Invoke(objectIndex);
            return valid ? effective.Id : Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
        }
    }

    // Ressources chargées par l'objet : « chemin résolu (fichier local ou chemin du jeu) » vers
    // l'ensemble des chemins du jeu qui l'utilisent.
    public Dictionary<string, HashSet<string>>? GetResourcePaths(ushort objectIndex)
    {
        if (!Available) return null;
        try
        {
            var result = resourcePaths.Invoke(objectIndex);
            return result.Length > 0 ? result[0] : null;
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.GetGameObjectResourcePaths a levé : {ex.Message}");
            return null;
        }
    }

    // Manipulations (méta) actives pour le joueur local.
    public string GetPlayerMetaManipulations()
    {
        if (!Available) return string.Empty;
        try
        {
            return playerMeta.Invoke() ?? string.Empty;
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Penumbra.GetPlayerMetaManipulations a levé : {ex.Message}");
            return string.Empty;
        }
    }

    public void Redraw(ushort objectIndex)
    {
        if (!Available) return;
        try { redrawObject.Invoke(objectIndex, RedrawType.Redraw); }
        catch (Exception ex) { log.Warning($"[MasterEvent] Penumbra.RedrawObject a levé : {ex.Message}"); }
    }

    public void DeleteCollection(Guid collection)
    {
        if (!Available || collection == Guid.Empty) return;
        try { deleteCollection.Invoke(collection); }
        catch (Exception ex) { log.Warning($"[MasterEvent] Penumbra.DeleteTemporaryCollection a levé : {ex.Message}"); }
    }

    public void Dispose()
    {
        initializedSub.Dispose();
        disposedSub.Dispose();
    }
}
