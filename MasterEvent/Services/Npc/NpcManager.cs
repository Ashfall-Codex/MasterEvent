using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using MasterEvent.Models;

namespace MasterEvent.Services.Npc;


public sealed unsafe class NpcManager : IDisposable
{
    public const int MaxConcurrentNpcs = 8;

    private readonly NpcSpawnGuard guard;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private readonly List<NpcInstance> instances = new();
    private ushort? burnerSlot0;

    public IReadOnlyList<NpcInstance> Instances => instances;
    public int Count => instances.Count;

    public event Action? OnInstancesChanged;

    public event Action<NpcInstance>? OnNpcDespawning;

    public NpcManager(NpcSpawnGuard guard, IClientState clientState, ICondition condition, IFramework framework, IPluginLog log)
    {
        this.guard = guard;
        this.clientState = clientState;
        this.condition = condition;
        this.framework = framework;
        this.log = log;

        clientState.TerritoryChanged += OnTerritoryChanged;
        condition.ConditionChange += OnConditionChange;
    }


    public bool TrySpawn(NpcAppearance appearance, out NpcInstance? instance, out string? error)
        => TrySpawnCore(appearance, Guid.NewGuid(), (ushort)clientState.TerritoryType,
            position: null, rotation: null, isReplicated: false, out instance, out error);


    public bool TrySpawnReplicated(NpcSyncData data, out NpcInstance? instance, out string? error)
    {
        instance = null;
        error = null;
        if (!Guid.TryParse(data.NetworkId, out var netId))
        {
            error = "NetworkId invalide.";
            return false;
        }
        return TrySpawnCore(data.Appearance, netId, data.Territory,
            position: new Vector3(data.X, data.Y, data.Z), rotation: data.Rotation,
            isReplicated: true, out instance, out error);
    }

    public bool TryRestoreOwned(NpcSyncData data, out NpcInstance? instance, out string? error)
    {
        instance = null;
        error = null;
        if (!Guid.TryParse(data.NetworkId, out var netId))
        {
            error = "NetworkId invalide.";
            return false;
        }
        return TrySpawnCore(data.Appearance, netId, data.Territory,
            position: new Vector3(data.X, data.Y, data.Z), rotation: data.Rotation,
            isReplicated: false, out instance, out error);
    }

    public NpcInstance? FindByNetworkId(Guid networkId)
        => instances.FirstOrDefault(n => n.NetworkId == networkId);


    public void NotifyChanged() => OnInstancesChanged?.Invoke();

    private bool TrySpawnCore(NpcAppearance appearance, Guid networkId, ushort territory,
        Vector3? position, float? rotation, bool isReplicated,
        out NpcInstance? instance, out string? error)
    {
        instance = null;
        error = null;

        if (instances.Count >= MaxConcurrentNpcs)
        {
            error = $"Limite atteinte ({MaxConcurrentNpcs} PNJ simultanés).";
            return false;
        }

        if (!guard.CanSpawn(out var reason))
        {
            error = guard.DescribeReason(reason);
            return false;
        }

        var manager = ClientObjectManager.Instance();
        if (manager == null)
        {
            error = "ClientObjectManager indisponible.";
            return false;
        }

        EnsureSlot0Burned(manager);

        var rawIndex = manager->CreateBattleCharacter();
        if (rawIndex == 0xFFFFFFFFu)
        {
            error = "Allocation native du PNJ refusée par le jeu.";
            return false;
        }

        var index = (ushort)rawIndex;
        var gameObject = manager->GetObjectByIndex(index);
        if (gameObject == null)
        {
            error = "Objet natif introuvable après allocation.";
            return false;
        }

        var battleChara = (BattleChara*)gameObject;
        battleChara->CharacterSetup.SetupBNpc(0);
        battleChara->ObjectKind = ObjectKind.BattleNpc;
        battleChara->BattleNpcSubKind = (BattleNpcSubKind)4;
        battleChara->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        battleChara->OwnerId = 0xE0000000u;
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local != null)
        {
            var localChara = (Character*)local.Address;
            battleChara->HomeWorld = localChara->HomeWorld;
            battleChara->CurrentWorld = localChara->CurrentWorld;

            if (position is { } p)
            {
                battleChara->SetPosition(p.X, p.Y, p.Z);
                battleChara->SetRotation(rotation ?? localChara->Rotation);
            }
            else
            {
                var pos = localChara->Position;
                battleChara->SetPosition(pos.X, pos.Y, pos.Z);
                battleChara->SetRotation(localChara->Rotation);
            }
        }

        var npc = new NpcInstance(index, appearance, networkId, territory, isReplicated, framework, log);
        npc.WriteIdentifierName();
        npc.ApplyAppearance(appearance);
        npc.RequestDraw();

        instances.Add(npc);
        OnInstancesChanged?.Invoke();

        log.Info($"[MasterEvent] PNJ spawné : index={index}, nom='{npc.DisplayName}', réplique={isReplicated}.");
        instance = npc;
        return true;
    }

    public void Despawn(NpcInstance instance)
    {
        if (!instances.Remove(instance)) return;
        OnNpcDespawning?.Invoke(instance);
        instance.Despawn();
        OnInstancesChanged?.Invoke();
    }

    public void DespawnAll()
    {
        if (instances.Count == 0) return;
        foreach (var npc in instances.ToArray())
        {
            OnNpcDespawning?.Invoke(npc);
            npc.Despawn();
        }
        instances.Clear();
        OnInstancesChanged?.Invoke();
    }

    public void PruneDead()
    {
        var removed = instances.RemoveAll(n => !n.IsAlive);
        if (removed > 0) OnInstancesChanged?.Invoke();
    }

    private void OnTerritoryChanged(uint _)
    {

        burnerSlot0 = null;

        if (instances.Count == 0) return;
        log.Info("[MasterEvent] Changement de zone : nettoyage des PNJ.");
        foreach (var npc in instances)
            npc.MarkDisposed();
        instances.Clear();
        OnInstancesChanged?.Invoke();
    }


    private void EnsureSlot0Burned(ClientObjectManager* manager)
    {
        if (burnerSlot0 == 0) return;

        var slot0Object = manager->GetObjectByIndex(0);
        if (slot0Object != null)
        {

            burnerSlot0 = 0;
            return;
        }

        var burnerIdx = manager->CreateBattleCharacter();
        if (burnerIdx == 0xFFFFFFFFu) return;
        if (burnerIdx == 0)
        {
            burnerSlot0 = 0;
            log.Info("[MasterEvent] Slot 0 du ClientObjectManager brûlé pour réserver le slot.");
        }
        else
        {

            manager->DeleteObjectByIndex((ushort)burnerIdx, 0);
        }
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (!value) return;
        if (instances.Count == 0) return;

        var triggers = flag is ConditionFlag.BoundByDuty
            or ConditionFlag.BoundByDuty56
            or ConditionFlag.BoundByDuty95
            or ConditionFlag.WatchingCutscene
            or ConditionFlag.WatchingCutscene78
            or ConditionFlag.OccupiedInCutSceneEvent;

        if (!triggers) return;

        log.Info($"[MasterEvent] Condition {flag} active : despawn des PNJ pour sécurité.");
        DespawnAll();
    }

    public void Dispose()
    {
        clientState.TerritoryChanged -= OnTerritoryChanged;
        condition.ConditionChange -= OnConditionChange;
        DespawnAll();

        // Cleanup du burner slot 0.
        if (burnerSlot0 is { } burnerIdx)
        {
            var manager = ClientObjectManager.Instance();
            if (manager != null)
            {
                manager->DeleteObjectByIndex(burnerIdx, 0);
                log.Info($"[MasterEvent] Slot 0 burner libéré (idx={burnerIdx}).");
            }
            burnerSlot0 = null;
        }
    }
}
