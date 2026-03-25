using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using MasterEvent.Communication;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.UI;
using MasterEvent.UI.Components;
using MasterEvent.Waymarks;

namespace MasterEvent.Services;

public class SessionManager(string pluginConfigDir)
{
    public MarkerSet CurrentMarkers { get; } = new();
    public MarkerSet SavedMarkers { get; private set; } = new();
    public bool IsGm { get; set; } = true;
    public bool IsPromoted { get; set; }
    public bool CanEdit => IsGm || IsPromoted || IsGmAsPlayer;

    /// <summary>
    /// True when the local player is the actual GM (in party data) but viewing as player with GmIsPlayer checked.
    /// </summary>
    public bool IsGmAsPlayer
    {
        get
        {
            if (!GmIsPlayer || IsGm) return false;
            var local = PartyMembers.FirstOrDefault(p => p.Hash == LocalPlayerHash);
            return local is { IsGm: true };
        }
    }
    public string LocalPlayerHash { get; private set; } = string.Empty;
    public bool IsConnected { get; set; }
    public int ConnectedPlayerCount { get; set; }
    public int DiceMax { get; set; } = 999;
    public HpMode HpMode { get; set; } = HpMode.Points;
    public bool ShowMpBar { get; set; } = true;
    public bool ShowShield { get; set; } = true;
    public HpMode MpMode { get; set; } = HpMode.Points;
    public bool GmIsPlayer { get; set; }

    public EventTemplate? ActiveTemplate { get; set; }
    public TurnState? CurrentTurnState { get; set; }

    // Mode Alliance
    public string? AllianceRoomCode { get; set; }
    public bool IsAllianceMode => !string.IsNullOrEmpty(AllianceRoomCode);

    private static readonly char[] AllianceCharset = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    public static string GenerateAllianceCode()
    {
        var rng = new Random();
        var code = new char[6];
        for (var i = 0; i < 6; i++)
            code[i] = AllianceCharset[rng.Next(AllianceCharset.Length)];
        return new string(code);
    }

    public event Action<bool>? OnPromotionChanged;

    public List<PlayerData> PartyMembers { get; } = new();

    private readonly SaveManager saveManager = new(pluginConfigDir);
    private readonly TemplateManager templateManager = new(pluginConfigDir);
    private RelayClient? relayClient;
    private RoundAnnouncementOverlay? roundOverlay;
    private DiceRollOverlay? diceRollOverlay;
    private WeatherService? weatherService;
    private readonly Dictionary<WaymarkId, int> movingWaymarks = new();
    private const int MoveDelayFrames = 10;
    private DateTime lastCacheSave;
    private const int CacheThrottleSeconds = 5;
    private const int CacheMaxAgeHours = 2;
    public bool CacheRestored { get; set; }

    public List<DiceResult> RollHistory { get; } = new();
    private const int MaxRollHistory = 50;

    private MarkerData[]? lastBroadcastSnapshot;
    private DateTime lastAutoBroadcast;

    public byte CurrentWeatherId { get; set; }
    public string? CurrentWeatherName { get; set; }

    public void SetRelayClient(RelayClient client)
    {
        relayClient = client;
    }

    public void SetRoundOverlay(RoundAnnouncementOverlay overlay)
    {
        roundOverlay = overlay;
    }

    public void SetDiceRollOverlay(DiceRollOverlay overlay)
    {
        diceRollOverlay = overlay;
    }

    public void SetWeatherService(WeatherService service)
    {
        weatherService = service;
    }

    public void TickTimeOverride()
    {
        weatherService?.TickTimeOverride();
    }

    public bool IsWeathermanInstalled => weatherService?.IsWeathermanInstalled ?? false;
    public bool IsWeathermanTimePatchActive => weatherService?.IsWeathermanTimePatchActive ?? false;
    public bool IsWeatherPatchActive => weatherService?.IsWeatherPatchActive ?? false;

    public void DisposeWeatherService()
    {
        weatherService?.Dispose();
    }

    /// <summary>
    /// Récupère les météos disponibles pour la zone courante.
    /// </summary>
    public Dictionary<byte, string> GetAvailableWeathers()
    {
        return weatherService?.GetWeathersForCurrentZone() ?? WeatherService.FallbackWeathers;
    }

    /// <summary>
    /// Récupère l'ID d'icône de jeu pour une météo.
    /// </summary>
    public uint GetWeatherIconId(byte weatherId)
    {
        return weatherService?.GetWeatherIconId(weatherId) ?? 0;
    }

    /// <summary>
    /// Envoie la météo sélectionnée à tous les joueurs connectés.
    /// </summary>
    public void BroadcastWeather(byte weatherId, string weatherName)
    {
        if (relayClient is not { IsConnected: true } || !CanEdit) return;

        CurrentWeatherId = weatherId;
        CurrentWeatherName = weatherName;

        var msg = new RelayMessage
        {
            Type = MessageType.WeatherUpdate,
            WeatherId = weatherId,
            WeatherName = weatherName,
        };
        _ = relayClient.SendAsync(msg);
    }

    // Applique une météo localement via WeatherService.
    public void ApplyWeather(byte weatherId)
    {
        CurrentWeatherId = weatherId;
        weatherService?.SetWeather(weatherId);
    }

    // Envoie l'heure éorzéenne à tous les joueurs connectés.
    public void BroadcastTime(uint eorzeaSeconds)
    {
        if (relayClient is not { IsConnected: true } || !CanEdit) return;

        CurrentEorzeaTime = eorzeaSeconds;

        var msg = new RelayMessage
        {
            Type = MessageType.TimeUpdate,
            EorzeaTime = eorzeaSeconds,
        };
        _ = relayClient.SendAsync(msg);
    }
    // Applique l'heure éorzéenne localement via WeatherService.
    public void ApplyTime(uint eorzeaSeconds)
    {
        CurrentEorzeaTime = eorzeaSeconds;
        weatherService?.SetTime(eorzeaSeconds);
    }

    // Désactive l'override de l'heure éorzéenne.
    public void ClearTime()
    {
        CurrentEorzeaTime = 0;
        weatherService?.ClearTime();
    }

    public uint CurrentEorzeaTime { get; set; }

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
        if (relayClient is not { IsConnected: true } || !CanEdit) return;

        // Envoyer les marqueurs sans les stats (les stats MJ ne doivent pas être visibles par les joueurs)
        var sanitized = new MarkerData[Constants.WaymarkCount];
        for (var i = 0; i < Constants.WaymarkCount; i++)
            sanitized[i] = CurrentMarkers.Markers[i].DeepCopyWithoutStats();

        var msg = new RelayMessage
        {
            Type = MessageType.Update,
            Markers = sanitized,
            ShowMpBar = ShowMpBar,
            ShowShield = ShowShield,
            HpMode = HpMode.ToString(),
            MpMode = MpMode.ToString(),
        };
        _ = relayClient.SendAsync(msg);
        SaveGmCache();
        TakeSnapshot();
    }

    // Called from Framework.Update. Broadcasts marker state every second if it has changed.
    public void CheckAutoBroadcast()
    {
        if (relayClient is not { IsConnected: true } || !CanEdit) return;
        if ((DateTime.UtcNow - lastAutoBroadcast).TotalSeconds < 1.0) return;

        lastAutoBroadcast = DateTime.UtcNow;

        if (!HasMarkerChanges()) return;

        BroadcastUpdate();
    }

    private bool HasMarkerChanges()
    {
        if (lastBroadcastSnapshot == null) return true;

        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            if (!CurrentMarkers.Markers[i].ContentEquals(lastBroadcastSnapshot[i]))
                return true;
        }

        return false;
    }

    private void TakeSnapshot()
    {
        lastBroadcastSnapshot = new MarkerData[Constants.WaymarkCount];
        for (var i = 0; i < Constants.WaymarkCount; i++)
            lastBroadcastSnapshot[i] = CurrentMarkers.Markers[i].DeepCopy();
    }

    public void BroadcastClear()
    {
        if (relayClient is not { IsConnected: true } || !CanEdit) return;

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

        if (relayClient is { IsConnected: true } && CanEdit)
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

    public void AddRollToHistory(DiceResult result)
    {
        RollHistory.Insert(0, result);
        if (RollHistory.Count > MaxRollHistory)
            RollHistory.RemoveAt(RollHistory.Count - 1);
    }

    public void ClearRollHistory()
    {
        RollHistory.Clear();
    }

    public void RollDiceWithStat(WaymarkId waymarkId, string? statId = null)
    {
        var marker = CurrentMarkers[waymarkId];
        var name = marker.Name;
        if (string.IsNullOrWhiteSpace(name)) return;

        var formula = ActiveTemplate?.DiceFormula ?? "1d100";
        var rawRoll = DiceEngine.Roll(formula);
        var diceMax = DiceEngine.GetMax(formula);
        var modifier = 0;
        string? statName = null;

        // Chercher le modificateur de la stat
        if (statId != null && marker.Stats != null)
        {
            var stat = marker.Stats.FirstOrDefault(s => s.Id == statId);
            if (stat != null)
            {
                modifier = stat.Modifier;
                statName = stat.Name;
            }
        }

        var tempMod = marker.TempModifier;
        var totalModifier = modifier + tempMod;

        var total = rawRoll + totalModifier;
        marker.LastRollResult = total;
        marker.LastRollMax = diceMax;

        var result = new DiceResult
        {
            RollerName = name,
            StatName = statName,
            RawRoll = rawRoll,
            Modifier = totalModifier,
            Total = total,
            DiceMax = diceMax,
        };
        AddRollToHistory(result);

        diceRollOverlay?.Show(name, total, diceMax, rawRoll, modifier, tempMod, statName);

        // Différer le message chat jusqu'à la fin de l'animation
        var modifierStr = totalModifier >= 0 ? $"+{totalModifier}" : totalModifier.ToString();
        var chatMsg = statName != null
            ? string.Format(Loc.Get("Chat.StatRoll"), name, rawRoll, diceMax, modifierStr, total, statName)
            : string.Format(Loc.Get("Chat.Roll"), name, total, diceMax);
        if (diceRollOverlay != null)
            diceRollOverlay.DeferChatMessage(chatMsg);
        else
            Plugin.ChatGui.Print(chatMsg);

        // Diffuser via relay
        if (relayClient is { IsConnected: true } && CanEdit)
        {
            var msg = new RelayMessage
            {
                Type = MessageType.StatRoll,
                RollMarkerName = name,
                RollResult = rawRoll,
                RollMax = diceMax,
                RollModifier = modifier,
                RollTempModifier = tempMod,
                RollTotal = total,
                StatName = statName,
                DiceFormula = formula,
            };
            _ = relayClient.SendAsync(msg);
        }
    }

    public void RollDiceForPlayer(string playerHash, string? statId = null)
    {
        var player = PartyMembers.FirstOrDefault(p => p.Hash == playerHash);
        if (player == null) return;

        var formula = ActiveTemplate?.DiceFormula ?? "1d100";
        var rawRoll = DiceEngine.Roll(formula);
        var diceMax = DiceEngine.GetMax(formula);
        var modifier = 0;
        string? statName = null;

        if (statId != null && player.Stats != null)
        {
            var stat = player.Stats.FirstOrDefault(s => s.Id == statId);
            if (stat != null)
            {
                modifier = stat.Modifier;
                statName = stat.Name;
            }
        }

        // Séparer le bonus/malus temporaire pour l'animation
        var tempMod = player.TempModifier;
        var totalModifier = modifier + tempMod;

        var total = rawRoll + totalModifier;

        var result = new DiceResult
        {
            RollerName = player.Name,
            RollerHash = playerHash,
            StatName = statName,
            RawRoll = rawRoll,
            Modifier = totalModifier,
            Total = total,
            DiceMax = diceMax,
        };
        AddRollToHistory(result);

        // Déclencher l'animation de dé (stat mod et temp mod séparés)
        diceRollOverlay?.Show(player.Name, total, diceMax, rawRoll, modifier, tempMod, statName);

        // Différer le message chat jusqu'à la fin de l'animation
        var modifierStr = totalModifier >= 0 ? $"+{totalModifier}" : totalModifier.ToString();
        var chatMsg = statName != null
            ? string.Format(Loc.Get("Chat.StatRoll"), player.Name, rawRoll, diceMax, modifierStr, total, statName)
            : string.Format(Loc.Get("Chat.Roll"), player.Name, total, diceMax);
        if (diceRollOverlay != null)
            diceRollOverlay.DeferChatMessage(chatMsg);
        else
            Plugin.ChatGui.Print(chatMsg);

        // Diffuser via relay
        if (relayClient is { IsConnected: true })
        {
            var msg = new RelayMessage
            {
                Type = MessageType.StatRoll,
                RollMarkerName = player.Name,
                RollerHash = playerHash,
                RollResult = rawRoll,
                RollMax = diceMax,
                RollModifier = modifier,
                RollTempModifier = tempMod,
                RollTotal = total,
                StatName = statName,
                DiceFormula = formula,
            };
            _ = relayClient.SendAsync(msg);
        }
    }

    public void RequestUpdate()
    {
        if (relayClient is not { IsConnected: true } || IsGm) return;

        var msg = new RelayMessage { Type = MessageType.RequestUpdate };
        _ = relayClient.SendAsync(msg);
    }

    public void SendPlayerStatUpdate()
    {
        if (relayClient is not { IsConnected: true }) return;

        var player = PartyMembers.FirstOrDefault(p => p.Hash == LocalPlayerHash);
        if (player?.Stats == null) return;

        var msg = new RelayMessage
        {
            Type = MessageType.PlayerStatUpdate,
            PlayerHash = LocalPlayerHash,
            Stats = player.Stats.Select(s => s.DeepCopy()).ToArray(),
        };
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
            CurrentMarkers.Markers[i].CopyFrom(loaded.Markers[i]);
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

    // Fiches de personnage (joueur)

    public void SavePlayerSheet(PlayerSheet sheet)
    {
        saveManager.SaveSheet(sheet);
    }

    public PlayerSheet? LoadPlayerSheet(string name)
    {
        return saveManager.LoadSheet(name);
    }

    public void DeletePlayerSheet(string name)
    {
        saveManager.DeleteSheet(name);
    }

    public List<string> GetPlayerSheetNames()
    {
        return saveManager.GetSheetNames();
    }


    public void ApplyPlayerSheet(PlayerSheet sheet)
    {
        var player = PartyMembers.FirstOrDefault(p => p.Hash == LocalPlayerHash);
        if (player == null) return;

        // Appliquer les valeurs de la fiche
        player.Hp = sheet.Hp;
        player.HpMax = sheet.HpMax;
        player.Mp = sheet.Mp;
        player.MpMax = sheet.MpMax;

        // Appliquer les stats
        if (sheet.Stats != null)
        {
            if (player.Stats != null)
            {
                foreach (var savedStat in sheet.Stats)
                {
                    var local = player.Stats.FirstOrDefault(s => s.Id == savedStat.Id || s.Name == savedStat.Name);
                    if (local != null)
                        local.Modifier = savedStat.Modifier;
                    else
                        player.Stats.Add(savedStat.DeepCopy());
                }
            }
            else
            {
                player.Stats = sheet.Stats.Select(s => s.DeepCopy()).ToList();
            }
        }

        // Appliquer les compteurs
        if (sheet.Counters != null)
            player.Counters = sheet.Counters.Select(c => c.DeepCopy()).ToList();

        SendPlayerStatUpdate();
        BroadcastPlayerUpdate();
    }

    // Export / Import de modèles via le relay

    private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static string GetRelayHttpBase(string wsUrl)
    {
        var http = wsUrl.Replace("wss://", "https://").Replace("ws://", "http://");
        return http.TrimEnd('/');
    }

    public static async Task<string?> ExportTemplateAsync(EventTemplate template, string relayUrl, bool permanent = false)
    {
        try
        {
            var baseUrl = GetRelayHttpBase(relayUrl);
            // Sérialiser avec le flag permanent pour le serveur
            var payload = new Dictionary<string, object?>
            {
                ["Name"] = template.Name,
                ["ShowHpBar"] = template.ShowHpBar,
                ["HpMode"] = (int)template.HpMode,
                ["ShowMpBar"] = template.ShowMpBar,
                ["MpMode"] = (int)template.MpMode,
                ["ShowShield"] = template.ShowShield,
                ["DiceMax"] = template.DiceMax,
                ["DiceFormula"] = template.DiceFormula,
                ["DefaultHpMax"] = template.DefaultHpMax,
                ["DefaultMpMax"] = template.DefaultMpMax,
                ["DefaultPlayerHpMax"] = template.DefaultPlayerHpMax,
                ["DefaultPlayerMpMax"] = template.DefaultPlayerMpMax,
                ["CounterDefinitions"] = template.CounterDefinitions,
                ["StatDefinitions"] = template.StatDefinitions,
            };
            if (permanent)
                payload["permanent"] = true;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync($"{baseUrl}/api/templates", content);
            if (!response.IsSuccessStatusCode) return null;

            var responseJson = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Failed to export template: {ex.Message}");
            return null;
        }
    }

    public static async Task<EventTemplate?> ImportTemplateAsync(string code, string relayUrl)
    {
        try
        {
            var baseUrl = GetRelayHttpBase(relayUrl);
            var response = await httpClient.GetAsync($"{baseUrl}/api/templates/{code.Trim().ToUpperInvariant()}");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<EventTemplate>(json);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Failed to import template: {ex.Message}");
            return null;
        }
    }

    public void BackupMarkers()
    {
        SavedMarkers = CurrentMarkers.DeepCopy();
    }

    public void RestoreMarkers()
    {
        for (var i = 0; i < Constants.WaymarkCount; i++)
            CurrentMarkers.Markers[i].CopyFrom(SavedMarkers.Markers[i]);
    }

    public void SyncPartyMembers(IPartyList partyList, IPlayerState playerState)
    {
        // Not ready yet (player not loaded)
        if (playerState.ContentId == 0 || Plugin.ObjectTable.LocalPlayer == null)
            return;

        LocalPlayerHash = Plugin.GeneratePlayerHash(playerState.ContentId);

        if (partyList.Length == 0)
        {
            // Solo mode: local player only
            var localHash = LocalPlayerHash;
            var localName = Plugin.ObjectTable.LocalPlayer!.Name.ToString();

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
        var addedOrRemoved = false;

        for (var i = 0; i < partyList.Length; i++)
        {
            var member = partyList[i];
            if (member == null) continue;

            var hash = Plugin.GeneratePlayerHash((ulong)member.ContentId);
            seen.Add(hash);

            var existing = PartyMembers.FirstOrDefault(p => p.Hash == hash);
            if (existing != null)
            {
                existing.Name = member.Name.ToString();
                existing.IsGm = i == leaderIndex;
            }
            else
            {
                var defaultHpMax = ActiveTemplate?.DefaultPlayerHpMax ?? 100;
                var defaultMpMax = ActiveTemplate?.DefaultPlayerMpMax ?? 100;
                PartyMembers.Add(new PlayerData
                {
                    Hash = hash,
                    Name = member.Name.ToString(),
                    HpMax = defaultHpMax,
                    Hp = defaultHpMax,
                    MpMax = defaultMpMax,
                    Mp = defaultMpMax,
                    Shield = 0,
                    Counters = ActiveTemplate?.CounterDefinitions?.Select(cd => cd.ToCounter()).ToList(),
                    Stats = ActiveTemplate?.StatDefinitions?.Select(sd => sd.ToStatValue()).ToList(),
                    IsGm = i == leaderIndex,
                });
                addedOrRemoved = true;
            }
        }

        // Remove members no longer in party (mais conserver les joueurs alliance)
        var removed = PartyMembers.RemoveAll(p => !seen.Contains(p.Hash) && !p.IsAlliancePlayer);
        if (removed > 0) addedOrRemoved = true;

        // Auto-broadcast when party composition changes
        if (addedOrRemoved && IsGm && relayClient is { IsConnected: true })
            BroadcastPlayerUpdate();
    }

    public void UpdatePlayerConnection(string playerHash, bool connected)
    {
        if (PartyMembers.FirstOrDefault(p => p.Hash == playerHash) is { } player)
            player.IsConnected = connected;
    }

    public void ResetAllPlayerConnections()
    {
        foreach (var player in PartyMembers)
            player.IsConnected = false;
    }

    // Ajoute un joueur alliance (d'un autre groupe FFXIV) à la liste des membres.
    public void AddAlliancePlayer(string hash, string name)
    {
        if (PartyMembers.Any(p => p.Hash == hash)) return;

        var defaultHpMax = ActiveTemplate?.DefaultPlayerHpMax ?? 100;
        var defaultMpMax = ActiveTemplate?.DefaultPlayerMpMax ?? 100;
        PartyMembers.Add(new PlayerData
        {
            Hash = hash,
            Name = name,
            HpMax = defaultHpMax,
            Hp = defaultHpMax,
            MpMax = defaultMpMax,
            Mp = defaultMpMax,
            Counters = ActiveTemplate?.CounterDefinitions?.Select(cd => cd.ToCounter()).ToList(),
            Stats = ActiveTemplate?.StatDefinitions?.Select(sd => sd.ToStatValue()).ToList(),
            IsConnected = true,
            IsAlliancePlayer = true,
        });

        if (IsGm && relayClient is { IsConnected: true })
            BroadcastPlayerUpdate();
    }

    // Retire un joueur alliance de la liste des membres.
    public void RemoveAlliancePlayer(string hash)
    {
        var removed = PartyMembers.RemoveAll(p => p.Hash == hash && p.IsAlliancePlayer);
        if (removed > 0 && IsGm && relayClient is { IsConnected: true })
            BroadcastPlayerUpdate();
    }

    // Retire tous les joueurs alliance de la liste (appelé lors de la désactivation du mode alliance).
    public void ClearAlliancePlayers()
    {
        PartyMembers.RemoveAll(p => p.IsAlliancePlayer);
    }

    public void BroadcastPlayerUpdate()
    {
        if (relayClient is not { IsConnected: true } || !(IsGm || IsGmAsPlayer)) return;

        var msg = new RelayMessage
        {
            Type = MessageType.PlayerUpdate,
            Players = PartyMembers.ToArray(),
            GmIsPlayer = GmIsPlayer,
        };
        _ = relayClient.SendAsync(msg);
    }

    public void SetPlayerHp(string hash, int hp)
    {
        var player = PartyMembers.FirstOrDefault(p => p.Hash == hash);
        if (player == null) return;
        if (hp < 0) hp = 0;
        if (hp > player.HpMax) hp = player.HpMax;
        player.Hp = hp;
        BroadcastPlayerUpdate();
    }

    public void SetPlayerHpMax(string hash, int hpMax)
    {
        var player = PartyMembers.FirstOrDefault(p => p.Hash == hash);
        if (player == null) return;
        if (hpMax < 1) hpMax = 1;
        if (hpMax > 99999) hpMax = 99999;
        player.HpMax = hpMax;
        player.Hp = hpMax;
        BroadcastPlayerUpdate();
    }

    public void SetPlayerMp(string hash, int mp)
    {
        var player = PartyMembers.FirstOrDefault(p => p.Hash == hash);
        if (player == null) return;
        if (mp < 0) mp = 0;
        if (mp > player.MpMax) mp = player.MpMax;
        player.Mp = mp;
        BroadcastPlayerUpdate();
    }

    public void SetPlayerMpMax(string hash, int mpMax)
    {
        var player = PartyMembers.FirstOrDefault(p => p.Hash == hash);
        if (player == null) return;
        if (mpMax < 1) mpMax = 1;
        if (mpMax > 99999) mpMax = 99999;
        player.MpMax = mpMax;
        player.Mp = mpMax;
        BroadcastPlayerUpdate();
    }

    public void SetPlayerShield(string hash, int shield)
    {
        var player = PartyMembers.FirstOrDefault(p => p.Hash == hash);
        if (player == null) return;
        if (shield < 0) shield = 0;
        if (shield > player.HpMax) shield = player.HpMax;
        player.Shield = shield;
        BroadcastPlayerUpdate();
    }

    public int ApplyWaymarks()
    {
        var placed = WaymarkManager.PlaceAllWaymarks(CurrentMarkers);
        return placed;
    }

    public void PromotePlayer(string hash, bool canEdit)
    {
        if (relayClient is not { IsConnected: true } || !(IsGm || IsGmAsPlayer)) return;

        var msg = new RelayMessage
        {
            Type = MessageType.Promote,
            TargetHash = hash,
            CanEdit = canEdit,
        };
        _ = relayClient.SendAsync(msg);

        // Update local state immediately
        if (PartyMembers.FirstOrDefault(p => p.Hash == hash) is { } player)
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
        marker.HpMax = ActiveTemplate?.DefaultHpMax ?? 100;
        marker.MpMax = ActiveTemplate?.DefaultMpMax ?? 100;
        marker.Hp = marker.HpMax;
        marker.Mp = marker.MpMax;
        marker.Shield = 0;

        if (ActiveTemplate?.CounterDefinitions != null && ActiveTemplate.CounterDefinitions.Count > 0)
            marker.Counters = ActiveTemplate.CounterDefinitions.Select(cd => cd.ToCounter()).ToList();

        if (ActiveTemplate?.StatDefinitions != null && ActiveTemplate.StatDefinitions.Count > 0)
            marker.Stats = ActiveTemplate.StatDefinitions.Select(sd => sd.ToStatValue()).ToList();
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

            if (template.StatDefinitions != null && template.StatDefinitions.Count > 0)
                marker.Stats = template.StatDefinitions.Select(sd => sd.ToStatValue()).ToList();
            else
                marker.Stats = null;
        }

        // Apply template defaults to existing players
        foreach (var player in PartyMembers)
        {
            player.HpMax = template.DefaultPlayerHpMax;
            player.Hp = template.DefaultPlayerHpMax;
            player.MpMax = template.DefaultPlayerMpMax;
            player.Mp = template.DefaultPlayerMpMax;
            player.Shield = 0;

            if (template.CounterDefinitions != null && template.CounterDefinitions.Count > 0)
                player.Counters = template.CounterDefinitions.Select(cd => cd.ToCounter()).ToList();
            else
                player.Counters = null;

            if (template.StatDefinitions != null && template.StatDefinitions.Count > 0)
                player.Stats = template.StatDefinitions.Select(sd => sd.ToStatValue()).ToList();
            else
                player.Stats = null;
        }
    }

    public void ClearActiveTemplate()
    {
        ActiveTemplate = null;
    }

    public void BroadcastTemplate()
    {
        if (relayClient is not { IsConnected: true } || !(IsGm || IsGmAsPlayer) || ActiveTemplate == null) return;

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
            CurrentMarkers.Markers[i].CopyFrom(cache.Markers[i]);

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
        var formula = ActiveTemplate?.DiceFormula ?? "1d100";
        var initStatId = ActiveTemplate?.InitiativeStatId;
        var diceMax = DiceEngine.GetMax(formula);

        var state = new TurnState
        {
            IsActive = true,
            Round = 1,
            DiceMax = diceMax,
        };

        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var marker = CurrentMarkers.Markers[i];
            if (!marker.HasData || string.IsNullOrEmpty(marker.Name)) continue;

            var roll = DiceEngine.Roll(formula);
            var (mod, statName) = GetInitiativeModifierAndName(marker.Stats, initStatId);

            state.Entries.Add(new TurnEntry
            {
                WaymarkIndex = i,
                Name = marker.Name,
                Initiative = roll + mod,
                InitiativeRoll = roll,
                InitiativeModifier = mod,
                InitiativeStatName = statName,
            });
        }

        foreach (var player in PartyMembers)
        {
            if (player.IsGm && !GmIsPlayer) continue;

            var roll = DiceEngine.Roll(formula);
            var (mod, statName) = GetInitiativeModifierAndName(player.Stats, initStatId);

            state.Entries.Add(new TurnEntry
            {
                PlayerHash = player.Hash,
                Name = player.Name,
                Initiative = roll + mod,
                InitiativeRoll = roll,
                InitiativeModifier = mod,
                InitiativeStatName = statName,
            });
        }

        state.Entries.Sort((a, b) => b.Initiative.CompareTo(a.Initiative));
        CurrentTurnState = state;

        // Afficher l'ordre d'initiative dans le chat
        PrintInitiativeOrder(state);

        BroadcastTurnState();
    }

    private static void PrintInitiativeOrder(TurnState state)
    {
        Plugin.ChatGui.Print($"[MasterEvent] {Loc.Get("Turns.InitiativeOrder")}");
        for (var i = 0; i < state.Entries.Count; i++)
        {
            var e = state.Entries[i];
            string detail;
            if (e.InitiativeStatName != null)
            {
                var modStr = e.InitiativeModifier >= 0 ? $"+{e.InitiativeModifier}" : e.InitiativeModifier.ToString();
                detail = $"{i + 1}. {e.Name} : {e.InitiativeRoll} ({e.InitiativeStatName} {modStr}) = {e.Initiative}";
            }
            else
            {
                detail = $"{i + 1}. {e.Name} : {e.Initiative}";
            }
            Plugin.ChatGui.Print($"[MasterEvent] {detail}");
        }
    }

    // Récupère le modificateur et le nom de la stat d'initiative.
    private static (int Modifier, string? StatName) GetInitiativeModifierAndName(List<StatValue>? stats, string? initStatId)
    {
        if (initStatId == null || stats == null) return (0, null);
        var stat = stats.FirstOrDefault(s => s.Id == initStatId);
        if (stat == null) return (0, null);
        return (stat.Modifier, stat.Name);
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

        // Décrémenter les tours restants des bonus/malus temporaires
        DecrementTempModTurns();

        ShowRoundToast(state.Round);
        BroadcastTurnState();
    }

    private void DecrementTempModTurns()
    {
        // Marqueurs
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var marker = CurrentMarkers[(WaymarkId)i];
            if (marker.TempModTurns > 0)
            {
                marker.TempModTurns--;
                if (marker.TempModTurns <= 0)
                {
                    marker.TempModifier = 0;
                    marker.TempModTurns = 0;
                }
            }
        }

        // Joueurs
        var playerChanged = false;
        foreach (var player in PartyMembers.Where(p => p.TempModTurns > 0))
        {
            player.TempModTurns--;
            if (player.TempModTurns <= 0)
            {
                player.TempModifier = 0;
                player.TempModTurns = 0;
            }
            playerChanged = true;
        }

        if (playerChanged)
            BroadcastPlayerUpdate();
    }

    public void ShowRoundToast(int round)
    {
        var text = string.Format(Loc.Get("Turns.Round"), round);
        roundOverlay?.Show(text);
    }

    public static void ShowTurnToast(string name)
    {
        var text = string.Format(Loc.Get("Turns.TurnToast"), name);
        Plugin.ToastGui.ShowQuest(text);
    }

    public static void ShowRoundEndToast(int round)
    {
        var text = string.Format(Loc.Get("Turns.RoundEnd"), round);
        Plugin.ToastGui.ShowQuest(text);
    }

    public void RerollInitiative(int index)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        if (index < 0 || index >= state.Entries.Count) return;

        var formula = ActiveTemplate?.DiceFormula ?? "1d100";
        var initStatId = ActiveTemplate?.InitiativeStatId;
        var entry = state.Entries[index];
        var (mod, statName) = GetInitiativeModifierForEntry(entry, initStatId);
        var roll = DiceEngine.Roll(formula);
        entry.InitiativeRoll = roll;
        entry.InitiativeModifier = mod;
        entry.InitiativeStatName = statName;
        entry.Initiative = roll + mod;

        state.Entries.Sort((a, b) => b.Initiative.CompareTo(a.Initiative));
        BroadcastTurnState();
    }

    public void RerollAllInitiative()
    {
        if (CurrentTurnState is not { IsActive: true } state) return;

        var formula = ActiveTemplate?.DiceFormula ?? "1d100";
        var initStatId = ActiveTemplate?.InitiativeStatId;
        state.DiceMax = DiceEngine.GetMax(formula);

        foreach (var entry in state.Entries)
        {
            var (mod, statName) = GetInitiativeModifierForEntry(entry, initStatId);
            var roll = DiceEngine.Roll(formula);
            entry.InitiativeRoll = roll;
            entry.InitiativeModifier = mod;
            entry.InitiativeStatName = statName;
            entry.Initiative = roll + mod;
        }

        state.Entries.Sort((a, b) => b.Initiative.CompareTo(a.Initiative));
        PrintInitiativeOrder(state);
        BroadcastTurnState();
    }

    public void AddTurnParticipant(TurnEntry entry)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;

        var formula = ActiveTemplate?.DiceFormula ?? "1d100";
        var initStatId = ActiveTemplate?.InitiativeStatId;
        var (mod, statName) = GetInitiativeModifierForEntry(entry, initStatId);
        var roll = DiceEngine.Roll(formula);
        entry.InitiativeRoll = roll;
        entry.InitiativeModifier = mod;
        entry.InitiativeStatName = statName;
        entry.Initiative = roll + mod;

        state.Entries.Add(entry);
        state.Entries.Sort((a, b) => b.Initiative.CompareTo(a.Initiative));
        BroadcastTurnState();
    }


    private (int Modifier, string? StatName) GetInitiativeModifierForEntry(TurnEntry entry, string? initStatId)
    {
        if (initStatId == null) return (0, null);

        if (entry.IsMarker && entry.WaymarkIndex.HasValue)
        {
            var marker = CurrentMarkers[(WaymarkId)entry.WaymarkIndex.Value];
            return GetInitiativeModifierAndName(marker.Stats, initStatId);
        }

        if (entry.PlayerHash != null)
        {
            var player = PartyMembers.FirstOrDefault(p => p.Hash == entry.PlayerHash);
            return GetInitiativeModifierAndName(player?.Stats, initStatId);
        }

        return (0, null);
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
        if (relayClient is not { IsConnected: true } || !CanEdit) return;
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
        if (relayClient is not { IsConnected: true } || !CanEdit) return;

        var msg = new RelayMessage { Type = MessageType.TurnClear };
        _ = relayClient.SendAsync(msg);
    }
}
