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
    // Rubriques de Réglages, dans l'ordre de la sous-barre de GmWindow. « À propos » vient en
    // dernier, à part : ce n'est pas un réglage.
    public const int General = 0;
    public const int Cloud = 1;
    public const int Privacy = 2;
    public const int Advanced = 3;
    public const int About = 4;

    // La clé du libellé sert aussi d'ancre : choisir un résultat fait défiler jusqu'au réglage.
    public static readonly SettingsEntry[] Entries =
    [
        new(General, "Config.Language"),
        new(General, "Config.UiGlass", "Config.UiGlass.Tooltip"),
        new(General, "General.AutoOpenPlayerWindow", "General.AutoOpenPlayerWindow.Tooltip"),
        new(General, "General.AutoApplyWaymarks", "General.AutoApplyWaymarks.Tooltip"),
        new(General, "General.SuppressInInstance", "General.SuppressInInstance.Tooltip"),
        new(General, "Config.AcceptModdedNpcs", "Config.AcceptModdedNpcsHint"),
        new(General, "General.ShowDiceAnimation", "General.ShowDiceAnimation.Tooltip"),
        new(General, "Settings.ShowPlayerToggleButton", "Settings.ShowPlayerToggleButtonTooltip"),
        new(General, "General.ShowTacticalOverlay", "General.ShowTacticalOverlay.Tooltip"),
        new(General, "General.TacticalCamera", "General.TacticalCamera.Tooltip"),
        new(General, "General.HideNameplatesInCombat", "General.HideNameplatesInCombat.Tooltip"),
        new(General, "General.PlayDeadAtZeroHp", "General.PlayDeadAtZeroHp.Tooltip"),
        new(Cloud, "Cloud.LinkButton", "Cloud.Intro"),
        new(Privacy, "Privacy.Revoke", "Privacy.Subtitle"),
        new(Privacy, "Privacy.RightsTitle", "Privacy.Title"),
        new(Advanced, "Advanced.DebugMode", "Advanced.Warning"),
        new(About, "About.RelaunchAssistant", "Guide.Landing.Description"),
        new(About, "About.Changelog"),
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
