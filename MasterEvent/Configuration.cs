using System;
using Dalamud.Configuration;
using MasterEvent.Models;

namespace MasterEvent;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int ExpectedRgpdVersion = 1;

    public int Version { get; set; } = 0;

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
    public bool DebugMode { get; set; }
    public bool RgpdConsentGiven { get; set; }
    public DateTime? RgpdConsentDate { get; set; }
    public int AcceptedRgpdVersion { get; set; }

    public bool IsRgpdConsentValid =>
        RgpdConsentGiven && AcceptedRgpdVersion >= ExpectedRgpdVersion;
    public bool Migrate()
    {
        var changed = false;

        // Migration ancienne URL IP vers le nouveau domaine
        if (Version < 1)
        {
            if (RelayServerUrl is "ws://83.228.223.246:8765" or "ws://83.228.223.246:8765/")
                RelayServerUrl = Constants.DefaultRelayUrl;
            Version = 1;
            changed = true;
        }

        return changed;
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
