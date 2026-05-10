using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using MasterEvent.Models;

namespace MasterEvent.Services.Npc;

// Coordonne le cycle de vie de tous les PNJ spawnés par le plugin :
// création via le ClientObjectManager natif, suivi de la liste,
// nettoyage automatique au changement de zone et au déchargement.
public sealed unsafe class NpcManager : IDisposable
{
    public const int MaxConcurrentNpcs = 8;

    private readonly NpcSpawnGuard guard;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private readonly List<NpcInstance> instances = new();

    public IReadOnlyList<NpcInstance> Instances => instances;
    public int Count => instances.Count;

    public event Action? OnInstancesChanged;

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
        // SubKind = 4 : valeur empirique correspondant à un PNJ générique
        // non interagissable côté gameplay (pas de cible, pas de combat).
        battleChara->BattleNpcSubKind = (BattleNpcSubKind)4;
        battleChara->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;

        // Positionner sur le joueur local pour qu'il apparaisse devant le MJ.
        var local = clientState.LocalPlayer;
        if (local != null)
        {
            var localChara = (Character*)local.Address;
            var pos = localChara->Position;
            battleChara->SetPosition(pos.X, pos.Y, pos.Z);
            battleChara->SetRotation(localChara->Rotation);
        }

        var npc = new NpcInstance(index, appearance, framework, log);
        npc.ApplyAppearance(appearance);
        npc.RequestDraw();

        instances.Add(npc);
        OnInstancesChanged?.Invoke();

        log.Info($"[MasterEvent] PNJ spawné : index={index}, nom='{npc.DisplayName}'.");
        instance = npc;
        return true;
    }

    public void Despawn(NpcInstance instance)
    {
        if (!instances.Remove(instance)) return;
        instance.Despawn();
        OnInstancesChanged?.Invoke();
    }

    public void DespawnAll()
    {
        if (instances.Count == 0) return;
        foreach (var npc in instances.ToArray())
            npc.Despawn();
        instances.Clear();
        OnInstancesChanged?.Invoke();
    }

    // Nettoie les références dont l'objet natif a déjà été détruit par le jeu
    // (changement de zone, mort forcée). À appeler depuis l'UI quand on
    // veut afficher la liste actuelle.
    public void PruneDead()
    {
        var removed = instances.RemoveAll(n => !n.IsAlive);
        if (removed > 0) OnInstancesChanged?.Invoke();
    }

    private void OnTerritoryChanged(ushort _)
    {
        if (instances.Count == 0) return;
        log.Info("[MasterEvent] Changement de zone : nettoyage des PNJ.");
        foreach (var npc in instances)
            npc.MarkDisposed();
        instances.Clear();
        OnInstancesChanged?.Invoke();
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
    }
}
