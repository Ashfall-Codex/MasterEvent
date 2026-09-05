using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MasterEvent.Localization;
using MasterEvent.UI.Components;
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

        SettingsControls.DrawLanguageSelector(configuration, 250f);

        ImGui.Spacing();
        SettingsControls.DrawAppearanceSection(configuration, 250f);

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
        SettingsControls.DrawRgpdRevoke(configuration, ref revokeConfirmPending, onConsentRevoked);

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
