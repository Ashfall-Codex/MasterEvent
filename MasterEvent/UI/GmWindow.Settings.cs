using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;

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

        // Separator line
        var drawList = ImGui.GetWindowDrawList();
        var sepStart = ImGui.GetCursorScreenPos();
        var sepH = ImGui.GetContentRegionAvail().Y;
        var sepColor = MasterEventTheme.AccentColor with { W = 0.6f };
        drawList.AddLine(sepStart, new Vector2(sepStart.X, sepStart.Y + sepH), ImGui.GetColorU32(sepColor), 1f * ImGuiHelpers.GlobalScale);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6f * ImGuiHelpers.GlobalScale);

        // Content
        if (ImGui.BeginChild("##settings_content", Vector2.Zero))
        {
            switch (activeSettingsTab)
            {
                case 0: DrawGeneralContent(); break;
                case 1: DrawPrivacyContent(); break;
                case 2: DrawAdvancedContent(); break;
                case 3: DrawAboutContent(); break;
            }
        }
        ImGui.EndChild();
    }

    private void DrawSettingsSidebar()
    {
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

        var textColor = isActive
            ? new Vector4(1f, 1f, 1f, 1f)
            : hovered
                ? new Vector4(0.9f, 0.85f, 1f, 1f)
                : new Vector4(0.7f, 0.65f, 0.8f, 1f);
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
        var indicatorColor = activeSettingsTab == 1
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
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), description);

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
    }


    private void DrawGeneralContent()
    {
        DrawSectionHeader(0);

        // Language selector
        ImGui.TextUnformatted(Loc.Get("Config.Language"));
        var currentLabel = Loc.GetLanguageDisplayName(Loc.CurrentLanguage);
        ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
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

        ImGuiHelpers.ScaledDummy(4f);

        var autoOpen = configuration.AutoOpenPlayerWindow;
        if (ImGui.Checkbox(Loc.Get("General.AutoOpenPlayerWindow"), ref autoOpen))
        {
            configuration.AutoOpenPlayerWindow = autoOpen;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("General.AutoOpenPlayerWindow.Tooltip"));
            ImGui.EndTooltip();
        }

        var autoApply = configuration.AutoApplyWaymarks;
        if (ImGui.Checkbox(Loc.Get("General.AutoApplyWaymarks"), ref autoApply))
        {
            configuration.AutoApplyWaymarks = autoApply;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("General.AutoApplyWaymarks.Tooltip"));
            ImGui.EndTooltip();
        }

        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.Button(Loc.Get("General.ShowPlayerWindow")))
        {
            if (PlayerWindowRef is { } playerWindow)
                playerWindow.IsOpen = !playerWindow.IsOpen;
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
        DrawSectionHeader(1);

        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Privacy.ConsentTitle"));
        ImGui.Spacing();

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

        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Privacy.RevokeTitle"));
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Privacy.RevokeDescription"));
        ImGui.Spacing();

        if (configuration.IsRgpdConsentValid)
        {
            if (!revokeConfirmPending)
            {
                if (ImGui.Button(Loc.Get("Privacy.Revoke")))
                    revokeConfirmPending = true;
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
                    revokeConfirmPending = false;
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

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
        var titleSize = ImGui.CalcTextSize(titleText);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - titleSize.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentColor, titleText);

        ImGuiHelpers.ScaledDummy(2f);

        // Version + author centered
        var versionLine = $"v{Constants.PluginVersion}  ·  {Loc.Get("About.Author")}";
        var vSz = ImGui.CalcTextSize(versionLine);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - vSz.X) / 2f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), versionLine);

        var buildLine = $"Build : {Constants.PluginBuild}";
        var buildSz = ImGui.CalcTextSize(buildLine);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - buildSz.X) / 2f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), buildLine);

        ImGuiHelpers.ScaledDummy(4f);

        // Description centered
        var descText = Loc.Get("About.Description");
        var descSz = ImGui.CalcTextSize(descText);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - descSz.X) / 2f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), descText);

        ImGuiHelpers.ScaledDummy(24f);

        // Links label centered
        var linksText = Loc.Get("About.Links");
        var linksSz = ImGui.CalcTextSize(linksText);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - linksSz.X) / 2f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), linksText);

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
            Dalamud.Utility.Util.OpenLink(Constants.ChangelogUrl);

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
            statusColor = new Vector4(0.7f, 0.7f, 0.7f, 1f);
        }
        else if (relayOnline == true)
        {
            statusLabel = Loc.Get("General.RelayOnline");
            statusColor = new Vector4(0.2f, 1f, 0.2f, 1f);
        }
        else
        {
            statusLabel = Loc.Get("General.RelayOffline");
            statusColor = new Vector4(0.8f, 0.4f, 0.4f, 1f);
        }

        var fullRelayLine = relayLabel + statusLabel;
        var relaySz = ImGui.CalcTextSize(fullRelayLine);
        var relayStartX = (availW - relaySz.X) / 2f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + relayStartX);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), relayLabel);
        ImGui.SameLine(0, 0);
        ImGui.TextColored(statusColor, statusLabel);

        ImGuiHelpers.ScaledDummy(8f);

        // Tagline centered
        var taglineText = Loc.Get("About.Tagline");
        var taglineSz = ImGui.CalcTextSize(taglineText);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - taglineSz.X) / 2f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), taglineText);

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
        var white = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));

        dl.AddText(UiBuilder.IconFont, fontSize, new Vector2(startX, pos.Y + (btnH - iconSz.Y) / 2f), white, iconStr);
        dl.AddText(ImGui.GetFont(), fontSize, new Vector2(startX + iconSz.X + gap, pos.Y + (btnH - labelSz.Y) / 2f), white, label);

        return clicked;
    }


    private void DrawAdvancedContent()
    {
        DrawSectionHeader(2);

        // ── Compatibilité plugins ──
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Advanced.PluginCompat"));
        ImGui.Spacing();

        DrawPluginStatus("Weatherman", session.IsWeathermanInstalled);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "-");
        ImGui.SameLine();
        if (session.IsWeathermanInstalled)
        {
            var weatherOk = session.IsWeatherPatchActive || session.IsWeathermanTimePatchActive;
            ImGui.TextColored(
                weatherOk ? new Vector4(0.5f, 0.5f, 0.5f, 1f) : new Vector4(0.7f, 0.7f, 0.2f, 1f),
                weatherOk ? Loc.Get("Advanced.PluginReady") : Loc.Get("Advanced.PluginPartial"));
        }
        else
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Advanced.PluginWeathermanHint"));
        }

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.6f, 0.2f, 1f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextWrapped(Loc.Get("Advanced.Warning"));
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.Spacing();

        // Debug mode toggle
        var debugMode = configuration.DebugMode;
        if (ImGui.Checkbox(Loc.Get("Advanced.DebugMode"), ref debugMode))
        {
            configuration.DebugMode = debugMode;
            configuration.Save();

            if (!debugMode)
                onDebugDisabled?.Invoke();
        }

        if (configuration.DebugMode)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Advanced.Commands"));
            ImGui.Spacing();

            var cmdColor = new Vector4(0.7f, 0.7f, 0.7f, 1f);
            ImGui.TextColored(cmdColor, "/masterevent connect");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "— " + Loc.Get("Advanced.Cmd.Connect"));

            ImGui.TextColored(cmdColor, "/masterevent disconnect");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "— " + Loc.Get("Advanced.Cmd.Disconnect"));

            ImGui.TextColored(cmdColor, "/masterevent joueur");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "— " + Loc.Get("Advanced.Cmd.Player"));

            ImGui.TextColored(cmdColor, "/masterevent mj");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "— " + Loc.Get("Advanced.Cmd.Gm"));
        }
    }

    private static void DrawPluginStatus(string name, bool installed)
    {
        var icon = installed ? FontAwesomeIcon.Check : FontAwesomeIcon.Times;
        var color = installed ? new Vector4(0.2f, 0.8f, 0.2f, 1f) : new Vector4(0.8f, 0.3f, 0.3f, 1f);

        ImGui.TextUnformatted(name);
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            ImGui.TextUnformatted(icon.ToIconString());
        }
        ImGui.PopStyleColor();
    }
}
