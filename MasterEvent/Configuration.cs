using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Dalamud.Configuration;
using MasterEvent.Models;

namespace MasterEvent;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int ExpectedRgpdVersion = 4;

    public int Version { get; set; }

    public string RelayServerUrl { get; set; } = Constants.DefaultRelayUrl;
    public string UiLanguage { get; set; } = "fr";
    public int DiceMax { get; set; } = 999;
    public HpMode HpMode { get; set; } = HpMode.Points;
    public bool ShowMpBar { get; set; } = true;
    public bool ShowShield { get; set; } = true;
    public bool ShowPlayerStatsInline { get; set; }
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
    public bool PlayerToggleButtonHorizontal { get; set; }
    public ToggleButtonLayout PlayerToggleLayout { get; set; } = ToggleButtonLayout.Grid;
    public bool TacticalCamera { get; set; }
    public bool TacticalCameraAutoCombat { get; set; }
    public bool HideNameplatesInCombat { get; set; }
    public bool PlayDeadAtZeroHp { get; set; }
    public float UiOpacity { get; set; } = 1f;
    public const float DefaultGlass = 0.5f;
    public float UiGlass { get; set; } = DefaultGlass;
    public bool UiReduceTransparency { get; set; }
    public bool DebugMode { get; set; }
    public string? LastTestBuildWarningVersion { get; set; }
    public string? LastSeenChangelogVersion { get; set; }
    public bool SetupCompleted { get; set; }
    public string? LobbyCode { get; set; }
    public bool LobbyIsCreator { get; set; }
    public string? AllianceRoomCode { get; set; }
    public bool AllianceIsCreator { get; set; }
    public bool RgpdConsentGiven { get; set; }
    public DateTime? RgpdConsentDate { get; set; }
    public int AcceptedRgpdVersion { get; set; }
    public string LeaderToken { get; set; } = string.Empty;
    public string? MasterEventAccountId { get; set; }
    public bool CloudSyncEnabled { get; set; } = true;
    public long CloudLastSyncAt { get; set; }
    public Dictionary<ulong, CharacterSettings> Characters { get; set; } = new();
    private ulong currentCharacterId;

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

        if (Version < 3)
        {
            if (!string.IsNullOrEmpty(AllianceRoomCode))
            {
                LobbyCode = AllianceRoomCode;
                LobbyIsCreator = AllianceIsCreator;
            }
            AllianceRoomCode = null;
            AllianceIsCreator = false;
            Version = 3;
            changed = true;
        }

        if (Version < 4)
        {
            PlayerToggleLayout = ToggleButtonLayout.Grid;
            PlayerToggleButtonHorizontal = false;
            Version = 4;
            changed = true;
        }

        if (Version < 5)
        {
            UiGlass = UiOpacity >= 1f ? DefaultGlass : UI.MasterEventTheme.GlassFromOpacity(UiOpacity);
            Version = 5;
            changed = true;
        }

        if (Version < 6)
        {
            if (UiReduceTransparency) UiGlass = 1f;
            UiReduceTransparency = false;
            Version = 6;
            changed = true;
        }

        return changed;
    }

    public void BindCharacter(ulong contentId)
    {
        currentCharacterId = contentId;
        if (contentId == 0 || Characters.ContainsKey(contentId)) return;

        var settings = new CharacterSettings();
        if (Characters.Count == 0)
        {
            settings.DefaultSheetName = DefaultSheetName;
            settings.ActiveTemplateName = ActiveTemplateName;
            DefaultSheetName = null;
            ActiveTemplateName = string.Empty;
        }
        Characters[contentId] = settings;
        Save();
    }

    public void UnbindCharacter() => currentCharacterId = 0;

    private CharacterSettings? Current =>
        currentCharacterId != 0 && Characters.TryGetValue(currentCharacterId, out var settings) ? settings : null;

    public string? GetDefaultSheetName() => Current is { } c ? c.DefaultSheetName : DefaultSheetName;

    public void SetDefaultSheetName(string? name)
    {
        if (Current is { } c) c.DefaultSheetName = name;
        else DefaultSheetName = name;
    }

    public string GetActiveTemplateName() => Current is { } c ? c.ActiveTemplateName ?? string.Empty : ActiveTemplateName;

    public void SetActiveTemplateName(string name)
    {
        if (Current is { } c) c.ActiveTemplateName = name;
        else ActiveTemplateName = name;
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

[Serializable]
public class CharacterSettings
{
    public string? DefaultSheetName { get; set; }
    public string? ActiveTemplateName { get; set; }
}
