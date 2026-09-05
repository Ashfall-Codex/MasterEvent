using System;
using System.Collections.Generic;
using System.Globalization;
using MasterEvent.Localization;

namespace MasterEvent.UI.Components;
public sealed record SettingsEntry(int Section, string LabelKey, string? DescriptionKey = null)
{
    public string Label => Loc.Get(LabelKey);
    public string Description => DescriptionKey is null ? string.Empty : Loc.Get(DescriptionKey);
}

public static class SettingsCatalog
{
    private const int General = 0;
    private const int Cloud = 1;
    private const int Guide = 2;
    private const int Privacy = 3;
    private const int Advanced = 4;

    public static readonly SettingsEntry[] Entries =
    [
        new(General, "Config.Language"),
        new(General, "Config.UiOpacity", "Config.UiOpacity.Tooltip"),
        new(General, "Config.UiReduceTransparency", "Config.UiReduceTransparency.Tooltip"),
        new(General, "General.AutoOpenPlayerWindow", "General.AutoOpenPlayerWindow.Tooltip"),
        new(General, "General.AutoApplyWaymarks", "General.AutoApplyWaymarks.Tooltip"),
        new(General, "General.SuppressInInstance", "General.SuppressInInstance.Tooltip"),
        new(General, "General.ShowDiceAnimation", "General.ShowDiceAnimation.Tooltip"),
        new(General, "Settings.ShowPlayerToggleButton", "Settings.ShowPlayerToggleButtonTooltip"),
        new(General, "General.ShowTacticalOverlay", "General.ShowTacticalOverlay.Tooltip"),
        new(General, "General.TacticalCamera", "General.TacticalCamera.Tooltip"),
        new(General, "General.HideNameplatesInCombat", "General.HideNameplatesInCombat.Tooltip"),
        new(General, "General.PlayDeadAtZeroHp", "General.PlayDeadAtZeroHp.Tooltip"),
        new(Cloud, "Cloud.LinkButton", "Cloud.Intro"),
        new(Guide, "Sidebar.Guide", "Guide.Landing.Description"),
        new(Privacy, "Privacy.Title", "Privacy.RightsTitle"),
        new(Advanced, "Advanced.DebugMode", "Advanced.Warning"),
    ];

    public static List<SettingsEntry> Search(string query)
    {
        var results = new List<SettingsEntry>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        var needle = query.Trim();
        foreach (var entry in Entries)
        {
            if (Contains(entry.Label, needle) || Contains(entry.Description, needle))
                results.Add(entry);
        }
        return results;
    }

    public static int IndexOf(string haystack, string needle, out int matchLength)
    {
        matchLength = 0;
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return -1;

        return CultureInfo.CurrentCulture.CompareInfo.IndexOf(
            haystack, needle, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace, out matchLength);
    }

    private static bool Contains(string haystack, string needle) =>
        IndexOf(haystack, needle, out _) >= 0;
}
