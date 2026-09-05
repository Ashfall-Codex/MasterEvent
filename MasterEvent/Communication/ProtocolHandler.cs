using System;
using System.Linq;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.Communication;

public class ProtocolHandler(SessionManager session, DiceRollOverlay diceRollOverlay, Configuration configuration, RelayClient relayClient)
{

    public void HandleMessage(RelayMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.Update:
                HandleUpdate(msg);
                break;
            case MessageType.Clear:
                HandleClear();
                break;
            case MessageType.RequestUpdate:
                HandleRequestUpdate();
                break;
            case MessageType.JoinConfirm:
                HandleJoinConfirm(msg);
                break;
            case MessageType.PlayerJoined:
                HandlePlayerJoined(msg);
                break;
            case MessageType.PlayerLeft:
                HandlePlayerLeft(msg);
                break;
            case MessageType.VersionMismatch:
                HandleVersionMismatch(msg);
                break;
            case MessageType.Roll:
                HandleRoll(msg);
                break;
            case MessageType.PlayerUpdate:
                HandlePlayerUpdate(msg);
                break;
            case MessageType.Promote:
                HandlePromote(msg);
                break;
            case MessageType.TemplateShare:
                HandleTemplateShare(msg);
                break;
            case MessageType.CachedState:
                HandleCachedState(msg);
                break;
            case MessageType.TurnUpdate:
                HandleTurnUpdate(msg);
                break;
            case MessageType.TurnClear:
                HandleTurnClear();
                break;
            case MessageType.StatRoll:
                HandleStatRoll(msg);
                break;
            case MessageType.PlayerStatUpdate:
                HandlePlayerStatUpdate(msg);
                break;
            case MessageType.WeatherUpdate:
                HandleWeatherUpdate(msg);
                break;
            case MessageType.TimeUpdate:
                HandleTimeUpdate(msg);
                break;
            case MessageType.AllianceKick:
                HandleAllianceKick(msg);
                break;
            case MessageType.AllianceInvite:
                HandleAllianceInvite(msg);
                break;
            case MessageType.AllianceDisband:
                HandleAllianceDisband();
                break;
            case MessageType.VersionRejected:
                HandleVersionRejected(msg);
                break;
            case MessageType.TemplateUpdated:
                HandleTemplateUpdated(msg);
                break;
            case MessageType.GmAnnouncement:
                HandleGmAnnouncement(msg);
                break;
            case MessageType.JoinRejected:
                HandleJoinRejected(msg);
                break;
            case MessageType.JoinPending:
                HandleJoinPending();
                break;
            case MessageType.JoinAdmitted:
                HandleJoinAdmitted();
                break;
            case MessageType.LobbyPending:
                HandleLobbyPending(msg);
                break;
            case MessageType.LobbyMoved:
                HandleLobbyMoved(msg);
                break;
            case MessageType.TurnEndSelf:
                HandleTurnEndSelf(msg);
                break;
        }
    }

    /// Un joueur signale la fin de son tour. Seul le MJ traite la demande, et `ApplyTurnEndRequest`
    /// revérifie que le demandeur est bien l'acteur courant avant de rediffuser l'état.
    private void HandleTurnEndSelf(RelayMessage msg)
    {
        if (msg.PlayerHash == null) return;
        session.ApplyTurnEndRequest(msg.PlayerHash);
    }

    private void HandleJoinRejected(RelayMessage msg)
    {
        session.IsAwaitingApproval = false;
        session.IsConnected = false;

        var key = msg.Reason switch
        {
            "roomLimit" => "Chat.JoinRejectedRoomLimit",
            "rateLimited" => "Chat.JoinRejectedRateLimited",
            "denied" => "Chat.JoinRejectedDenied",
            _ => "Chat.JoinRejectedInvalid",
        };

#pragma warning disable CA1508
        Plugin.Log.Warning($"[MasterEvent] Adhésion refusée par le relais : {msg.Reason ?? "?"}");
#pragma warning restore CA1508
        Plugin.ChatGui.Print(Loc.Get(key));
    }

    private void HandleJoinPending()
    {
        session.IsAwaitingApproval = true;
        session.IsConnected = false;
        Plugin.ChatGui.Print(Loc.Get("Chat.JoinPending"));
    }

    private void HandleJoinAdmitted()
    {
        session.IsAwaitingApproval = false;
        Plugin.ChatGui.Print(Loc.Get("Chat.JoinAdmitted"));
        session.OnRejoinRequested?.Invoke();
    }

    private void HandleLobbyPending(RelayMessage msg)
    {
        var incoming = msg.Pending ?? [];

        if (session.IsGm || session.IsPromoted)
        {
            foreach (var member in incoming)
            {
                if (session.PendingMembers.Any(p => p.Hash == member.Hash)) continue;
                Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.LobbyAccessRequest"), member.Name));
            }
        }

        session.PendingMembers.Clear();
        session.PendingMembers.AddRange(incoming);
    }


    private void HandleLobbyMoved(RelayMessage msg)
    {
        if (string.IsNullOrEmpty(msg.LobbyCode)) return;
        if (session.AllianceRoomCode == msg.LobbyCode) return;

        Plugin.Log.Info($"[MasterEvent] Redirection vers le lobby {msg.LobbyCode}.");
        session.OnLobbyMoved?.Invoke(msg.LobbyCode);
    }

    // Annonce libre du MJ : affiche l'overlay rouge + ligne dans le chat.
    // Le sender ne reçoit pas son propre message (le serveur exclut l'émetteur du broadcast),
    // donc ce handler ne tourne que chez les destinataires.
    private void HandleGmAnnouncement(RelayMessage msg)
    {
        if (string.IsNullOrWhiteSpace(msg.AnnouncementText)) return;
        session.ApplyGmAnnouncement(msg.AnnouncementText);
    }
    private void HandleTemplateUpdated(RelayMessage msg)
    {
        if (string.IsNullOrEmpty(msg.TemplateCode) || msg.TemplateVersion <= 0) return;

        var subscribed = session.FindSubscribedTemplateByCode(msg.TemplateCode);
        if (subscribed == null) return;
        if (msg.TemplateVersion <= subscribed.SourceVersion) return;

        _ = session.PullSubscribedTemplateAsync(subscribed.Name, configuration.RelayServerUrl);
    }

    private void HandleUpdate(RelayMessage msg)
    {
        if (!session.IsGm) session.ApplyRemoteNpcs(msg.Npcs);

        if (session.CanEdit) return;

        if (msg.Markers == null) return;
        ApplyMarkersFromMessage(msg);

        if (configuration.AutoApplyWaymarks)
            session.ApplyWaymarks();
    }

    private void HandleClear()
    {
        if (session.CanEdit) return;
        session.CurrentMarkers.ResetAll();
    }

    private void HandleRequestUpdate()
    {
        if (!session.IsGm) return;
        session.BroadcastUpdate();
    }

    private void HandleJoinConfirm(RelayMessage msg)
    {
        session.IsConnected = true;
        session.IsAwaitingApproval = false;
        session.ConnectedPlayerCount = msg.PlayerCount;
        session.UpdatePlayerConnection(session.LocalPlayerHash, true);
        Plugin.Log.Info($"[MasterEvent] Joined relay room. Players: {msg.PlayerCount}");

        if (session.IsGm && !msg.IsLeader)
        {
            Plugin.Log.Warning("[MasterEvent] Leadership refusé par le relay pour cette salle.");

            if (session.IsAllianceMode)
                session.IsGm = false;

            Plugin.ChatGui.Print(Loc.Get("Chat.LeadershipDenied"));
        }

        // Fallback: if GM and no server cache was restored, try local cache
        if (session.IsGm && !session.CacheRestored)
        {
            var cache = session.LoadGmCache();
            if (cache != null)
            {
                session.RestoreFromCache(cache);
                session.CacheRestored = true;
                Plugin.ChatGui.Print(Loc.Get("Chat.CacheRestoredLocal"));
                Plugin.Log.Info("[MasterEvent] Session restored from local cache.");
                session.BroadcastUpdate();
            }
        }
    }

    private void HandlePlayerJoined(RelayMessage msg)
    {
        session.ConnectedPlayerCount = msg.PlayerCount;

        // En mode alliance, ajouter le joueur s'il n'est pas dans le groupe local
        if (session.IsAllianceMode && msg.PlayerHash != null && msg.PlayerName != null)
            session.AddAlliancePlayer(msg.PlayerHash, msg.PlayerName, msg.GroupId);

        if (msg.PlayerHash != null)
            session.UpdatePlayerConnection(msg.PlayerHash, true);
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.PlayerJoined"), msg.PlayerName ?? "?"));

        // Auto-send current state to new player
        if (session.IsGm)
        {
            session.BroadcastUpdate();
            session.BroadcastPlayerUpdate();
            if (session.ActiveTemplate != null)
                session.BroadcastTemplate();
            if (session.CurrentTurnState is { IsActive: true })
                session.BroadcastTurnState();
            if (session.CurrentWeatherId != 0)
                session.BroadcastWeather(session.CurrentWeatherId, session.CurrentWeatherName ?? "");
            if (session.CurrentEorzeaTime != null)
                session.BroadcastTime(session.CurrentEorzeaTime);
        }
    }

    private void HandlePlayerLeft(RelayMessage msg)
    {
        session.ConnectedPlayerCount = msg.PlayerCount;
        if (msg.PlayerHash != null)
            session.UpdatePlayerConnection(msg.PlayerHash, false);

        // En mode alliance, retirer le joueur s'il vient d'un autre groupe
        if (session.IsAllianceMode && msg.PlayerHash != null)
            session.RemoveAlliancePlayer(msg.PlayerHash);

        // `Voluntary` absent = relais antérieur à ce champ : on garde le message neutre plutôt
        // que d'annoncer une déconnexion brutale qui n'a peut-être pas eu lieu.
        var key = msg.Voluntary switch
        {
            true => "Chat.PlayerLeft",
            false => "Chat.PlayerDropped",
            null => "Chat.PlayerLeft",
        };

        Plugin.ChatGui.Print(string.Format(Loc.Get(key), msg.PlayerName ?? "?"));
    }

    private static void HandleVersionMismatch(RelayMessage _)
    {
        Plugin.ChatGui.Print(Loc.Get("Chat.VersionMismatch"));
    }

    private void HandleVersionRejected(RelayMessage msg)
    {
        var minVersion = msg.MinVersion ?? "?";
        Plugin.ChatGui.PrintError(string.Format(Loc.Get("Chat.VersionRejected"), Constants.PluginVersion, minVersion));
        relayClient.SuppressReconnect = true;
        _ = relayClient.DisconnectAsync();
    }

    private void HandleRoll(RelayMessage msg)
    {
        if (session.CanEdit || msg.RollMarkerName == null) return;

        // Store roll result on the matching marker
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var marker = session.CurrentMarkers.Markers[i];
            if (marker.Name == msg.RollMarkerName)
            {
                marker.LastRollResult = msg.RollResult;
                marker.LastRollMax = msg.RollMax;
                break;
            }
        }

        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.Roll"), msg.RollMarkerName, msg.RollResult, msg.RollMax));
    }

    private void HandlePromote(RelayMessage msg)
    {
        if (msg.TargetHash == null) return;

        // Update player's CanEdit in party list
        var player = session.PartyMembers.FirstOrDefault(p => p.Hash == msg.TargetHash);
        // ReSharper disable once UseNullPropagation — null propagation inapplicable sur un setter
        if (player != null)
            player.CanEdit = msg.CanEdit;

        // If this promotion targets us, update local promoted state
        if (msg.TargetHash == session.LocalPlayerHash)
        {
            session.SetPromoted(msg.CanEdit);
            Plugin.ChatGui.Print(Loc.Get(msg.CanEdit ? "Chat.Promoted" : "Chat.Demoted"));
        }
    }

    private void HandleTemplateShare(RelayMessage msg)
    {
        if (session.IsGm || msg.Template == null) return;

        session.ApplyTemplate(msg.Template);
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.TemplateReceived"), msg.Template.Name));
    }

    private void HandlePlayerUpdate(RelayMessage msg)
    {
        if (session.IsGm || msg.Players == null) return;

        session.GmIsPlayer = msg.GmIsPlayer;

        foreach (var incoming in msg.Players)
        {
            var local = session.PartyMembers.FirstOrDefault(p => p.Hash == incoming.Hash);
            if (local != null)
            {
                local.Hp = incoming.Hp;
                local.HpMax = incoming.HpMax;
                local.Mp = incoming.Mp;
                local.MpMax = incoming.MpMax;
                local.Shield = incoming.Shield;
                local.Counters = incoming.Counters?.Select(c => c.DeepCopy()).ToList();
                local.Stats = incoming.Stats?.Select(s => s.DeepCopy()).ToList();
                local.TempModifier = incoming.TempModifier;
                local.TempModTurns = incoming.TempModTurns;
                local.IsGm = incoming.IsGm;
                local.MoveBonus = incoming.MoveBonus;
            }
            else if (session.IsAllianceMode)
            {
                // Joueur d'un autre groupe en mode alliance : l'ajouter localement
                session.PartyMembers.Add(new PlayerData
                {
                    Hash = incoming.Hash,
                    Name = incoming.Name,
                    Hp = incoming.Hp,
                    HpMax = incoming.HpMax,
                    Mp = incoming.Mp,
                    MpMax = incoming.MpMax,
                    Shield = incoming.Shield,
                    Counters = incoming.Counters?.Select(c => c.DeepCopy()).ToList(),
                    Stats = incoming.Stats?.Select(s => s.DeepCopy()).ToList(),
                    TempModifier = incoming.TempModifier,
                    TempModTurns = incoming.TempModTurns,
                    IsGm = incoming.IsGm,
                    IsAlliancePlayer = true,
                    IsConnected = true,
                });
            }
        }
    }

    private void HandleTurnUpdate(RelayMessage msg)
    {
        if (session.CanEdit || msg.TurnState == null) return;

        var oldState = session.CurrentTurnState;
        var oldRound = oldState?.Round ?? 0;
        var newRound = msg.TurnState.Round;

        // Detect newly checked participant → announce next unchecked
        if (oldState != null && newRound == oldRound
            && oldState.Entries.Count == msg.TurnState.Entries.Count)
        {
            // Détection "quelqu'un vient d'agir" résolu au niveau bloc (groupe ou solo).
            // On compare le HasActed résolu (via group) entre ancien et nouvel état, par identifiant stable.
            var someoneJustActed = false;
            foreach (var newEntry in msg.TurnState.Entries)
            {
                var oldEntry = FindMatchingEntry(oldState, newEntry);
                if (oldEntry == null) continue;
                var oldActed = oldState.HasEntryActed(oldEntry);
                var newActed = msg.TurnState.HasEntryActed(newEntry);
                if (!oldActed && newActed)
                {
                    someoneJustActed = true;
                    break;
                }
            }

            if (someoneJustActed)
            {
                var nextNames = SessionManager.GetNextBlockNames(msg.TurnState);
                if (nextNames.Count > 0)
                    SessionManager.ShowTurnToast(SessionManager.FormatNameList(nextNames));
                else
                    SessionManager.ShowRoundEndToast(newRound);
            }
        }

        session.CurrentTurnState = msg.TurnState.DeepCopy();

        if (oldState == null && msg.TurnState is { IsActive: true })
            SessionManager.PrintInitiativeOrder(msg.TurnState);
        if (newRound > oldRound && oldRound > 0)
            session.ShowRoundToast(newRound);
    }

    private void HandleTurnClear()
    {
        if (session.CanEdit) return;
        session.CurrentTurnState = null;
    }

    private static TurnEntry? FindMatchingEntry(TurnState state, TurnEntry target)
    {
        if (target.PlayerHash != null)
            return state.Entries.FirstOrDefault(e => e.PlayerHash == target.PlayerHash);
        if (target.WaymarkIndex.HasValue)
            return state.Entries.FirstOrDefault(e => e.WaymarkIndex == target.WaymarkIndex);
        return state.Entries.FirstOrDefault(e => e.Name == target.Name);
    }

    private void HandleStatRoll(RelayMessage msg)
    {
        if (msg.RollMarkerName == null) return;

        // Ajouter à l'historique
        var rolls = msg.RollDice is { Length: > 1 } ? msg.RollDice : null;
        var result = new DiceResult
        {
            RollerName = msg.RollMarkerName,
            RollerHash = msg.RollerHash,
            StatName = msg.StatName,
            RawRoll = msg.RollResult,
            Modifier = msg.RollModifier,
            Total = msg.RollTotal,
            DiceMax = msg.RollMax,
            Target = msg.RollTarget,
            Success = msg.RollSuccess,
            IndividualRolls = rolls,
        };
        session.AddRollToHistory(result);

        // Construire le message chat
        var rollMarkerName = msg.RollMarkerName;
        var rollTotal = msg.RollTotal;
        var rollMax = msg.RollMax;
        var totalMod = msg.RollModifier + msg.RollTempModifier;
        var modifierStr = totalMod >= 0 ? $"+{totalMod}" : totalMod.ToString();
        var breakdown = rolls != null ? string.Join(" + ", rolls.Length > 6 ? rolls[..5].Append(0).ToArray() : rolls).Replace(" + 0", " + ...") : "";
        string chatMsg;
        if (msg is { RollTarget: { } target, RollSuccess: { } success })
        {
            var verdict = Loc.Get(success ? "Chat.RollSuccess" : "Chat.RollFailure");
            chatMsg = string.Format(Loc.Get("Chat.StatRollTarget"), msg.RollMarkerName, msg.RollResult,
                msg.RollMax, msg.StatName ?? "?", target, verdict);
            if (breakdown.Length > 0)
                chatMsg = $"{chatMsg} {breakdown}";
        }
        else if (msg.StatName != null)
        {
            chatMsg = breakdown.Length > 0
                ? string.Format(Loc.Get("Chat.StatRollMulti"), msg.RollMarkerName, msg.RollResult, msg.RollMax, modifierStr, msg.RollTotal, msg.StatName, breakdown)
                : string.Format(Loc.Get("Chat.StatRoll"), msg.RollMarkerName, msg.RollResult, msg.RollMax, modifierStr, msg.RollTotal, msg.StatName);
        }
        else
        {
            chatMsg = breakdown.Length > 0
                ? string.Format(Loc.Get("Chat.RollMulti"), msg.RollMarkerName, msg.RollResult, msg.RollMax, breakdown)
                : string.Format(Loc.Get("Chat.Roll"), msg.RollMarkerName, msg.RollTotal, msg.RollMax);
        }

        // Mise à jour du marqueur (immédiate ou différée selon l'animation)
        void UpdateMarkerResult()
        {
            for (var i = 0; i < Constants.WaymarkCount; i++)
            {
                var marker = session.CurrentMarkers.Markers[i];
                if (marker.Name == rollMarkerName)
                {
                    marker.LastRollResult = rollTotal;
                    marker.LastRollMax = rollMax;
                    break;
                }
            }
        }

        if (configuration.ShowDiceAnimation)
        {
            diceRollOverlay.Show(msg.RollMarkerName, msg.RollTotal, msg.RollMax, msg.RollResult, msg.RollModifier, msg.RollTempModifier, msg.StatName, rolls,
                session.ActiveTemplate?.CriticalSuccessThreshold ?? 0,
                session.ActiveTemplate?.CriticalFailureThreshold ?? 0,
                session.ActiveTemplate?.RollLowerIsBetter ?? false,
                msg.RollTarget, msg.RollSuccess);
            diceRollOverlay.DeferAction(UpdateMarkerResult);
            diceRollOverlay.DeferChatMessage(chatMsg);
        }
        else
        {
            UpdateMarkerResult();
            Plugin.ChatGui.Print(chatMsg);
        }
    }

    private void HandlePlayerStatUpdate(RelayMessage msg)
    {
        // Seul le DM traite les mises à jour de stats des joueurs
        if (!session.IsGm || msg.PlayerHash == null) return;

        var player = session.PartyMembers.FirstOrDefault(p => p.Hash == msg.PlayerHash);
        if (player == null) return;

        // Appliquer PV / PE depuis la fiche du joueur
        if (msg.HpMax is > 0)
        {
            player.HpMax = msg.HpMax.Value;
            player.Hp = msg.Hp ?? player.Hp;
            if (player.Hp > player.HpMax) player.Hp = player.HpMax;
        }
        if (msg.MpMax is > 0)
        {
            player.MpMax = msg.MpMax.Value;
            player.Mp = msg.Mp ?? player.Mp;
            if (player.Mp > player.MpMax) player.Mp = player.MpMax;
        }


        if (msg.MoveMax is { } moveMax)
        {
            player.MoveMax = moveMax;
            player.MoveLeft = msg.MoveLeft ?? 0f;
        }

        // Appliquer les stats
        if (msg.Stats != null)
            player.Stats = msg.Stats.Select(s => s.DeepCopy()).ToList();

        // Appliquer les compteurs
        if (msg.Counters != null)
            player.Counters = msg.Counters.Select(c => c.DeepCopy()).ToList();

        session.BroadcastPlayerUpdate();
    }

    private void HandleCachedState(RelayMessage msg)
    {
        if (!session.IsGm) return;

        // Les PNJ sont restaurés avant le test sur les marqueurs : une session dont seuls des
        // PNJ étaient posés a un cache sans marqueurs, et sortir ici les aurait perdus.
        session.RestoreCachedNpcs(msg.Npcs);

        if (msg.Markers == null) return;
        ApplyMarkersFromMessage(msg);

        session.CacheRestored = true;
        Plugin.ChatGui.Print(Loc.Get("Chat.CacheRestoredServer"));
        Plugin.Log.Info("[MasterEvent] Session restored from server cache.");
    }

    private void ApplyMarkersFromMessage(RelayMessage msg)
    {
        if (msg.Markers == null) return;

        for (var i = 0; i < msg.Markers.Length && i < Constants.WaymarkCount; i++)
        {
            var src = msg.Markers[i];
            var dst = session.CurrentMarkers.Markers[i];
            dst.CopyFrom(src);
        }

        if (msg.HpMode != null && Enum.TryParse<HpMode>(msg.HpMode, out var hpMode))
            session.HpMode = hpMode;
        if (msg.MpMode != null && Enum.TryParse<HpMode>(msg.MpMode, out var mpMode))
            session.MpMode = mpMode;
        session.ShowMpBar = msg.ShowMpBar;
        session.ShowShield = msg.ShowShield;
    }

    private void HandleWeatherUpdate(RelayMessage msg)
    {
        if (session.CanEdit) return;

        if (msg.WeatherId == 0)
        {
            session.ApplyWeather(0);
            Plugin.ChatGui.Print(Loc.Get("Chat.WeatherReset"));
            return;
        }

        session.ApplyWeather(msg.WeatherId);
        var weatherName = msg.WeatherName ?? msg.WeatherId.ToString();
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.WeatherApplied"), weatherName));
        // Le conflit se joue sur la machine du joueur : c'est là qu'il faut l'avertir.
        Plugin.PluginConflicts.NotifyWeatherConflict();
    }

    private void HandleTimeUpdate(RelayMessage msg)
    {
        if (session.CanEdit) return;

        // Champ absent = retour à l'heure du jeu ; 0 reste une heure valide (minuit).
        if (msg.EorzeaTime is not { } eorzeaSeconds)
        {
            session.ClearTime();
            Plugin.ChatGui.Print(Loc.Get("Chat.TimeReset"));
            return;
        }

        session.ApplyTime(eorzeaSeconds);
        var hour = WeatherService.SecondsToHour(eorzeaSeconds);
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.TimeApplied"), $"{hour:00}:00"));
        Plugin.PluginConflicts.NotifyWeatherConflict();
    }

    private void HandleAllianceKick(RelayMessage msg)
    {
        // Le joueur kické vérifie si c'est lui qui est ciblé
        if (msg.TargetHash == null || msg.TargetHash != session.LocalPlayerHash) return;

        Plugin.ChatGui.Print(Loc.Get("Chat.AllianceKicked"));
        session.OnAllianceKicked?.Invoke();
    }

    private void HandleAllianceInvite(RelayMessage msg)
    {
        // Ignorer si déjà en mode alliance ou si le code est manquant
        if (session.IsAllianceMode || string.IsNullOrEmpty(msg.AllianceCode)) return;

        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.AllianceInvite"), msg.AllianceCode));
        session.OnAllianceInvite?.Invoke(msg.AllianceCode);
    }

    private void HandleAllianceDisband()
    {
        // Ignorer si pas en mode alliance ou si on est le GM
        if (!session.IsAllianceMode || session.IsGm) return;

        Plugin.ChatGui.Print(Loc.Get("Chat.AllianceDisband"));
        session.OnAllianceDisband?.Invoke();
    }
}
