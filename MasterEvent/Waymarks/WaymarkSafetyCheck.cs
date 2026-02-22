using Dalamud.Game.ClientState.Conditions;

namespace MasterEvent.Waymarks;

public static class WaymarkSafetyCheck
{
    public static bool IsInCombat()
    {
        return Plugin.Condition[ConditionFlag.InCombat];
    }

    public static bool CanModifyWaymarks()
    {
        return !IsInCombat();
    }
}
