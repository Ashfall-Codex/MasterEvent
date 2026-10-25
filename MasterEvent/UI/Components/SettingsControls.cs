using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;

namespace MasterEvent.UI.Components;

// Contrôles de réglages de la rubrique Général et de la rubrique Vie privée.
public static class SettingsControls
{
    // Une largeur trop grande pour la place disponible est ramenée à celle-ci.
    private static float FitWidth(float width)
        => Math.Min(width * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X);

    // Sélecteur de langue de l'interface.
    public static void DrawLanguageSelector(Configuration configuration, float width)
    {
        ImGui.TextUnformatted(Loc.Get("Config.Language"));
        var currentLabel = Loc.GetLanguageDisplayName(Loc.CurrentLanguage);
        ImGui.SetNextItemWidth(FitWidth(width));
        if (!ImGui.BeginCombo("##ui_language", currentLabel))
            return;

        foreach (var option in Loc.AvailableLanguages)
        {
            var isSelected = string.Equals(option.Key, Loc.CurrentLanguage, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(option.Value, isSelected))
            {
                Loc.SetLanguage(option.Key);
                configuration.UiLanguage = option.Key;
                configuration.Save();
            }
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    public static void DrawAppearanceSection(Configuration configuration, float width)
    {
        ImGui.TextUnformatted(Loc.Get("Config.UiGlass"));

        var glass = configuration.UiGlass;
        var released = GlassSlider.Draw("##ui_glass", ref glass, FitWidth(width),
            Loc.Get("Config.UiGlass.Tooltip"));
        configuration.UiGlass = glass;
        if (released) configuration.Save();
    }

    // Retrait du consentement, confirmé en ligne : le rouge plein n'apparaît qu'à la confirmation.
    public static void DrawRgpdRevoke(Configuration configuration, Action? onConsentRevoked)
    {
        if (!configuration.IsRgpdConsentValid)
            return;

        if (!ConfirmInline.Draw("privacy_revoke", Loc.Get("Privacy.Revoke"),
                Loc.Get("Privacy.RevokeWarning"), Loc.Get("Privacy.RevokeConfirm")))
            return;

        configuration.RgpdConsentGiven = false;
        configuration.RgpdConsentDate = null;
        configuration.AcceptedRgpdVersion = 0;
        configuration.Save();
        onConsentRevoked?.Invoke();
    }
}
