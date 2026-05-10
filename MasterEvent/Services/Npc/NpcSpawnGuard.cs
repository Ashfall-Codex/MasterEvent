using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace MasterEvent.Services.Npc;

// Centralise les vérifications de sécurité avant tout spawn de PNJ.
// Politique stricte par défaut : on refuse dans les duties matchmakés,
// le PvP, les cinématiques et pendant les transitions de zone.
public sealed class NpcSpawnGuard
{
    private readonly ICondition condition;
    private readonly IClientState clientState;

    public NpcSpawnGuard(ICondition condition, IClientState clientState)
    {
        this.condition = condition;
        this.clientState = clientState;
    }

    public enum BlockReason
    {
        None,
        InDuty,
        InPvP,
        InCutscene,
        BetweenAreas,
        NoLocalPlayer,
    }

    public bool CanSpawn(out BlockReason reason)
    {
        if (clientState.LocalPlayer == null)
        {
            reason = BlockReason.NoLocalPlayer;
            return false;
        }

        if (clientState.IsPvP)
        {
            reason = BlockReason.InPvP;
            return false;
        }

        if (condition[ConditionFlag.WatchingCutscene]
            || condition[ConditionFlag.WatchingCutscene78]
            || condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            reason = BlockReason.InCutscene;
            return false;
        }

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            reason = BlockReason.BetweenAreas;
            return false;
        }

        if (condition[ConditionFlag.BoundByDuty]
            || condition[ConditionFlag.BoundByDuty56]
            || condition[ConditionFlag.BoundByDuty95])
        {
            reason = BlockReason.InDuty;
            return false;
        }

        reason = BlockReason.None;
        return true;
    }

    public string DescribeReason(BlockReason reason)
    {
        return reason switch
        {
            BlockReason.InDuty => "Spawn impossible : vous êtes en instance.",
            BlockReason.InPvP => "Spawn impossible : zone PvP.",
            BlockReason.InCutscene => "Spawn impossible : cinématique en cours.",
            BlockReason.BetweenAreas => "Spawn impossible : transition de zone en cours.",
            BlockReason.NoLocalPlayer => "Spawn impossible : joueur local introuvable.",
            _ => string.Empty,
        };
    }
}
