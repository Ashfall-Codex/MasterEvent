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
            });
        }
        return result.ToArray();
    }

    // Réconcilie les répliques locales avec la liste reçue du GM, filtrée par
    // territoire courant. Appelé côté récepteur uniquement (cf. ProtocolHandler).
    public void ApplyRemote(NpcSyncData[]? data)
    {
        lastRemote = data ?? Array.Empty<NpcSyncData>();
        Reconcile();
    }

    private void Reconcile()
    {
        var myTerritory = (ushort)clientState.TerritoryType;

        // PNJ désirés ici = ceux ancrés dans mon territoire courant.
        var desired = lastRemote.Where(d => d.Territory == myTerritory).ToList();
        var desiredIds = new HashSet<Guid>();
        foreach (var d in desired)
            if (Guid.TryParse(d.NetworkId, out var id)) desiredIds.Add(id);

        // Despawn des répliques qui ne sont plus désirées (PNJ retiré ou zone quittée).
        foreach (var npc in npcManager.Instances.Where(n => n.IsReplicated).ToArray())
            if (!desiredIds.Contains(npc.NetworkId))
                npcManager.Despawn(npc);

        // Spawn des nouveaux désirés absents localement.
        foreach (var d in desired)
        {
            if (!Guid.TryParse(d.NetworkId, out var id)) continue;
            if (npcManager.FindByNetworkId(id) != null) continue;
            if (!npcManager.TrySpawnReplicated(d, out _, out var err))
                log.Warning($"[MasterEvent] Réplique PNJ '{d.Name}' refusée : {err}");
        }
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
