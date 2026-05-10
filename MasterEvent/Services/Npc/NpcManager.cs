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

    // Slot 0 du ClientObjectManager est intentionnellement « brûlé » par
    // un acteur fantôme jamais dessiné, pour que les vrais PNJ utilisateur
    // partent toujours du slot 1+. Évite à l'utilisateur de voir un slot 0
    // qui se comportait différemment (residual IA Penumbra, indexing 0-based).
    // Re-burnt au besoin (changement de zone vide CIM).
    private ushort? burnerSlot0;

    public IReadOnlyList<NpcInstance> Instances => instances;
    public int Count => instances.Count;

    public event Action? OnInstancesChanged;

    // Levé juste avant la destruction native du PNJ : l'objet est encore vivant,
    // donc instance.GameObjectIndex et autres lectures restent valides. Sert
    // notamment au cleanup côté Penumbra (retrait d'individual assignment).
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

        // Brûle slot 0 si nécessaire pour que les PNJ utilisateur partent
        // de slot 1. L'acteur burner reste invisible (jamais dessiné, pas
        // d'EnableDraw). Si un changement de zone a vidé le CIM entre temps,
        // on re-burn ici.
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
        // SubKind = 4 : valeur empirique correspondant à un PNJ générique
        // non interagissable côté gameplay (pas de cible, pas de combat).
        battleChara->BattleNpcSubKind = (BattleNpcSubKind)4;
        battleChara->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        // OwnerId = 0xE0000000 = sentinelle « no owner ». Si la valeur diffère,
        // Penumbra.GameData.ActorIdentifierFactory tente de résoudre un owner et
        // produit un identifier Owned-NPC (au lieu du fallback Player-NPC qu'on
        // cible avec NameId=0). On force la sentinelle pour rester sur le bon
        // chemin d'identification côté Glamourer.
        battleChara->OwnerId = 0xE0000000u;

        // Pour que Glamourer (et plus généralement Penumbra.GameData.ActorManager)
        // puisse identifier ce PNJ, il faut que :
        //   1) le HomeWorld soit un id valide (présent dans la table Worlds Lumina)
        //   2) le Name natif respecte les règles SE de VerifyPlayerName :
        //      "Forename Surname", 5-21 chars total, un seul espace, chaque partie
        //      2-15 chars, première lettre A-Z, le reste en a-z + '\''+ '-'.
        // Sinon GetIdentifier() retourne Invalid et Glamourer répond ActorNotFound
        // sur ApplyDesign, même si l'objet existe bien à l'index demandé.
        // On délègue la fabrique du nom à NpcInstance pour garder l'unicité par slot.
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local != null)
        {
            var localChara = (Character*)local.Address;
            var pos = localChara->Position;
            battleChara->SetPosition(pos.X, pos.Y, pos.Z);
            battleChara->SetRotation(localChara->Rotation);
            battleChara->HomeWorld = localChara->HomeWorld;
            battleChara->CurrentWorld = localChara->CurrentWorld;
        }

        var npc = new NpcInstance(index, appearance, framework, log);
        npc.WriteIdentifierName();
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
