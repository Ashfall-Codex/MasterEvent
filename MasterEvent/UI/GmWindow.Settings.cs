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
    //Settings content

    private void DrawSettingsContent()
    {
        var sidebarW = SettingsSidebarWidth * ImGuiHelpers.GlobalScale;

        // Sub-sidebar
        if (ImGui.BeginChild("##settings_sidebar", new Vector2(sidebarW, 0), false, ImGuiWindowFlags.NoScrollbar))
        {
            DrawSettingsSidebar();
        }
        ImGui.EndChild();

        ImGui.SameLine();

        LayoutControls.DrawVerticalSeparator(6f, rootBounds);

        // Content
        if (ImGui.BeginChild("##settings_content", Vector2.Zero))
        {
            if (settingsSearch.Length > 0)
            {
                DrawSettingsSearchResults();
            }
            else
            {
                DrawSettingsBreadcrumb();
                switch (activeSettingsTab)
                {
                    case 0: DrawGeneralContent(); break;
                    case 1: DrawCloudContent(); break;
                    case 2: DrawGuideContent(); break;
                    case 3: DrawPrivacyContent(); break;
                    case 4: DrawAdvancedContent(); break;
                    case 5: DrawAboutContent(); break;
                }
            }
        }
        ImGui.EndChild();
    }

    private void DrawSettingsBreadcrumb()
    {
        var section = Loc.Get(SettingsLabelKeys[activeSettingsTab]).ToUpperInvariant();
        ImGui.TextColored(MasterEventTheme.MutedTextColor,
            $"{Loc.Get("Sidebar.Settings").ToUpperInvariant()}  ›  {section}");
        ImGuiHelpers.ScaledDummy(2f);
    }

    private void DrawSettingsSearchResults()
    {
        var results = SettingsCatalog.Search(settingsSearch);

        ImGui.TextColored(MasterEventTheme.MutedTextColor,
            string.Format(Loc.Get("Settings.Search.Results"), results.Count));
        ImGuiHelpers.ScaledDummy(4f);

        if (results.Count == 0)
        {
            ImGui.TextWrapped(Loc.Get("Settings.Search.Empty"));
            return;
        }

        var availWidth = ImGui.GetContentRegionAvail().X;
        for (var i = 0; i < results.Count; i++)
        {
            var entry = results[i];
            var cursor = ImGui.GetCursorScreenPos();

            if (ImGui.InvisibleButton($"##search_hit{i}", new Vector2(availWidth, ImGui.GetTextLineHeightWithSpacing() * 2f)))
            {
                activeSettingsTab = entry.Section;
                settingsSearch = string.Empty;
            }

            var hovered = ImGui.IsItemHovered();
            var dl = ImGui.GetWindowDrawList();
            var max = cursor + new Vector2(availWidth, ImGui.GetTextLineHeightWithSpacing() * 2f);
            dl.AddRectFilled(cursor, max,
                ImGui.GetColorU32(hovered ? MasterEventTheme.ThemeButtonHovered : MasterEventTheme.ThemeHeaderBg),
                MasterEventTheme.RadiusCard * ImGuiHelpers.GlobalScale);

            var pad = 6f * ImGuiHelpers.GlobalScale;
            DrawHighlightedLabel(dl, cursor + new Vector2(pad, pad * 0.5f), entry.Label, settingsSearch);
            dl.AddText(cursor + new Vector2(pad, pad * 0.5f + ImGui.GetTextLineHeight()),
                ImGui.GetColorU32(MasterEventTheme.MutedTextColor),
                Loc.Get(SettingsLabelKeys[entry.Section]).ToUpperInvariant());

            ImGuiHelpers.ScaledDummy(2f);
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

    private void DrawSettingsSidebar()
    {
        DrawSettingsSearchBox();

        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        settingsSidebarRects.Clear();

        ImGuiHelpers.ScaledDummy(4f);

        for (var i = 0; i < SettingsLabelKeys.Length; i++)
        {
            DrawSettingsSidebarButton(i);
            ImGuiHelpers.ScaledDummy(1f);
        }

        drawList.ChannelsSetCurrent(0);
        DrawSettingsSidebarIndicator(drawList);
        drawList.ChannelsMerge();
    }

    private void DrawSettingsSearchBox()
    {
        ImGuiHelpers.ScaledDummy(4f);

        var width = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(width);
        var buffer = settingsSearch;
        if (ImGui.InputTextWithHint("##settings_search", Loc.Get("Settings.Search.Hint"), ref buffer, 64))
            settingsSearch = buffer;

        if (settingsSearch.Length > 0)
        {
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Settings.Search.Clear"));
                ImGui.EndTooltip();
            }

            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                settingsSearch = string.Empty;
        }

        ImGuiHelpers.ScaledDummy(2f);
    }

    private void DrawSettingsSidebarButton(int tabIndex)
    {
        ImGui.PushID(tabIndex);

        const float btnH = 24f;
        const float iconTextGap = 6f;
        const float paddingX = 8f;
        var scaledBtnH = btnH * ImGuiHelpers.GlobalScale;
        var availWidth = ImGui.GetContentRegionAvail().X;

        var isActive = activeSettingsTab == tabIndex;

        var p = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##settingsSidebarBtn", new Vector2(availWidth, scaledBtnH));
        var hovered = ImGui.IsItemHovered();

        settingsSidebarRects[tabIndex] = (p, p + new Vector2(availWidth, scaledBtnH));

        // Hover background
        if (hovered && !isActive)
        {
            var hoverColor = new Vector4(
                MasterEventTheme.AccentColor.X * 0.4f,
                MasterEventTheme.AccentColor.Y * 0.4f,
                MasterEventTheme.AccentColor.Z * 0.4f, 1f);
            var dl = ImGui.GetWindowDrawList();
            var rounding = 6f * ImGuiHelpers.GlobalScale;
            var pad = 2f * ImGuiHelpers.GlobalScale;
            dl.AddRectFilled(p - new Vector2(pad), p + new Vector2(availWidth + pad, scaledBtnH + pad), ImGui.GetColorU32(hoverColor), rounding);
        }

        // Icon + text
        var dl2 = ImGui.GetWindowDrawList();
        var iconStr = SettingsIcons[tabIndex].ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();

        var textColor = (isActive, hovered) switch
        {
            (true, _) => MasterEventTheme.TextStrong,
            (false, true) => new Vector4(0.9f, 0.85f, 1f, 1f),
            _ => new Vector4(0.7f, 0.65f, 0.8f, 1f),
        };
        var textColorU32 = ImGui.GetColorU32(textColor);

        var startX = p.X + paddingX * ImGuiHelpers.GlobalScale;

        ImGui.PushFont(UiBuilder.IconFont);
        dl2.AddText(new Vector2(startX, p.Y + (scaledBtnH - iconSz.Y) / 2f), textColorU32, iconStr);
        ImGui.PopFont();

        var label = Loc.Get(SettingsLabelKeys[tabIndex]);
        dl2.AddText(new Vector2(startX + iconSz.X + iconTextGap * ImGuiHelpers.GlobalScale, p.Y + (scaledBtnH - iconSz.Y) / 2f), textColorU32, label);

        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked) activeSettingsTab = tabIndex;

        ImGui.PopID();
    }

    private void DrawSettingsSidebarIndicator(ImDrawListPtr drawList)
    {
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
        var indicatorColor = activeSettingsTab == PrivacySettingsTab
            ? new Vector4(0f, 0.2f, 0.6f, 1f) // EU blue for Privacy
            : MasterEventTheme.AccentColor;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(indicatorColor), rounding);
    }

    private static void DrawSectionHeader(int tabIndex)
    {
        var icon = SettingsIcons[tabIndex];
        var title = Loc.Get(SettingsLabelKeys[tabIndex]);
        var description = Loc.Get(SettingsDescriptionKeys[tabIndex]);
        var availWidth = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(6f);

        // Icon — large, centered
        var iconStr = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr);
        const float scale = 1.6f;
        var scaledSz = iconSz * scale;
        var pos = ImGui.GetCursorScreenPos();
        var iconX = pos.X + (availWidth - scaledSz.X) / 2f;
        var iconY = pos.Y;
        ImGui.Dummy(new Vector2(0, scaledSz.Y));
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(iconX, iconY), ImGui.GetColorU32(MasterEventTheme.AccentColor), iconStr);
        ImGui.PopFont();

        ImGuiHelpers.ScaledDummy(4f);

        var titleSz = ImGui.CalcTextSize(title);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentColor, title);

        ImGuiHelpers.ScaledDummy(2f);

        var descSz = ImGui.CalcTextSize(description);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - descSz.X) / 2f);
        ImGui.TextColored(MasterEventTheme.TextDim, description);

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
    }


    private void DrawGeneralContent()
    {
        DrawSectionHeader(GeneralSettingsTab);

        var accent = MasterEventTheme.AccentColor;
        LayoutControls.DrawCard(Loc.Get("Settings.Group.Appearance"), FontAwesomeIcon.Palette, accent, DrawAppearanceGroup);
        LayoutControls.DrawCard(Loc.Get("Settings.Group.Session"), FontAwesomeIcon.Users, accent, DrawSessionGroup);
        LayoutControls.DrawCard(Loc.Get("Settings.Group.Dice"), FontAwesomeIcon.Dice, accent, DrawDiceGroup);
        LayoutControls.DrawCard(Loc.Get("Settings.Group.FloatingBar"), FontAwesomeIcon.Bars, accent, DrawFloatingBarGroup);
        LayoutControls.DrawCard(Loc.Get("Settings.Group.Tactical"), FontAwesomeIcon.ChessKnight, accent, DrawTacticalGroup);

        if (ImGui.Button(Loc.Get("General.ShowPlayerWindow")))
        {
            if (PlayerWindowRef is { } playerWindow)
                playerWindow.IsOpen = !playerWindow.IsOpen;
        }
    }

    private void DrawAppearanceGroup()
    {
        SettingsControls.DrawLanguageSelector(configuration, 200f);

        ImGuiHelpers.ScaledDummy(4f);

        SettingsControls.DrawAppearanceSection(configuration, 200f);

        ImGuiHelpers.ScaledDummy(4f);
    }

    private void DrawSessionGroup()
    {
        var autoOpen = configuration.AutoOpenPlayerWindow;
        if (ToggleSwitch.Draw("##autoOpen", Loc.Get("General.AutoOpenPlayerWindow"), ref autoOpen,
                Loc.Get("General.AutoOpenPlayerWindow.Tooltip")))
        {
            configuration.AutoOpenPlayerWindow = autoOpen;
            configuration.Save();
        }

        var autoApply = configuration.AutoApplyWaymarks;
        if (ToggleSwitch.Draw("##autoApply", Loc.Get("General.AutoApplyWaymarks"), ref autoApply,
                Loc.Get("General.AutoApplyWaymarks.Tooltip")))
        {
            configuration.AutoApplyWaymarks = autoApply;
            configuration.Save();
        }

        var suppressInstance = configuration.SuppressInInstance;
        if (ToggleSwitch.Draw("##suppressInstance", Loc.Get("General.SuppressInInstance"), ref suppressInstance,
                Loc.Get("General.SuppressInInstance.Tooltip")))
        {
            configuration.SuppressInInstance = suppressInstance;
            configuration.Save();
        }

        DrawPlayerStatsInlineToggle();
    }

    private void DrawPlayerStatsInlineToggle()
    {
        var inlineStats = configuration.ShowPlayerStatsInline;
        if (!ToggleSwitch.Draw("##inlineStats", Loc.Get("General.ShowPlayerStatsInline"), ref inlineStats,
                Loc.Get("General.ShowPlayerStatsInline.Tooltip")))
            return;

        configuration.ShowPlayerStatsInline = inlineStats;
        configuration.Save();
    }

    private void DrawDiceGroup()
    {
        var showDice = configuration.ShowDiceAnimation;
        if (ToggleSwitch.Draw("##showDice", Loc.Get("General.ShowDiceAnimation"), ref showDice,
                showDice ? Loc.Get("General.ShowDiceAnimation.Tooltip") : "Gros fragile de Nina"))
        {
            configuration.ShowDiceAnimation = showDice;
            session.ShowDiceAnimation = showDice;
            configuration.Save();
        }

        if (showDice)
        {
            ImGui.TextUnformatted(Loc.Get("General.DiceAnimationSpeed"));
            ImGui.SameLine();
            var currentSpeed = configuration.DiceAnimationSpeed;
            foreach (var option in new[] { 1.0f, 1.5f, 2.0f })
            {
                var selected = Math.Abs(currentSpeed - option) < 0.01f;
                if (ImGui.RadioButton($"x{option:0.##}##dicespeed", selected) && !selected)
                {
                    configuration.DiceAnimationSpeed = option;
                    session.DiceAnimationSpeed = option;
                    configuration.Save();
                }
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }
    }

    private void DrawFloatingBarGroup()
    {
        var showPlayerToggle = configuration.ShowPlayerToggleButton;
        if (ToggleSwitch.Draw("##showPlayerToggle", Loc.Get("Settings.ShowPlayerToggleButton"), ref showPlayerToggle,
                Loc.Get("Settings.ShowPlayerToggleButtonTooltip")))
        {
            configuration.ShowPlayerToggleButton = showPlayerToggle;
            configuration.Save();
        }

        // Filet de sécurité : un bouton laissé dans un coin oublié
        if (showPlayerToggle)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.Get("Settings.ResetPlayerTogglePosition") + "##reset_player_toggle"))
            {
                configuration.PlayerToggleButtonX = -1f;
                configuration.PlayerToggleButtonY = -1f;
                configuration.Save();
            }

            ImGui.Indent();
            ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Settings.PlayerToggleOrientation"));
            ImGui.SameLine();

            var layout = configuration.PlayerToggleLayout;
            if (ImGui.RadioButton(Loc.Get("Settings.OrientationVertical") + "##toggle_vertical",
                    layout == ToggleButtonLayout.Vertical))
            {
                configuration.PlayerToggleLayout = ToggleButtonLayout.Vertical;
                configuration.Save();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton(Loc.Get("Settings.OrientationHorizontal") + "##toggle_horizontal",
                    layout == ToggleButtonLayout.Horizontal))
            {
                configuration.PlayerToggleLayout = ToggleButtonLayout.Horizontal;
                configuration.Save();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton(Loc.Get("Settings.OrientationGrid") + "##toggle_grid",
                    layout == ToggleButtonLayout.Grid))
            {
                configuration.PlayerToggleLayout = ToggleButtonLayout.Grid;
                configuration.Save();
            }
            ImGui.Unindent();
        }
    }

    private void DrawTacticalGroup()
    {
        var showTactical = configuration.ShowTacticalOverlay;
        if (ToggleSwitch.Draw("##showTactical", Loc.Get("General.ShowTacticalOverlay"), ref showTactical,
                Loc.Get("General.ShowTacticalOverlay.Tooltip")))
        {
            configuration.ShowTacticalOverlay = showTactical;
            configuration.Save();
        }

        var tacticalCam = configuration.TacticalCamera;
        if (ToggleSwitch.Draw("##tacticalCam", Loc.Get("General.TacticalCamera"), ref tacticalCam,
                Loc.Get("General.TacticalCamera.Tooltip")))
        {
            configuration.TacticalCamera = tacticalCam;
            configuration.Save();
        }

        var hideNameplates = configuration.HideNameplatesInCombat;
        if (ToggleSwitch.Draw("##hideNameplates", Loc.Get("General.HideNameplatesInCombat"), ref hideNameplates,
                Loc.Get("General.HideNameplatesInCombat.Tooltip")))
        {
            configuration.HideNameplatesInCombat = hideNameplates;
            configuration.Save();
        }

        var playDead = configuration.PlayDeadAtZeroHp;
        if (ToggleSwitch.Draw("##playDead", Loc.Get("General.PlayDeadAtZeroHp"), ref playDead,
                Loc.Get("General.PlayDeadAtZeroHp.Tooltip")))
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

        LayoutControls.DrawCard(Loc.Get("Privacy.ConsentTitle"), FontAwesomeIcon.ShieldAlt,
            MasterEventTheme.AccentColor, () =>
            {
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

                ImGuiHelpers.ScaledDummy(4f);
                ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Privacy.RevokeTitle"));
                ImGui.TextWrapped(Loc.Get("Privacy.RevokeDescription"));
                ImGuiHelpers.ScaledDummy(2f);

                SettingsControls.DrawRgpdRevoke(configuration, ref revokeConfirmPending, onConsentRevoked);
            });

        LayoutControls.DrawCard(Loc.Get("Privacy.RightsTitle"), FontAwesomeIcon.UserShield,
            MasterEventTheme.AccentColor, () =>
            {
                DrawPrivacyRight("Privacy.RightAccessTitle", "Privacy.RightAccess");
                DrawPrivacyRight("Privacy.RightErasureTitle", "Privacy.RightErasure");
                DrawPrivacyRight("Privacy.RightObjectTitle", "Privacy.RightObject", last: true);
            });

        LayoutControls.DrawCard(Loc.Get("Privacy.HostingTitle"), FontAwesomeIcon.Server,
            MasterEventTheme.AccentColor, () =>
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

    // Un droit : son intitulé en évidence, son explication en dessous.
    private static void DrawPrivacyRight(string titleKey, string bodyKey, bool last = false)
    {
        ImGui.TextColored(MasterEventTheme.TextStrong, Loc.Get(titleKey));
        ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.TextSecondary);
        ImGui.TextWrapped(Loc.Get(bodyKey));
        ImGui.PopStyleColor();

        if (!last) ImGuiHelpers.ScaledDummy(6f);
    }

    private void DrawAboutContent()
    {
        var availW = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(20f);

        var moonIcon = FontAwesomeIcon.Dice.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(moonIcon);
        const float iconScale = 1.6f;
        var scaledIconSz = iconSz * iconScale;
        var iconPos = ImGui.GetCursorScreenPos();
        var iconX = iconPos.X + (availW - scaledIconSz.X) / 2f;
        ImGui.Dummy(new Vector2(0, scaledIconSz.Y));
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * iconScale, new Vector2(iconX, iconPos.Y), ImGui.GetColorU32(MasterEventTheme.AccentColor), moonIcon);
        ImGui.PopFont();

        ImGuiHelpers.ScaledDummy(8f);

        // Title centered
        var titleText = Loc.Get("About.Title");
        LayoutControls.DrawCenteredWrapped(titleText, MasterEventTheme.AccentColor, availW);

        ImGuiHelpers.ScaledDummy(2f);

        // Version + author centered
        var versionLine = $"v{Constants.PluginVersion}  ·  {Loc.Get("About.Author")}";
        LayoutControls.DrawCenteredWrapped(versionLine, MasterEventTheme.TextDim, availW);

        var buildLine = $"Build : {Constants.PluginBuild}";
        LayoutControls.DrawCenteredWrapped(buildLine, MasterEventTheme.TextDim, availW);

        ImGuiHelpers.ScaledDummy(4f);

        // Description centered
        LayoutControls.DrawCenteredWrapped(Loc.Get("About.Description"), MasterEventTheme.TextDim, availW);

        ImGuiHelpers.ScaledDummy(24f);

        // Links label centered
        var linksText = Loc.Get("About.Links");
        LayoutControls.DrawCenteredWrapped(linksText, MasterEventTheme.TextDim, availW);

        ImGuiHelpers.ScaledDummy(6f);

        // 3 link buttons
        var btnSpacing = 6f * ImGuiHelpers.GlobalScale;
        var margin = 8f * ImGuiHelpers.GlobalScale;
        var usableWidth = availW - margin * 2f;
        var btnWidth = (usableWidth - btnSpacing * 2f) / 3f;

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + margin);

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(MasterEventTheme.AccentColor.X * 0.3f, MasterEventTheme.AccentColor.Y * 0.3f, MasterEventTheme.AccentColor.Z * 0.3f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(MasterEventTheme.AccentColor.X * 0.45f, MasterEventTheme.AccentColor.Y * 0.45f, MasterEventTheme.AccentColor.Z * 0.45f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(MasterEventTheme.AccentColor.X * 0.6f, MasterEventTheme.AccentColor.Y * 0.6f, MasterEventTheme.AccentColor.Z * 0.6f, 1f));

        if (DrawAboutLinkButton(FontAwesomeIcon.Globe, Loc.Get("About.Discord"), btnWidth))
            Dalamud.Utility.Util.OpenLink(Constants.DiscordUrl);
        ImGui.SameLine(0, btnSpacing);
        if (DrawAboutLinkButton(FontAwesomeIcon.Code, Loc.Get("About.GitHub"), btnWidth))
            Dalamud.Utility.Util.OpenLink(Constants.GitHubUrl);
        ImGui.SameLine(0, btnSpacing);
        if (DrawAboutLinkButton(FontAwesomeIcon.FileAlt, Loc.Get("About.Changelog"), btnWidth))
        {
            if (ChangelogWindowRef is { } window) window.IsOpen = true;
            else Dalamud.Utility.Util.OpenLink(Constants.ChangelogUrl);
        }

        ImGui.PopStyleColor(3);

        ImGuiHelpers.ScaledDummy(20f);

        // Relay status centered
        if (!healthCheckInProgress && (DateTime.UtcNow - lastHealthCheck).TotalSeconds >= HealthCheckIntervalSeconds)
            CheckRelayHealth();

        var relayLabel = Loc.Get("General.RelayStatus") + " : ";
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

        var fullRelayLine = relayLabel + statusLabel;
        var relaySz = ImGui.CalcTextSize(fullRelayLine);
        var relayStartX = (availW - relaySz.X) / 2f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + relayStartX);
        ImGui.TextColored(MasterEventTheme.TextDim, relayLabel);
        ImGui.SameLine(0, 0);
        ImGui.TextColored(statusColor, statusLabel);

        ImGuiHelpers.ScaledDummy(8f);

        // Tagline centered
        var taglineText = Loc.Get("About.Tagline");
        var taglineSz = ImGui.CalcTextSize(taglineText);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - taglineSz.X) / 2f);
        ImGui.TextColored(MasterEventTheme.TextDim, taglineText);

        // Ashfall Codex branding
        ImGuiHelpers.ScaledDummy(16f);

        var sepStart = ImGui.GetCursorScreenPos();
        var sepW = 120f * ImGuiHelpers.GlobalScale;
        var sepX2 = sepStart.X + (availW - sepW) / 2f;
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(sepX2, sepStart.Y),
            new Vector2(sepX2 + sepW, sepStart.Y),
            ImGui.GetColorU32(new Vector4(0.831f, 0.686f, 0.416f, 0.12f)), 1f);
        ImGuiHelpers.ScaledDummy(16f);

        var logoSize = 72f * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - logoSize) / 2f);
        var logoPos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(logoSize, logoSize));
        DrawAshfallCodexLogo(new Vector2(logoPos.X + logoSize / 2f, logoPos.Y + logoSize / 2f), logoSize);

        ImGuiHelpers.ScaledDummy(4f);

        var brandName = "Ashfall Codex";
        var brandSz = ImGui.CalcTextSize(brandName);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.831f, 0.686f, 0.416f, 0.4f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.831f, 0.384f, 0.165f, 0.2f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.831f, 0.384f, 0.165f, 0.3f));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - brandSz.X) / 2f);
        if (ImGui.Selectable(brandName, false, ImGuiSelectableFlags.None, brandSz))
            Dalamud.Utility.Util.OpenLink("https://ashfall-codex.dev/");
        ImGui.PopStyleColor(3);
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
        var glowPulse = 0.5f + 0.3f * MathF.Sin((float)(t * Math.PI * 2.0 / 2.5));
        var haloR = 80f * s;
        for (var ring = 5; ring >= 0; ring--)
        {
            var r = haloR * (1f + ring * 0.5f);
            var a = 0.04f * glowPulse * (1f - ring / 6f);
            dl.AddCircleFilled(logoCenter, r,
                ImGui.GetColorU32(new Vector4(0.831f, 0.384f, 0.165f, a)), 48);
        }

        dl.AddCircle(new Vector2(ox + 256f * s, oy + 256f * s), 241f * s,
            ImGui.GetColorU32(new Vector4(0.831f, 0.686f, 0.416f, 0.25f)), 64, MathF.Max(1.5f, 4.4f * s));
        var bookR = MathF.Max(2f, 11f * s);
        var bookMin = new Vector2(ox + 150f * s, oy + 117f * s);
        var bookMax = new Vector2(ox + 362f * s, oy + 395f * s);
        dl.AddRectFilled(bookMin, bookMax,
            ImGui.GetColorU32(new Vector4(0.075f, 0.075f, 0.082f, 1f)), bookR);
        dl.AddRect(bookMin, bookMax,
            ImGui.GetColorU32(new Vector4(0.831f, 0.686f, 0.416f, 0.85f)), bookR, ImDrawFlags.None, MathF.Max(1.5f, 6.6f * s));

        dl.AddRectFilled(
            new Vector2(ox + 150f * s, oy + 117f * s),
            new Vector2(ox + 176f * s, oy + 395f * s),
            ImGui.GetColorU32(new Vector4(0.102f, 0.102f, 0.118f, 0.7f)), bookR);
        dl.AddLine(
            new Vector2(ox + 176f * s, oy + 125f * s),
            new Vector2(ox + 176f * s, oy + 387f * s),
            ImGui.GetColorU32(new Vector4(0.831f, 0.686f, 0.416f, 0.3f)), MathF.Max(1f, 1.5f * s));

        var d1 = new Vector2(ox + cxVb * s, oy + 212f * s);
        var d2 = new Vector2(ox + 305f * s, oy + cyVb * s);
        var d3 = new Vector2(ox + cxVb * s, oy + 300f * s);
        var d4 = new Vector2(ox + 217f * s, oy + cyVb * s);
        var outerThk = MathF.Max(1.5f, 5.5f * s);
        var outerCol = ImGui.GetColorU32(new Vector4(0.831f, 0.384f, 0.165f, 0.85f));
        dl.AddLine(d1, d2, outerCol, outerThk);
        dl.AddLine(d2, d3, outerCol, outerThk);
        dl.AddLine(d3, d4, outerCol, outerThk);
        dl.AddLine(d4, d1, outerCol, outerThk);

        var i1 = new Vector2(ox + cxVb * s, oy + 229f * s);
        var i2 = new Vector2(ox + 288f * s, oy + cyVb * s);
        var i3 = new Vector2(ox + cxVb * s, oy + 283f * s);
        var i4 = new Vector2(ox + 234f * s, oy + cyVb * s);
        var innerThk = MathF.Max(1.2f, 3.7f * s);
        var innerCol = ImGui.GetColorU32(new Vector4(0.941f, 0.565f, 0.259f, 0.65f));
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
                ImGui.GetColorU32(new Vector4(0.941f, 0.565f, 0.259f, a)), 32);
        }
        dl.AddCircleFilled(logoCenter, emberR,
            ImGui.GetColorU32(new Vector4(0.941f, 0.565f, 0.259f, 0.5f + 0.35f * glowPulse)), 32);

        var cThk = MathF.Max(1.2f, 2.9f * s);
        var cCol = ImGui.GetColorU32(new Vector4(0.831f, 0.686f, 0.416f, 0.4f));
        dl.AddLine(new Vector2(ox + 187f * s, oy + 128f * s), new Vector2(ox + 187f * s, oy + 146f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 187f * s, oy + 146f * s), new Vector2(ox + 206f * s, oy + 146f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 342f * s, oy + 128f * s), new Vector2(ox + 342f * s, oy + 146f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 342f * s, oy + 146f * s), new Vector2(ox + 324f * s, oy + 146f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 187f * s, oy + 383f * s), new Vector2(ox + 187f * s, oy + 365f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 187f * s, oy + 365f * s), new Vector2(ox + 206f * s, oy + 365f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 342f * s, oy + 383f * s), new Vector2(ox + 342f * s, oy + 365f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 342f * s, oy + 365f * s), new Vector2(ox + 324f * s, oy + 365f * s), cCol, cThk);
    }

    private static bool DrawAboutLinkButton(FontAwesomeIcon icon, string label, float width)
    {
        var fontSize = ImGui.GetFontSize() * 0.85f;
        var iconStr = icon.ToIconString();

        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr) * 0.85f;
        ImGui.PopFont();

        var labelSz = ImGui.CalcTextSize(label) * 0.85f;
        var gap = 4f * ImGuiHelpers.GlobalScale;
        var totalW = iconSz.X + gap + labelSz.X;
        var btnH = 26f * ImGuiHelpers.GlobalScale;

        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.Button($"##{label}Link", new Vector2(width, btnH));

        var dl = ImGui.GetWindowDrawList();
        var startX = pos.X + (width - totalW) / 2f;
        var white = ImGui.GetColorU32(MasterEventTheme.TextStrong);

        dl.AddText(UiBuilder.IconFont, fontSize, new Vector2(startX, pos.Y + (btnH - iconSz.Y) / 2f), white, iconStr);
        dl.AddText(ImGui.GetFont(), fontSize, new Vector2(startX + iconSz.X + gap, pos.Y + (btnH - labelSz.Y) / 2f), white, label);

        return clicked;
    }


    private void DrawAdvancedContent()
    {
        DrawSectionHeader(AdvancedSettingsTab);

        LayoutControls.DrawCard(Loc.Get("Settings.Group.Debug"), FontAwesomeIcon.Wrench,
            MasterEventTheme.AccentColor, () =>
            {
                ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.WarningColor);
                ImGui.TextWrapped(Loc.Get("Advanced.Warning"));
                ImGui.PopStyleColor();

                ImGuiHelpers.ScaledDummy(4f);

                var debugMode = configuration.DebugMode;
                if (ToggleSwitch.Draw("##debugMode", Loc.Get("Advanced.DebugMode"), ref debugMode))
                {
                    configuration.DebugMode = debugMode;
                    configuration.Save();

                    if (!debugMode)
                        onDebugDisabled?.Invoke();
                }
            });

        // La carte des commandes n'apparaît qu'avec le mode debug : elles n'ont aucun effet sans lui.
        if (!configuration.DebugMode) return;

        LayoutControls.DrawCard(Loc.Get("Settings.Group.Commands"), FontAwesomeIcon.Terminal,
            MasterEventTheme.AccentColor, () =>
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
