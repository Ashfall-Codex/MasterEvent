using System;
using System.Security.Cryptography;
using Dalamud.Configuration;
using MasterEvent.Models;

namespace MasterEvent;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int ExpectedRgpdVersion = 3;

    public int Version { get; set; }

    public string RelayServerUrl { get; set; } = Constants.DefaultRelayUrl;
    public string UiLanguage { get; set; } = "fr";
    public int DiceMax { get; set; } = 999;
    public HpMode HpMode { get; set; } = HpMode.Points;
    public bool ShowMpBar { get; set; } = true;
    public bool ShowShield { get; set; } = true;
    public HpMode MpMode { get; set; } = HpMode.Points;
    public string ActiveTemplateName { get; set; } = "Standard";
    public string DefaultTemplateName { get; set; } = "Standard";
    public bool GmIsPlayer { get; set; }
    public string? DefaultSheetName { get; set; }
    public bool AutoOpenPlayerWindow { get; set; } = true;
    public bool AutoApplyWaymarks { get; set; } = true;
    public bool SuppressInInstance { get; set; } = true;
    public bool ShowDiceAnimation { get; set; } = true;
    public float DiceAnimationSpeed { get; set; } = 1f;
    public bool ShowTacticalOverlay { get; set; }
    public bool ShowPlayerToggleButton { get; set; } = true;
    public float PlayerToggleButtonX { get; set; } = -1f;
    public float PlayerToggleButtonY { get; set; } = -1f;
    public bool TacticalCamera { get; set; }
    public bool TacticalCameraAutoCombat { get; set; }
    public bool HideNameplatesInCombat { get; set; }
    public bool PlayDeadAtZeroHp { get; set; }
    public bool DebugMode { get; set; }
    public bool SetupCompleted { get; set; }
    public string? AllianceRoomCode { get; set; }
    public bool AllianceIsCreator { get; set; }
    public bool RgpdConsentGiven { get; set; }
    public DateTime? RgpdConsentDate { get; set; }
    public int AcceptedRgpdVersion { get; set; }
    public string LeaderToken { get; set; } = string.Empty;
    public string? MasterEventAccountId { get; set; }
    public bool CloudSyncEnabled { get; set; } = true;
    public long CloudLastSyncAt { get; set; }

    public bool IsRgpdConsentValid =>
        RgpdConsentGiven && AcceptedRgpdVersion >= ExpectedRgpdVersion;
    public bool Migrate()
    {
        var changed = false;

        if (Version < 1)
        {
            if (RelayServerUrl is "ws://83.228.223.246:8765" or "ws://83.228.223.246:8765/")
                RelayServerUrl = Constants.DefaultRelayUrl;
            Version = 1;
            changed = true;
        }

        if (Version < 2)
        {
            if (TacticalCameraAutoCombat) TacticalCamera = true;
            TacticalCameraAutoCombat = false;
            Version = 2;
            changed = true;
        }

        return changed;
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
    // Garantit la présence d'un LeaderToken, le générant et sauvegardant si absent.
    public string EnsureLeaderToken()
    {
        if (!string.IsNullOrEmpty(LeaderToken))
            return LeaderToken;

        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        LeaderToken = Convert.ToBase64String(bytes);
        Save();
        return LeaderToken;
    }
}
