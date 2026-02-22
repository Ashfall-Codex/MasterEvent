using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;

namespace MasterEvent.UI;

public sealed class RgpdConsentWindow : MasterEventWindowBase
{
    private readonly Configuration configuration;
    private readonly Action onConsentGiven;
    private bool checkboxAccepted;

    public RgpdConsentWindow(Configuration configuration, Action onConsentGiven)
        : base("MasterEvent###MasterEventRgpdConsent",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoDocking)
    {
        this.configuration = configuration;
        this.onConsentGiven = onConsentGiven;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(580, 450),
            MaximumSize = new Vector2(580, 450),
        };
    }

    protected override void DrawContents()
    {
        // Title
        ImGui.PushFont(ImGui.GetIO().Fonts.Fonts[0]);
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Rgpd.Consent.Title"));
        ImGui.PopFont();
        ImGui.Separator();
        ImGui.Spacing();

        // Intro
        ImGui.TextWrapped(Loc.Get("Rgpd.Consent.Intro"));
        ImGui.Spacing();

        // Reassurance
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.8f, 0.5f, 1f));
        ImGui.TextWrapped(Loc.Get("Rgpd.Consent.Reassurance"));
        ImGui.PopStyleColor();
        ImGui.Spacing();
        ImGui.Spacing();

        // Data collected section
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Rgpd.Consent.DataCollected"));
        ImGui.Spacing();

        DrawBulletItem(Loc.Get("Rgpd.Consent.Data1"));
        DrawBulletItem(Loc.Get("Rgpd.Consent.Data2"));
        DrawBulletItem(Loc.Get("Rgpd.Consent.Data3"));
        DrawBulletItem(Loc.Get("Rgpd.Consent.Data4"));

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Checkbox
        ImGui.Checkbox(Loc.Get("Rgpd.Consent.Checkbox"), ref checkboxAccepted);

        ImGui.Spacing();

        // Accept button (disabled if checkbox not checked)
        if (!checkboxAccepted)
            ImGui.BeginDisabled();

        var buttonWidth = 200f * ImGuiHelpers.GlobalScale;
        var availWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX((availWidth - buttonWidth) / 2f + ImGui.GetCursorPosX());

        if (ImGui.Button(Loc.Get("Rgpd.Consent.Accept"), new Vector2(buttonWidth, 32f * ImGuiHelpers.GlobalScale)))
        {
            configuration.RgpdConsentGiven = true;
            configuration.RgpdConsentDate = DateTime.Now;
            configuration.AcceptedRgpdVersion = Configuration.ExpectedRgpdVersion;
            configuration.Save();

            IsOpen = false;
            onConsentGiven.Invoke();
        }

        if (!checkboxAccepted)
            ImGui.EndDisabled();

        ImGui.Spacing();

        // Rights notice
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
        ImGui.TextWrapped(Loc.Get("Rgpd.Consent.Rights"));
        ImGui.PopStyleColor();
    }

    private static void DrawBulletItem(string text)
    {
        ImGui.TextColored(MasterEventTheme.AccentColor, "\u2022");
        ImGui.SameLine();
        ImGui.TextWrapped(text);
    }
}
