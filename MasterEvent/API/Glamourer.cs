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
    private readonly GetDesignList getDesignList;
    private readonly ApplyDesign applyDesign;
    private readonly ApplyDesignName applyDesignName;
    private readonly ApplyState applyState;
    private readonly RevertStateName revertStateName;

    private readonly EventSubscriber initializedSub;
    private readonly EventSubscriber disposedSub;

    public bool Available { get; private set; }
    public (int Major, int Minor) Version { get; private set; }

    public event Action? OnAvailabilityChanged;

    public Glamourer(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;

        apiVersion = new ApiVersion(pi);
        getDesignList = new GetDesignList(pi);
        applyDesign = new ApplyDesign(pi);
        applyDesignName = new ApplyDesignName(pi);
        applyState = new ApplyState(pi);
        revertStateName = new RevertStateName(pi);
        initializedSub = Initialized.Subscriber(pi, OnGlamourerInitialized);
        disposedSub = Disposed.Subscriber(pi, OnGlamourerDisposed);
        initializedSub.Enable();
        disposedSub.Enable();

        RefreshAvailability();
    }

    private void OnGlamourerInitialized() => RefreshAvailability();

    private void OnGlamourerDisposed()
    {
        if (!Available) return;
        Available = false;
        Version = (0, 0);
        log.Info("[MasterEvent] Glamourer s'est déchargé : pont désactivé.");
        OnAvailabilityChanged?.Invoke();
    }

    public void RefreshAvailability()
    {
        var prev = Available;
        var newAvailable = false;
        var newVersion = (Major: 0, Minor: 0);
        try
        {
            newVersion = apiVersion.Invoke();
            newAvailable = newVersion.Major >= 1;
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
                log.Info($"[MasterEvent] Glamourer détecté (API v{newVersion.Major}.{newVersion.Minor}).");
            OnAvailabilityChanged?.Invoke();
        }
    }

    public IReadOnlyList<DesignEntry> ListDesigns()
    {
        if (!Available) return Array.Empty<DesignEntry>();
        try
        {
            var dict = getDesignList.Invoke();
            return dict
                .Select(kv => new DesignEntry(kv.Key, kv.Value))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] Glamourer.GetDesignList a échoué : {ex.Message}");
            return Array.Empty<DesignEntry>();
        }
    }

    public bool TryApplyDesignByName(Guid designId, string identifierName, out string? error)
    {
        error = null;
        if (!Available)
        {
            error = "Glamourer indisponible.";
            return false;
        }

        try
        {
            var ec = applyDesignName.Invoke(designId, identifierName);
            log.Info($"[MasterEvent] Glamourer.ApplyDesignName(design={designId}, name='{identifierName}') → {ec}");
            if (ec != GlamourerApiEc.Success)
            {
                error = $"Glamourer a refusé l'application (code {ec}).";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Erreur Glamourer : {ex.Message}";
            log.Warning($"[MasterEvent] Glamourer.ApplyDesignName a levé : {ex}");
            return false;
        }
    }

    // Variante par index global de l'ObjectTable. Conservée pour compatibilité
    // mais à éviter pour les PNJ MasterEvent : préférer TryApplyDesignByName.
    public bool TryApplyDesign(Guid designId, ushort objectIndex, out string? error)
    {
        error = null;
        if (!Available)
        {
            error = "Glamourer indisponible.";
            return false;
        }

        try
        {
            var ec = applyDesign.Invoke(designId, objectIndex);
            log.Info($"[MasterEvent] Glamourer.ApplyDesign(design={designId}, idx={objectIndex}) → {ec}");
            if (ec != GlamourerApiEc.Success)
            {
                error = $"Glamourer a refusé l'application (code {ec}).";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Erreur Glamourer : {ex.Message}";
            log.Warning($"[MasterEvent] Glamourer.ApplyDesign a levé : {ex}");
            return false;
        }
    }

    // Applique un état Glamourer sérialisé en base64
    public bool TryApplyStateBase64(string base64State, ushort objectIndex, out string? error)
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
            var ec = applyState.Invoke(base64State, objectIndex);
            log.Info($"[MasterEvent] Glamourer.ApplyState(base64, idx={objectIndex}) → {ec}");
            if (ec != GlamourerApiEc.Success)
            {
                error = $"Glamourer a refusé l'application (code {ec}).";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Erreur Glamourer : {ex.Message}";
            log.Warning($"[MasterEvent] Glamourer.ApplyState a levé : {ex}");
            return false;
        }
    }

    // Réinitialise l'état Glamourer associé à un nom interne MasterEvent.
    public void RevertByName(string identifierName)
    {
        if (!Available) return;
        try
        {
            var ec = revertStateName.Invoke(identifierName);
            log.Info($"[MasterEvent] Glamourer.RevertStateName(name='{identifierName}') → {ec}");
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

    public sealed record DesignEntry(Guid Id, string Name);
}
