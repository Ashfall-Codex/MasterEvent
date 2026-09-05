using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;

namespace MasterEvent.UI.Components;

// Contrôles de réglages partagés entre la fenêtre de configuration autonome
// et l'onglet Paramètres de la fenêtre MJ, qui affichaient jusqu'ici le même
// code en double.
public static class SettingsControls
{
    // Sélecteur de langue de l'interface. La largeur diffère selon la fenêtre hôte.
    public static void DrawLanguageSelector(Configuration configuration, float width)
    {
        ImGui.TextUnformatted(Loc.Get("Config.Language"));
        var currentLabel = Loc.GetLanguageDisplayName(Loc.CurrentLanguage);
        ImGui.SetNextItemWidth(width * ImGuiHelpers.GlobalScale);
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
        ImGui.TextUnformatted(Loc.Get("Config.UiOpacity"));

        var reduce = configuration.UiReduceTransparency;

        // Le curseur n'a plus de sens quand la transparence est désactivée.
        if (reduce) ImGui.BeginDisabled();
        var opacity = configuration.UiOpacity;
        ImGui.SetNextItemWidth(width * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##ui_opacity", ref opacity, MasterEventTheme.MinOpacity, 1f, "%.2f"))
        {
            configuration.UiOpacity = Math.Clamp(opacity, MasterEventTheme.MinOpacity, 1f);
            configuration.Save();
        }
        if (reduce) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(400f * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted(Loc.Get("Config.UiOpacity.Tooltip"));
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        if (ImGui.Checkbox(Loc.Get("Config.UiReduceTransparency"), ref reduce))
        {
            configuration.UiReduceTransparency = reduce;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(400f * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted(Loc.Get("Config.UiReduceTransparency.Tooltip"));
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    // Bouton de révocation du consentement RGPD, avec sa confirmation en deux temps.
    // L'état de confirmation appartient à la fenêtre appelante, d'où le paramètre ref.
    public static void DrawRgpdRevoke(
        Configuration configuration,
        ref bool revokeConfirmPending,
        Action? onConsentRevoked)
    {
        if (!configuration.IsRgpdConsentValid)
            return;

        if (!revokeConfirmPending)
        {
            if (ImGui.Button(Loc.Get("Privacy.Revoke")))
                revokeConfirmPending = true;
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), Loc.Get("Privacy.RevokeWarning"));
        ImGui.Spacing();

        if (ImGui.Button(Loc.Get("Privacy.RevokeConfirm")))
        {
            configuration.RgpdConsentGiven = false;
            configuration.RgpdConsentDate = null;
            configuration.AcceptedRgpdVersion = 0;
            configuration.Save();
            revokeConfirmPending = false;
            onConsentRevoked?.Invoke();
        }

        ImGui.SameLine();
        if (ImGui.Button(Loc.Get("Gm.Cancel")))
            revokeConfirmPending = false;
    }
}
