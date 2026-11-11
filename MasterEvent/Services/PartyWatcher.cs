using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace MasterEvent.Services;

public class PartyWatcher : IDisposable
{
    public event Action? OnPartyJoined;
    public event Action? OnPartyLeft;
    public event Action? OnLeaderChanged;
    public event Action? OnMembersChanged;
    public event Action<bool>? OnAllianceChanged;
    public event Action<bool>? OnRecruitingChanged;

    public bool InParty { get; private set; }
    public bool IsLeader { get; private set; }
    public long PartyId { get; private set; }
    public bool IsAlliance { get; private set; }
    public bool IsRecruiting { get; private set; }

    private readonly IPartyList partyList;
    private readonly IPlayerState playerState;
    private readonly IFramework framework;
    private readonly ICondition condition;

    // Si la liste se remplit une frame trop tard après le chargement, un simple téléport
    // vaudrait un départ suivi d'un retour, avec passation de scène à la clé.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);
    private DateTime settledAt;

    private bool wasInParty;
    private bool wasLeader;
    private bool wasAlliance;
    private bool wasRecruiting;
    private bool allianceKnown;
    private bool recruitingKnown;
    private int lastMemberCount;

    public PartyWatcher(IPartyList partyList, IPlayerState playerState, IFramework framework, ICondition condition)
    {
        this.partyList = partyList;
        this.playerState = playerState;
        this.framework = framework;
        this.condition = condition;

        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }
    
    public void Reset()
    {
        InParty = false;
        IsLeader = false;
        PartyId = 0;
        wasInParty = false;
        wasLeader = false;
        lastMemberCount = 0;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (playerState.ContentId == 0)
            return;

        var currentInParty = partyList.Length > 0;
        var currentPartyId = partyList.PartyId;
        var currentMemberCount = partyList.Length;

        var currentIsLeader = currentInParty && IsLocalPlayerLeader();
        var currentIsAlliance = ReadAlliance();

        InParty = currentInParty;
        IsLeader = currentIsLeader;
        PartyId = currentPartyId;
        IsAlliance = currentIsAlliance;

        // Pendant un chargement ou une instance, la liste n'est pas fiable. La référence d'avant
        // est gardée telle quelle : une dissolution survenue entre-temps sort à la fin, comparée
        // à l'état réel, au lieu d'être perdue dans la transition.
        if (IsSettled())
            DetectPartyChanges(currentInParty, currentIsLeader, currentMemberCount);

        DetectAllianceChange(currentIsAlliance);
        DetectRecruitingChange(currentIsAlliance);
    }

    private void DetectPartyChanges(bool currentInParty, bool currentIsLeader, int currentMemberCount)
    {
        if (currentInParty != wasInParty || currentIsLeader != wasLeader || currentMemberCount != lastMemberCount)
            Plugin.Log.Debug($"[PartyWatcher] Groupe : en groupe {wasInParty} → {currentInParty}, " +
                             $"chef {wasLeader} → {currentIsLeader}, membres {lastMemberCount} → {currentMemberCount} " +
                             $"(partyId={PartyId})");

        if (currentInParty && !wasInParty)
        {
            OnPartyJoined?.Invoke();
        }
        else if (!currentInParty && wasInParty)
        {
            OnPartyLeft?.Invoke();
        }

        if (currentIsLeader != wasLeader && currentInParty)
        {
            OnLeaderChanged?.Invoke();
        }

        if (currentMemberCount != lastMemberCount && currentInParty)
        {
            OnMembersChanged?.Invoke();
        }

        wasInParty = currentInParty;
        wasLeader = currentIsLeader;
        lastMemberCount = currentMemberCount;
    }

    private bool IsSettled()
    {
        var now = DateTime.UtcNow;
        if (IsPartyStateUnreliable())
        {
            settledAt = now + SettleDelay;
            return false;
        }
        return now >= settledAt;
    }

    private bool IsPartyStateUnreliable()
    {
        return condition[ConditionFlag.BetweenAreas]
            || condition[ConditionFlag.BetweenAreas51]
            || condition[ConditionFlag.BoundByDuty]
            || condition[ConditionFlag.BoundByDuty56]
            || condition[ConditionFlag.BoundByDuty95];
    }

    private bool IsLocalPlayerLeader()
    {
        var leaderIndex = (int)partyList.PartyLeaderIndex;
        if (leaderIndex < 0 || leaderIndex >= partyList.Length) return false;

        var leader = partyList[leaderIndex];
        return leader != null && leader.ContentId == playerState.ContentId;
    }

    private void DetectAllianceChange(bool currentIsAlliance)
    {
        if (!allianceKnown)
        {
            allianceKnown = true;
        }
        else if (currentIsAlliance != wasAlliance)
        {
            Plugin.Log.Debug($"[PartyWatcher] Alliance : {(currentIsAlliance ? "formée" : "dissoute")} " +
                             $"(party={partyList.IsAlliance}, crossRealm={ReadCrossRealmAlliance()}, " +
                             $"membres={partyList.Length}, partyId={partyList.PartyId})");
            OnAllianceChanged?.Invoke(currentIsAlliance);
        }
        wasAlliance = currentIsAlliance;
    }

    private void DetectRecruitingChange(bool currentIsAlliance)
    {
        var currentIsRecruiting = ReadRecruiting();
        IsRecruiting = currentIsRecruiting;

        if (!recruitingKnown)
        {
            recruitingKnown = true;
        }
        else if (currentIsRecruiting != wasRecruiting)
        {
            Plugin.Log.Debug($"[PartyWatcher] Recherche d'équipe : recrutement {(currentIsRecruiting ? "publié" : "retiré")} " +
                             $"(alliance={currentIsAlliance} [party={partyList.IsAlliance}, " +
                             $"crossRealm={ReadCrossRealmAlliance()}], membres={partyList.Length})");
            OnRecruitingChanged?.Invoke(currentIsRecruiting);
        }
        wasRecruiting = currentIsRecruiting;
    }

    private static unsafe bool ReadCrossRealmAlliance()
    {
        try
        {
            return FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCrossRealm.IsAllianceRaid();
        }
        catch
        {
            return false;
        }
    }

    private unsafe bool ReadAlliance()
    {
        return partyList.IsAlliance || ReadCrossRealmAlliance();
    }

    private static unsafe bool ReadRecruiting()
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLookingForGroup.Instance();
            return agent != null && agent->OwnListingId != 0;
        }
        catch
        {
            return false;
        }
    }
}
