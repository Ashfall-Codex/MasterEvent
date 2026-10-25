using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{

    private const double SettingsHighlightSeconds = 1.6;

    private void DrawSettingsContent()
    {
        var sidebarW = SettingsSidebarContentWidth();

        settingsCompact = ImGui.GetContentRegionAvail().X - sidebarW
                          < SettingsMinContentWidth * ImGuiHelpers.GlobalScale;

        if (settingsCompact)
        {
            DrawSettingsCompactBar();
        }
        else
        {
            if (ImGui.BeginChild("##settings_sidebar", new Vector2(sidebarW, 0), false, ImGuiWindowFlags.NoScrollbar))
            {
                DrawSettingsSidebar();
            }
            ImGui.EndChild();

            ImGui.SameLine();

            LayoutControls.DrawVerticalSeparator(6f, rootBounds);
        }

        // Une zone qui défile par rubrique : changer de rubrique repart du haut.
        var contentId = settingsSearch.Length > 0
            ? "##settings_results"
            : "##settings_content_" + SettingsSectionIds[activeSettingsTab];
        if (ImGui.BeginChild(contentId, Vector2.Zero))
        {
            if (settingsSearch.Length > 0)
            {
                DrawSettingsSearchResults();
            }
            else
            {
                switch (activeSettingsTab)
                {
                    case GeneralSettingsTab: DrawGeneralContent(); break;
                    case CloudSettingsTab: DrawCloudContent(); break;
                    case PrivacySettingsTab: DrawPrivacyContent(); break;
                    case AdvancedSettingsTab: DrawAdvancedContent(); break;
                    case AboutSettingsTab: DrawAboutContent(); break;
                }
            }
        }
        ImGui.EndChild();

        if (settingsScrollTarget != null && ImGui.GetFrameCount() > settingsScrollFrame + 2)
            settingsScrollTarget = null;
    }

    private float SettingsSidebarContentWidth()
    {
        var scale = ImGuiHelpers.GlobalScale;
        float iconWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            iconWidth = ImGui.CalcTextSize(FontAwesomeIcon.Cog.ToIconString()).X;

        var labelWidth = 0f;
        foreach (var key in SettingsLabelKeys)
            labelWidth = MathF.Max(labelWidth, ImGui.CalcTextSize(Loc.Get(key)).X);

        var needed = 8f * scale * 2f + iconWidth + 6f * scale + labelWidth;
        return MathF.Max(SettingsSidebarWidth * scale, needed);
    }

    private void SelectSettingsTab(int tabIndex)
    {
        settingsSearch = string.Empty;
        if (tabIndex == activeSettingsTab) return;

        activeSettingsTab = tabIndex;
        configuration.LastSettingsSection = SettingsSectionIds[tabIndex];
        configuration.Save();
    }

    private void OpenSettingFromSearch(SettingsEntry entry)
    {
        SelectSettingsTab(entry.Section);
        settingsScrollTarget = entry.LabelKey;
        settingsScrollFrame = ImGui.GetFrameCount();
        settingsHighlightKey = entry.LabelKey;
        settingsHighlightStart = DateTime.UtcNow;
    }

    private static Vector2 BeginSettingAnchor() => ImGui.GetCursorScreenPos();

    private void EndSettingAnchor(string labelKey, Vector2 start)
    {
        if (settingsScrollTarget == labelKey && ImGui.GetFrameCount() >= settingsScrollFrame + 2)
        {
            ImGui.SetScrollHereY(0.3f);
            settingsScrollTarget = null;
        }

        if (settingsHighlightKey != labelKey) return;

        var elapsed = (DateTime.UtcNow - settingsHighlightStart).TotalSeconds;
        if (elapsed >= SettingsHighlightSeconds)
        {
            settingsHighlightKey = null;
            return;
        }

        var fade = 1f - (float)(elapsed / SettingsHighlightSeconds);
        var scale = ImGuiHelpers.GlobalScale;
        var pad = 3f * scale;
        var bottom = ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y;
        var min = start - new Vector2(pad);
        var max = new Vector2(start.X + LayoutControls.CardContentWidth + pad, bottom + pad);
        var rounding = MasterEventTheme.RadiusControl * scale;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(min, max, ImGui.GetColorU32(MasterEventTheme.AccentColor with { W = 0.18f * fade }), rounding);
        dl.AddRect(min, max, ImGui.GetColorU32(MasterEventTheme.AccentText with { W = 0.9f * fade }), rounding,
            ImDrawFlags.None, 1.5f * scale);
    }

    private bool DrawSettingToggle(string labelKey, string? tooltipKey, ref bool value)
    {
        var start = BeginSettingAnchor();
        var toggled = ToggleSwitch.Draw("##" + labelKey, Loc.Get(labelKey), ref value,
            tooltipKey is null ? null : Loc.Get(tooltipKey));
        EndSettingAnchor(labelKey, start);
        return toggled;
    }

    // Le titre de carte prend la teinte lisible de l'accent.
    private static void DrawSettingsCard(string titleKey, FontAwesomeIcon icon, Action content)
    {
        LayoutControls.BeginCard(Loc.Get(titleKey), icon);
        content();
        LayoutControls.EndCard();
    }

    private void DrawSettingsSearchResults()
    {
        var results = SettingsCatalog.Search(settingsSearch);

        ImGuiHelpers.ScaledDummy(4f);
        if (results.Count == 0)
        {
            ImGui.TextWrapped(Loc.Get("Settings.Search.Empty"));
            return;
        }

        ImGui.TextColored(MasterEventTheme.MutedTextColor, results.Count == 1
            ? Loc.Get("Settings.Search.ResultOne")
            : string.Format(Loc.Get("Settings.Search.Results"), results.Count));
        ImGuiHelpers.ScaledDummy(4f);

        var availWidth = ImGui.GetContentRegionAvail().X;
        var rowHeight = ImGui.GetTextLineHeightWithSpacing() * 2f;
        var scale = ImGuiHelpers.GlobalScale;
        for (var i = 0; i < results.Count; i++)
        {
            var entry = results[i];
            var cursor = ImGui.GetCursorScreenPos();
            var max = cursor + new Vector2(availWidth, rowHeight);

            var clicked = ImGui.InvisibleButton($"##search_hit{i}", new Vector2(availWidth, rowHeight));
            var hovered = ImGui.IsItemHovered();
            var focused = ImGui.IsItemFocused() && ImGui.GetIO().NavVisible;
            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            var dl = ImGui.GetWindowDrawList();
            var rounding = MasterEventTheme.RadiusCard * scale;
            dl.AddRectFilled(cursor, max,
                ImGui.GetColorU32(hovered ? MasterEventTheme.ThemeButtonHovered : MasterEventTheme.ThemeHeaderBg),
                rounding);
            if (focused)
                dl.AddRect(cursor, max, ImGui.GetColorU32(MasterEventTheme.AccentText), rounding, ImDrawFlags.None, 1.5f * scale);

            var pad = 6f * scale;
            dl.PushClipRect(cursor, max, true);
            DrawHighlightedLabel(dl, cursor + new Vector2(pad, pad * 0.5f), entry.Label, settingsSearch);
            dl.AddText(cursor + new Vector2(pad, pad * 0.5f + ImGui.GetTextLineHeight()),
                ImGui.GetColorU32(MasterEventTheme.MutedTextColor),
                Loc.Get(SettingsLabelKeys[entry.Section]));
            dl.PopClipRect();

            ImGuiHelpers.ScaledDummy(2f);

            if (clicked)
            {
                OpenSettingFromSearch(entry);
                break;
            }
        }
    }

    private static void DrawHighlightedLabel(ImDrawListPtr dl, Vector2 pos, string label, string query)
    {
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var index = SettingsCatalog.IndexOf(label, query.Trim(), out var length);
        if (index < 0 || length <= 0)
        {
            dl.AddText(pos, textColor, label);
            return;
        }

        var before = label[..index];
        var match = label.Substring(index, length);
        var after = label[(index + length)..];

        var x = pos.X;
        if (before.Length > 0)
        {
            dl.AddText(new Vector2(x, pos.Y), textColor, before);
            x += ImGui.CalcTextSize(before).X;
        }

        var matchSize = ImGui.CalcTextSize(match);
        dl.AddRectFilled(
            new Vector2(x, pos.Y),
            new Vector2(x + matchSize.X, pos.Y + matchSize.Y),
            ImGui.GetColorU32(MasterEventTheme.AccentColor with { W = 0.35f }),
            2f * ImGuiHelpers.GlobalScale);
        dl.AddText(new Vector2(x, pos.Y), textColor, match);
        x += matchSize.X;

        if (after.Length > 0)
            dl.AddText(new Vector2(x, pos.Y), textColor, after);
    }

    private void DrawSettingsCompactBar()
    {
        ImGuiHelpers.ScaledDummy(4f);

        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(MathF.Min(avail * 0.45f, 160f * ImGuiHelpers.GlobalScale));
        var searching = settingsSearch.Length > 0;
        if (ImGui.BeginCombo("##settings_section", Loc.Get(SettingsLabelKeys[activeSettingsTab])))
        {
            for (var i = 0; i < SettingsLabelKeys.Length; i++)
            {
                if (i == AboutSettingsTab) ImGui.Separator();

                var selected = i == activeSettingsTab && !searching;
                if (ImGui.Selectable(Loc.Get(SettingsLabelKeys[i]) + "##settings_section_" + i, selected))
                    SelectSettingsTab(i);
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        SearchField.Draw("settings_search", ref settingsSearch, Loc.Get("Settings.Search.Hint"),
            ImGui.GetContentRegionAvail().X);

        ImGuiHelpers.ScaledDummy(2f);
    }

    private void DrawSettingsSidebar()
    {
        ImGuiHelpers.ScaledDummy(4f);
        SearchField.Draw("settings_search", ref settingsSearch, Loc.Get("Settings.Search.Hint"),
            ImGui.GetContentRegionAvail().X);
        ImGuiHelpers.ScaledDummy(6f);

        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        settingsSidebarRects.Clear();

        for (var i = 0; i < SettingsLabelKeys.Length; i++)
        {
            if (i == AboutSettingsTab)
            {
                ImGuiHelpers.ScaledDummy(2f);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(2f);
            }

            DrawSettingsSidebarButton(i);
            ImGuiHelpers.ScaledDummy(1f);
        }

        drawList.ChannelsSetCurrent(0);
        DrawSettingsSidebarIndicator(drawList);
        drawList.ChannelsMerge();
    }

    private void DrawSettingsSidebarButton(int tabIndex)
    {
        ImGui.PushID(tabIndex);

        const float iconTextGap = 6f;
        const float paddingX = 8f;
        var scale = ImGuiHelpers.GlobalScale;
        var height = MathF.Max(24f * scale, ImGui.GetFrameHeight());
        var availWidth = ImGui.GetContentRegionAvail().X;

        var isActive = activeSettingsTab == tabIndex && settingsSearch.Length == 0;

        var p = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##settingsSidebarBtn", new Vector2(availWidth, height));
        var hovered = ImGui.IsItemHovered();
        var focused = ImGui.IsItemFocused() && ImGui.GetIO().NavVisible;

        settingsSidebarRects[tabIndex] = (p, p + new Vector2(availWidth, height));

        var dl = ImGui.GetWindowDrawList();
        var rounding = 6f * scale;
        var pad = 2f * scale;
        if (hovered && !isActive)
        {
            dl.AddRectFilled(p - new Vector2(pad), p + new Vector2(availWidth + pad, height + pad),
                ImGui.GetColorU32(MasterEventTheme.ThemeButtonHovered), rounding);
        }

        if (focused)
        {
            dl.AddRect(p - new Vector2(pad), p + new Vector2(availWidth + pad, height + pad),
                ImGui.GetColorU32(MasterEventTheme.AccentText), rounding, ImDrawFlags.None, 1.5f * scale);
        }

        var textColor = ImGui.GetColorU32(isActive || hovered ? MasterEventTheme.TextStrong : MasterEventTheme.TextSecondary);
        var startX = p.X + paddingX * scale;

        var iconStr = SettingsIcons[tabIndex].ToIconString();
        Vector2 iconSz;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            iconSz = ImGui.CalcTextSize(iconStr);
            dl.AddText(new Vector2(startX, p.Y + (height - iconSz.Y) / 2f), textColor, iconStr);
        }

        var label = Loc.Get(SettingsLabelKeys[tabIndex]);
        var labelSz = ImGui.CalcTextSize(label);
        dl.AddText(new Vector2(startX + iconSz.X + iconTextGap * scale, p.Y + (height - labelSz.Y) / 2f), textColor, label);

        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked) SelectSettingsTab(tabIndex);

        ImGui.PopID();
    }

    private void DrawSettingsSidebarIndicator(ImDrawListPtr drawList)
    {
        if (settingsSearch.Length > 0) return;
        if (!settingsSidebarRects.TryGetValue(activeSettingsTab, out var rect))
            return;

        var windowPos = ImGui.GetWindowPos();
        var targetPos = rect.Min;
        var targetSize = rect.Max - rect.Min;

        if (!settingsSidebarIndicatorInit || settingsSidebarWindowPos != windowPos)
        {
            settingsSidebarIndicatorPos = targetPos;
            settingsSidebarIndicatorSize = targetSize;
            settingsSidebarIndicatorInit = true;
            settingsSidebarWindowPos = windowPos;
        }
        else
        {
            var dt = ImGui.GetIO().DeltaTime;
            var lerpT = 1f - MathF.Exp(-SettingsSidebarAnimSpeed * dt);
            settingsSidebarIndicatorPos = Vector2.Lerp(settingsSidebarIndicatorPos, targetPos, lerpT);
            settingsSidebarIndicatorSize = Vector2.Lerp(settingsSidebarIndicatorSize, targetSize, lerpT);
        }

        var padding = 2f * ImGuiHelpers.GlobalScale;
        var min = settingsSidebarIndicatorPos - new Vector2(padding);
        var max = settingsSidebarIndicatorPos + settingsSidebarIndicatorSize + new Vector2(padding);
        var rounding = 6f * ImGuiHelpers.GlobalScale;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(MasterEventTheme.AccentColor), rounding);
    }

    private void DrawSectionHeader(int tabIndex)
    {
        var subtitle = Loc.Get(SettingsDescriptionKeys[tabIndex]);
        if (!settingsCompact)
        {
            PageHeader.Draw(SettingsIcons[tabIndex], Loc.Get(SettingsLabelKeys[tabIndex]), subtitle);
            return;
        }

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(MasterEventTheme.MutedTextColor, subtitle);
        ImGui.PopTextWrapPos();
        ImGuiHelpers.ScaledDummy(2f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
    }


    private void DrawGeneralContent()
    {
        DrawSectionHeader(GeneralSettingsTab);

        DrawSettingsCard("Settings.Group.Appearance", FontAwesomeIcon.Palette, DrawAppearanceGroup);
        DrawSettingsCard("Settings.Group.Session", FontAwesomeIcon.Users, DrawSessionGroup);
        DrawSettingsCard("Settings.Group.Dice", FontAwesomeIcon.Dice, DrawDiceGroup);
        DrawSettingsCard("Settings.Group.FloatingBar", FontAwesomeIcon.Bars, DrawFloatingBarGroup);
        DrawSettingsCard("Settings.Group.Tactical", FontAwesomeIcon.ChessKnight, DrawTacticalGroup);
    }

    private void DrawAppearanceGroup()
    {
        var start = BeginSettingAnchor();
        SettingsControls.DrawLanguageSelector(configuration, 200f);
        EndSettingAnchor("Config.Language", start);

        ImGuiHelpers.ScaledDummy(4f);

        start = BeginSettingAnchor();
        SettingsControls.DrawAppearanceSection(configuration, 260f);
        EndSettingAnchor("Config.UiGlass", start);

        ImGuiHelpers.ScaledDummy(4f);
    }

    private void DrawSessionGroup()
    {
        var autoOpen = configuration.AutoOpenPlayerWindow;
        if (DrawSettingToggle("General.AutoOpenPlayerWindow", "General.AutoOpenPlayerWindow.Tooltip", ref autoOpen))
        {
            configuration.AutoOpenPlayerWindow = autoOpen;
            configuration.Save();
        }

        var autoApply = configuration.AutoApplyWaymarks;
        if (DrawSettingToggle("General.AutoApplyWaymarks", "General.AutoApplyWaymarks.Tooltip", ref autoApply))
        {
            configuration.AutoApplyWaymarks = autoApply;
            configuration.Save();
        }

        var suppressInstance = configuration.SuppressInInstance;
        if (DrawSettingToggle("General.SuppressInInstance", "General.SuppressInInstance.Tooltip", ref suppressInstance))
        {
            configuration.SuppressInInstance = suppressInstance;
            configuration.Save();
        }

        // Repris de l'ancienne fenêtre de configuration, seul endroit où il se trouvait.
        var acceptMods = configuration.AcceptModdedNpcs;
        if (DrawSettingToggle("Config.AcceptModdedNpcs", "Config.AcceptModdedNpcsHint", ref acceptMods))
        {
            configuration.AcceptModdedNpcs = acceptMods;
            configuration.Save();
        }
    }

    private void DrawDiceGroup()
    {
        var showDice = configuration.ShowDiceAnimation;
        if (DrawSettingToggle("General.ShowDiceAnimation",
                showDice ? "General.ShowDiceAnimation.Tooltip" : "General.ShowDiceAnimation.OffTooltip", ref showDice))
        {
            configuration.ShowDiceAnimation = showDice;
            session.ShowDiceAnimation = showDice;
            configuration.Save();
        }

        if (showDice)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Loc.Get("General.DiceAnimationSpeed"));
            var currentSpeed = configuration.DiceAnimationSpeed;
            foreach (var option in new[] { 1.0f, 1.5f, 2.0f })
            {
                var label = $"x{option:0.##}";
                SameLineIfFits(RadioWidth(label));
                var selected = Math.Abs(currentSpeed - option) < 0.01f;
                if (ImGui.RadioButton(label + "##dicespeed", selected) && !selected)
                {
                    configuration.DiceAnimationSpeed = option;
                    session.DiceAnimationSpeed = option;
                    configuration.Save();
                }
            }
        }
    }

    private void DrawFloatingBarGroup()
    {
        var showPlayerToggle = configuration.ShowPlayerToggleButton;
        if (DrawSettingToggle("Settings.ShowPlayerToggleButton", "Settings.ShowPlayerToggleButtonTooltip", ref showPlayerToggle))
        {
            configuration.ShowPlayerToggleButton = showPlayerToggle;
            configuration.Save();
        }

        if (!showPlayerToggle) return;

        ImGui.Indent();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Settings.PlayerToggleOrientation"));

        DrawOrientationRadio("Settings.OrientationVertical", "##toggle_vertical", ToggleButtonLayout.Vertical);
        DrawOrientationRadio("Settings.OrientationHorizontal", "##toggle_horizontal", ToggleButtonLayout.Horizontal);
        DrawOrientationRadio("Settings.OrientationGrid", "##toggle_grid", ToggleButtonLayout.Grid);

        if (Buttons.Quiet(Loc.Get("Settings.ResetPlayerTogglePosition") + "##reset_player_toggle"))
        {
            configuration.PlayerToggleButtonX = -1f;
            configuration.PlayerToggleButtonY = -1f;
            configuration.Save();
        }
        ImGui.Unindent();
    }

    private void DrawOrientationRadio(string labelKey, string id, ToggleButtonLayout layout)
    {
        var label = Loc.Get(labelKey);
        SameLineIfFits(RadioWidth(label));
        if (!ImGui.RadioButton(label + id, configuration.PlayerToggleLayout == layout)) return;

        configuration.PlayerToggleLayout = layout;
        configuration.Save();
    }

    private static float RadioWidth(string label)
        => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;

    private static void SameLineIfFits(float nextWidth)
    {
        var used = ImGui.GetItemRectMax().X - ImGui.GetCursorScreenPos().X;
        if (used + ImGui.GetStyle().ItemSpacing.X + nextWidth <= ImGui.GetContentRegionAvail().X)
            ImGui.SameLine();
    }

    private void DrawTacticalGroup()
    {
        var showTactical = configuration.ShowTacticalOverlay;
        if (DrawSettingToggle("General.ShowTacticalOverlay", "General.ShowTacticalOverlay.Tooltip", ref showTactical))
        {
            configuration.ShowTacticalOverlay = showTactical;
            configuration.Save();
        }

        var tacticalCam = configuration.TacticalCamera;
        if (DrawSettingToggle("General.TacticalCamera", "General.TacticalCamera.Tooltip", ref tacticalCam))
        {
            configuration.TacticalCamera = tacticalCam;
            configuration.Save();
        }

        var hideNameplates = configuration.HideNameplatesInCombat;
        if (DrawSettingToggle("General.HideNameplatesInCombat", "General.HideNameplatesInCombat.Tooltip", ref hideNameplates))
        {
            configuration.HideNameplatesInCombat = hideNameplates;
            configuration.Save();
        }

        var playDead = configuration.PlayDeadAtZeroHp;
        if (DrawSettingToggle("General.PlayDeadAtZeroHp", "General.PlayDeadAtZeroHp.Tooltip", ref playDead))
        {
            configuration.PlayDeadAtZeroHp = playDead;
            configuration.Save();
        }
    }

    private void CheckRelayHealth()
    {
        healthCheckInProgress = true;
        lastHealthCheck = DateTime.UtcNow;

        var healthUrl = configuration.RelayServerUrl
            .Replace("ws://", "http://")
            .Replace("wss://", "https://")
            .TrimEnd('/') + "/health";

        Task.Run(async () =>
        {
            try
            {
                var response = await httpClient.GetAsync(healthUrl);
                relayOnline = response.IsSuccessStatusCode;
            }
            catch
            {
                relayOnline = false;
            }
            finally
            {
                healthCheckInProgress = false;
            }
        });
    }


    private void DrawPrivacyContent()
    {
        DrawSectionHeader(PrivacySettingsTab);

        DrawSettingsCard("Privacy.ConsentTitle", FontAwesomeIcon.ShieldAlt, () =>
        {
            var start = BeginSettingAnchor();
            if (configuration.IsRgpdConsentValid && configuration.RgpdConsentDate.HasValue)
            {
                var dateStr = configuration.RgpdConsentDate.Value.ToString("dd/MM/yyyy HH:mm");
                ImGui.TextColored(MasterEventTheme.SuccessColor,
                    string.Format(Loc.Get("Privacy.ConsentActive"), dateStr));
            }
            else
            {
                ImGui.TextColored(MasterEventTheme.DangerColor, Loc.Get("Privacy.ConsentNone"));
            }

            if (configuration.IsRgpdConsentValid)
            {
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Privacy.RevokeTitle"));
                ImGui.TextWrapped(Loc.Get("Privacy.RevokeDescription"));
                ImGuiHelpers.ScaledDummy(2f);

                SettingsControls.DrawRgpdRevoke(configuration, onConsentRevoked);
            }
            EndSettingAnchor("Privacy.Revoke", start);
        });

        DrawSettingsCard("Privacy.RightsTitle", FontAwesomeIcon.UserShield, () =>
        {
            var start = BeginSettingAnchor();
            DrawPrivacyRight("Privacy.RightAccessTitle", "Privacy.RightAccess");
            DrawPrivacyRight("Privacy.RightErasureTitle", "Privacy.RightErasure");
            DrawPrivacyRight("Privacy.RightObjectTitle", "Privacy.RightObject", last: true);
            EndSettingAnchor("Privacy.RightsTitle", start);
        });

        DrawSettingsCard("Privacy.HostingTitle", FontAwesomeIcon.Server, () =>
        {
            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.TextSecondary);
            ImGui.TextWrapped(Loc.Get("Privacy.Hosting"));
            ImGui.PopStyleColor();

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.TextDim);
            ImGui.TextWrapped(Loc.Get("Privacy.Controller"));
            ImGui.TextWrapped(Loc.Get("Privacy.LegalBasis"));
            ImGui.PopStyleColor();
        });
    }

    private static void DrawPrivacyRight(string titleKey, string bodyKey, bool last = false)
    {
        ImGui.TextColored(MasterEventTheme.TextStrong, Loc.Get(titleKey));
        ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.TextSecondary);
        ImGui.TextWrapped(Loc.Get(bodyKey));
        ImGui.PopStyleColor();

        if (!last) ImGuiHelpers.ScaledDummy(6f);
    }

    private static readonly Vector4 BrandGold = new(0.831f, 0.686f, 0.416f, 1f);
    private static readonly Vector4 BrandEmber = new(0.831f, 0.384f, 0.165f, 1f);
    private static readonly Vector4 BrandFlame = new(0.941f, 0.565f, 0.259f, 1f);
    private static readonly Vector4 BrandBook = new(0.075f, 0.075f, 0.082f, 1f);
    private static readonly Vector4 BrandSpine = new(0.102f, 0.102f, 0.118f, 1f);

    private void DrawAboutContent()
    {
        var availW = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(20f);

        LayoutControls.DrawCenteredIcon(FontAwesomeIcon.Dice, availW, 1.6f);

        ImGuiHelpers.ScaledDummy(8f);

        LayoutControls.DrawCenteredWrapped(Loc.Get("About.Title"), MasterEventTheme.AccentText, availW);

        ImGuiHelpers.ScaledDummy(2f);

        var versionLine = $"v{Constants.PluginVersion}  ·  {Loc.Get("About.Author")}";
        LayoutControls.DrawCenteredWrapped(versionLine, MasterEventTheme.TextDim, availW);
        LayoutControls.DrawCenteredWrapped($"Build : {Constants.PluginBuild}", MasterEventTheme.TextDim, availW);

        ImGuiHelpers.ScaledDummy(4f);

        LayoutControls.DrawCenteredWrapped(Loc.Get("About.Description"), MasterEventTheme.TextDim, availW);

        ImGuiHelpers.ScaledDummy(12f);

        DrawRelaunchAssistant(availW);

        ImGuiHelpers.ScaledDummy(20f);

        LayoutControls.DrawCenteredWrapped(Loc.Get("About.Links"), MasterEventTheme.TextDim, availW);

        ImGuiHelpers.ScaledDummy(6f);

        DrawAboutLinks(availW);

        ImGuiHelpers.ScaledDummy(20f);

        DrawOnlineServiceStatus(availW);

        ImGuiHelpers.ScaledDummy(8f);

        LayoutControls.DrawCenteredWrapped(Loc.Get("About.Tagline"), MasterEventTheme.TextDim, availW);

        // Ashfall Codex branding
        ImGuiHelpers.ScaledDummy(16f);

        var sepStart = ImGui.GetCursorScreenPos();
        var sepW = 120f * ImGuiHelpers.GlobalScale;
        var sepX2 = sepStart.X + (availW - sepW) / 2f;
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(sepX2, sepStart.Y),
            new Vector2(sepX2 + sepW, sepStart.Y),
            ImGui.GetColorU32(BrandGold with { W = 0.12f }), 1f);
        ImGuiHelpers.ScaledDummy(16f);

        var logoSize = 72f * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - logoSize) / 2f);
        var logoPos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(logoSize, logoSize));
        DrawAshfallCodexLogo(new Vector2(logoPos.X + logoSize / 2f, logoPos.Y + logoSize / 2f), logoSize);

        ImGuiHelpers.ScaledDummy(4f);

        var brandName = "Ashfall Codex";
        var brandSz = ImGui.CalcTextSize(brandName);
        ImGui.PushStyleColor(ImGuiCol.Text, BrandGold with { W = 0.4f });
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, BrandEmber with { W = 0.2f });
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, BrandEmber with { W = 0.3f });
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - brandSz.X) / 2f);
        if (ImGui.Selectable(brandName, false, ImGuiSelectableFlags.None, brandSz))
            Dalamud.Utility.Util.OpenLink("https://ashfall-codex.dev/");
        ImGui.PopStyleColor(3);
    }

    private void DrawAboutLinks(float availW)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = 6f * scale;
        var margin = 8f * scale;
        var usable = availW - margin * 2f;

        var links = new (FontAwesomeIcon Icon, string Label, Action Open)[]
        {
            (FontAwesomeIcon.Globe, Loc.Get("About.Discord"), () => Dalamud.Utility.Util.OpenLink(Constants.DiscordUrl)),
            (FontAwesomeIcon.Code, Loc.Get("About.GitHub"), () => Dalamud.Utility.Util.OpenLink(Constants.GitHubUrl)),
            (FontAwesomeIcon.FileAlt, Loc.Get("About.Changelog"), () =>
            {
                if (ChangelogWindowRef is { } window) window.IsOpen = true;
                else Dalamud.Utility.Util.OpenLink(Constants.ChangelogUrl);
            }),
        };

        var needed = 0f;
        foreach (var link in links)
            needed = MathF.Max(needed, AboutLinkContentWidth(link.Icon, link.Label));
        needed += ImGui.GetStyle().FramePadding.X * 2f;

        var stacked = needed * links.Length + spacing * (links.Length - 1) > usable;
        var width = stacked ? usable : (usable - spacing * (links.Length - 1)) / links.Length;

        var start = BeginSettingAnchor();
        for (var i = 0; i < links.Length; i++)
        {
            if (i == 0 || stacked) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + margin);
            else ImGui.SameLine(0, spacing);

            if (DrawAboutLinkButton(links[i].Icon, links[i].Label, width))
                links[i].Open();
        }
        EndSettingAnchor("About.Changelog", start);
    }

    private void DrawOnlineServiceStatus(float availW)
    {
        // Sans consentement, rien ne part vers le service, pas même ce contrôle d'état.
        if (!configuration.IsRgpdConsentValid) return;

        if (!healthCheckInProgress && (DateTime.UtcNow - lastHealthCheck).TotalSeconds >= HealthCheckIntervalSeconds)
            CheckRelayHealth();

        var serviceLabel = Loc.Get("General.RelayStatus") + " ";
        string statusLabel;
        Vector4 statusColor;
        if (healthCheckInProgress && !relayOnline.HasValue)
        {
            statusLabel = Loc.Get("General.RelayChecking");
            statusColor = MasterEventTheme.TextSecondary;
        }
        else if (relayOnline == true)
        {
            statusLabel = Loc.Get("General.RelayOnline");
            statusColor = MasterEventTheme.SuccessColor;
        }
        else
        {
            statusLabel = Loc.Get("General.RelayOffline");
            statusColor = MasterEventTheme.DangerColor;
        }

        var lineSz = ImGui.CalcTextSize(serviceLabel + statusLabel);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (availW - lineSz.X) / 2f));
        ImGui.TextColored(MasterEventTheme.TextDim, serviceLabel);
        ImGui.SameLine(0, 0);
        ImGui.TextColored(statusColor, statusLabel);
    }

    private static void DrawAshfallCodexLogo(Vector2 center, float size)
    {
        var dl = ImGui.GetWindowDrawList();
        var s = size / 512f;
        var ox = center.X - 256f * s;
        var oy = center.Y - 256f * s;
        var t = ImGui.GetTime();
        var cxVb = 261f;
        var cyVb = 256f;
        var logoCenter = new Vector2(ox + cxVb * s, oy + cyVb * s);
        var glowPulse = 0.5f + 0.12f * MathF.Sin((float)(t * Math.PI * 2.0 / 6.0));
        var haloR = 80f * s;
        for (var ring = 5; ring >= 0; ring--)
        {
            var r = haloR * (1f + ring * 0.5f);
            var a = 0.04f * glowPulse * (1f - ring / 6f);
            dl.AddCircleFilled(logoCenter, r,
                ImGui.GetColorU32(BrandEmber with { W = a }), 48);
        }

        dl.AddCircle(new Vector2(ox + 256f * s, oy + 256f * s), 241f * s,
            ImGui.GetColorU32(BrandGold with { W = 0.25f }), 64, MathF.Max(1.5f, 4.4f * s));
        var bookR = MathF.Max(2f, 11f * s);
        var bookMin = new Vector2(ox + 150f * s, oy + 117f * s);
        var bookMax = new Vector2(ox + 362f * s, oy + 395f * s);
        dl.AddRectFilled(bookMin, bookMax,
            ImGui.GetColorU32(BrandBook), bookR);
        dl.AddRect(bookMin, bookMax,
            ImGui.GetColorU32(BrandGold with { W = 0.85f }), bookR, ImDrawFlags.None, MathF.Max(1.5f, 6.6f * s));

        dl.AddRectFilled(
            new Vector2(ox + 150f * s, oy + 117f * s),
            new Vector2(ox + 176f * s, oy + 395f * s),
            ImGui.GetColorU32(BrandSpine with { W = 0.7f }), bookR);
        dl.AddLine(
            new Vector2(ox + 176f * s, oy + 125f * s),
            new Vector2(ox + 176f * s, oy + 387f * s),
            ImGui.GetColorU32(BrandGold with { W = 0.3f }), MathF.Max(1f, 1.5f * s));

        var d1 = new Vector2(ox + cxVb * s, oy + 212f * s);
        var d2 = new Vector2(ox + 305f * s, oy + cyVb * s);
        var d3 = new Vector2(ox + cxVb * s, oy + 300f * s);
        var d4 = new Vector2(ox + 217f * s, oy + cyVb * s);
        var outerThk = MathF.Max(1.5f, 5.5f * s);
        var outerCol = ImGui.GetColorU32(BrandEmber with { W = 0.85f });
        dl.AddLine(d1, d2, outerCol, outerThk);
        dl.AddLine(d2, d3, outerCol, outerThk);
        dl.AddLine(d3, d4, outerCol, outerThk);
        dl.AddLine(d4, d1, outerCol, outerThk);

        var i1 = new Vector2(ox + cxVb * s, oy + 229f * s);
        var i2 = new Vector2(ox + 288f * s, oy + cyVb * s);
        var i3 = new Vector2(ox + cxVb * s, oy + 283f * s);
        var i4 = new Vector2(ox + 234f * s, oy + cyVb * s);
        var innerThk = MathF.Max(1.2f, 3.7f * s);
        var innerCol = ImGui.GetColorU32(BrandFlame with { W = 0.65f });
        dl.AddLine(i1, i2, innerCol, innerThk);
        dl.AddLine(i2, i3, innerCol, innerThk);
        dl.AddLine(i3, i4, innerCol, innerThk);
        dl.AddLine(i4, i1, innerCol, innerThk);

        var emberR = MathF.Max(2f, 11f * s);
        for (var g = 3; g >= 0; g--)
        {
            var r = emberR * (1f + g * 0.8f);
            var a = 0.12f * glowPulse * (1f - g / 4f);
            dl.AddCircleFilled(logoCenter, r,
                ImGui.GetColorU32(BrandFlame with { W = a }), 32);
        }
        dl.AddCircleFilled(logoCenter, emberR,
            ImGui.GetColorU32(BrandFlame with { W = 0.5f + 0.35f * glowPulse }), 32);

        var cThk = MathF.Max(1.2f, 2.9f * s);
        var cCol = ImGui.GetColorU32(BrandGold with { W = 0.4f });
        dl.AddLine(new Vector2(ox + 187f * s, oy + 128f * s), new Vector2(ox + 187f * s, oy + 146f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 187f * s, oy + 146f * s), new Vector2(ox + 206f * s, oy + 146f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 342f * s, oy + 128f * s), new Vector2(ox + 342f * s, oy + 146f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 342f * s, oy + 146f * s), new Vector2(ox + 324f * s, oy + 146f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 187f * s, oy + 383f * s), new Vector2(ox + 187f * s, oy + 365f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 187f * s, oy + 365f * s), new Vector2(ox + 206f * s, oy + 365f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 342f * s, oy + 383f * s), new Vector2(ox + 342f * s, oy + 365f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 342f * s, oy + 365f * s), new Vector2(ox + 324f * s, oy + 365f * s), cCol, cThk);
    }

    private static float AboutLinkContentWidth(FontAwesomeIcon icon, string label)
    {
        float iconWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            iconWidth = ImGui.CalcTextSize(icon.ToIconString()).X;
        return iconWidth + 4f * ImGuiHelpers.GlobalScale + ImGui.CalcTextSize(label).X;
    }

    private static bool DrawAboutLinkButton(FontAwesomeIcon icon, string label, float width)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var iconStr = icon.ToIconString();
        Vector2 iconSz;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            iconSz = ImGui.CalcTextSize(iconStr);

        var labelSz = ImGui.CalcTextSize(label);
        var gap = 4f * scale;
        var totalW = iconSz.X + gap + labelSz.X;
        var btnH = MathF.Max(ImGui.GetFrameHeight(), 26f * scale);

        var pos = ImGui.GetCursorScreenPos();
        var clicked = Buttons.Quiet($"##{label}Link", width, btnH);
        if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var dl = ImGui.GetWindowDrawList();
        var startX = pos.X + MathF.Max(0f, (width - totalW) / 2f);
        var color = ImGui.GetColorU32(MasterEventTheme.AccentText);

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            dl.AddText(new Vector2(startX, pos.Y + (btnH - iconSz.Y) / 2f), color, iconStr);
        dl.AddText(new Vector2(startX + iconSz.X + gap, pos.Y + (btnH - labelSz.Y) / 2f), color, label);

        return clicked;
    }


    private void DrawAdvancedContent()
    {
        DrawSectionHeader(AdvancedSettingsTab);

        DrawSettingsCard("Settings.Group.Debug", FontAwesomeIcon.Wrench, () =>
        {
            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.WarningColor);
            ImGui.TextWrapped(Loc.Get("Advanced.Warning"));
            ImGui.PopStyleColor();

            ImGuiHelpers.ScaledDummy(4f);

            var debugMode = configuration.DebugMode;
            if (DrawSettingToggle("Advanced.DebugMode", null, ref debugMode))
            {
                configuration.DebugMode = debugMode;
                configuration.Save();

                if (!debugMode)
                    onDebugDisabled?.Invoke();
            }
        });

        // La carte des commandes n'apparaît qu'avec le mode debug : elles n'ont aucun effet sans lui.
        if (!configuration.DebugMode) return;

        DrawSettingsCard("Settings.Group.Commands", FontAwesomeIcon.Terminal, () =>
        {
            DrawDebugCommand("/masterevent connect", "Advanced.Cmd.Connect");
            DrawDebugCommand("/masterevent disconnect", "Advanced.Cmd.Disconnect");
            DrawDebugCommand("/masterevent joueur", "Advanced.Cmd.Player");
            DrawDebugCommand("/masterevent mj", "Advanced.Cmd.Gm");
        });
    }

    // Une commande et ce qu'elle fait, sur la même ligne.
    private static void DrawDebugCommand(string command, string descriptionKey)
    {
        ImGui.TextColored(MasterEventTheme.TextSecondary, command);
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.TextDim, "· " + Loc.Get(descriptionKey));
    }
}
