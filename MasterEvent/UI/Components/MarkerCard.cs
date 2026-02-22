using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.UI.Components;

public static class MarkerCard
{
    private const float IconSize = 24f;
    private const uint BossIconId = 61804;

    private static void DrawWaymarkIcon(WaymarkId waymarkId)
    {
        var iconId = waymarkId.ToIconId();
        var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
        var size = IconSize * ImGuiHelpers.GlobalScale;
        ImGui.Image(wrap.Handle, new Vector2(size, size));
    }

    public static bool DrawEdit(WaymarkId waymarkId, MarkerData marker, Action? onPlace, Action? onClear, Action? onMove = null, Action? onRoll = null, HpMode hpMode = HpMode.Points, bool showShield = false, bool showMpBar = false, HpMode mpMode = HpMode.Points)
    {
        var changed = false;
        var label = waymarkId.ToLabel();
        var borderColor = GetAttitudeBorderColor(marker.Attitude, marker.IsVisible);

        ImGui.PushStyleColor(ImGuiCol.Border, borderColor);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);

        var cardWidth = ImGui.GetContentRegionAvail().X;
        var extraRows = 0;
        if (showShield) extraRows++;
        if (showMpBar) extraRows++;
        var counterCount = marker.Counters?.Count ?? 0;
        extraRows += counterCount;
        var cardHeight = ImGui.GetFrameHeightWithSpacing() * (3 + extraRows) + ImGui.GetStyle().WindowPadding.Y * 2 + ImGui.GetStyle().ItemSpacing.Y * extraRows;
        if (ImGui.BeginChild($"##marker_{label}", new Vector2(cardWidth, cardHeight), true))
        {
            // Header: waymark icon + name input + boss checkbox
            DrawWaymarkIcon(waymarkId);
            ImGui.SameLine();

            // Name input
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 70f * ImGuiHelpers.GlobalScale);
            var name = marker.Name;
            if (ImGui.InputText($"##name_{label}", ref name, Constants.MaxNameLength))
            {
                marker.Name = name;
                changed = true;
            }
            ImGui.SameLine();
            var isBoss = marker.IsBoss;
            if (ImGui.Checkbox($"{Loc.Get("Marker.Boss")}##boss_{label}", ref isBoss))
            {
                marker.IsBoss = isBoss;
                changed = true;
            }


            var barFramePad = ImGui.GetStyle().FramePadding.X * 2;
            var barSpacing = ImGui.GetStyle().ItemSpacing.X;
            var pmBtnW = ImGui.GetFrameHeight();
            var hp = marker.Hp;
            var hpStep = 1;
            var hpClampMax = hpMode == HpMode.Percentage ? 100 : marker.HpMax;

            float editBtnW;
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                editBtnW = ImGui.CalcTextSize(FontAwesomeIcon.Pen.ToIconString()).X + barFramePad;

            var barRightPad = editBtnW + barSpacing;

            if (showShield)
            {
                var shield = marker.Shield;
                var shieldStep = 1;
                var shieldMax = hpMode == HpMode.Percentage ? 100 : marker.HpMax;
                if (ImGui.Button($"-##sh_dec_{label}", new Vector2(pmBtnW, 0)))
                {
                    shield = Math.Max(0, shield - shieldStep);
                    marker.Shield = shield;
                    changed = true;
                }
                ImGui.SameLine();
                ImGui.TextColored(MasterEventTheme.ShieldOverlayColor, $"{Loc.Get("Marker.Shield")}: {marker.Shield}");
                ImGui.SameLine();
                if (ImGui.Button($"+##sh_inc_{label}", new Vector2(pmBtnW, 0)))
                {
                    shield = Math.Min(shieldMax, shield + shieldStep);
                    marker.Shield = shield;
                    changed = true;
                }
            }

            if (hpMode == HpMode.Points)
            {
                var editIcon = FontAwesomeIcon.Pen.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button($"{editIcon}##hpmax_edit_{label}"))
                        ImGui.OpenPopup($"hpmax_popup_{label}");
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(Loc.Get("Marker.HpMax"));
                    ImGui.EndTooltip();
                }
                if (ImGui.BeginPopup($"hpmax_popup_{label}"))
                {
                    ImGui.TextUnformatted(Loc.Get("Marker.HpMax"));
                    ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                    var editHpMax = marker.HpMax;
                    if (ImGui.InputInt($"##hpmax_{label}", ref editHpMax))
                    {
                        if (editHpMax < 1) editHpMax = 1;
                        if (editHpMax > 99999) editHpMax = 99999;
                        marker.HpMax = editHpMax;
                        marker.Hp = editHpMax;
                        changed = true;
                    }
                    ImGui.EndPopup();
                }
                ImGui.SameLine();
            }

            if (ImGui.Button($"-##hp_dec_{label}", new Vector2(pmBtnW, 0)))
            {
                hp = Math.Max(0, hp - hpStep);
                marker.Hp = hp;
                changed = true;
            }
            ImGui.SameLine();

            var barWidth = ImGui.GetContentRegionAvail().X - pmBtnW - barSpacing - barRightPad;
            HpBar.Draw(marker.Hp, marker.Attitude, barWidth, hpMode, marker.HpMax, shield: showShield ? marker.Shield : 0);
            ImGui.SameLine();

            if (ImGui.Button($"+##hp_inc_{label}", new Vector2(pmBtnW, 0)))
            {
                hp = Math.Min(hpClampMax, hp + hpStep);
                marker.Hp = hp;
                changed = true;
            }

            if (showMpBar)
            {
                var mp = marker.Mp;
                var mpStep = 1;
                var mpClampMax = mpMode == HpMode.Percentage ? 100 : marker.MpMax;

                if (mpMode == HpMode.Points)
                {
                    var editIcon = FontAwesomeIcon.Pen.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        if (ImGui.Button($"{editIcon}##mpmax_edit_{label}"))
                            ImGui.OpenPopup($"mpmax_popup_{label}");
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(Loc.Get("Marker.MpMax"));
                        ImGui.EndTooltip();
                    }
                    if (ImGui.BeginPopup($"mpmax_popup_{label}"))
                    {
                        ImGui.TextUnformatted(Loc.Get("Marker.MpMax"));
                        ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                        var editMpMax = marker.MpMax;
                        if (ImGui.InputInt($"##mpmax_{label}", ref editMpMax))
                        {
                            if (editMpMax < 1) editMpMax = 1;
                            if (editMpMax > 99999) editMpMax = 99999;
                            marker.MpMax = editMpMax;
                            marker.Mp = editMpMax;
                            changed = true;
                        }
                        ImGui.EndPopup();
                    }
                    ImGui.SameLine();
                }

                if (ImGui.Button($"-##mp_dec_{label}", new Vector2(pmBtnW, 0)))
                {
                    mp = Math.Max(0, mp - mpStep);
                    marker.Mp = mp;
                    changed = true;
                }
                ImGui.SameLine();
                var mpBarWidth = ImGui.GetContentRegionAvail().X - pmBtnW - barSpacing - barRightPad;
                HpBar.DrawMpBar(marker.Mp, mpBarWidth, mpMode, marker.MpMax);
                ImGui.SameLine();
                if (ImGui.Button($"+##mp_inc_{label}", new Vector2(pmBtnW, 0)))
                {
                    mp = Math.Min(mpClampMax, mp + mpStep);
                    marker.Mp = mp;
                    changed = true;
                }
            }

            var counters = marker.Counters;
            if (counters != null)
            {
                for (var ci = 0; ci < counters.Count; ci++)
                {
                    var counter = counters[ci];
                    var step = 1;

                    // Edit button (pen) — same position as HP edit
                    var editIcon = FontAwesomeIcon.Pen.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        if (ImGui.Button($"{editIcon}##cnt_edit_{label}_{ci}"))
                            ImGui.OpenPopup($"cnt_popup_{label}_{ci}");
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(Loc.Get("Counter.Edit"));
                        ImGui.EndTooltip();
                    }
                    if (ImGui.BeginPopup($"cnt_popup_{label}_{ci}"))
                    {
                        ImGui.TextUnformatted(Loc.Get("Counter.Edit"));
                        ImGui.Separator();
                        var cMax = counter.Max;
                        ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                        if (ImGui.InputInt($"##cnt_max_{label}_{ci}", ref cMax))
                        {
                            if (cMax < 1) cMax = 1;
                            counter.Max = cMax;
                            counter.Value = cMax;
                            changed = true;
                        }
                        ImGui.EndPopup();
                    }
                    ImGui.SameLine();

                    if (ImGui.Button($"-##cnt_dec_{label}_{ci}", new Vector2(pmBtnW, 0)))
                    {
                        counter.Value = Math.Max(0, counter.Value - step);
                        changed = true;
                    }
                    ImGui.SameLine();
                    var cntBarWidth = ImGui.GetContentRegionAvail().X - pmBtnW - barSpacing - barRightPad;
                    CounterBar.Draw(counter, cntBarWidth);
                    ImGui.SameLine();
                    if (ImGui.Button($"+##cnt_inc_{label}_{ci}", new Vector2(pmBtnW, 0)))
                    {
                        counter.Value = Math.Min(counter.Max, counter.Value + step);
                        changed = true;
                    }
                }
            }

            var attitude = marker.Attitude;
            if (AttitudePicker.Draw(label, ref attitude))
            {
                marker.Attitude = attitude;
                changed = true;
            }

            ImGui.SameLine();
            var attitudeColor = GetAttitudeColor(marker.Attitude);
            var attitudeText = GetAttitudeText(marker.Attitude);
            ImGui.TextColored(attitudeColor, attitudeText);

            if (marker.LastRollResult > 0)
            {
                ImGui.SameLine();
                var rollDisplay = FontAwesomeIcon.Dice.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), rollDisplay);
                ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
                ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), $"{marker.LastRollResult} / {marker.LastRollMax}");
            }

            ImGui.SameLine();

            if (marker.IsVisible)
            {
                var moveText = Loc.Get("Marker.Move");
                var clearText = Loc.Get("Marker.Clear");
                var framePad = ImGui.GetStyle().FramePadding.X * 2;
                var itemSpacing = ImGui.GetStyle().ItemSpacing.X;
                var hasName = !string.IsNullOrWhiteSpace(marker.Name);

                // Dice icon button (always shown if onRoll provided, disabled when no name)
                var diceIcon = FontAwesomeIcon.Dice.ToIconString();
                float diceIconBtnWidth;
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    diceIconBtnWidth = ImGui.CalcTextSize(diceIcon).X + framePad;

                var totalBtnWidth = ImGui.CalcTextSize(moveText).X + framePad
                                  + ImGui.CalcTextSize(clearText).X + framePad
                                  + itemSpacing;
                if (onRoll != null)
                    totalBtnWidth += diceIconBtnWidth + itemSpacing;
                var spacing = ImGui.GetContentRegionAvail().X - totalBtnWidth;
                if (spacing > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + spacing);

                if (onRoll != null)
                {
                    if (!hasName)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.3f, 0.3f, 0.5f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.3f, 0.5f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.3f, 0.3f, 0.3f, 0.5f));
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 0.5f));
                    }
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        if (ImGui.Button($"{diceIcon}##roll_{label}") && hasName)
                            onRoll.Invoke();
                    }
                    if (!hasName)
                        ImGui.PopStyleColor(4);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(hasName ? Loc.Get("Marker.Roll") : Loc.Get("Marker.RollDisabled"));
                        ImGui.EndTooltip();
                    }
                    ImGui.SameLine();
                }

                if (ImGui.Button($"{moveText}##move_{label}"))
                    onMove?.Invoke();

                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
                if (ImGui.Button($"{clearText}##clear_{label}"))
                    onClear?.Invoke();
                ImGui.PopStyleColor();
            }
            else
            {
                var spacing = ImGui.GetContentRegionAvail().X - 70f * ImGuiHelpers.GlobalScale;
                if (spacing > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + spacing);

                if (ImGui.Button($"{Loc.Get("Marker.Place")}##place_{label}"))
                    onPlace?.Invoke();
            }
        }
        ImGui.EndChild();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();

        return changed;
    }

    public static void DrawReadOnly(WaymarkId waymarkId, MarkerData marker, HpMode hpMode = HpMode.Points, bool showShield = false, bool showMpBar = false, HpMode mpMode = HpMode.Points)
    {
        var label = waymarkId.ToLabel();
        var borderColor = GetAttitudeBorderColor(marker.Attitude, true);

        ImGui.PushStyleColor(ImGuiCol.Border, borderColor);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);

        var cardWidth = ImGui.GetContentRegionAvail().X;
        var extraRowsRo = 0;
        if (showMpBar) extraRowsRo++;
        if (marker.Counters != null) extraRowsRo += marker.Counters.Count;
        var cardHeight = ImGui.GetFrameHeightWithSpacing() * (2 + extraRowsRo) + ImGui.GetStyle().WindowPadding.Y * 2;
        if (marker.LastRollResult > 0)
            cardHeight += ImGui.GetTextLineHeight();
        if (ImGui.BeginChild($"##pmarker_{label}", new Vector2(cardWidth, cardHeight), true))
        {
            // Header: icon + boss icon + name
            DrawWaymarkIcon(waymarkId);
            if (marker.IsBoss)
            {
                ImGui.SameLine();
                var bossWrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(BossIconId)).GetWrapOrEmpty();
                var bossSize = IconSize * ImGuiHelpers.GlobalScale;
                ImGui.Image(bossWrap.Handle, new Vector2(bossSize, bossSize));
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(Loc.Get("Marker.Boss"));
                    ImGui.EndTooltip();
                }
            }
            // Center name relative to full card width
            var nameWidth = ImGui.CalcTextSize(marker.Name).X;
            var nameX = (cardWidth - nameWidth) / 2f;
            var minX = ImGui.GetCursorPosX();
            ImGui.SameLine();
            if (nameX > minX)
                ImGui.SetCursorPosX(nameX);
            ImGui.TextUnformatted(marker.Name);

            // HP bar
            HpBar.Draw(marker.Hp, marker.Attitude, ImGui.GetContentRegionAvail().X, hpMode, marker.HpMax, shield: showShield ? marker.Shield : 0);

            // Ether bar
            if (showMpBar)
                HpBar.DrawMpBar(marker.Mp, ImGui.GetContentRegionAvail().X, mpMode, marker.MpMax);

            // Custom counters
            if (marker.Counters != null)
            {
                foreach (var counter in marker.Counters)
                    CounterBar.Draw(counter, ImGui.GetContentRegionAvail().X);
            }

            // Last roll result (centered, compact)
            if (marker.LastRollResult > 0)
            {
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + 2f * ImGuiHelpers.GlobalScale);
                var rollIcon = FontAwesomeIcon.Dice.ToIconString();
                var rollText = $"{marker.LastRollResult} / {marker.LastRollMax}";
                var rollColor = new Vector4(1f, 1f, 1f, 1f);
                var gap = 4f * ImGuiHelpers.GlobalScale;

                float iconW;
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    iconW = ImGui.CalcTextSize(rollIcon).X;
                var textW = ImGui.CalcTextSize(rollText).X;
                var totalW = iconW + gap + textW;
                var rollX = (cardWidth - totalW) / 2f - ImGui.GetStyle().WindowPadding.X;
                if (rollX > 0) ImGui.SetCursorPosX(rollX);

                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(rollColor, rollIcon);
                ImGui.SameLine(0, gap);
                ImGui.TextColored(rollColor, rollText);
            }
        }
        ImGui.EndChild();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private static Vector4 GetAttitudeBorderColor(Attitude attitude, bool isVisible)
    {
        if (!isVisible)
            return new Vector4(0.3f, 0.3f, 0.3f, 0.5f);

        return attitude switch
        {
            Attitude.Hostile => MasterEventTheme.AttitudeHostile with { W = 0.8f },
            Attitude.Neutral => MasterEventTheme.AttitudeNeutral with { W = 0.8f },
            Attitude.Friendly => MasterEventTheme.AttitudeFriendly with { W = 0.8f },
            _ => MasterEventTheme.ThemeBorder,
        };
    }

    private static Vector4 GetAttitudeColor(Attitude attitude) => attitude switch
    {
        Attitude.Hostile => MasterEventTheme.AttitudeHostile,
        Attitude.Neutral => MasterEventTheme.AttitudeNeutral,
        Attitude.Friendly => MasterEventTheme.AttitudeFriendly,
        _ => new Vector4(0.5f, 0.5f, 0.5f, 1f),
    };

    private static string GetAttitudeText(Attitude attitude) => attitude switch
    {
        Attitude.Hostile => Loc.Get("Attitude.Hostile"),
        Attitude.Neutral => Loc.Get("Attitude.Neutral"),
        Attitude.Friendly => Loc.Get("Attitude.Friendly"),
        _ => "?",
    };
}
