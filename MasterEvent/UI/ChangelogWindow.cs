using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Notes de version, ouvertes d'office quand la version installée change.
public sealed class ChangelogWindow : MasterEventWindowBase
{
    private readonly ChangelogService changelog;

    private readonly Configuration configuration;

    public ChangelogWindow(ChangelogService changelog, Configuration configuration)
        : base($"{Loc.Get("Changelog.Title")}###MasterEventChangelog", ImGuiWindowFlags.NoCollapse)
    {
        this.changelog = changelog;
        this.configuration = configuration;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(900, 1400),
        };
        Size = new Vector2(560, 620);
        SizeCondition = ImGuiCond.FirstUseEver;

        // Comparaison sur la version affichée et non sur le build : sinon la fenêtre
        // reviendrait à chaque compilation.
        if (!string.Equals(configuration.LastSeenChangelogVersion, Constants.PluginVersion, StringComparison.Ordinal))
            IsOpen = true;
    }

    public override void OnClose() => Acknowledge();

    // Marque ces notes comme lues. Appelée par le bouton comme par la croix : fermer d'une
    // façon ou de l'autre revient au même, la fenêtre ne doit pas revenir sans raison.
    private void Acknowledge()
    {
        if (string.Equals(configuration.LastSeenChangelogVersion, Constants.PluginVersion, StringComparison.Ordinal))
            return;

        configuration.LastSeenChangelogVersion = Constants.PluginVersion;
        configuration.Save();
    }

    protected override void DrawContents()
    {
        var document = changelog.Document;
        if (document == null)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Changelog.Unavailable"));
            return;
        }

        // Le titre de la fenêtre dit déjà « Notes de version » : l'en-tête sert à situer la
        // version, pas à répéter.
        LayoutControls.DrawTabHeader(FontAwesomeIcon.FileAlt, Loc.Get("About.Title"),
            $"v{Constants.PluginVersion}");

        if (!string.IsNullOrEmpty(document.Tagline))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(MasterEventTheme.AccentColor, document.Tagline);
            ImGui.PopTextWrapPos();
            ImGuiHelpers.ScaledDummy(6f);
        }

        // La liste laisse la place du bouton, sinon elle le pousse hors de la fenêtre.
        var footer = ImGui.GetFrameHeightWithSpacing() + (4f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginChild("##changelog_entries", new Vector2(0f, -footer)))
        {
            foreach (var entry in document.Entries)
                DrawEntry(entry);
        }
        ImGui.EndChild();

        if (ImGui.Button(Loc.Get("Changelog.Acknowledge"), new Vector2(ImGui.GetContentRegionAvail().X, 0f)))
        {
            Acknowledge();
            IsOpen = false;
        }
    }

    private static void DrawEntry(ChangelogEntry entry)
    {
        var title = string.IsNullOrEmpty(entry.Date) ? entry.Version : $"{entry.Version}   {entry.Date}";
        LayoutControls.BeginCard(title, FontAwesomeIcon.CodeBranch);

        if (!string.IsNullOrEmpty(entry.Headline))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + LayoutControls.CardContentWidth);
            ImGui.TextColored(MasterEventTheme.TextStrong, entry.Headline);
            ImGui.PopTextWrapPos();
            ImGuiHelpers.ScaledDummy(4f);
        }

        foreach (var section in entry.Sections)
        {
            if (!string.IsNullOrEmpty(section.Title))
            {
                if (ResolveIcon(section.Icon) is { } icon)
                {
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.AccentColor, icon.ToIconString());
                    ImGui.SameLine();
                }

                ImGui.TextColored(MasterEventTheme.AccentColor, section.Title);
            }

            foreach (var item in section.Items)
            {
                ImGui.TextColored(MasterEventTheme.TextDim, "  \u2022");
                ImGui.SameLine(0f, 4f);
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + LayoutControls.CardContentWidth
                    - (ImGui.GetCursorPosX() - ImGui.GetCursorStartPos().X));
                ImGui.TextColored(MasterEventTheme.TextDim, item);
                ImGui.PopTextWrapPos();
            }

            ImGuiHelpers.ScaledDummy(4f);
        }

        LayoutControls.EndCard();
        ImGui.Spacing();
    }

    private static FontAwesomeIcon? ResolveIcon(string? name)
        => string.IsNullOrEmpty(name) || !Enum.TryParse<FontAwesomeIcon>(name, true, out var icon)
            ? null
            : icon;
}
