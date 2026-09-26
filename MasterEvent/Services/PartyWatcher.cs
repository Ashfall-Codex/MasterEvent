using System;
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

    private bool wasInParty;
    private bool wasLeader;
    private bool wasAlliance;
    private bool wasRecruiting;
    private bool allianceKnown;
    private bool recruitingKnown;
    private int lastMemberCount;

    public PartyWatcher(IPartyList partyList, IPlayerState playerState, IFramework framework)
    {
        this.partyList = partyList;
        this.playerState = playerState;
        this.framework = framework;

        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (playerState.ContentId == 0)
            return;

        var currentInParty = partyList.Length > 0;
        var currentPartyId = partyList.PartyId;
        var currentMemberCount = partyList.Length;

        // Determine if local player is party leader
        var currentIsLeader = false;
        if (currentInParty)
        {
            var leaderIndex = (int)partyList.PartyLeaderIndex;
            if (leaderIndex >= 0 && leaderIndex < partyList.Length)
            {
                var leader = partyList[leaderIndex];
                if (leader != null)
                {
                    currentIsLeader = leader.ContentId == playerState.ContentId;
                }
            }
        }

        var currentIsAlliance = ReadAlliance();

        InParty = currentInParty;
        IsLeader = currentIsLeader;
        PartyId = currentPartyId;
        IsAlliance = currentIsAlliance;

        // Detect state changes
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

        if (!allianceKnown)
        {
            allianceKnown = true;
        }
        else if (currentIsAlliance != wasAlliance)
        {
            Plugin.Log.Debug($"[PartyWatcher] Alliance : {(currentIsAlliance ? "formée" : "dissoute")} " +
                             $"(party={partyList.IsAlliance}, crossRealm={ReadCrossRealmAlliance()}, " +
                             $"membres={currentMemberCount}, partyId={currentPartyId})");
            OnAllianceChanged?.Invoke(currentIsAlliance);
        }
        wasAlliance = currentIsAlliance;

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
                             $"crossRealm={ReadCrossRealmAlliance()}], membres={currentMemberCount})");
            OnRecruitingChanged?.Invoke(currentIsRecruiting);
        }
        wasRecruiting = currentIsRecruiting;

        wasInParty = currentInParty;
        wasLeader = currentIsLeader;
        lastMemberCount = currentMemberCount;
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
