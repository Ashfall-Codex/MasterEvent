using System;
using System.Linq;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;

namespace MasterEvent.Communication;

public class ProtocolHandler
{
    private readonly SessionManager session;

    public ProtocolHandler(SessionManager session)
    {
        this.session = session;
    }

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
        }
    }

    private void HandleUpdate(RelayMessage msg)
    {
        if (session.CanEdit || msg.Markers == null) return;

        for (var i = 0; i < msg.Markers.Length && i < Constants.WaymarkCount; i++)
        {
            var src = msg.Markers[i];
            var dst = session.CurrentMarkers.Markers[i];
            dst.Name = src.Name;
            dst.Hp = src.Hp;
            dst.Mp = src.Mp;
            dst.HpMax = src.HpMax;
            dst.MpMax = src.MpMax;
            dst.Shield = src.Shield;
            dst.Counters = src.Counters?.Select(c => c.DeepCopy()).ToList();
            dst.Stats = src.Stats?.Select(s => s.DeepCopy()).ToList();
            dst.Attitude = src.Attitude;
            dst.IsBoss = src.IsBoss;
            dst.IsVisible = src.IsVisible;
            dst.X = src.X;
            dst.Y = src.Y;
            dst.Z = src.Z;
        }

        // Sync display settings from GM
        if (msg.HpMode != null && Enum.TryParse<HpMode>(msg.HpMode, out var hpMode))
            session.HpMode = hpMode;
        if (msg.MpMode != null && Enum.TryParse<HpMode>(msg.MpMode, out var mpMode))
            session.MpMode = mpMode;
        session.ShowMpBar = msg.ShowMpBar;
        session.ShowShield = msg.ShowShield;
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
        session.ConnectedPlayerCount = msg.PlayerCount;
        session.UpdatePlayerConnection(session.LocalPlayerHash, true);
        Plugin.Log.Info($"[MasterEvent] Joined relay room. Players: {msg.PlayerCount}");

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
        if (msg.PlayerHash != null)
            session.UpdatePlayerConnection(msg.PlayerHash, true);
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.PlayerJoined"), msg.PlayerName));

        // Auto-send current state to new player
        if (session.IsGm)
        {
            session.BroadcastUpdate();
            session.BroadcastPlayerUpdate();
            if (session.ActiveTemplate != null)
                session.BroadcastTemplate();
            if (session.CurrentTurnState is { IsActive: true })
                session.BroadcastTurnState();
        }
    }

    private void HandlePlayerLeft(RelayMessage msg)
    {
        session.ConnectedPlayerCount = msg.PlayerCount;
        if (msg.PlayerHash != null)
            session.UpdatePlayerConnection(msg.PlayerHash, false);
        Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.PlayerLeft"), msg.PlayerName));
    }

    private void HandleVersionMismatch(RelayMessage _)
    {
        Plugin.ChatGui.Print(Loc.Get("Chat.VersionMismatch"));
    }

    private void HandleRoll(RelayMessage msg)
    {
        if (session.CanEdit) return;

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
                local.IsGm = incoming.IsGm;
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
            var someoneJustActed = false;
            for (var i = 0; i < msg.TurnState.Entries.Count; i++)
            {
                if (!oldState.Entries[i].HasActed && msg.TurnState.Entries[i].HasActed)
                {
                    someoneJustActed = true;
                    break;
                }
            }

            if (someoneJustActed)
            {
                var next = msg.TurnState.Entries.FirstOrDefault(e => !e.HasActed);
                if (next != null)
                    session.ShowTurnToast(next.Name);
                else
                    session.ShowRoundEndToast(newRound);
            }
        }

        session.CurrentTurnState = msg.TurnState.DeepCopy();

        if (newRound > oldRound && oldRound > 0)
            session.ShowRoundToast(newRound);
    }

    private void HandleTurnClear()
    {
        if (session.CanEdit) return;
        session.CurrentTurnState = null;
    }

    private void HandleStatRoll(RelayMessage msg)
    {
        if (msg.RollMarkerName == null) return;

        // Stocker le résultat sur le marqueur correspondant
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var marker = session.CurrentMarkers.Markers[i];
            if (marker.Name == msg.RollMarkerName)
            {
                marker.LastRollResult = msg.RollTotal;
                marker.LastRollMax = msg.RollMax;
                break;
            }
        }

        // Ajouter à l'historique
        var result = new DiceResult
        {
            RollerName = msg.RollMarkerName,
            RollerHash = msg.RollerHash,
            StatName = msg.StatName,
            RawRoll = msg.RollResult,
            Modifier = msg.RollModifier,
            Total = msg.RollTotal,
            DiceMax = msg.RollMax,
        };
        session.AddRollToHistory(result);

        // Afficher en chat avec détail
        var modifierStr = msg.RollModifier >= 0 ? $"+{msg.RollModifier}" : msg.RollModifier.ToString();
        var statInfo = msg.StatName != null ? $" ({msg.StatName} {modifierStr})" : "";
        Plugin.ChatGui.Print(string.Format(
            Loc.Get("Chat.StatRoll"),
            msg.RollMarkerName, msg.RollResult, msg.RollMax, modifierStr, msg.RollTotal) + statInfo);
    }

    private void HandlePlayerStatUpdate(RelayMessage msg)
    {
        // Seul le DM traite les mises à jour de stats des joueurs
        if (!session.IsGm || msg.PlayerHash == null || msg.Stats == null) return;

        var player = session.PartyMembers.FirstOrDefault(p => p.Hash == msg.PlayerHash);
        if (player == null) return;

        player.Stats = msg.Stats.Select(s => s.DeepCopy()).ToList();
        session.BroadcastPlayerUpdate();
    }

    private void HandleCachedState(RelayMessage msg)
    {
        if (!session.IsGm || msg.Markers == null) return;

        for (var i = 0; i < msg.Markers.Length && i < Constants.WaymarkCount; i++)
        {
            var src = msg.Markers[i];
            var dst = session.CurrentMarkers.Markers[i];
            dst.Name = src.Name;
            dst.Hp = src.Hp;
            dst.Mp = src.Mp;
            dst.HpMax = src.HpMax;
            dst.MpMax = src.MpMax;
            dst.Shield = src.Shield;
            dst.Counters = src.Counters?.Select(c => c.DeepCopy()).ToList();
            dst.Stats = src.Stats?.Select(s => s.DeepCopy()).ToList();
            dst.Attitude = src.Attitude;
            dst.IsBoss = src.IsBoss;
            dst.IsVisible = src.IsVisible;
            dst.X = src.X;
            dst.Y = src.Y;
            dst.Z = src.Z;
        }

        if (msg.HpMode != null && Enum.TryParse<HpMode>(msg.HpMode, out var hpMode))
            session.HpMode = hpMode;
        if (msg.MpMode != null && Enum.TryParse<HpMode>(msg.MpMode, out var mpMode))
            session.MpMode = mpMode;
        session.ShowMpBar = msg.ShowMpBar;
        session.ShowShield = msg.ShowShield;

        session.CacheRestored = true;
        Plugin.ChatGui.Print(Loc.Get("Chat.CacheRestoredServer"));
        Plugin.Log.Info("[MasterEvent] Session restored from server cache.");
    }
}
