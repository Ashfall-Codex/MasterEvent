using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;
using MasterEvent.Communication;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.UI;
using MasterEvent.Waymarks;

namespace MasterEvent.Services;

public class SessionManager
{
    public MarkerSet CurrentMarkers { get; } = new();
    public MarkerSet SavedMarkers { get; private set; } = new();
    public bool IsGm { get; set; } = true;
    public bool IsPromoted { get; set; }
    public bool CanEdit => IsGm || IsPromoted;
    public string LocalPlayerHash { get; private set; } = string.Empty;
    public bool IsConnected { get; set; }
    public int ConnectedPlayerCount { get; set; }
    public int DiceMax { get; set; } = 999;
    public HpMode HpMode { get; set; } = HpMode.Points;
    public bool ShowMpBar { get; set; } = true;
    public bool ShowShield { get; set; } = true;
    public HpMode MpMode { get; set; } = HpMode.Points;

    public EventTemplate? ActiveTemplate { get; set; }
    public TurnState? CurrentTurnState { get; set; }

    public event Action<bool>? OnPromotionChanged;

    public List<PlayerData> PartyMembers { get; } = new();

    private readonly SaveManager saveManager;
    private readonly TemplateManager templateManager;
    private readonly string pluginConfigDir;
    private RelayClient? relayClient;
    private RoundAnnouncementOverlay? roundOverlay;
    private readonly Dictionary<WaymarkId, int> movingWaymarks = new();
    private const int MoveDelayFrames = 10;
    private DateTime lastCacheSave;
    private const int CacheThrottleSeconds = 5;
    private const int CacheMaxAgeHours = 2;
    public bool CacheRestored { get; set; }

    public SessionManager(string pluginConfigDir)
    {
        this.pluginConfigDir = pluginConfigDir;
        saveManager = new SaveManager(pluginConfigDir);
        templateManager = new TemplateManager(pluginConfigDir);
    }

    public void SetRelayClient(RelayClient client)
    {
        relayClient = client;
    }

    public void SetRoundOverlay(RoundAnnouncementOverlay overlay)
    {
        roundOverlay = overlay;
    }

    public void SyncWaymarks()
    {
        WaymarkManager.SyncWaymarkVisibility(CurrentMarkers);
    }

    public void PollWaymarkChanges()
    {
        var states = WaymarkManager.ReadCurrentWaymarks();
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var waymarkId = (WaymarkId)i;
            var marker = CurrentMarkers.Markers[i];
            var wasVisible = marker.IsVisible;
            var isNowActive = states[i].Active;

            // Moving waymark: protect card data, don't update IsVisible, enter placement after delay
            if (movingWaymarks.TryGetValue(waymarkId, out var framesLeft))
            {
                if (isNowActive)
                {
                    // Waymark was placed back, move complete
                    marker.IsVisible = true;
                    marker.X = states[i].Position.X;
                    marker.Y = states[i].Position.Y;
                    marker.Z = states[i].Position.Z;
                    movingWaymarks.Remove(waymarkId);
                    Plugin.Log.Info($"[MasterEvent] Move waymark {waymarkId}: placed back successfully.");
                }
                else if (framesLeft > 0)
                {
                    // Counting down, don't touch IsVisible
                    movingWaymarks[waymarkId] = framesLeft - 1;
                }
                else if (framesLeft == 0)
                {
                    // Delay elapsed, enter placement mode via /waymark command
                    Plugin.Log.Info($"[MasterEvent] Move waymark {waymarkId}: entering placement mode.");
                    WaymarkManager.EnterPlacementMode(waymarkId);
                    movingWaymarks[waymarkId] = -1;
                }
                // framesLeft < 0: already attempted, just keep card alive

                continue;
            }

            var hadData = marker.HasData;
            marker.IsVisible = isNowActive;

            if (isNowActive)
            {
                marker.X = states[i].Position.X;
                marker.Y = states[i].Position.Y;
                marker.Z = states[i].Position.Z;
            }

            // Waymark just placed in game and marker has no data yet -> auto-create
            if (isNowActive && !wasVisible && !hadData)
            {
                marker.Name = string.Empty;
                InitMarkerFromTemplate(marker);
            }

            // Waymark removed in game -> clear data
            if (!isNowActive && wasVisible)
                marker.Reset();
        }
    }

    public void MoveMarker(WaymarkId id)
    {
        Plugin.Log.Info($"[MasterEvent] Move waymark {id}: clearing before replacement.");
        var result = WaymarkManager.ClearWaymark(id);
        if (result == 0)
            movingWaymarks[id] = MoveDelayFrames;
        else
            Plugin.Log.Warning($"[MasterEvent] Move waymark {id}: ClearWaymark failed (code {result}).");
    }

    public void ClearMarker(WaymarkId id)
    {
        movingWaymarks.Remove(id);
        var result = WaymarkManager.ClearWaymark(id);
        if (result == 0)
        {
            CurrentMarkers[id].Reset();
            BroadcastUpdate();
        }
    }

    public void ClearAllMarkers()
    {
        var result = WaymarkManager.ClearAllWaymarks();
        if (result == 0)
        {
            for (var i = 0; i < Constants.WaymarkCount; i++)
                CurrentMarkers.Markers[i].IsVisible = false;
            BroadcastClear();
            DeleteGmCache();
        }
    }

    public void ResetAllData()
    {
        CurrentMarkers.ResetAll();
    }

    public void BroadcastUpdate()
    {
        if (relayClient == null || !relayClient.IsConnected || !CanEdit) return;

        var msg = new RelayMessage
        {
            Type = MessageType.Update,
            Markers = CurrentMarkers.Markers,
            ShowMpBar = ShowMpBar,
            ShowShield = ShowShield,
            HpMode = HpMode.ToString(),
            MpMode = MpMode.ToString(),
        };
        _ = relayClient.SendAsync(msg);
        SaveGmCache();
    }

    public void BroadcastClear()
    {
        if (relayClient == null || !relayClient.IsConnected || !CanEdit) return;

        var msg = new RelayMessage { Type = MessageType.Clear };
        _ = relayClient.SendAsync(msg);
    }

    public void RollDice(WaymarkId waymarkId)
    {
        var marker = CurrentMarkers[waymarkId];
        var name = marker.Name;
        if (string.IsNullOrWhiteSpace(name)) return;

        var result = Random.Shared.Next(1, DiceMax + 1);
        marker.LastRollResult = result;
        marker.LastRollMax = DiceMax;
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.Roll"), name, result, DiceMax));

        if (relayClient != null && relayClient.IsConnected && CanEdit)
        {
            var msg = new RelayMessage
            {
                Type = MessageType.Roll,
                RollMarkerName = name,
                RollResult = result,
                RollMax = DiceMax,
            };
            _ = relayClient.SendAsync(msg);
        }

    }

    public void RequestUpdate()
    {
        if (relayClient == null || !relayClient.IsConnected || IsGm) return;

        var msg = new RelayMessage { Type = MessageType.RequestUpdate };
        _ = relayClient.SendAsync(msg);
    }

    public void SaveCurrentAsPreset(string name)
    {
        saveManager.SavePreset(CurrentMarkers, name);
    }

    public bool LoadPreset(string name)
    {
        var loaded = saveManager.LoadPreset(name);
        if (loaded == null) return false;

        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var src = loaded.Markers[i];
            var dst = CurrentMarkers.Markers[i];
            dst.Name = src.Name;
            dst.Hp = src.Hp;
            dst.Mp = src.Mp;
            dst.HpMax = src.HpMax;
            dst.MpMax = src.MpMax;
            dst.Shield = src.Shield;
            dst.Counters = src.Counters?.Select(c => c.DeepCopy()).ToList();
            dst.Attitude = src.Attitude;
            dst.IsBoss = src.IsBoss;
            dst.X = src.X;
            dst.Y = src.Y;
            dst.Z = src.Z;
        }
        return true;
    }

    public void DeletePreset(string name)
    {
        saveManager.DeletePreset(name);
    }

    public List<string> GetPresetNames()
    {
        return saveManager.GetPresetNames();
    }

    public void BackupMarkers()
    {
        SavedMarkers = CurrentMarkers.DeepCopy();
    }

    public void RestoreMarkers()
    {
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var src = SavedMarkers.Markers[i];
            var dst = CurrentMarkers.Markers[i];
            dst.Name = src.Name;
            dst.Hp = src.Hp;
            dst.Mp = src.Mp;
            dst.HpMax = src.HpMax;
            dst.MpMax = src.MpMax;
            dst.Shield = src.Shield;
            dst.Counters = src.Counters?.Select(c => c.DeepCopy()).ToList();
            dst.Attitude = src.Attitude;
            dst.IsBoss = src.IsBoss;
            dst.X = src.X;
            dst.Y = src.Y;
            dst.Z = src.Z;
        }
    }

    public void SyncPartyMembers(IPartyList partyList, IPlayerState playerState)
    {
        // Not ready yet (player not loaded)
#pragma warning disable CS0618
        if (playerState.ContentId == 0 || Plugin.ClientState.LocalPlayer == null)
            return;
#pragma warning restore CS0618

        LocalPlayerHash = Plugin.GeneratePlayerHash(playerState.ContentId);

        if (partyList.Length == 0)
        {
            // Solo mode: local player only
            var localHash = LocalPlayerHash;
#pragma warning disable CS0618
            var localName = Plugin.ClientState.LocalPlayer.Name.TextValue;
#pragma warning restore CS0618

            if (PartyMembers.Count == 1 && PartyMembers[0].Hash == localHash)
            {
                PartyMembers[0].Name = localName;
                PartyMembers[0].IsGm = true;
                return;
            }

            PartyMembers.Clear();
            PartyMembers.Add(new PlayerData
            {
                Hash = localHash,
                Name = localName,
                IsGm = true,
                IsConnected = false,
            });
            return;
        }

        var leaderIndex = (int)partyList.PartyLeaderIndex;
        var seen = new HashSet<string>();

        for (var i = 0; i < partyList.Length; i++)
        {
            var member = partyList[i];
            if (member == null) continue;

            var hash = Plugin.GeneratePlayerHash((ulong)member.ContentId);
            seen.Add(hash);

            var existing = PartyMembers.FirstOrDefault(p => p.Hash == hash);
            if (existing != null)
            {
                existing.Name = member.Name.TextValue;
                existing.IsGm = i == leaderIndex;
            }
            else
            {
                PartyMembers.Add(new PlayerData
                {
                    Hash = hash,
                    Name = member.Name.TextValue,
                    Hp = 100,
                    IsGm = i == leaderIndex,
                });
            }
        }

        // Remove members no longer in party
        PartyMembers.RemoveAll(p => !seen.Contains(p.Hash));
    }

    public void UpdatePlayerConnection(string playerHash, bool connected)
    {
        var player = PartyMembers.FirstOrDefault(p => p.Hash == playerHash);
        if (player != null)
            player.IsConnected = connected;
    }

    public void BroadcastPlayerUpdate()
    {
        if (relayClient == null || !relayClient.IsConnected || !IsGm) return;

        var msg = new RelayMessage
        {
            Type = MessageType.PlayerUpdate,
            Players = PartyMembers.ToArray(),
        };
        _ = relayClient.SendAsync(msg);
    }

    public void SetPlayerHp(string hash, int hp)
    {
        var player = PartyMembers.FirstOrDefault(p => p.Hash == hash);
        if (player == null) return;
        player.Hp = hp;
        BroadcastPlayerUpdate();
    }

    public int ApplyWaymarks()
    {
        var placed = WaymarkManager.PlaceAllWaymarks(CurrentMarkers);
        return placed;
    }

    public void PromotePlayer(string hash, bool canEdit)
    {
        if (relayClient == null || !relayClient.IsConnected || !IsGm) return;

        var msg = new RelayMessage
        {
            Type = MessageType.Promote,
            TargetHash = hash,
            CanEdit = canEdit,
        };
        _ = relayClient.SendAsync(msg);

        // Update local state immediately
        var player = PartyMembers.FirstOrDefault(p => p.Hash == hash);
        if (player != null)
            player.CanEdit = canEdit;
    }

    public void SetPromoted(bool promoted)
    {
        if (IsPromoted == promoted) return;
        IsPromoted = promoted;
        OnPromotionChanged?.Invoke(promoted);
    }

    public void ClearAllPromotions()
    {
        IsPromoted = false;
        foreach (var player in PartyMembers)
            player.CanEdit = false;
    }


    private void InitMarkerFromTemplate(MarkerData marker)
    {
        marker.Hp = 100;
        marker.Mp = 100;
        marker.HpMax = 100;
        marker.MpMax = 100;
        marker.Shield = 0;

        if (ActiveTemplate?.CounterDefinitions != null && ActiveTemplate.CounterDefinitions.Count > 0)
            marker.Counters = ActiveTemplate.CounterDefinitions.Select(cd => cd.ToCounter()).ToList();
    }

    public void ApplyTemplate(EventTemplate template)
    {
        ActiveTemplate = template;
        HpMode = template.HpMode;
        ShowMpBar = template.ShowMpBar;
        MpMode = template.MpMode;
        ShowShield = template.ShowShield;
        DiceMax = template.DiceMax;

        // Only touch markers that already have data — leave empty ones alone
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var marker = CurrentMarkers.Markers[i];
            if (!marker.HasData) continue;

            if (template.CounterDefinitions != null && template.CounterDefinitions.Count > 0)
                marker.Counters = template.CounterDefinitions.Select(cd => cd.ToCounter()).ToList();
            else
                marker.Counters = null;
        }
    }

    public void ClearActiveTemplate()
    {
        ActiveTemplate = null;
    }

    public void BroadcastTemplate()
    {
        if (relayClient == null || !relayClient.IsConnected || !IsGm || ActiveTemplate == null) return;

        var msg = new RelayMessage
        {
            Type = MessageType.TemplateShare,
            Template = ActiveTemplate,
        };
        _ = relayClient.SendAsync(msg);
    }

    public void SaveTemplate(EventTemplate template)
    {
        templateManager.SaveTemplate(template);
    }

    public EventTemplate? LoadTemplate(string name)
    {
        return templateManager.LoadTemplate(name);
    }

    public void DeleteTemplate(string name)
    {
        templateManager.DeleteTemplate(name);
    }

    public List<string> GetTemplateNames()
    {
        return templateManager.GetTemplateNames();
    }

    public EventTemplate GetOrCreateDefaultTemplate()
    {
        return templateManager.GetOrCreateDefault();
    }

    //  GM Cache
    private string GmCachePath => Path.Combine(pluginConfigDir, "gm_cache.json");

    public void SaveGmCache()
    {
        var now = DateTime.UtcNow;
        if ((now - lastCacheSave).TotalSeconds < CacheThrottleSeconds) return;
        lastCacheSave = now;

        try
        {
            var cache = new GmCache
            {
                SavedAt = now,
                Markers = CurrentMarkers.Markers.Select(m => m.DeepCopy()).ToArray(),
                HpMode = HpMode.ToString(),
                MpMode = MpMode.ToString(),
                ShowMpBar = ShowMpBar,
                ShowShield = ShowShield,
                DiceMax = DiceMax,
                ActiveTemplate = ActiveTemplate?.DeepCopy(),
            };
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GmCachePath, json);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Failed to save GM cache: {ex.Message}");
        }
    }

    public GmCache? LoadGmCache()
    {
        try
        {
            if (!File.Exists(GmCachePath)) return null;

            var json = File.ReadAllText(GmCachePath);
            var cache = JsonSerializer.Deserialize<GmCache>(json);
            if (cache == null) return null;

            // Check freshness
            if ((DateTime.UtcNow - cache.SavedAt).TotalHours > CacheMaxAgeHours)
            {
                DeleteGmCache();
                return null;
            }

            return cache;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Failed to load GM cache: {ex.Message}");
            return null;
        }
    }

    public void DeleteGmCache()
    {
        try
        {
            if (File.Exists(GmCachePath))
                File.Delete(GmCachePath);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Failed to delete GM cache: {ex.Message}");
        }
    }

    public void RestoreFromCache(GmCache cache)
    {
        if (cache.Markers is not { Length: > 0 }) return;

        for (var i = 0; i < cache.Markers.Length && i < Constants.WaymarkCount; i++)
        {
            var src = cache.Markers[i];
            var dst = CurrentMarkers.Markers[i];
            dst.Name = src.Name;
            dst.Hp = src.Hp;
            dst.Mp = src.Mp;
            dst.HpMax = src.HpMax;
            dst.MpMax = src.MpMax;
            dst.Shield = src.Shield;
            dst.Counters = src.Counters?.Select(c => c.DeepCopy()).ToList();
            dst.Attitude = src.Attitude;
            dst.IsBoss = src.IsBoss;
            dst.IsVisible = src.IsVisible;
            dst.X = src.X;
            dst.Y = src.Y;
            dst.Z = src.Z;
        }

        if (!string.IsNullOrEmpty(cache.HpMode) && Enum.TryParse<HpMode>(cache.HpMode, out var hpMode))
            HpMode = hpMode;
        if (!string.IsNullOrEmpty(cache.MpMode) && Enum.TryParse<HpMode>(cache.MpMode, out var mpMode))
            MpMode = mpMode;
        ShowMpBar = cache.ShowMpBar;
        ShowShield = cache.ShowShield;
        DiceMax = cache.DiceMax;
        if (cache.ActiveTemplate != null)
            ActiveTemplate = cache.ActiveTemplate;
    }


    public void StartEncounter()
    {
        var state = new TurnState
        {
            IsActive = true,
            Round = 1,
            DiceMax = DiceMax > 0 ? DiceMax : 20,
        };

        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var marker = CurrentMarkers.Markers[i];
            if (!marker.HasData || string.IsNullOrEmpty(marker.Name)) continue;

            state.Entries.Add(new TurnEntry
            {
                WaymarkIndex = i,
                Name = marker.Name,
                Initiative = Random.Shared.Next(1, state.DiceMax + 1),
            });
        }

        foreach (var player in PartyMembers)
        {
            if (player.IsGm) continue;

            state.Entries.Add(new TurnEntry
            {
                PlayerHash = player.Hash,
                Name = player.Name,
                Initiative = Random.Shared.Next(1, state.DiceMax + 1),
            });
        }

        state.Entries.Sort((a, b) => b.Initiative.CompareTo(a.Initiative));
        CurrentTurnState = state;
        BroadcastTurnState();
    }

    public void EndEncounter()
    {
        CurrentTurnState = null;
        BroadcastTurnClear();
    }

    public void ToggleHasActed(int index)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        if (index < 0 || index >= state.Entries.Count) return;

        var entry = state.Entries[index];
        entry.HasActed = !entry.HasActed;

        // Just checked = has played → announce next or end of round
        if (entry.HasActed)
        {
            var next = state.Entries.FirstOrDefault(e => !e.HasActed);
            if (next != null)
                ShowTurnToast(next.Name);
            else
                ShowRoundEndToast(state.Round);
        }

        BroadcastTurnState();
    }

    public void NextRound()
    {
        if (CurrentTurnState is not { IsActive: true } state) return;

        state.Round++;
        foreach (var entry in state.Entries)
            entry.HasActed = false;

        ShowRoundToast(state.Round);
        BroadcastTurnState();
    }

    public void ShowRoundToast(int round)
    {
        var text = string.Format(Loc.Get("Turns.Round"), round);
        roundOverlay?.Show(text);
    }

    public void ShowTurnToast(string name)
    {
        var text = string.Format(Loc.Get("Turns.TurnToast"), name);
        Plugin.ToastGui.ShowQuest(text);
    }

    public void ShowRoundEndToast(int round)
    {
        var text = string.Format(Loc.Get("Turns.RoundEnd"), round);
        Plugin.ToastGui.ShowQuest(text);
    }

    public void RerollInitiative(int index)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        if (index < 0 || index >= state.Entries.Count) return;

        state.Entries[index].Initiative = Random.Shared.Next(1, state.DiceMax + 1);
        state.Entries.Sort((a, b) => b.Initiative.CompareTo(a.Initiative));
        BroadcastTurnState();
    }

    public void RerollAllInitiative()
    {
        if (CurrentTurnState is not { IsActive: true } state) return;

        foreach (var entry in state.Entries)
            entry.Initiative = Random.Shared.Next(1, state.DiceMax + 1);

        state.Entries.Sort((a, b) => b.Initiative.CompareTo(a.Initiative));
        BroadcastTurnState();
    }

    public void AddTurnParticipant(TurnEntry entry)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;

        entry.Initiative = Random.Shared.Next(1, state.DiceMax + 1);
        state.Entries.Add(entry);
        state.Entries.Sort((a, b) => b.Initiative.CompareTo(a.Initiative));
        BroadcastTurnState();
    }

    public void MoveParticipantUp(int index)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        if (index <= 0 || index >= state.Entries.Count) return;

        (state.Entries[index], state.Entries[index - 1]) = (state.Entries[index - 1], state.Entries[index]);
        BroadcastTurnState();
    }

    public void MoveParticipantDown(int index)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        if (index < 0 || index >= state.Entries.Count - 1) return;

        (state.Entries[index], state.Entries[index + 1]) = (state.Entries[index + 1], state.Entries[index]);
        BroadcastTurnState();
    }

    public void RemoveTurnParticipant(int index)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        if (index < 0 || index >= state.Entries.Count) return;

        state.Entries.RemoveAt(index);
        BroadcastTurnState();
    }

    public void BroadcastTurnState()
    {
        if (relayClient == null || !relayClient.IsConnected || !CanEdit) return;
        if (CurrentTurnState == null) return;

        var msg = new RelayMessage
        {
            Type = MessageType.TurnUpdate,
            TurnState = CurrentTurnState.DeepCopy(),
        };
        _ = relayClient.SendAsync(msg);
    }

    public void BroadcastTurnClear()
    {
        if (relayClient == null || !relayClient.IsConnected || !CanEdit) return;

        var msg = new RelayMessage { Type = MessageType.TurnClear };
        _ = relayClient.SendAsync(msg);
    }
}
