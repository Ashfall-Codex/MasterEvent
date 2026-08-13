using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using MasterEvent.Models;

namespace MasterEvent.Services.Npc;

public sealed class NpcSyncCoordinator : IDisposable
{
    private readonly NpcManager npcManager;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly Action requestBroadcast;

    // Dernière liste de PNJ reçue du GM, conservée pour re-réconcilier au
    // changement de zone du récepteur.
    private NpcSyncData[] lastRemote = Array.Empty<NpcSyncData>();

    // Compteurs du dernier tracé.
    private int lastSentCount = -1;
    private int lastReceivedCount = -1;

    public NpcSyncCoordinator(NpcManager npcManager, IClientState clientState, IPluginLog log,
        Action requestBroadcast)
    {
        this.npcManager = npcManager;
        this.clientState = clientState;
        this.log = log;
        this.requestBroadcast = requestBroadcast;

        npcManager.OnInstancesChanged += OnInstancesChanged;
        clientState.TerritoryChanged += OnTerritoryChanged;
    }

    // Construit le payload réseau à partir des seuls PNJ owned (non répliqués)
    // vivants. Appelé par SessionManager.BroadcastUpdate (donc uniquement côté GM).
    public NpcSyncData[] BuildPayload()
    {
        var result = new List<NpcSyncData>();
        foreach (var npc in npcManager.Instances)
        {
            if (npc.IsReplicated || !npc.IsAlive) continue;

            var pos = npc.GetPosition() ?? System.Numerics.Vector3.Zero;
            var rot = npc.GetRotation() ?? 0f;
            result.Add(new NpcSyncData
            {
                NetworkId = npc.NetworkId.ToString("N"),
                Name = npc.DisplayName,
                Territory = npc.Territory,
                Appearance = npc.Appearance,
                X = pos.X,
                Y = pos.Y,
                Z = pos.Z,
                Rotation = rot,
                EmoteId = npc.EmoteId,
                EmoteHeld = npc.EmoteHeld,
                WeaponDrawn = npc.WeaponDrawn,
            });
        }

        if (result.Count != lastSentCount)
        {
            lastSentCount = result.Count;
            log.Information($"[MasterEvent][NpcSync] Envoi de {result.Count} PNJ : "
                + string.Join(", ", result.Select(r => $"{r.Name}@z{r.Territory}")));
        }

        return result.ToArray();
    }

    // Réconcilie les répliques locales avec la liste reçue du GM, filtrée par
    // territoire courant. Appelé côté récepteur uniquement (cf. ProtocolHandler).
    public void ApplyRemote(NpcSyncData[]? data)
    {
        lastRemote = data ?? Array.Empty<NpcSyncData>();

        if (lastRemote.Length != lastReceivedCount)
        {
            lastReceivedCount = lastRemote.Length;
            log.Information($"[MasterEvent][NpcSync] Reçu {lastRemote.Length} PNJ du MJ : "
                + string.Join(", ", lastRemote.Select(d => $"{d.Name}@z{d.Territory}")));
        }

        Reconcile();
    }

    public void RestoreOwned(NpcSyncData[]? data)
    {
        if (data is not { Length: > 0 }) return;

        var myTerritory = (ushort)clientState.TerritoryType;
        var restored = 0;

        foreach (var d in data)
        {
            if (d.Territory != myTerritory) continue;
            if (!Guid.TryParse(d.NetworkId, out var id)) continue;
            if (npcManager.FindByNetworkId(id) != null) continue;

            if (npcManager.TryRestoreOwned(d, out _, out var err))
                restored++;
            else
                log.Warning($"[MasterEvent][NpcSync] Restauration de '{d.Name}' impossible : {err}");
        }

        if (restored > 0)
            log.Information($"[MasterEvent][NpcSync] {restored} PNJ restauré(s) depuis le cache serveur.");
    }

    private void Reconcile()
    {
        var myTerritory = (ushort)clientState.TerritoryType;

        // PNJ désirés ici = ceux ancrés dans mon territoire courant.
        var desired = lastRemote.Where(d => d.Territory == myTerritory).ToList();
        var desiredIds = new HashSet<Guid>();
        foreach (var d in desired)
            if (Guid.TryParse(d.NetworkId, out var id)) desiredIds.Add(id);

        var despawned = 0;
        var spawned = 0;

        // Despawn des répliques qui ne sont plus désirées (PNJ retiré ou zone quittée).
        foreach (var npc in npcManager.Instances.Where(n => n.IsReplicated).ToArray())
            if (!desiredIds.Contains(npc.NetworkId))
            {
                npcManager.Despawn(npc);
                despawned++;
            }

        foreach (var d in desired)
        {
            if (!Guid.TryParse(d.NetworkId, out var id)) continue;

            if (npcManager.FindByNetworkId(id) is { } existing)
            {
                // Une emote changée en cours de scène doit suivre : sans ça, seule la première
                // valeur reçue serait jouée et le PNJ resterait figé dessus.
                if (existing.EmoteId != d.EmoteId || existing.EmoteHeld != d.EmoteHeld)
                {
                    if (d.EmoteId == 0) existing.ClearEmote();
                    else existing.SetEmote(d.EmoteId, d.EmoteHeld);
                }
                if (existing.WeaponDrawn != d.WeaponDrawn) existing.SetWeaponDrawn(d.WeaponDrawn);
                continue;
            }

            if (npcManager.TrySpawnReplicated(d, out var instance, out var err))
            {
                spawned++;
                if (d.WeaponDrawn) instance?.SetWeaponDrawn(true);
                if (d.EmoteId != 0) instance?.SetEmote(d.EmoteId, d.EmoteHeld);
            }
            else
            {
                log.Warning($"[MasterEvent][NpcSync] Réplique PNJ '{d.Name}' refusée : {err}");
            }
        }

        if (spawned > 0 || despawned > 0)
            log.Information($"[MasterEvent][NpcSync] Zone {myTerritory} : {desired.Count} PNJ "
                + $"attendus sur {lastRemote.Length} reçus — {spawned} apparu(s), {despawned} retiré(s).");
    }

    private void OnInstancesChanged() => requestBroadcast();

    // Au changement de zone : NpcManager a déjà vidé ses instances ; on rejoue
    // la réconciliation pour (re)spawner les répliques ancrées dans la nouvelle
    // zone. No-op côté GM (aucune instance répliquée).
    private void OnTerritoryChanged(uint territory) => Reconcile();

    public void Dispose()
    {
        npcManager.OnInstancesChanged -= OnInstancesChanged;
        clientState.TerritoryChanged -= OnTerritoryChanged;
    }
}
