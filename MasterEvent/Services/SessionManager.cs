using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using MasterEvent.Communication;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.UI;
using MasterEvent.UI.Components;
using MasterEvent.Waymarks;

namespace MasterEvent.Services;

public enum ScenePrimaryKind { None, Start, EndTurn, NextRound }

public enum PlayerPrimaryKind { None, Roll, EndTurn }

// Pourquoi « Finir mon tour » est grisé : déjà envoyé, ou personne pour appliquer la demande.
public enum EndTurnHold { None, Sent, Handover, GmAway }

public enum JoinRejection { None, Denied, RoomLimit, RateLimited, Invalid }

/// L'action pleine de la vue joueur. StatName : la stat du jet demandé, nulle pour « Lancer le dé ».
/// CanEndTurn : c'est le tour du joueur, à finir en action pleine ou, quand un jet attend, en teinté.
public readonly record struct PlayerPrimary(PlayerPrimaryKind Kind, string? StatName, bool CanEndTurn,
    bool GroupTurn, EndTurnHold Hold);

/// MJ de la scène vu du joueur. Name est nul tant que personne ne l'a nommé.
public readonly record struct GmPresenceInfo(string Hash, string? Name, bool Connected);

public class SessionManager(string pluginConfigDir)
{
    public MarkerSet CurrentMarkers { get; } = new();
    public MarkerSet SavedMarkers { get; private set; } = new();
    public bool IsGm { get; set; } = true;
    public string? RelayRoomKey { get; set; }
    public string? RoomFileToken { get; set; }
    public event Action? RoomJoined;
    public void NotifyRoomJoined() => RoomJoined?.Invoke();
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
    public bool HasGmAccess => IsGm || IsGmAsPlayer;
    public bool IsGmAsPlayer
    {
        get
        {
            if (!GmIsPlayer || IsGm) return false;
            if (IsLobbyMode) return false;

            var local = PartyMembers.FirstOrDefault(p => p.Hash == LocalPlayerHash);
            return local is { IsGm: true };
        }
    }
    public string LocalPlayerHash { get; private set; } = string.Empty;
    public bool IsConnected { get; set; }
    public int ConnectedPlayerCount { get; set; }
    public RelayLinkState LinkState
    {
        get
        {
            if (IsConnected) return RelayLinkState.InSession;
            if (relayClient is { IsConnected: true }) return RelayLinkState.ServerOnly;
            return relayClient is { IsReconnecting: true } ? RelayLinkState.Reconnecting : RelayLinkState.Offline;
        }
    }

    // La garde exacte d'AdmitPending, DenyPending et RequestRoll, qui sinon sortent sans rien dire.
    public bool IsRelayConnected => relayClient is { IsConnected: true };
    public int DiceMax { get; set; } = 999;
    public HpMode HpMode { get; set; } = HpMode.Points;
    public bool ShowMpBar { get; set; } = true;
    public bool ShowShield { get; set; } = true;
    public HpMode MpMode { get; set; } = HpMode.Points;
    public bool GmIsPlayer { get; set; }

    private EventTemplate? activeTemplate;

    public EventTemplate? ActiveTemplate
    {
        get => activeTemplate;
        set
        {
            activeTemplate = value;
            VitalLabels.ActiveTemplate = value;
        }
    }
    public TurnState? CurrentTurnState { get; set; }

    // Mode Alliance
    public string? LobbyCode { get; set; }
    public bool IsLobbyMode => !string.IsNullOrEmpty(LobbyCode);
    public bool IsAwaitingApproval { get; set; }

    /// Refus du dernier join, gardé pour la vue tant qu'une nouvelle tentative ne l'a pas remplacé.
    public JoinRejection JoinRejection { get; set; }

    /// Version minimale exigée par le relais qui a refusé ce client, nulle sans refus. Seul un plugin
    /// mis à jour en sort : le relais coupe la socket et la reconnexion automatique s'arrête.
    public string? RejectedMinVersion { get; set; }
    public bool IsVersionRejected => RejectedMinVersion != null;

    /// File d'admission telle que le relais la présente au MJ.
    public List<PendingMember> PendingMembers { get; } = new();

    /// <summary>
    /// Nombre de décisions à prendre dans la file. Les coéquipiers d'un même sous-groupe
    /// entrent ensemble sur une seule approbation : ils ne comptent que pour une. Un
    /// demandeur sans groupe connu compte pour lui-même.
    /// </summary>
    public int PendingGroupCount => PendingMembers
        .Select(p => p.GroupId ?? p.Hash)
        .Distinct()
        .Count();

    /// Demande au plugin de renvoyer son `join` (approbation reçue, ou redirection de lobby).
    public Action? OnRejoinRequested { get; set; }

    /// Le relais a rattaché notre party à un autre lobby : il faut l'y suivre.
    public Action<string>? OnLobbyMoved { get; set; }

    /// Le relais nous a placés dans le lobby de notre groupe sans code saisi : on en prend l'état.
    public Action<string>? OnLobbyAdopted { get; set; }

    /// Salle attendue au dernier `join` hors lobby (la party). Une autre salle confirmée est un lobby.
    public string? ExpectedRoomKey { get; set; }

    /// MJ de la salle selon le relais, inconnu tant qu'il ne l'a pas dit. Il prime sur le chef du
    /// groupe en jeu : après une passation au co-MJ, le chef du groupe n'est plus le MJ.
    public string? SceneLeaderHash
    {
        get => sceneLeaderHash;
        set
        {
            if (sceneLeaderHash == value) return;
            sceneLeaderHash = value;
            sceneLeaderName = null;
            OnSceneLeaderChanged?.Invoke();
        }
    }
    private string? sceneLeaderHash;
    // Nom reçu avec leaderChanged : le MJ n'est pas toujours dans la liste des membres.
    private string? sceneLeaderName;
    public Action? OnSceneLeaderChanged { get; set; }

    /// Passation en cours : le successeur désigné, ou personne quand le premier qui la réclame l'obtient.
    public bool HandoverPending { get; private set; }
    public string? HandoverTarget { get; private set; }
    public Action? OnHandoverPending { get; set; }
    public Action<NpcSyncData[]?>? OnSceneNpcsHandedOver { get; set; }
    public string? LocalGroupId { get; set; }

    private const int LobbyCodeLength = 6;
    private static readonly char[] LobbyCharset = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();
    private static readonly Regex LobbyCommandPrefix = new(@"^\s*(/\S+\s*)?((JOIN|REJOINDRE)\b)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LobbyCodeToken = new("[A-Z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string GenerateLobbyCode()
    {
        var code = new char[LobbyCodeLength];
        for (var i = 0; i < LobbyCodeLength; i++)
            code[i] = LobbyCharset[RandomNumberGenerator.GetInt32(LobbyCharset.Length)];
        return new string(code);
    }

    public static string FormatLobbyInvitation(string code) => $"{Constants.CommandName} join {code}";

    // Extrait un code d'une saisie libre : le code seul, en minuscules ou espacé comme l'affiche le
    // MJ, ou l'invitation entière collée telle quelle (« /masterevent join ABC234 »).
    public static bool TryParseLobbyCode(string? raw, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = LobbyCommandPrefix.Replace(raw.ToUpperInvariant(), string.Empty, 1);
        var token = LobbyCodeToken.Matches(text).Select(m => m.Value).LastOrDefault(IsLobbyCode);
        if (token != null)
        {
            code = token;
            return true;
        }

        var compact = new string(text.Where(char.IsLetterOrDigit).ToArray());
        if (!IsLobbyCode(compact)) return false;
        code = compact;
        return true;
    }

    private static bool IsLobbyCode(string value)
        => value.Length == LobbyCodeLength && value.All(c => Array.IndexOf(LobbyCharset, c) >= 0);

    public event Action<bool>? OnPromotionChanged;
    public Action? OnAllianceKicked { get; set; }
    public Action<string>? OnAllianceInvite { get; set; }
    public Action? OnAllianceDisband { get; set; }
    public RollRequest? PendingRollRequest { get; private set; }
    public Action? OnRollRequested { get; set; }

    private readonly HashSet<string> awaitingRoll = [];
    public bool IsAwaitingRoll(string playerHash) => awaitingRoll.Contains(playerHash);

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
    private static readonly TimeSpan StateBroadcastDelay = TimeSpan.FromMilliseconds(400);
    private DateTime? stateBroadcastDue;
    private bool stateBroadcastIsFull;

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

    public bool IsAmbianceReceived { get; private set; }

    // Applique une météo localement via WeatherService.
    public void ApplyWeather(byte weatherId, bool received = false)
    {
        CurrentWeatherId = weatherId;
        IsAmbianceReceived = received;
        weatherService?.SetWeather(weatherId);
    }

    // Envoie l'heure éorzéenne à tous les joueurs connectés.
    public void BroadcastTime(uint? eorzeaSeconds)
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
    public void ApplyTime(uint eorzeaSeconds, bool received = false)
    {
        CurrentEorzeaTime = eorzeaSeconds;
        IsAmbianceReceived = received;
        weatherService?.SetTime(eorzeaSeconds);
    }

    // Désactive l'override de l'heure éorzéenne.
    public void ClearTime()
    {
        CurrentEorzeaTime = null;
        weatherService?.ClearTime();
    }

    // L'horloge n'est relâchée que si l'override est le nôtre : WeatherService.ClearTime
    // rendrait aussi la main sur une heure posée par Brio ou Ktisis.
    public void ResetAmbiance()
    {
        weatherService?.SetWeather(0);
        if (IsTimeOverrideActive) weatherService?.ClearTime();
        CurrentWeatherId = 0;
        CurrentWeatherName = null;
        CurrentEorzeaTime = null;
        IsAmbianceReceived = false;
    }

    // L'ambiance reçue ne survit pas à la salle quittée. Le MJ de la scène garde la sienne, posée
    // par lui ou héritée d'une passation : à appeler avant que le rôle ne change.
    public void ReleaseReceivedAmbiance()
    {
        if (IsAmbianceReceived && !IsGm) ResetAmbiance();
    }

    public uint? CurrentEorzeaTime { get; set; }

    /// Dernière annonce reçue du MJ, gardée jusqu'au masquage, à l'annonce suivante ou à la sortie de salle.
    public string? LastGmAnnouncement { get; private set; }

    public void DismissGmAnnouncement() => LastGmAnnouncement = null;

    // Ce qu'une salle laisse chez un joueur et que rien ne lèverait s'il l'a quittée ou s'il a manqué
    // la fin : le relais ne renvoie au retour ni turnClear, ni la météo rendue au jeu. Le MJ de la
    // scène garde son combat et son ambiance. À appeler avant que le rôle ne change.
    public void ReleaseRoomState()
    {
        heldRoomStateDue = null;
        ReleaseReceivedAmbiance();
        LastGmAnnouncement = null;
        PendingRollRequest = null;
        EndTurnSentAt = null;
        // Seul leaderChanged lève une passation : celle d'une salle quittée resterait affichée. Le
        // relais la renvoie après joinConfirm si elle court toujours.
        HandoverPending = false;
        HandoverTarget = null;
        if (!IsGm) CurrentTurnState = null;
    }

    private static readonly TimeSpan HeldRoomStateLimit = TimeSpan.FromSeconds(3);
    private DateTime? heldRoomStateDue;
    private bool turnRefreshed, weatherRefreshed, timeRefreshed;

    // Retour dans la même salle après une coupure : le MJ renvoie combat et ambiance quelques
    // centaines de ms après joinConfirm. Les garder jusque-là évite de réimprimer l'ordre du tour,
    // de rejouer le toast et de faire sauter la météo ; ce qu'il n'a pas renvoyé tombe à l'échéance.
    public void HoldRoomStateForRefresh()
    {
        EndTurnSentAt = null;
        HandoverPending = false;
        HandoverTarget = null;
        heldRoomStateDue = DateTime.UtcNow + HeldRoomStateLimit;
        turnRefreshed = weatherRefreshed = timeRefreshed = false;
    }

    public void NoteWeatherRefreshed() => weatherRefreshed = true;

    public void NoteTimeRefreshed() => timeRefreshed = true;

    // Une nouvelle coupure suspend l'échéance : le joinConfirm suivant la relance.
    public void ExpireHeldRoomState()
    {
        if (heldRoomStateDue is not { } due || DateTime.UtcNow < due || !IsConnected) return;
        heldRoomStateDue = null;
        if (IsGm) return;

        if (!turnRefreshed) CurrentTurnState = null;
        if (!weatherRefreshed && !timeRefreshed)
        {
            ReleaseReceivedAmbiance();
            return;
        }

        if (!IsAmbianceReceived) return;
        if (!weatherRefreshed && CurrentWeatherId != 0)
        {
            ApplyWeather(0, received: true);
            CurrentWeatherName = null;
        }
        if (!timeRefreshed && CurrentEorzeaTime != null) ClearTime();
    }

    public void SyncWaymarks()
    {
        WaymarkManager.SyncWaymarkVisibility(CurrentMarkers);
    }

    public void PollWaymarkChanges()
    {
        // Celui qui dirige la scène possède les waymarks du jeu, même ceux reçus quand il était joueur :
        // MasterEvent ne les retirera plus s'il redevient joueur ailleurs.
        Array.Clear(placedWaymarks);

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


    public Func<IReadOnlyList<(string id, string name, List<StatValue>? stats)>>? NpcParticipantProvider { get; set; }
    public Action<EventTemplate>? NpcTemplateApplier { get; set; }
    public Func<NpcSyncData[]>? NpcSyncProvider { get; set; }
    public Action<NpcSyncData[]?>? OnRemoteNpcSync { get; set; }
    public void ApplyRemoteNpcs(NpcSyncData[]? npcs) => OnRemoteNpcSync?.Invoke(npcs);
    public Action<NpcSyncData[]?>? OnCachedNpcRestore { get; set; }
    public void RestoreCachedNpcs(NpcSyncData[]? npcs) => OnCachedNpcRestore?.Invoke(npcs);

    public void BroadcastUpdate()
    {
        if (relayClient is not { IsConnected: true } || !CanEdit) return;

        // Envoyer les marqueurs sans les stats (les stats MJ ne doivent pas être visibles par les joueurs)
        var sanitized = new MarkerData[Constants.WaymarkCount];
        for (var i = 0; i < Constants.WaymarkCount; i++)
            sanitized[i] = CurrentMarkers.Markers[i].DeepCopyWithoutStats();

        // Un co-MJ sans PNJ à lui n'envoie pas de liste : une liste vide les retirerait chez tous.
        var npcs = NpcSyncProvider?.Invoke();
        if (!IsGm && npcs is { Length: 0 }) npcs = null;

        var msg = new RelayMessage
        {
            Type = MessageType.Update,
            Markers = sanitized,
            ShowMpBar = ShowMpBar,
            ShowShield = ShowShield,
            HpMode = HpMode.ToString(),
            MpMode = MpMode.ToString(),
            Npcs = npcs,
        };
        _ = relayClient.SendAsync(msg);
        SaveGmCache();
        TakeSnapshot();
    }

    public void ScheduleStateBroadcast(bool fullState)
    {
        stateBroadcastDue ??= DateTime.UtcNow + StateBroadcastDelay;
        stateBroadcastIsFull |= fullState;
    }

    public void FlushStateBroadcast()
    {
        if (stateBroadcastDue is not { } due || DateTime.UtcNow < due) return;

        var full = stateBroadcastIsFull;
        stateBroadcastDue = null;
        stateBroadcastIsFull = false;
        if (!IsGm) return;

        BroadcastPlayerUpdate();
        if (!full) return;

        BroadcastUpdate();
        if (ActiveTemplate != null) BroadcastTemplate();
        if (CurrentTurnState is { IsActive: true }) BroadcastTurnState();
        if (CurrentWeatherId != 0) BroadcastWeather(CurrentWeatherId, CurrentWeatherName ?? "");
        if (CurrentEorzeaTime != null) BroadcastTime(CurrentEorzeaTime);
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
        LastOwnRoll = null;
    }

    /// Dernier jet du joueur local, posé à la fin de l'animation du dé comme la ligne de chat :
    /// l'historique, lui, le reçoit dès le tirage.
    public DiceResult? LastOwnRoll { get; private set; }

    // Différé derrière l'animation quand elle joue, pour ne pas montrer le résultat sous le dé qui roule.
    public void NoteOwnRoll(DiceResult result, DiceRollOverlay? overlay)
    {
        if (result.RollerHash == null || result.RollerHash != LocalPlayerHash) return;

        if (overlay != null) overlay.DeferAction(() => LastOwnRoll = result);
        else LastOwnRoll = result;
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
            var line = FormatThresholdChat(name, rawRoll, diceMax, modifierStr, total, statName, target, success);
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

    /// <summary>
    /// Ligne de chat d'un jet jugé contre un seuil. En mode cible le résultat est le dé brut ;
    /// quand un modificateur s'ajoute au dé, le seuil porte sur le total, qu'il faut alors
    /// montrer avec son détail pour que le verdict se comprenne.
    /// </summary>
    public static string FormatThresholdChat(string name, int rawRoll, int diceMax, string modifierStr,
        int total, string? statName, int target, bool success)
    {
        var verdict = Loc.Get(success ? "Chat.RollSuccess" : "Chat.RollFailure");
        return total == rawRoll
            ? string.Format(Loc.Get("Chat.StatRollTarget"), name, rawRoll, diceMax, statName ?? "?", target, verdict)
            : string.Format(Loc.Get("Chat.StatRollThreshold"), name, statName ?? "?", rawRoll, diceMax,
                modifierStr, total, target, verdict);
    }

    public void RollDiceRaw(string name, List<StatValue>? stats, int tempModifier, string? statId = null)
        => ExecuteRoll(name, stats, tempModifier, statId);
    public void RollDiceFor(IVitalEntity entity, string? statId = null)
        => ExecuteRoll(entity.EntityName, entity.Stats, entity.TempModifier, statId, target: entity);

    public void RollDiceWithStat(WaymarkId waymarkId, string? statId = null)
        => RollDiceFor(CurrentMarkers[waymarkId], statId);

    /// <summary>
    /// Corps unique de tous les jets : marqueur, PNJ, joueur et jet libre du MJ.
    /// <paramref name="target"/> est la fiche qui reçoit le résultat affiché, <paramref name="rollerHash"/>
    /// le joueur à l'origine du jet, et <paramref name="requireEditRights"/> distingue la diffusion
    /// réservée au MJ de celle d'un joueur qui lance son propre dé.
    /// </summary>
    private void ExecuteRoll(string name, List<StatValue>? stats, int tempModifier, string? statId,
        IVitalEntity? target = null, string? rollerHash = null, bool requireEditRights = true,
        int? requiredThreshold = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var formula = ActiveTemplate?.DiceFormula ?? "1d100";
        var detail = DiceEngine.RollDetailed(formula);
        var rawRoll = detail.Sum;
        var diceMax = DiceEngine.GetMax(formula);
        var modifier = 0;
        string? statName = null;
        if (statId != null && stats != null)
        {
            var stat = stats.FirstOrDefault(s => s.Id == statId);
            if (stat != null)
            {
                modifier = stat.Modifier;
                statName = stat.Name;
            }
        }

        var totalModifier = modifier + tempModifier;
        var outcome = DiceEngine.Resolve(ActiveTemplate, rawRoll, statName != null ? modifier : null, tempModifier);

        // Seuil fixé par le MJ : il prime sur celui du modèle et se compare au résultat affiché,
        // le total en mode modificateur, le dé brut en mode cible.
        if (requiredThreshold is { } threshold)
        {
            var success = ActiveTemplate?.IsSuccess(outcome.Total, threshold) ?? outcome.Total >= threshold;
            outcome = outcome with { Target = threshold, Success = success };
        }

        var total = outcome.Total;
        var rolls = detail.Rolls.Length > 1 ? detail.Rolls : null;

        var result = new DiceResult
        {
            RollerName = name,
            RollerHash = rollerHash,
            StatName = statName,
            RawRoll = rawRoll,
            Modifier = totalModifier,
            StatModifier = statName != null ? modifier : null,
            TempModifier = tempModifier != 0 ? tempModifier : null,
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
            // Mode seuil : le total est le dé brut, la stat et le bonus déplacent la cible. Les animer comme
            // des additions montrerait « 80 + 60 » avant de revenir à 80.
            var targetMode = outcome.Target != null && total == rawRoll;
            diceRollOverlay.Show(name, total, diceMax, rawRoll, targetMode ? 0 : modifier,
                targetMode ? 0 : tempModifier, statName, rolls,
                ActiveTemplate?.CriticalSuccessThreshold ?? 0,
                ActiveTemplate?.CriticalFailureThreshold ?? 0,
                ActiveTemplate?.RollLowerIsBetter ?? false,
                outcome.Target, outcome.Success);
            // Le résultat n'atterrit sur la fiche qu'à la fin de l'animation, sinon il la précède.
            if (target != null)
                diceRollOverlay.DeferAction(() => ApplyRollResult(target, total, diceMax));
            NoteOwnRoll(result, diceRollOverlay);
            diceRollOverlay.DeferChatMessage(chatMsg);
        }
        else
        {
            if (target != null) ApplyRollResult(target, total, diceMax);
            NoteOwnRoll(result, null);
            Plugin.ChatGui.Print(chatMsg);
        }

        // Diffuser via relay
        if (relayClient is { IsConnected: true } && (!requireEditRights || CanEdit))
        {
            var msg = new RelayMessage
            {
                Type = MessageType.StatRoll,
                RollMarkerName = name,
                RollerHash = rollerHash,
                RollResult = rawRoll,
                RollMax = diceMax,
                RollModifier = modifier,
                RollTempModifier = tempModifier,
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

    private static void ApplyRollResult(IVitalEntity entity, int total, int diceMax)
    {
        entity.LastRollResult = total;
        entity.LastRollMax = diceMax;
    }
    public void RollDiceForPlayer(string playerHash, string? statId = null, int? requiredThreshold = null)
    {
        var player = PartyMembers.FirstOrDefault(p => p.Hash == playerHash);
        if (player == null) return;

        if (requiredThreshold == null && playerHash == LocalPlayerHash
            && PendingRollRequest is { } pending && pending.StatId == statId)
        {
            requiredThreshold = pending.Threshold;
            PendingRollRequest = null;
        }

        ExecuteRoll(player.Name, player.Stats, player.TempModifier, statId,
            rollerHash: playerHash, requireEditRights: false, requiredThreshold: requiredThreshold);
    }
    public void RequestRoll(PlayerData player, StatValue? stat, int threshold)
    {
        if (!CanEdit || relayClient is not { IsConnected: true }) return;

        var statName = stat?.Name ?? Loc.Get("Dice.NoStat");
        _ = relayClient.SendAsync(new RelayMessage
        {
            Type = MessageType.RollRequest,
            TargetHash = player.Hash,
            RollMarkerName = player.Name,
            StatId = stat?.Id,
            StatName = statName,
            RollTarget = threshold,
        });

        awaitingRoll.Add(player.Hash);
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.RollRequested"), player.Name, statName, threshold));
    }

    public void ReceiveRollRequest(RelayMessage msg)
    {
        if (msg.TargetHash == null || msg.RollTarget is not { } threshold) return;

        var statName = msg.StatName ?? Loc.Get("Dice.NoStat");
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.RollRequested"),
            msg.RollMarkerName ?? "?", statName, threshold));

        if (msg.TargetHash != LocalPlayerHash)
        {
            Plugin.Log.Debug($"[MasterEvent] Demande de jet pour {msg.TargetHash}, "
                + $"empreinte locale {LocalPlayerHash} : ce n'est pas pour moi.");
            return;
        }

        // Sans StatId, le MJ a demandé un jet sans stat ; avec, une stat introuvable ici part sans stat.
        var statId = ResolveRequestedStat(msg);
        PendingRollRequest = new RollRequest(statId, statName, threshold, StatMissing: msg.StatId != null && statId == null);
        Plugin.ToastGui.ShowQuest(string.Format(Loc.Get("RollRequest.Toast"), statName, threshold));
        OnRollRequested?.Invoke();
    }

    private string? ResolveRequestedStat(RelayMessage msg)
    {
        var stats = PartyMembers.FirstOrDefault(p => p.Hash == LocalPlayerHash)?.Stats;
        if (stats == null) return null;

        if (msg.StatId != null && stats.Any(s => s.Id == msg.StatId)) return msg.StatId;
        return stats.FirstOrDefault(s => s.Name == msg.StatName)?.Id;
    }

    public void NoteRollAnswered(string? rollerHash)
    {
        if (rollerHash != null) awaitingRoll.Remove(rollerHash);
    }

    public void AnswerRollRequest()
    {
        if (PendingRollRequest is not { } request) return;

        PendingRollRequest = null;
        RollDiceForPlayer(LocalPlayerHash, request.StatId, request.Threshold);
    }

    public void DismissRollRequest() => PendingRollRequest = null;

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
            MoveLeft = player.MoveLeft,
            MoveMax = player.MoveMax,
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


    // Nom de la dernière fiche appliquée à ce joueur, pour l'afficher dans l'onglet « Fiches ».
    public string? LoadedSheetName { get; private set; }

    // Appelé quand charger une fiche a activé son modèle : le plugin en retient le nom dans la configuration.
    public Action<string>? ActiveTemplateFollowed { get; set; }

    // Un invité d'une salle suit le modèle du MJ : seul le MJ (ou un utilisateur hors salle) change de règles.
    private bool MayChooseTemplate => IsGm || IsGmAsPlayer || !IsConnected;

    public void ApplyPlayerSheet(PlayerSheet sheet)
    {
        var player = PartyMembers.FirstOrDefault(p => p.Hash == LocalPlayerHash);
        if (player == null) return;

        FollowSheetTemplate(sheet);
        LoadedSheetName = sheet.Name;

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

    // Charger une fiche active son modèle, pour que dés, libellés et stats suivent le personnage.
    private void FollowSheetTemplate(PlayerSheet sheet)
    {
        if (!MayChooseTemplate || string.IsNullOrEmpty(sheet.TemplateName)) return;
        if (string.Equals(ActiveTemplate?.Name, sheet.TemplateName, StringComparison.Ordinal)) return;

        var template = LoadTemplate(sheet.TemplateName);
        if (template == null) return;

        SwitchActiveTemplate(template);
        ActiveTemplateFollowed?.Invoke(template.Name);
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.TemplateFollowed"), template.Name));
    }

    /// <summary>
    /// Inversement : activer un modèle charge la première fiche qui l'utilise. Sans effet si la fiche
    /// déjà chargée utilise ce modèle (on ne remplace pas un choix explicite) ou si aucune n'existe.
    /// </summary>
    public PlayerSheet? LoadFirstSheetFor(string templateName)
    {
        if (LoadedSheetName != null
            && string.Equals(LoadPlayerSheet(LoadedSheetName)?.TemplateName, templateName, StringComparison.Ordinal))
            return null;

        foreach (var name in GetPlayerSheetNames())
        {
            var sheet = LoadPlayerSheet(name);
            if (sheet == null || !string.Equals(sheet.TemplateName, templateName, StringComparison.Ordinal)) continue;

            ApplyPlayerSheet(sheet);
            Plugin.ChatGui.Print(string.Format(Loc.Get("Player.ProfileLoaded"), sheet.Name));
            return sheet;
        }

        return null;
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
            ["StatResolution"] = (int)template.StatResolution,
            ["RollLowerIsBetter"] = template.RollLowerIsBetter,
            ["CriticalSuccessThreshold"] = template.CriticalSuccessThreshold,
            ["CriticalFailureThreshold"] = template.CriticalFailureThreshold,
            ["HpLabel"] = template.HpLabel,
            ["MpLabel"] = template.MpLabel,
            ["MovementQuota"] = template.MovementQuota,
            ["MovementStatId"] = template.MovementStatId,
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
            SyncSoloMember();
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
                // En lobby, assigner le groupe local
                if (IsLobbyMode && existing.GroupId == null && !string.IsNullOrEmpty(LocalGroupId))
                {
                    existing.GroupId = LocalGroupId;
                    existing.GroupLabel = GetOrAssignGroupLabel(LocalGroupId);
                }
            }
            else
            {
                var defaultHpMax = ActiveTemplate?.DefaultPlayerHpMax ?? 100;
                var defaultMpMax = ActiveTemplate?.DefaultPlayerMpMax ?? 100;
                var groupId = IsLobbyMode ? LocalGroupId : null;
                var groupLabel = IsLobbyMode && !string.IsNullOrEmpty(LocalGroupId) ? GetOrAssignGroupLabel(LocalGroupId) : null;
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
        var removed = PartyMembers.RemoveAll(p => !seen.Contains(p.Hash) && !p.IsLobbyPlayer);
        if (removed > 0) addedOrRemoved = true;

        // Auto-broadcast when party composition changes
        if (addedOrRemoved && IsGm && relayClient is { IsConnected: true })
            BroadcastPlayerUpdate();
    }

    // Solo mode: local player only
    private void SyncSoloMember()
    {
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
    private readonly Dictionary<string, string> lobbyGroupLabels = new();
    private static readonly string[] GroupLetters = ["A", "B", "C", "D", "E", "F", "G", "H"];
    private string GetOrAssignGroupLabel(string? groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return "?";
        if (lobbyGroupLabels.TryGetValue(groupId, out var label)) return label;

        var nextIndex = lobbyGroupLabels.Count;
        label = nextIndex < GroupLetters.Length ? GroupLetters[nextIndex] : $"G{nextIndex + 1}";
        lobbyGroupLabels[groupId] = label;
        return label;
    }

    // Sans attribution, contrairement à GetOrAssignGroupLabel : un groupe encore dans la file y
    // prendrait une lettre, même refusé, et décalerait celles des groupes admis ensuite.
    public string? FindLobbyGroupLabel(string? groupId)
        => !string.IsNullOrEmpty(groupId) && lobbyGroupLabels.TryGetValue(groupId, out var label) ? label : null;

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
    public void AddLobbyPlayer(string hash, string name, string? groupId = null)
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
            IsLobbyPlayer = true,
            GroupId = groupId,
            GroupLabel = groupLabel,
        });

        if (IsGm && relayClient is { IsConnected: true })
            ScheduleStateBroadcast(fullState: false);
    }

    // Retire de la liste un joueur parti de lui-même. Il n'est pas exclu : il peut revenir.
    public void RemoveLobbyPlayer(string hash)
    {
        var removed = PartyMembers.RemoveAll(p => p.Hash == hash && p.IsLobbyPlayer);
        if (removed > 0 && IsGm && relayClient is { IsConnected: true })
            ScheduleStateBroadcast(fullState: false);
    }

    public void KickLobbyPlayer(string hash)
    {
        if (IsGm && relayClient is { IsConnected: true })
        {
            var kickMsg = new RelayMessage
            {
                Type = MessageType.AllianceKick,
                TargetHash = hash,
            };
            _ = relayClient.SendAsync(kickMsg);
        }

        RemoveLobbyPlayer(hash);
    }
    public void AssignLocalGroup()
    {
        if (string.IsNullOrEmpty(LocalGroupId)) return;
        var label = GetOrAssignGroupLabel(LocalGroupId);
        foreach (var p in PartyMembers.Where(p => !p.IsLobbyPlayer))
        {
            p.GroupId = LocalGroupId;
            p.GroupLabel = label;
        }
    }

    // Retire tous les joueurs alliance de la liste (appelé lors de la désactivation du mode alliance).
    public void ClearLobbyPlayers()
    {
        PendingRollRequest = null;
        awaitingRoll.Clear();
        PartyMembers.RemoveAll(p => p.IsLobbyPlayer);
        lobbyGroupLabels.Clear();
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

    // Waymarks posés chez le joueur par MasterEvent, avec leur position. Hors du groupe du MJ, le jeu
    // ne lui transmet pas les retraits : c'est à nous d'enlever ceux que nous avions posés.
    private readonly Vector3?[] placedWaymarks = new Vector3?[Constants.WaymarkCount];

    public int ApplyWaymarks()
    {
        var placed = 0;
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var marker = CurrentMarkers.Markers[i];
            if (!marker.IsVisible || (marker.X == 0 && marker.Y == 0 && marker.Z == 0)) continue;
            if (!WaymarkManager.PlaceWaymark((WaymarkId)i, marker.X, marker.Y, marker.Z)) continue;

            placedWaymarks[i] = new Vector3(marker.X, marker.Y, marker.Z);
            placed++;
        }

        return placed;
    }

    // Retire les waymarks que nous avions posés et que le MJ a enlevés depuis.
    public void RemoveWithdrawnWaymarks()
    {
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            if (placedWaymarks[i] is not { } position || CurrentMarkers.Markers[i].IsVisible) continue;
            if (WaymarkManager.HideWaymark((WaymarkId)i, position) is not { } hidden) continue;

            placedWaymarks[i] = null;
            if (hidden) Plugin.Log.Info($"[MasterEvent] Waymark {((WaymarkId)i).ToLabel()} retiré : le MJ ne l'affiche plus.");
        }
    }

    // Le MJ sortant confie sa scène : marqueurs complets, stats comprises, et PNJ. Le relais ne la
    // remet qu'au successeur qui la réclame, les joueurs ne voient donc jamais les stats.
    public void SendGmHandover(string? targetHash)
    {
        if (relayClient is not { IsConnected: true } || !IsGm) return;

        var msg = new RelayMessage
        {
            Type = MessageType.GmHandover,
            TargetHash = targetHash,
            Markers = CurrentMarkers.Markers.Select(m => m.DeepCopy()).ToArray(),
            ShowMpBar = ShowMpBar,
            ShowShield = ShowShield,
            HpMode = HpMode.ToString(),
            MpMode = MpMode.ToString(),
            Npcs = NpcSyncProvider?.Invoke(),
        };
        _ = relayClient.SendAsync(msg);
    }

    public void ReceiveHandoverPending(string? targetHash, string? fromName)
    {
        HandoverPending = true;
        HandoverTarget = targetHash;
        Plugin.Log.Info($"[MasterEvent] {fromName ?? "?"} confie sa scène à {targetHash ?? "son successeur"}.");
        OnHandoverPending?.Invoke();
    }

    public void ClaimLeadership(string leaderToken)
    {
        if (relayClient is not { IsConnected: true }) return;
        _ = relayClient.SendAsync(new RelayMessage { Type = MessageType.ClaimLeadership, LeaderToken = leaderToken });
    }

    // Les marqueurs sont déjà appliqués par le protocole : restent les PNJ, que l'on prend à son compte.
    public void AcceptSceneHandover(NpcSyncData[]? npcs, string? fromName)
    {
        OnSceneNpcsHandedOver?.Invoke(npcs);
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.SceneReceived"), fromName ?? "?"));
    }

    public void SetSceneLeader(string hash, string? name)
    {
        var afterHandover = HandoverPending;
        HandoverPending = false;
        HandoverTarget = null;
        SceneLeaderHash = hash;
        if (!string.IsNullOrEmpty(name)) sceneLeaderName = name;

        if (afterHandover && hash != LocalPlayerHash && !string.IsNullOrEmpty(name))
            Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.SceneLeaderChanged"), name));
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

    /// <summary>
    /// Répercute un modèle qui vient d'être enregistré sur le modèle actif, s'il s'agit du même.
    /// Sans cela, les règles modifiées (résolution par seuil, sens du dé, critiques) ne prennent
    /// effet qu'au prochain « Charger », et les joueurs gardent l'ancienne copie.
    /// Contrairement à <see cref="ApplyTemplate"/>, les PV, boucliers et valeurs de stats en
    /// cours ne sont pas réinitialisés : seules les définitions sont réalignées.
    /// Retourne vrai quand le modèle actif a été remplacé.
    /// </summary>
    public bool RefreshActiveTemplate(EventTemplate saved, string? previousName)
    {
        if (ActiveTemplate == null) return false;
        if (!string.Equals(ActiveTemplate.Name, saved.Name, StringComparison.Ordinal)
            && !string.Equals(ActiveTemplate.Name, previousName, StringComparison.Ordinal))
            return false;

        SwitchActiveTemplate(saved);
        return true;
    }

    /// Joueur, on suit les règles reçues du MJ de la salle ; devenu MJ, on reprend celles de son
    /// modèle actif, celui qu'annonce « Modèle actif ». Vrai si une règle a changé.
    public bool RealignRulesWithTemplate()
    {
        if (ActiveTemplate is not { } template) return false;

        var changed = HpMode != template.HpMode || ShowMpBar != template.ShowMpBar
                      || MpMode != template.MpMode || ShowShield != template.ShowShield
                      || DiceMax != template.DiceMax;
        HpMode = template.HpMode;
        ShowMpBar = template.ShowMpBar;
        MpMode = template.MpMode;
        ShowShield = template.ShowShield;
        DiceMax = template.DiceMax;
        return changed;
    }

    /// <summary>
    /// Remplace le modèle actif sans réinitialiser PV, boucliers ni valeurs de stats en cours
    /// (contrairement à <see cref="ApplyTemplate"/>) : seules les définitions sont réalignées.
    /// Sert à l'enregistrement d'un modèle actif et au suivi du modèle d'une fiche chargée.
    /// </summary>
    public void SwitchActiveTemplate(EventTemplate saved)
    {
        var template = saved.DeepCopy();
        ActiveTemplate = template;
        HpMode = template.HpMode;
        ShowMpBar = template.ShowMpBar;
        MpMode = template.MpMode;
        ShowShield = template.ShowShield;
        DiceMax = template.DiceMax;

        var hasDefinitions = template.StatDefinitions is { Count: > 0 } || template.CounterDefinitions is { Count: > 0 };
        if (hasDefinitions)
        {
            foreach (var marker in CurrentMarkers.Markers.Where(m => m.HasData))
            {
                marker.Stats ??= [];
                marker.Counters ??= [];
                TemplateSyncHelper.SyncStatsAndCounters(marker.Stats, marker.Counters, template);
            }

            foreach (var player in PartyMembers)
            {
                player.Stats ??= [];
                player.Counters ??= [];
                TemplateSyncHelper.SyncStatsAndCounters(player.Stats, player.Counters, template);
            }

            NpcTemplateApplier?.Invoke(template);
        }

        BroadcastTemplate();
        BroadcastUpdate();
        BroadcastPlayerUpdate();
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

    /// <summary>
    /// Aligne les fiches RP rattachées à un modèle local qui vient d'être enregistré : stats et
    /// compteurs ajoutés, retirés ou renommés par identifiant, sans toucher aux valeurs saisies.
    /// Une fiche rattachée à l'ancien nom du modèle suit son renommage.
    /// Sans cela, un profil créé avant l'ajout d'une stat ne la montrait jamais.
    /// </summary>
    public List<(string Sheet, TemplateSyncHelper.SheetSyncReport Report)> SyncSheetsWithTemplate(
        EventTemplate template, string? previousName)
    {
        var reports = new List<(string, TemplateSyncHelper.SheetSyncReport)>();
        foreach (var sheetName in saveManager.GetSheetNames())
        {
            var sheet = saveManager.LoadSheet(sheetName);
            if (sheet == null) continue;

            var matchesName = string.Equals(sheet.TemplateName, template.Name, StringComparison.Ordinal);
            var matchesPrevious = previousName != null
                                  && string.Equals(sheet.TemplateName, previousName, StringComparison.Ordinal);
            if (!matchesName && !matchesPrevious) continue;

            var renamed = !matchesName;
            sheet.TemplateName = template.Name;

            var report = TemplateSyncHelper.SyncSheetWithTemplate(sheet, template);
            if (!report.HasChanges && !renamed) continue;

            saveManager.SaveSheet(sheet);
            if (report.HasChanges) reports.Add((sheet.Name, report));
        }

        return reports;
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


    /// Numéro du combat lancé ici : distingue deux combats au même round pour une commande en retard.
    public int EncounterSerial { get; private set; }

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

        // PNJ incarnés : ils rejoignent l'ordre au même titre que les marqueurs.
        foreach (var npc in NpcParticipantProvider?.Invoke() ?? [])
        {
            var npcRoll = DiceEngine.Roll(formula);
            var (npcMod, npcStatName) = GetInitiativeModifierAndName(npc.stats, initStatId);

            state.Entries.Add(new TurnEntry
            {
                NpcId = npc.id,
                Name = npc.name,
                Initiative = npcRoll + npcMod,
                InitiativeRoll = npcRoll,
                InitiativeModifier = npcMod,
                InitiativeStatName = npcStatName,
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
        EncounterSerial++;
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

    /// L'action pleine de la vue Scène, partagée par sa barre et par le second écran pour qu'ils ne
    /// proposent jamais deux actions différentes. Name : groupe ou participant dont c'est le tour.
    public (ScenePrimaryKind Kind, int TurnIndex, string? Name) ResolveScenePrimary(bool gmAccess)
    {
        if (!gmAccess) return (ScenePrimaryKind.None, -1, null);
        if (CurrentTurnState is not { IsActive: true } state) return (ScenePrimaryKind.Start, -1, null);

        var index = ActiveTurnIndex;
        if (index < 0) return (ScenePrimaryKind.NextRound, -1, null);

        var entry = state.Entries[index];
        // Un groupe sans nom laisserait « Finir le tour de » sans personne : on nomme celui qui joue.
        var label = state.FindGroupFor(entry)?.Label;
        return (ScenePrimaryKind.EndTurn, index, string.IsNullOrWhiteSpace(label) ? entry.Name : label);
    }

    /// Vrai si le joueur joue dans le bloc actif : l'entrée active, ou n'importe quel membre de son
    /// groupe, qui finit alors le tour de tout le groupe. Ouvre aussi le suivi du déplacement et le
    /// bouton du bandeau tactique.
    public bool IsInActiveBlock(string? playerHash)
    {
        if (playerHash == null) return false;
        var idx = ActiveTurnIndex;
        if (idx < 0 || CurrentTurnState is not { } state) return false;

        var active = state.Entries[idx];
        if (active.PlayerHash == playerHash) return true;
        return active.GroupId != null
               && state.Entries.Any(e => e.GroupId == active.GroupId && e.PlayerHash == playerHash);
    }

    public bool IsLocalPlayerTurn => LocalPlayerHash.Length > 0 && IsInActiveBlock(LocalPlayerHash);

    /// Le bloc actif est un groupe : « Finir le tour du groupe » plutôt que « Finir mon tour ».
    public bool IsLocalTurnGrouped
        => IsLocalPlayerTurn && CurrentTurnState is { } state && state.Entries[ActiveTurnIndex].GroupId != null;

    /// Moment où la fin de tour est partie vers le MJ, levé au turnUpdate suivant.
    public DateTime? EndTurnSentAt { get; private set; }

    private static readonly TimeSpan EndTurnPendingLimit = TimeSpan.FromSeconds(5);

    // Borné : un MJ qui ne répond pas ne doit pas laisser le bouton grisé pour toujours.
    public bool IsEndTurnPending => EndTurnSentAt is { } sentAt && DateTime.UtcNow - sentAt < EndTurnPendingLimit;

    public void NoteTurnUpdate()
    {
        EndTurnSentAt = null;
        turnRefreshed = true;
    }

    /// Envoie au MJ la demande de fin de tour du joueur local. Le joueur n'a pas le droit
    /// d'écrire l'état des tours : le relais rejette `turnUpdate` venant d'un non-leader.
    public void RequestEndOwnTurn()
    {
        if (relayClient is not { IsConnected: true }) return;
        if (!IsLocalPlayerTurn || IsEndTurnPending) return;

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
        EndTurnSentAt = DateTime.UtcNow;
    }

    /// Applique la demande d'un joueur, côté MJ uniquement. On revérifie que le demandeur joue dans
    /// le bloc actif : sans ce contrôle, n'importe qui pourrait clore le tour d'autrui. Un second
    /// membre du même groupe arrive après la bascule, sur un autre bloc, et n'a donc aucun effet.
    public void ApplyTurnEndRequest(string playerHash)
    {
        if (!CanEdit) return;
        if (!IsInActiveBlock(playerHash)) return;

        ToggleHasActed(ActiveTurnIndex);
    }

    /// L'action pleine de la vue joueur, comme ResolveScenePrimary pour le MJ : le jet demandé passe
    /// devant la fin de tour, qui reste alors à côté en teinté. Rien hors séance ni en instance, où
    /// l'état affiché peut dater et où la réponse ne partirait pas.
    public PlayerPrimary ResolvePlayerPrimary(bool inInstance)
    {
        if (inInstance || LinkState != RelayLinkState.InSession)
            return new PlayerPrimary(PlayerPrimaryKind.None, null, false, false, EndTurnHold.None);

        var canEndTurn = IsLocalPlayerTurn;
        var grouped = canEndTurn && IsLocalTurnGrouped;
        var hold = canEndTurn ? EndTurnHoldNow() : EndTurnHold.None;

        if (PendingRollRequest is { } request)
        {
            var statName = request.StatId is { } statId
                ? PartyMembers.FirstOrDefault(p => p.Hash == LocalPlayerHash)?.Stats?
                      .FirstOrDefault(s => s.Id == statId)?.Name ?? request.StatName
                : null;
            return new PlayerPrimary(PlayerPrimaryKind.Roll, statName, canEndTurn, grouped, hold);
        }

        return canEndTurn
            ? new PlayerPrimary(PlayerPrimaryKind.EndTurn, null, true, grouped, hold)
            : new PlayerPrimary(PlayerPrimaryKind.None, null, false, false, EndTurnHold.None);
    }

    // Ce qui retient la fin de tour du joueur local, dans l'ordre où la vue l'annonce.
    private EndTurnHold EndTurnHoldNow()
    {
        if (IsEndTurnPending) return EndTurnHold.Sent;
        if (HandoverPending) return EndTurnHold.Handover;
        if (GmPresence is not { Connected: true } && !CanEdit) return EndTurnHold.GmAway;
        return EndTurnHold.None;
    }

    /// Le MJ de la scène tel que le joueur le voit : celui que le relais désigne, sinon le membre
    /// marqué MJ. Null tant que personne ne l'est.
    public GmPresenceInfo? GmPresence
    {
        get
        {
            if (SceneLeaderHash is { } leader)
            {
                var member = PartyMembers.FirstOrDefault(p => p.Hash == leader);
                return new GmPresenceInfo(leader, member?.Name ?? sceneLeaderName, member?.IsConnected ?? false);
            }

            // En lobby, IsGm marque le chef de son propre groupe en jeu (soi-même, seul) : sans MJ
            // désigné par le relais, il n'y a personne.
            if (IsLobbyMode) return null;

            return PartyMembers.FirstOrDefault(p => p.IsGm) is { } gm
                ? new GmPresenceInfo(gm.Hash, gm.Name, gm.IsConnected)
                : null;
        }
    }

    /// Accorde ou retire des yalms au personnage, pour le tour en cours uniquement. Réservé au
    /// MJ : le bonus descend ensuite vers le client du joueur, qui l'ajoute à son quota.
    public void GrantMovement(string playerHash, float delta)
    {
        if (!CanEdit) return;

        var player = PartyMembers.FirstOrDefault(p => p.Hash == playerHash);
        if (player == null) return;

        // Borné pour que le quota total ne puisse pas devenir négatif : un malus supérieur au
        // quota de base immobilise, il n'inverse pas la mécanique.
        var baseQuota = MovementTracker.ResolveMax(ActiveTemplate, player) - player.MoveBonus;
        player.MoveBonus = MathF.Max(-baseQuota, player.MoveBonus + delta);

        BroadcastPlayerUpdate();
    }

    /// Remet à zéro les yalms accordés. Appelé à la fin d'un tour et au changement de round :
    /// un bonus est valable pour le tour où il a été donné, pas pour toute la rencontre.
    private void ClearMovementBonus(string? playerHash)
    {
        foreach (var player in PartyMembers)
        {
            if (playerHash != null && player.Hash != playerHash) continue;
            player.MoveBonus = 0f;
        }
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
            // Un groupe joue d'un bloc : chacun de ses joueurs perd les yalms accordés pour ce tour.
            List<string> actedHashes;
            if (group != null)
                actedHashes = state.Entries.Where(e => e.GroupId == group.Id && e.PlayerHash != null).Select(e => e.PlayerHash!).ToList();
            else
                actedHashes = entry.PlayerHash is { } soloHash ? [soloHash] : [];
            if (actedHashes.Count > 0)
            {
                foreach (var actedHash in actedHashes)
                    ClearMovementBonus(actedHash);
                BroadcastPlayerUpdate();
            }

            // Un MJ qui participe et joue ensuite reçoit le toast qui s'adresse à lui.
            var nextNames = GetNextBlockNames(state);
            if (nextNames.Count == 0)
                ShowRoundEndToast(state.Round);
            else if (IsLocalPlayerTurn)
                ShowYourTurnToast();
            else
                ShowTurnToast(FormatNameList(nextNames));
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
        ClearMovementBonus(null);
        BroadcastPlayerUpdate();

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
        LastGmAnnouncement = message;
        var hold = Math.Clamp(message.Length * 0.06f, 3f, 10f);
        roundOverlay?.Show(message, RoundAnnouncementOverlay.RubyRgb, holdDurationSeconds: hold);
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.GmAnnouncement"), message));
    }

    public static void ShowTurnToast(string name)
    {
        var text = string.Format(Loc.Get("Turns.TurnToast"), name);
        Plugin.ToastGui.ShowQuest(text);
    }

    public static void ShowYourTurnToast() => Plugin.ToastGui.ShowQuest(Loc.Get("Turns.YourTurnToast"));

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

    /// <summary>
    /// Fait entrer un joueur dans le combat en cours avec son propre jet d'initiative,
    /// plutôt que de le laisser spectateur jusqu'au round suivant. Retourne faux hors
    /// combat, ou s'il y figure déjà. Diffuse l'état par <see cref="AddTurnParticipant"/>.
    /// </summary>
    public bool AddPlayerToEncounter(string playerHash, string? fallbackName)
    {
        if (CurrentTurnState is not { IsActive: true } state) return false;
        if (state.Entries.Any(e => e.PlayerHash == playerHash)) return false;

        // Le nom du roster prime : il est déjà à jour quand le joueur figure dans la party,
        // le nom annoncé à la connexion ne sert que de repli.
        var name = PartyMembers.FirstOrDefault(p => p.Hash == playerHash)?.Name
                   ?? fallbackName ?? "?";

        var entry = new TurnEntry { PlayerHash = playerHash, Name = name };
        AddTurnParticipant(entry);

        Plugin.ChatGui.Print(string.Format(
            Loc.Get("Chat.PlayerJoinedEncounter"), name, entry.Initiative));
        return true;
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
