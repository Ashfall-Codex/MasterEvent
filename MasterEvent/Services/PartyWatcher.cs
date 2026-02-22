using System;
using Dalamud.Plugin.Services;

namespace MasterEvent.Services;

public class PartyWatcher : IDisposable
{
    public event Action? OnPartyJoined;
    public event Action? OnPartyLeft;
    public event Action? OnLeaderChanged;
    public event Action? OnMembersChanged;

    public bool InParty { get; private set; }
    public bool IsLeader { get; private set; }
    public long PartyId { get; private set; }

    private readonly IPartyList partyList;
    private readonly IPlayerState playerState;
    private readonly IFramework framework;

    private bool wasInParty;
    private bool wasLeader;
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
                    currentIsLeader = (ulong)leader.ContentId == playerState.ContentId;
                }
            }
        }

        InParty = currentInParty;
        IsLeader = currentIsLeader;
        PartyId = currentPartyId;

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

        wasInParty = currentInParty;
        wasLeader = currentIsLeader;
        lastMemberCount = currentMemberCount;
    }
}
