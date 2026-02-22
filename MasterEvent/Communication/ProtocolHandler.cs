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

        foreach (var incoming in msg.Players)
        {
            var local = session.PartyMembers.FirstOrDefault(p => p.Hash == incoming.Hash);
            if (local != null)
            {
                local.Hp = incoming.Hp;
                local.IsGm = incoming.IsGm;
            }
        }
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
