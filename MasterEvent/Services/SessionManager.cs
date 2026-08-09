using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
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
    public bool ShowDiceAnimation { get; set; } = true;
    private float diceAnimationSpeed = 1f;
    public float DiceAnimationSpeed
    {
        get => diceAnimationSpeed;
        set
        {
            diceAnimationSpeed = value;
            if (diceRollOverlay is { } overlay) overlay.SpeedMultiplier = value;
        }
    }
    public bool CanEdit => IsGm || IsPromoted || IsGmAsPlayer;
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
    public bool IsAwaitingApproval { get; set; }

    /// File d'admission telle que le relais la présente au MJ.
    public List<PendingMember> PendingMembers { get; } = new();

    /// Demande au plugin de renvoyer son `join` (approbation reçue, ou redirection de lobby).
    public Action? OnRejoinRequested { get; set; }

    /// Le relais a rattaché notre party à un autre lobby : il faut l'y suivre.
    public Action<string>? OnLobbyMoved { get; set; }
    public string? LocalGroupId { get; set; }

    private static readonly char[] AllianceCharset = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    public static string GenerateAllianceCode()
    {
        var code = new char[6];
        for (var i = 0; i < 6; i++)
            code[i] = AllianceCharset[RandomNumberGenerator.GetInt32(AllianceCharset.Length)];
        return new string(code);
    }

    public event Action<bool>? OnPromotionChanged;
    public Action? OnAllianceKicked { get; set; }
    public Action<string>? OnAllianceInvite { get; set; }
    public Action? OnAllianceDisband { get; set; }

    public List<PlayerData> PartyMembers { get; } = new();

    private readonly SaveManager saveManager = new(pluginConfigDir);
    private readonly TemplateManager templateManager = new(pluginConfigDir);
    private readonly GmCacheStore cacheStore = new(pluginConfigDir);
    public CloudSyncService? CloudSync { get; set; }
    private RelayClient? relayClient;
    private RoundAnnouncementOverlay? roundOverlay;
    private DiceRollOverlay? diceRollOverlay;
    private WeatherService? weatherService;
    private readonly Dictionary<WaymarkId, int> movingWaymarks = new();
    private const int MoveDelayFrames = 10;
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
        diceRollOverlay.SpeedMultiplier = diceAnimationSpeed;
    }

    public void SetWeatherService(WeatherService service)
    {
        weatherService = service;
    }

    public void TickTimeOverride()
    {
        weatherService?.TickTimeOverride();
    }

    public bool IsWeatherEngineReady => weatherService?.IsReady ?? false;
    public bool IsWeatherOverrideActive => weatherService?.IsWeatherOverrideActive ?? false;
    public bool IsTimeOverrideActive => weatherService?.IsTimeOverrideActive ?? false;

    public void DisposeWeatherService()
    {
        weatherService?.Dispose();
    }

    public Dictionary<byte, string> GetAvailableWeathers()
    {
        return weatherService?.GetWeathersForCurrentZone() ?? WeatherService.FallbackWeathers;
    }
    public uint GetWeatherIconId(byte weatherId)
    {
        return weatherService?.GetWeatherIconId(weatherId) ?? 0;
    }
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

    public Func<NpcSyncData[]>? NpcSyncProvider { get; set; }
    public Action<NpcSyncData[]?>? OnRemoteNpcSync { get; set; }
    public void ApplyRemoteNpcs(NpcSyncData[]? npcs) => OnRemoteNpcSync?.Invoke(npcs);

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
            Npcs = NpcSyncProvider?.Invoke(),
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

    // Formate le détail des dés individuels (ex: "14 + 13") ou vide si un seul dé.
    private static string FormatRollBreakdown(int[]? rolls)
    {
        if (rolls is not { Length: > 1 }) return string.Empty;
        return rolls.Length > 6
            ? string.Join(" + ", rolls[..5]) + " + ..."
            : string.Join(" + ", rolls);
    }

    private static string FormatRollChat(string name, int rawRoll, int diceMax, int totalModifier, int total, string? statName, int[]? rolls, RollOutcome outcome = default)
    {
        var modifierStr = totalModifier >= 0 ? $"+{totalModifier}" : totalModifier.ToString();
        var breakdown = FormatRollBreakdown(rolls);
        var hasBreakdown = breakdown.Length > 0;

        if (outcome is { Target: { } target, Success: { } success })
        {
            var verdict = Loc.Get(success ? "Chat.RollSuccess" : "Chat.RollFailure");
            var line = string.Format(Loc.Get("Chat.StatRollTarget"), name, rawRoll, diceMax,
                statName ?? "?", target, verdict);
            return hasBreakdown ? $"{line} {breakdown}" : line;
        }

        if (statName != null)
        {
            return hasBreakdown
                ? string.Format(Loc.Get("Chat.StatRollMulti"), name, rawRoll, diceMax, modifierStr, total, statName, breakdown)
                : string.Format(Loc.Get("Chat.StatRoll"), name, rawRoll, diceMax, modifierStr, total, statName);
        }

        return hasBreakdown
            ? string.Format(Loc.Get("Chat.RollMulti"), name, rawRoll, diceMax, breakdown)
            : string.Format(Loc.Get("Chat.Roll"), name, total, diceMax);
    }

    public void RollDiceWithStat(WaymarkId waymarkId, string? statId = null)
    {
        var marker = CurrentMarkers[waymarkId];
        var name = marker.Name;
        if (string.IsNullOrWhiteSpace(name)) return;

        var formula = ActiveTemplate?.DiceFormula ?? "1d100";
        var detail = DiceEngine.RollDetailed(formula);
        var rawRoll = detail.Sum;
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

        // En mode cible, `Total` vaut le dé brut et le seuil visé est porté à part.
        // `statName` n'est renseigné que si une stat a effectivement été trouvée : c'est ce qui
        // distingue un jet de stat d'un jet libre.
        var outcome = DiceEngine.Resolve(ActiveTemplate, rawRoll, statName != null ? modifier : null, tempMod);
        var total = outcome.Total;

        var rolls = detail.Rolls.Length > 1 ? detail.Rolls : null;

        var result = new DiceResult
        {
            RollerName = name,
            StatName = statName,
            RawRoll = rawRoll,
            Modifier = totalModifier,
            Total = total,
            DiceMax = diceMax,
            Target = outcome.Target,
            Success = outcome.Success,
            IndividualRolls = rolls,
        };
        AddRollToHistory(result);

        var chatMsg = FormatRollChat(name, rawRoll, diceMax, totalModifier, total, statName, rolls, outcome);
        if (ShowDiceAnimation && diceRollOverlay != null)
        {
            diceRollOverlay.Show(name, total, diceMax, rawRoll, modifier, tempMod, statName, rolls,
                ActiveTemplate?.CriticalSuccessThreshold ?? 0,
                ActiveTemplate?.CriticalFailureThreshold ?? 0,
                ActiveTemplate?.RollLowerIsBetter ?? false,
                outcome.Target, outcome.Success);
            diceRollOverlay.DeferAction(() =>
            {
                marker.LastRollResult = total;
                marker.LastRollMax = diceMax;
            });
            diceRollOverlay.DeferChatMessage(chatMsg);
        }
        else
        {
            marker.LastRollResult = total;
            marker.LastRollMax = diceMax;
            Plugin.ChatGui.Print(chatMsg);
        }

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
                RollDice = rolls,
                RollTarget = outcome.Target,
                RollSuccess = outcome.Success,
            };
            _ = relayClient.SendAsync(msg);
        }
    }

    public void RollDiceForPlayer(string playerHash, string? statId = null)
    {
        var player = PartyMembers.FirstOrDefault(p => p.Hash == playerHash);
        if (player == null) return;

        var formula = ActiveTemplate?.DiceFormula ?? "1d100";
        var detail = DiceEngine.RollDetailed(formula);
        var rawRoll = detail.Sum;
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

        // En mode cible, `Total` vaut le dé brut et le seuil visé est porté à part.
        // `statName` n'est renseigné que si une stat a effectivement été trouvée : c'est ce qui
        // distingue un jet de stat d'un jet libre.
        var outcome = DiceEngine.Resolve(ActiveTemplate, rawRoll, statName != null ? modifier : null, tempMod);
        var total = outcome.Total;

        var rolls = detail.Rolls.Length > 1 ? detail.Rolls : null;

        var result = new DiceResult
        {
            RollerName = player.Name,
            RollerHash = playerHash,
            StatName = statName,
            RawRoll = rawRoll,
            Modifier = totalModifier,
            Total = total,
            DiceMax = diceMax,
            Target = outcome.Target,
            Success = outcome.Success,
            IndividualRolls = rolls,
        };
        AddRollToHistory(result);

        // Affiche ou diffère le message chat jusqu'à la fin de l'animation
        var chatMsg = FormatRollChat(player.Name, rawRoll, diceMax, totalModifier, total, statName, rolls, outcome);
        if (ShowDiceAnimation && diceRollOverlay != null)
        {
            diceRollOverlay.Show(player.Name, total, diceMax, rawRoll, modifier, tempMod, statName, rolls,
                ActiveTemplate?.CriticalSuccessThreshold ?? 0,
                ActiveTemplate?.CriticalFailureThreshold ?? 0,
                ActiveTemplate?.RollLowerIsBetter ?? false,
                outcome.Target, outcome.Success);
            diceRollOverlay.DeferChatMessage(chatMsg);
        }
        else
        {
            Plugin.ChatGui.Print(chatMsg);
        }

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
                RollDice = rolls,
                RollTarget = outcome.Target,
                RollSuccess = outcome.Success,
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

    public void AdmitPending(string playerHash)
    {
        if (relayClient is not { IsConnected: true }) return;
        if (!IsGm && !IsPromoted) return;

        _ = relayClient.SendAsync(new RelayMessage
        {
            Type = MessageType.Admit,
            TargetHash = playerHash,
        });

        PendingMembers.RemoveAll(p => p.Hash == playerHash);
    }

    public void DenyPending(string playerHash)
    {
        if (relayClient is not { IsConnected: true }) return;
        if (!IsGm && !IsPromoted) return;

        _ = relayClient.SendAsync(new RelayMessage
        {
            Type = MessageType.Deny,
            TargetHash = playerHash,
        });

        PendingMembers.RemoveAll(p => p.Hash == playerHash);
    }

    public void SendPlayerStatUpdate()
    {
        if (relayClient is not { IsConnected: true }) return;

        var player = PartyMembers.FirstOrDefault(p => p.Hash == LocalPlayerHash);
        if (player == null) return;

        var msg = new RelayMessage
        {
            Type = MessageType.PlayerStatUpdate,
            PlayerHash = LocalPlayerHash,
            Hp = player.Hp,
            HpMax = player.HpMax,
            Mp = player.Mp,
            MpMax = player.MpMax,
            Stats = player.Stats?.Select(s => s.DeepCopy()).ToArray(),
            Counters = player.Counters?.Select(c => c.DeepCopy()).ToArray(),
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
        CloudSync?.QueueSheetPush(sheet.Name);
    }

    public PlayerSheet? LoadPlayerSheet(string name)
    {
        return saveManager.LoadSheet(name);
    }

    public void DeletePlayerSheet(string name)
    {
        saveManager.DeleteSheet(name);
        CloudSync?.QueueDelete("sheet", name);
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

    // Sérialise un EventTemplate vers le payload JSON attendu par le serveur.
    private static Dictionary<string, object?> BuildTemplatePayload(EventTemplate template)
    {
        return new Dictionary<string, object?>
        {
            ["Name"] = template.Name,
            ["ShowHpBar"] = template.ShowHpBar,
            ["HpMode"] = (int)template.HpMode,
            ["ShowMpBar"] = template.ShowMpBar,
            ["MpMode"] = (int)template.MpMode,
            ["ShowShield"] = template.ShowShield,
            ["DiceMax"] = template.DiceMax,
            ["DiceFormula"] = template.DiceFormula,
            ["InitiativeStatId"] = template.InitiativeStatId,
            ["DefaultHpMax"] = template.DefaultHpMax,
            ["DefaultMpMax"] = template.DefaultMpMax,
            ["DefaultPlayerHpMax"] = template.DefaultPlayerHpMax,
            ["DefaultPlayerMpMax"] = template.DefaultPlayerMpMax,
            ["CounterDefinitions"] = template.CounterDefinitions,
            ["StatDefinitions"] = template.StatDefinitions,
        };
    }

    public record ExportResult(string Code, int Version);
    public static async Task<ExportResult?> ExportTemplateAsync(EventTemplate template, string relayUrl, bool permanent = false, string? leaderToken = null)
    {
        try
        {
            var baseUrl = GetRelayHttpBase(relayUrl);
            var payload = BuildTemplatePayload(template);
            if (permanent)
                payload["permanent"] = true;

            var json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/templates");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(leaderToken))
                request.Headers.Add("X-Leader-Token", leaderToken);

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var responseJson = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(responseJson);
            var code = doc.RootElement.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : null;
            var version = doc.RootElement.TryGetProperty("version", out var verProp) ? verProp.GetInt32() : 1;
            return code != null ? new ExportResult(code, version) : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Failed to export template: {ex.Message}");
            return null;
        }
    }

    // Télécharge un modèle par code. Marque le template retourné comme abonnement lecture seule.
    public static async Task<EventTemplate?> ImportTemplateAsync(string code, string relayUrl)
    {
        try
        {
            var baseUrl = GetRelayHttpBase(relayUrl);
            var normalizedCode = code.Trim().ToUpperInvariant();
            var response = await httpClient.GetAsync($"{baseUrl}/api/templates/{normalizedCode}");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataProp))
                return null;

            var template = JsonSerializer.Deserialize<EventTemplate>(dataProp.GetRawText());
            if (template == null) return null;

            template.SourceCode = normalizedCode;
            template.SourceVersion = doc.RootElement.TryGetProperty("version", out var verProp) ? verProp.GetInt32() : 1;
            template.IsSubscription = true;
            return template;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Failed to import template: {ex.Message}");
            return null;
        }
    }

    public static async Task<int?> PublishTemplateUpdateAsync(EventTemplate template, string relayUrl, string leaderToken)
    {
        if (string.IsNullOrEmpty(template.SourceCode))
        {
            Plugin.Log.Warning("[MasterEvent] PublishTemplateUpdateAsync appelé sans SourceCode.");
            return null;
        }

        try
        {
            var baseUrl = GetRelayHttpBase(relayUrl);
            var payload = BuildTemplatePayload(template);
            var json = JsonSerializer.Serialize(payload);

            using var request = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api/templates/{template.SourceCode}");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.Add("X-Leader-Token", leaderToken);

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Plugin.Log.Warning($"[MasterEvent] PublishTemplateUpdateAsync : HTTP {(int)response.StatusCode}");
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.TryGetProperty("version", out var verProp) ? verProp.GetInt32() : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Failed to publish template update: {ex.Message}");
            return null;
        }
    }

    // Récupère uniquement la version d'un modèle publié (requête légère pour polling).
    public static async Task<int?> CheckTemplateVersionAsync(string code, string relayUrl)
    {
        try
        {
            var baseUrl = GetRelayHttpBase(relayUrl);
            var response = await httpClient.GetAsync($"{baseUrl}/api/templates/{code}/version");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("version", out var verProp) ? verProp.GetInt32() : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[MasterEvent] CheckTemplateVersionAsync failed for {code}: {ex.Message}");
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

            var hash = Plugin.GeneratePlayerHash(member.ContentId);
            seen.Add(hash);

            var existing = PartyMembers.FirstOrDefault(p => p.Hash == hash);
            if (existing != null)
            {
                existing.Name = member.Name.ToString();
                existing.IsGm = i == leaderIndex;
                // En mode alliance, assigner le groupe local
                if (IsAllianceMode && existing.GroupId == null && !string.IsNullOrEmpty(LocalGroupId))
                {
                    existing.GroupId = LocalGroupId;
                    existing.GroupLabel = GetOrAssignGroupLabel(LocalGroupId);
                }
            }
            else
            {
                var defaultHpMax = ActiveTemplate?.DefaultPlayerHpMax ?? 100;
                var defaultMpMax = ActiveTemplate?.DefaultPlayerMpMax ?? 100;
                var groupId = IsAllianceMode ? LocalGroupId : null;
                var groupLabel = IsAllianceMode && !string.IsNullOrEmpty(LocalGroupId) ? GetOrAssignGroupLabel(LocalGroupId) : null;
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
                    GroupId = groupId,
                    GroupLabel = groupLabel,
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

    // Groupes connus dans l'alliance (groupId → label attribué)
    private readonly Dictionary<string, string> allianceGroupLabels = new();
    private static readonly string[] GroupLetters = ["A", "B", "C", "D", "E", "F", "G", "H"];
    private string GetOrAssignGroupLabel(string? groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return "?";
        if (allianceGroupLabels.TryGetValue(groupId, out var label)) return label;

        var nextIndex = allianceGroupLabels.Count;
        label = nextIndex < GroupLetters.Length ? GroupLetters[nextIndex] : $"G{nextIndex + 1}";
        allianceGroupLabels[groupId] = label;
        return label;
    }

    public Dictionary<string, int> GetGroupCounts()
    {
        var counts = new Dictionary<string, int>();
        foreach (var p in PartyMembers.Where(p => !p.IsGm))
        {
            var label = p.GroupLabel ?? "?";
            counts[label] = counts.GetValueOrDefault(label, 0) + 1;
        }
        return counts;
    }

    // Ajoute un joueur alliance (d'un autre groupe FFXIV) à la liste des membres.
    public void AddAlliancePlayer(string hash, string name, string? groupId = null)
    {
        if (PartyMembers.Any(p => p.Hash == hash)) return;

        var groupLabel = GetOrAssignGroupLabel(groupId);
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
            GroupId = groupId,
            GroupLabel = groupLabel,
        });

        if (IsGm && relayClient is { IsConnected: true })
            BroadcastPlayerUpdate();
    }

    // Retire un joueur alliance de la liste des membres et notifie le joueur kické.
    public void RemoveAlliancePlayer(string hash)
    {
        var removed = PartyMembers.RemoveAll(p => p.Hash == hash && p.IsAlliancePlayer);
        if (removed > 0 && IsGm && relayClient is { IsConnected: true })
        {
            // Notifier le joueur kické
            var kickMsg = new RelayMessage
            {
                Type = MessageType.AllianceKick,
                TargetHash = hash,
            };
            _ = relayClient.SendAsync(kickMsg);

            BroadcastPlayerUpdate();
        }
    }
    public void AssignLocalGroup()
    {
        if (string.IsNullOrEmpty(LocalGroupId)) return;
        var label = GetOrAssignGroupLabel(LocalGroupId);
        foreach (var p in PartyMembers.Where(p => !p.IsAlliancePlayer))
        {
            p.GroupId = LocalGroupId;
            p.GroupLabel = label;
        }
    }

    // Retire tous les joueurs alliance de la liste (appelé lors de la désactivation du mode alliance).
    public void ClearAlliancePlayers()
    {
        PartyMembers.RemoveAll(p => p.IsAlliancePlayer);
        allianceGroupLabels.Clear();
        // Nettoyer les labels des joueurs locaux
        foreach (var p in PartyMembers)
        {
            p.GroupId = null;
            p.GroupLabel = null;
        }
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
        CloudSync?.QueueTemplatePush(template.Name);
    }

    public EventTemplate? LoadTemplate(string name)
    {
        return templateManager.LoadTemplate(name);
    }

    public void DeleteTemplate(string name)
    {
        templateManager.DeleteTemplate(name);
        CloudSync?.QueueDelete("template", name);
    }

    public List<string> GetTemplateNames()
    {
        return templateManager.GetTemplateNames();
    }

    public EventTemplate GetOrCreateDefaultTemplate()
    {
        return templateManager.GetOrCreateDefault();
    }

    // Retourne le template local abonné pour un code donné (ou null si aucun / non abonné).
    public EventTemplate? FindSubscribedTemplateByCode(string code)
    {
        foreach (var name in GetTemplateNames())
        {
            var tpl = LoadTemplate(name);
            if (tpl != null && tpl.IsSubscription && tpl.SourceCode == code)
                return tpl;
        }
        return null;
    }

    // Pull la dernière version d'un template abonné et écrase le local.
    // Signale aussi l'utilisateur si des fiches RP sont rattachées à ce modèle.
    public async Task PullSubscribedTemplateAsync(string templateName, string relayUrl)
    {
        var local = LoadTemplate(templateName);
        if (local is not { IsSubscription: true } || string.IsNullOrEmpty(local.SourceCode)) return;

        var remote = await ImportTemplateAsync(local.SourceCode, relayUrl);
        if (remote == null) return;

        // Conserver le nom local si l'utilisateur l'avait adapté
        // (non — le design valide le rename du créateur, on prend celui du serveur).
        SaveTemplate(remote);

        // Si le template mis à jour est actif, ré-appliquer pour refléter les changements en jeu.
        if (ActiveTemplate?.Name == templateName || ActiveTemplate?.Name == remote.Name)
            ApplyTemplate(remote);

        // Aligne les fiches RP rattachées avec les nouvelles définitions du modèle
        // (ajout/retrait/renommage des stats et counters par Id, autoritaire).
        var syncReports = new List<(string Sheet, TemplateSyncHelper.SheetSyncReport Report)>();
        foreach (var sheetName in saveManager.GetSheetNames())
        {
            var sheet = saveManager.LoadSheet(sheetName);
            if (sheet == null || sheet.TemplateName != templateName) continue;

            var report = TemplateSyncHelper.SyncSheetWithTemplate(sheet, remote);
            if (!report.HasChanges) continue;

            saveManager.SaveSheet(sheet);
            syncReports.Add((sheet.Name, report));
        }

        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            Plugin.ChatGui.Print(string.Format(
                Loc.Get("Chat.TemplateUpdated"),
                remote.Name, remote.SourceVersion));

            if (syncReports.Count > 0)
            {
                Plugin.ChatGui.Print(string.Format(
                    Loc.Get("Chat.SheetsAutoSyncedHeader"),
                    syncReports.Count));
                foreach (var (sheetName, r) in syncReports)
                {
                    Plugin.ChatGui.Print(string.Format(
                        Loc.Get("Chat.SheetAutoSyncedLine"),
                        sheetName,
                        r.StatsAdded, r.StatsRemoved, r.StatsRenamed,
                        r.CountersAdded, r.CountersRemoved, r.CountersRenamed));
                }
            }
        });
    }

    // Vérifie au démarrage si des mises à jour sont disponibles pour les modèles abonnés.
    public async Task CheckAllSubscriptionsAsync(string relayUrl)
    {
        var subscriptions = GetTemplateNames()
            .Select(LoadTemplate)
            .Where(t => t is { IsSubscription: true } && !string.IsNullOrEmpty(t.SourceCode))
            .ToList();

        foreach (var tpl in subscriptions)
        {
            var remoteVersion = await CheckTemplateVersionAsync(tpl!.SourceCode!, relayUrl);
            if (remoteVersion == null || remoteVersion <= tpl.SourceVersion) continue;

            Plugin.Log.Info($"[MasterEvent] Modèle '{tpl.Name}' (code {tpl.SourceCode}) : v{tpl.SourceVersion} → v{remoteVersion}, pull automatique.");
            await PullSubscribedTemplateAsync(tpl.Name, relayUrl);
        }
    }

    // Modèles partagés
    public List<SharedTemplate> GetSharedTemplates() => saveManager.LoadSharedTemplates();
    public void AddSharedTemplate(SharedTemplate shared) => saveManager.AddSharedTemplate(shared);
    public void RemoveSharedTemplate(string code) => saveManager.RemoveSharedTemplate(code);

    //  GM Cache — la persistance est déléguée à GmCacheStore ; on ne garde ici que
    //  la construction du snapshot et la restauration (logique métier).
    public void SaveGmCache()
    {
        var cache = new GmCache
        {
            Markers = CurrentMarkers.Markers.Select(m => m.DeepCopy()).ToArray(),
            HpMode = HpMode.ToString(),
            MpMode = MpMode.ToString(),
            ShowMpBar = ShowMpBar,
            ShowShield = ShowShield,
            DiceMax = DiceMax,
            ActiveTemplate = ActiveTemplate?.DeepCopy(),
        };
        cacheStore.Save(cache);
    }

    public GmCache? LoadGmCache() => cacheStore.Load();

    public void DeleteGmCache() => cacheStore.Delete();

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

    internal static void PrintInitiativeOrder(TurnState state)
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

    /// Index de l'entrée dont c'est le tour : la première qui n'a pas encore agi. Partagé par
    /// l'overlay et le contrôle de fin de tour pour qu'ils ne puissent pas désigner deux acteurs
    /// différents.
    public int ActiveTurnIndex
    {
        get
        {
            if (CurrentTurnState is not { IsActive: true } state) return -1;
            for (var i = 0; i < state.Entries.Count; i++)
                if (!state.HasEntryActed(state.Entries[i])) return i;
            return -1;
        }
    }

    /// Vrai si c'est au joueur local d'agir. Sert à n'ouvrir le bouton de fin de tour qu'à
    /// l'intéressé, sans lui donner la main sur le reste du bandeau.
    public bool IsLocalPlayerTurn
    {
        get
        {
            var idx = ActiveTurnIndex;
            if (idx < 0 || CurrentTurnState is not { } state) return false;
            var hash = state.Entries[idx].PlayerHash;
            return hash != null && hash == LocalPlayerHash;
        }
    }

    /// Envoie au MJ la demande de fin de tour du joueur local. Le joueur n'a pas le droit
    /// d'écrire l'état des tours : le relais rejette `turnUpdate` venant d'un non-leader.
    public void RequestEndOwnTurn()
    {
        if (relayClient is not { IsConnected: true }) return;
        if (!IsLocalPlayerTurn) return;

        // Le MJ, lui, agit directement : inutile de passer par le réseau.
        if (CanEdit)
        {
            ToggleHasActed(ActiveTurnIndex);
            return;
        }

        _ = relayClient.SendAsync(new RelayMessage
        {
            Type = MessageType.TurnEndSelf,
            PlayerHash = LocalPlayerHash,
        });
    }

    /// Applique la demande d'un joueur, côté MJ uniquement. On revérifie que le demandeur est
    /// bien l'acteur courant : sans ce contrôle, n'importe qui pourrait clore le tour d'autrui.
    public void ApplyTurnEndRequest(string playerHash)
    {
        if (!CanEdit) return;
        var idx = ActiveTurnIndex;
        if (idx < 0 || CurrentTurnState is not { } state) return;
        if (state.Entries[idx].PlayerHash != playerHash) return;

        ToggleHasActed(idx);
    }

    public void ToggleHasActed(int index)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        if (index < 0 || index >= state.Entries.Count) return;

        var entry = state.Entries[index];
        var group = state.FindGroupFor(entry);

        // Résolution du HasActed au niveau groupe ou entry selon l'appartenance
        bool newHasActed;
        if (group != null)
        {
            newHasActed = !group.HasActed;
            group.HasActed = newHasActed;
        }
        else
        {
            newHasActed = !entry.HasActed;
            entry.HasActed = newHasActed;
        }

        // Just checked = has played → announce next bloc (group or solo) or end of round
        if (newHasActed)
        {
            var nextNames = GetNextBlockNames(state);
            if (nextNames.Count > 0)
                ShowTurnToast(FormatNameList(nextNames));
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
        foreach (var group in state.Groups)
            group.HasActed = false;

        // Décrémenter les tours restants des bonus/malus temporaires
        DecrementTempModTurns();

        ShowRoundToast(state.Round);
        BroadcastTurnState();
    }

    // Retourne la liste des noms du prochain bloc (groupe ou solo) qui n'a pas encore joué.
    internal static List<string> GetNextBlockNames(TurnState state)
    {
        var seenGroupIds = new HashSet<string>();
        foreach (var entry in state.Entries)
        {
            if (entry.GroupId != null)
            {
                if (seenGroupIds.Contains(entry.GroupId)) continue;
                seenGroupIds.Add(entry.GroupId);
                var group = state.Groups.FirstOrDefault(g => g.Id == entry.GroupId);
                if (group != null && !group.HasActed)
                {
                    return state.Entries.Where(e => e.GroupId == entry.GroupId).Select(e => e.Name).ToList();
                }
            }
            else if (!entry.HasActed)
            {
                return new List<string> { entry.Name };
            }
        }
        return new List<string>();
    }

    internal static string FormatNameList(List<string> names)
    {
        if (names.Count == 0) return string.Empty;
        if (names.Count == 1) return names[0];
        if (names.Count == 2) return $"{names[0]}{Loc.Get("Turns.NamesAnd")}{names[1]}";
        return string.Join(", ", names.Take(names.Count - 1)) + Loc.Get("Turns.NamesAnd") + names[^1];
    }

    // Trie les entries par initiative DESC tout en gardant les membres d'un même groupe contigus.
    // Chaque groupe est positionné selon la meilleure initiative de ses membres.
    private static void SortEntriesPreservingGroups(TurnState state)
    {
        // Construction des blocs : chaque solo = 1 bloc, chaque groupe = 1 bloc (tous ses membres).
        var blocks = new List<(int MaxInit, List<TurnEntry> Members)>();
        var processedGroupIds = new HashSet<string>();

        foreach (var entry in state.Entries)
        {
            if (entry.GroupId == null)
            {
                blocks.Add((entry.Initiative, new List<TurnEntry> { entry }));
            }
            else if (processedGroupIds.Add(entry.GroupId))
            {
                var members = state.Entries
                    .Where(e => e.GroupId == entry.GroupId)
                    .OrderByDescending(e => e.Initiative)
                    .ToList();
                blocks.Add((members[0].Initiative, members));
            }
        }

        blocks.Sort((a, b) => b.MaxInit.CompareTo(a.MaxInit));
        state.Entries = blocks.SelectMany(b => b.Members).ToList();
    }

    public string? CreateGroup(int entryIdx1, int entryIdx2)
    {
        if (CurrentTurnState is not { IsActive: true } state) return null;
        if (entryIdx1 < 0 || entryIdx1 >= state.Entries.Count) return null;
        if (entryIdx2 < 0 || entryIdx2 >= state.Entries.Count) return null;
        if (entryIdx1 == entryIdx2) return null;

        var e1 = state.Entries[entryIdx1];
        var e2 = state.Entries[entryIdx2];
        if (e1.GroupId != null || e2.GroupId != null) return null;

        var group = new TurnGroup
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Label = string.Format(Loc.Get("Turns.GroupDefaultLabel"), state.Groups.Count + 1),
        };
        state.Groups.Add(group);
        e1.GroupId = group.Id;
        e2.GroupId = group.Id;

        SortEntriesPreservingGroups(state);
        BroadcastTurnState();
        return group.Id;
    }

    // Ajoute une entry solo à un groupe existant.
    public void AddToGroup(int entryIdx, string groupId)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        if (entryIdx < 0 || entryIdx >= state.Entries.Count) return;
        if (state.Groups.All(g => g.Id != groupId)) return;

        var entry = state.Entries[entryIdx];
        if (entry.GroupId != null) return;

        entry.GroupId = groupId;
        SortEntriesPreservingGroups(state);
        BroadcastTurnState();
    }

    // Détache une entry de son groupe. Si le groupe devient singleton ou vide, il est supprimé.
    public void RemoveFromGroup(int entryIdx)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        if (entryIdx < 0 || entryIdx >= state.Entries.Count) return;

        var entry = state.Entries[entryIdx];
        if (entry.GroupId == null) return;

        var groupId = entry.GroupId;
        entry.GroupId = null;

        // Nettoyage : si moins de 2 membres restants, le groupe perd sa raison d'être
        var remaining = state.Entries.Where(e => e.GroupId == groupId).ToList();
        if (remaining.Count < 2)
        {
            foreach (var e in remaining)
                e.GroupId = null;
            state.Groups.RemoveAll(g => g.Id == groupId);
        }

        SortEntriesPreservingGroups(state);
        BroadcastTurnState();
    }

    public void RenameGroup(string groupId, string newLabel)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        var group = state.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null) return;
        group.Label = newLabel;
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

    // Affiche une annonce libre du MJ : overlay rouge rubis à l'écran + message dans le chat,
    // et diffuse aux autres joueurs connectés.
    public void ShowGmAnnouncement(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var trimmed = message.Trim();

        // Affichage local
        ApplyGmAnnouncement(trimmed);

        // Diffusion aux autres clients
        if (relayClient is { IsConnected: true } && CanEdit)
        {
            var msg = new RelayMessage
            {
                Type = MessageType.GmAnnouncement,
                AnnouncementText = trimmed,
            };
            _ = relayClient.SendAsync(msg);
        }
    }

    // Applique une annonce MJ reçue (locale ou distante) : overlay + chat.
    // Durée d'affichage proportionnelle à la longueur : ~0,06s par caractère,
    // bornée entre 3s (messages très courts) et 10s (limite haute des 180 chars).
    public void ApplyGmAnnouncement(string message)
    {
        var hold = Math.Clamp(message.Length * 0.06f, 3f, 10f);
        roundOverlay?.Show(message, RoundAnnouncementOverlay.RubyRgb, holdDurationSeconds: hold);
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.GmAnnouncement"), message));
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

        SortEntriesPreservingGroups(state);
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

        SortEntriesPreservingGroups(state);
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
        SortEntriesPreservingGroups(state);
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

        // Swap autorisé uniquement si les deux entries appartiennent au même groupe (ou sont toutes les deux solos).
        // Pour déplacer un groupe entier par rapport à un autre bloc, utiliser MoveGroupUp/Down.
        var a = state.Entries[index];
        var b = state.Entries[index - 1];
        if (a.GroupId != b.GroupId) return;

        (state.Entries[index], state.Entries[index - 1]) = (b, a);
        BroadcastTurnState();
    }

    public void MoveParticipantDown(int index)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        if (index < 0 || index >= state.Entries.Count - 1) return;

        var a = state.Entries[index];
        var b = state.Entries[index + 1];
        if (a.GroupId != b.GroupId) return;

        (state.Entries[index], state.Entries[index + 1]) = (b, a);
        BroadcastTurnState();
    }

     public void MoveGroupUp(string groupId)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        MoveBlock(state, groupId, direction: -1);
    }

    public void MoveGroupDown(string groupId)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        MoveBlock(state, groupId, direction: 1);
    }

    private static void MoveBlock(TurnState state, string groupId, int direction)
    {
        var groupEntries = state.Entries.Where(e => e.GroupId == groupId).ToList();
        if (groupEntries.Count == 0) return;

        var firstIdx = state.Entries.IndexOf(groupEntries[0]);
        var lastIdx = state.Entries.IndexOf(groupEntries[^1]);

        if (direction < 0 && firstIdx == 0) return;
        if (direction > 0 && lastIdx == state.Entries.Count - 1) return;

        // Identifie le bloc voisin dans la direction demandée
        var neighborIdx = direction < 0 ? firstIdx - 1 : lastIdx + 1;
        var neighbor = state.Entries[neighborIdx];
        List<TurnEntry> neighborBlock;
        if (neighbor.GroupId != null)
            neighborBlock = state.Entries.Where(e => e.GroupId == neighbor.GroupId).ToList();
        else
            neighborBlock = new List<TurnEntry> { neighbor };

        // Retirer les deux blocs puis réinsérer dans l'ordre inversé.
        // On part de la position du bloc le plus en amont avant retrait, en compensant si nécessaire.
        foreach (var e in groupEntries) state.Entries.Remove(e);
        foreach (var e in neighborBlock) state.Entries.Remove(e);

        var baseIdx = direction < 0 ? firstIdx - neighborBlock.Count : firstIdx;
        if (baseIdx < 0) baseIdx = 0;

        if (direction < 0)
        {
            // groupe d'abord, puis voisin
            foreach (var e in groupEntries) state.Entries.Insert(baseIdx++, e);
            foreach (var e in neighborBlock) state.Entries.Insert(baseIdx++, e);
        }
        else
        {
            // voisin d'abord, puis groupe
            foreach (var e in neighborBlock) state.Entries.Insert(baseIdx++, e);
            foreach (var e in groupEntries) state.Entries.Insert(baseIdx++, e);
        }
    }

    public void RemoveTurnParticipant(int index)
    {
        if (CurrentTurnState is not { IsActive: true } state) return;
        if (index < 0 || index >= state.Entries.Count) return;

        var entry = state.Entries[index];
        state.Entries.RemoveAt(index);

        // Si l'entry appartenait à un groupe, nettoyer si celui-ci devient singleton/vide
        if (entry.GroupId != null)
        {
            var remaining = state.Entries.Where(e => e.GroupId == entry.GroupId).ToList();
            if (remaining.Count < 2)
            {
                foreach (var e in remaining)
                    e.GroupId = null;
                state.Groups.RemoveAll(g => g.Id == entry.GroupId);
            }
        }

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
