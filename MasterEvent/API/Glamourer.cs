using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using global::Glamourer.Api.Enums;
using global::Glamourer.Api.Helpers;
using global::Glamourer.Api.IpcSubscribers;

namespace MasterEvent.API;

public sealed class Glamourer : IDisposable
{
    private readonly IPluginLog log;
    private readonly ApiVersion apiVersion;
    private readonly ApplyState applyState;
    private readonly RevertStateName revertStateName;
    private readonly GetDesignList getDesignList;
    private readonly ApplyDesign applyDesign;
    private readonly GetStateBase64 getStateBase64;
    private readonly EventSubscriber initializedSub;
    private readonly EventSubscriber disposedSub;

    public bool Available { get; private set; }

    public Glamourer(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        apiVersion = new ApiVersion(pi);
        applyState = new ApplyState(pi);
        revertStateName = new RevertStateName(pi);
        getDesignList = new GetDesignList(pi);
        applyDesign = new ApplyDesign(pi);
        getStateBase64 = new GetStateBase64(pi);
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
            Available = version.Major >= 1;
        }
        catch
        {
            // Plugin absent ou IPC pas encore enregistré.
            Available = false;
        }
    }

    public bool TryApplyState(string base64State, ushort objectIndex, out string? error)
    {
        error = null;
        if (!Available)
        {
            error = "Glamourer indisponible.";
            return false;
        }

        if (string.IsNullOrEmpty(base64State))
        {
            error = "État Glamourer vide.";
            return false;
        }

        try
        {
            var code = applyState.Invoke(base64State, objectIndex);
            log.Info($"[MasterEvent] Glamourer.ApplyState(idx={objectIndex}) : {code}");
            if (code == GlamourerApiEc.Success) return true;

            error = $"Glamourer a refusé l'état ({code}).";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Erreur Glamourer : {ex.Message}";
            log.Warning($"[MasterEvent] Glamourer.ApplyState a levé : {ex}");
            return false;
        }
    }

    // Bibliothèque de designs, triée par nom.
    public IReadOnlyList<(Guid Id, string Name)> ListDesigns()
    {
        if (!Available) return [];
        try
        {
            return getDesignList.Invoke()
                .Select(kv => (kv.Key, kv.Value))
                .OrderBy(d => d.Value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Glamourer.GetDesignList a levé : {ex.Message}");
            return [];
        }
    }

    // État Glamourer sérialisé (base64) de l'objet, ou chaîne vide.
    public string GetState(int objectIndex)
    {
        if (!Available) return string.Empty;
        try
        {
            return getStateBase64.Invoke(objectIndex).Item2 ?? string.Empty;
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Glamourer.GetStateBase64 a levé : {ex.Message}");
            return string.Empty;
        }
    }

    public bool TryApplyDesign(Guid designId, int objectIndex)
    {
        if (!Available) return false;
        try
        {
            var code = applyDesign.Invoke(designId, objectIndex, 0);
            log.Info($"[MasterEvent] Glamourer.ApplyDesign(idx={objectIndex}) : {code}");
            return code == GlamourerApiEc.Success;
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Glamourer.ApplyDesign a levé : {ex.Message}");
            return false;
        }
    }

    // Remet à zéro l'état associé à un nom de personnage (« Prénom Nom »).
    public void RevertByName(string identifierName)
    {
        if (!Available) return;
        try
        {
            revertStateName.Invoke(identifierName);
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Glamourer.RevertStateName a levé : {ex.Message}");
        }
    }

    public void Dispose()
    {
        initializedSub.Dispose();
        disposedSub.Dispose();
    }
}
