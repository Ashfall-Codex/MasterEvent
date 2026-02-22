using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using System.Numerics;

namespace MasterEvent.UI;

public sealed class ConfigWindow : MasterEventWindowBase
{
    private readonly Configuration configuration;
    private readonly Action? onConsentRevoked;
    private bool revokeConfirmPending;

    public ConfigWindow(Configuration configuration, Action? onConsentRevoked = null)
        : base("MasterEvent - Configuration###MasterEventConfig")
    {
        this.configuration = configuration;
        this.onConsentRevoked = onConsentRevoked;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 450),
            MaximumSize = new Vector2(550, 700),
        };
    }

    protected override void DrawContents()
    {
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Config.Title"));
        ImGui.Separator();

        // Language selector
        ImGui.TextUnformatted(Loc.Get("Config.Language"));
        var currentLabel = Loc.GetLanguageDisplayName(Loc.CurrentLanguage);
        ImGui.SetNextItemWidth(250f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##ui_language", currentLabel))
        {
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // --- Privacy / RGPD section ---
        DrawPrivacySection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"Version : {Constants.PluginVersion}");
    }

    private void DrawPrivacySection()
    {
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Privacy.Title"));
        ImGui.Spacing();

        // Consent status
        if (configuration.IsRgpdConsentValid && configuration.RgpdConsentDate.HasValue)
        {
            var dateStr = configuration.RgpdConsentDate.Value.ToString("dd/MM/yyyy HH:mm");
            ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1f),
                string.Format(Loc.Get("Privacy.ConsentActive"), dateStr));
        }
        else
        {
            ImGui.TextColored(new Vector4(0.8f, 0.4f, 0.4f, 1f), Loc.Get("Privacy.ConsentNone"));
        }

        ImGui.Spacing();

        // Revoke consent
        if (configuration.IsRgpdConsentValid)
        {
            if (!revokeConfirmPending)
            {
                if (ImGui.Button(Loc.Get("Privacy.Revoke")))
                {
                    revokeConfirmPending = true;
                }
            }
            else
            {
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
                {
                    revokeConfirmPending = false;
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Rights
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Privacy.RightsTitle"));
        ImGui.Spacing();

        var dimColor = new Vector4(0.7f, 0.7f, 0.7f, 1f);
        ImGui.TextColored(dimColor, Loc.Get("Privacy.RightAccess"));
        ImGui.TextColored(dimColor, Loc.Get("Privacy.RightErasure"));
        ImGui.TextColored(dimColor, Loc.Get("Privacy.RightObject"));

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Privacy.Controller"));
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Privacy.LegalBasis"));
    }
}
