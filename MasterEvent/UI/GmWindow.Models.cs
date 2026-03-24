using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
    private void DrawModelsContent()
    {
        var availWidth = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(6f);

        // Header icon
        var iconStr = FontAwesomeIcon.FileAlt.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr);
        const float scale = 1.6f;
        var scaledSz = iconSz * scale;
        var pos = ImGui.GetCursorScreenPos();
        var iconX = pos.X + (availWidth - scaledSz.X) / 2f;
        ImGui.Dummy(new Vector2(0, scaledSz.Y));
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(iconX, pos.Y), ImGui.GetColorU32(MasterEventTheme.AccentColor), iconStr);
        ImGui.PopFont();

        ImGuiHelpers.ScaledDummy(4f);

        var titleSz = ImGui.CalcTextSize(Loc.Get("Sidebar.Models"));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Sidebar.Models"));

        ImGuiHelpers.ScaledDummy(2f);

        var descColor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
        var descSz = ImGui.CalcTextSize(Loc.Get("Models.Subtitle"));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - descSz.X) / 2f);
        ImGui.TextColored(descColor, Loc.Get("Models.Subtitle"));

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.BeginChild("##models_scroll", Vector2.Zero))
        {
            // Active template
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Active"));
            ImGui.SameLine();
            if (session.ActiveTemplate != null)
            {
                ImGui.TextUnformatted(session.ActiveTemplate.Name);
                ImGui.SameLine();
                if (ImGui.Button(Loc.Get("Models.Clear") + "##deactivate"))
                {
                    session.ClearActiveTemplate();
                    configuration.ActiveTemplateName = string.Empty;
                    configuration.Save();
                }
                ImGui.SameLine();
                if (ImGui.Button(Loc.Get("Models.Share") + "##share"))
                {
                    session.BroadcastTemplate();
                    session.BroadcastUpdate();
                }
            }
            else
            {
                ImGui.TextColored(descColor, "—");
            }

            // Afficher le dernier code exporté
            if (lastExportCode != null)
            {
                ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f),
                    string.Format(Loc.Get("Models.ExportCode"), lastExportCode));
                ImGui.SameLine();
                var copyIcon = FontAwesomeIcon.Copy.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.SmallButton(copyIcon + "##copy_code"))
                        ImGui.SetClipboardText(lastExportCode);
                }
            }
            if (exportInProgress)
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), Loc.Get("Models.Exporting"));

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            // New template creation
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.CreateTitle"));
            ImGui.SetNextItemWidth(availWidth - 80f * ImGuiHelpers.GlobalScale);
            ImGui.InputText("##new_template_name", ref newTemplateName, 64);
            ImGui.SameLine();
            if (ImGui.Button(Loc.Get("Models.Create") + "##create_template"))
            {
                if (!string.IsNullOrWhiteSpace(newTemplateName))
                {
                    var template = EventTemplate.CreateDefault();
                    template.Name = newTemplateName.Trim();
                    editingTemplate = template;
                    editingTemplateName = template.Name;
                    newTemplateName = string.Empty;
                }
            }

            // Template editor
            if (editingTemplate != null)
            {
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(4f);

                // Editor card
                ImGui.PushStyleColor(ImGuiCol.Border, MasterEventTheme.AccentColor with { W = 0.6f });
                ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
                ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);

                if (ImGui.BeginChild("##tpl_editor", new Vector2(0, 0), true, ImGuiWindowFlags.AlwaysAutoResize))
                {
                    // Header
                    var penIcon = FontAwesomeIcon.Pen.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.AccentColor, penIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(MasterEventTheme.AccentColor, string.Format(Loc.Get("Models.Editing"), editingTemplateName));

                    ImGuiHelpers.ScaledDummy(4f);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(4f);

                    var fieldWidth = ImGui.GetContentRegionAvail().X;

                    // ── Name ──
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Models.Name"));
                    ImGui.SetNextItemWidth(fieldWidth);
                    var tplName = editingTemplate.Name;
                    if (ImGui.InputText("##tpl_name", ref tplName, 64))
                        editingTemplate.Name = tplName;

                    ImGuiHelpers.ScaledDummy(6f);

                    // ── HP / MP modes side by side ──
                    var hpModeLabels = new[] { Loc.Get("Config.HpMode.Percentage"), Loc.Get("Config.HpMode.Points") };
                    var halfWidth = (fieldWidth - ImGui.GetStyle().ItemSpacing.X) / 2f;
                    var labelColor = new Vector4(0.7f, 0.7f, 0.7f, 1f);
                    var secondColX = ImGui.GetCursorPosX() + halfWidth + ImGui.GetStyle().ItemSpacing.X;

                    // Labels row
                    var heartIcon = FontAwesomeIcon.Heart.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.AttitudeHostile, heartIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(labelColor, Loc.Get("Config.HpMode"));
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(secondColX);
                    var mpDisabled = !editingTemplate.ShowMpBar;
                    if (mpDisabled) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.4f);
                    var magicIcon = FontAwesomeIcon.Magic.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.MpBarColor, magicIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(labelColor, Loc.Get("Config.MpMode"));
                    if (mpDisabled) ImGui.PopStyleVar();

                    // Combos row
                    ImGui.SetNextItemWidth(halfWidth);
                    var hpModeIdx = (int)editingTemplate.HpMode;
                    if (ImGui.Combo("##tpl_hp_mode", ref hpModeIdx, hpModeLabels, hpModeLabels.Length))
                        editingTemplate.HpMode = (HpMode)hpModeIdx;
                    ImGui.SameLine();
                    if (mpDisabled) ImGui.BeginDisabled();
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    var mpModeIdx = (int)editingTemplate.MpMode;
                    if (ImGui.Combo("##tpl_mp_mode", ref mpModeIdx, hpModeLabels, hpModeLabels.Length))
                        editingTemplate.MpMode = (HpMode)mpModeIdx;
                    if (mpDisabled) ImGui.EndDisabled();

                    ImGuiHelpers.ScaledDummy(4f);

                    // ── Toggles on same line ──
                    var tplShield = editingTemplate.ShowShield;
                    if (ImGui.Checkbox(Loc.Get("Config.ShowShield") + "##tpl_shield", ref tplShield))
                        editingTemplate.ShowShield = tplShield;

                    ImGui.SameLine();

                    var tplMpBar = editingTemplate.ShowMpBar;
                    if (ImGui.Checkbox(Loc.Get("Config.ShowMpBar") + "##tpl_mp_bar", ref tplMpBar))
                        editingTemplate.ShowMpBar = tplMpBar;

                    ImGuiHelpers.ScaledDummy(4f);

                    // ── Default HP / MP max ──
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.HpMax"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                    var tplHpMax = editingTemplate.DefaultHpMax;
                    if (ImGui.InputInt("##tpl_hp_max", ref tplHpMax))
                    {
                        if (tplHpMax < 1) tplHpMax = 1;
                        if (tplHpMax > 99999) tplHpMax = 99999;
                        editingTemplate.DefaultHpMax = tplHpMax;
                    }

                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.PlayerHpMax"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                    var tplPlayerHpMax = editingTemplate.DefaultPlayerHpMax;
                    if (ImGui.InputInt("##tpl_player_hp_max", ref tplPlayerHpMax))
                    {
                        if (tplPlayerHpMax < 1) tplPlayerHpMax = 1;
                        if (tplPlayerHpMax > 99999) tplPlayerHpMax = 99999;
                        editingTemplate.DefaultPlayerHpMax = tplPlayerHpMax;
                    }

                    if (mpDisabled) ImGui.BeginDisabled();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.MpMax"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                    var tplMpMax = editingTemplate.DefaultMpMax;
                    if (ImGui.InputInt("##tpl_mp_max", ref tplMpMax))
                    {
                        if (tplMpMax < 1) tplMpMax = 1;
                        if (tplMpMax > 99999) tplMpMax = 99999;
                        editingTemplate.DefaultMpMax = tplMpMax;
                    }
                    if (mpDisabled) ImGui.EndDisabled();

                    ImGuiHelpers.ScaledDummy(4f);

                    // ── Formule de dé ──
                    var diceIcon = FontAwesomeIcon.Dice.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), diceIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Dice.Formula"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                    var tplDiceFormula = editingTemplate.DiceFormula;
                    if (ImGui.InputText("##tpl_dice_formula", ref tplDiceFormula, 16))
                        editingTemplate.DiceFormula = tplDiceFormula;
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(Loc.Get("Dice.FormulaTooltip"));
                        ImGui.EndTooltip();
                    }

                    // ── Stat d'initiative ──
                    ImGuiHelpers.ScaledDummy(2f);
                    var initIcon = FontAwesomeIcon.SortNumericDown.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), initIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Models.InitiativeStat"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
                    var currentInitStatName = Loc.Get("Models.InitiativeNone");
                    if (editingTemplate.InitiativeStatId != null && editingTemplate.StatDefinitions != null)
                    {
                        var initStat = editingTemplate.StatDefinitions.FirstOrDefault(s => s.Id == editingTemplate.InitiativeStatId);
                        if (initStat != null) currentInitStatName = initStat.Name;
                    }
                    if (ImGui.BeginCombo("##tpl_init_stat", currentInitStatName))
                    {
                        if (ImGui.Selectable(Loc.Get("Models.InitiativeNone"), editingTemplate.InitiativeStatId == null))
                            editingTemplate.InitiativeStatId = null;
                        foreach (var sd in (editingTemplate.StatDefinitions ?? []).Where(sd => ImGui.Selectable(sd.Name, sd.Id == editingTemplate.InitiativeStatId)))
                        {
                            editingTemplate.InitiativeStatId = sd.Id;
                        }
                        ImGui.EndCombo();
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(Loc.Get("Models.InitiativeStatHint"));
                        ImGui.EndTooltip();
                    }

                    ImGuiHelpers.ScaledDummy(4f);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(4f);

                    // ── Counter definitions ──
                    var listIcon = FontAwesomeIcon.ListUl.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.AccentColor, listIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Counters"));
                    ImGuiHelpers.ScaledDummy(2f);

                    var counterDefs = editingTemplate.CounterDefinitions;
                    if (counterDefs != null)
                    {
                        for (var ci = 0; ci < counterDefs.Count; ci++)
                        {
                            var cd = counterDefs[ci];
                            ImGui.PushID(ci);
                            ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                            var cdName = cd.Name;
                            if (ImGui.InputText("##cd_name", ref cdName, 32))
                                cd.Name = cdName;
                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(60f * ImGuiHelpers.GlobalScale);
                            var cdMax = cd.DefaultMax;
                            if (ImGui.InputInt("##cd_max", ref cdMax))
                            {
                                if (cdMax < 1) cdMax = 1;
                                cd.DefaultMax = cdMax;
                            }
                            ImGui.SameLine();
                            var cdColor = new Vector3(cd.ColorR, cd.ColorG, cd.ColorB);
                            ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                            if (ImGui.ColorEdit3("##cd_color", ref cdColor, ImGuiColorEditFlags.NoInputs))
                            {
                                cd.ColorR = cdColor.X;
                                cd.ColorG = cdColor.Y;
                                cd.ColorB = cdColor.Z;
                            }
                            ImGui.SameLine();
                            var xIcon = FontAwesomeIcon.Times.ToIconString();
                            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                            {
                                if (ImGui.Button(xIcon + "##cd_del"))
                                {
                                    counterDefs.RemoveAt(ci);
                                    if (counterDefs.Count == 0) editingTemplate.CounterDefinitions = null;
                                }
                            }
                            ImGui.PopID();
                        }
                    }

                    var plusIcon = FontAwesomeIcon.Plus.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        if (ImGui.Button(plusIcon + "##add_cd_btn"))
                        {
                            editingTemplate.CounterDefinitions ??= new List<CounterDefinition>();
                            editingTemplate.CounterDefinitions.Add(new CounterDefinition());
                        }
                    }
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Models.AddCounter"));

                    ImGuiHelpers.ScaledDummy(6f);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(4f);

                    var chartIcon = FontAwesomeIcon.ChartBar.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.AccentColor, chartIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Stats"));
                    ImGuiHelpers.ScaledDummy(2f);

                    var statDefs = editingTemplate.StatDefinitions;
                    if (statDefs != null)
                    {
                        for (var si = 0; si < statDefs.Count; si++)
                        {
                            var sd = statDefs[si];
                            ImGui.PushID(1000 + si);
                            ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
                            var sdName = sd.Name;
                            if (ImGui.InputTextWithHint("##sd_name", Loc.Get("Stat.Name"), ref sdName, 32))
                                sd.Name = sdName;
                            ImGui.SameLine();
                            var xIcon = FontAwesomeIcon.Times.ToIconString();
                            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                            {
                                if (ImGui.Button(xIcon + "##sd_del"))
                                {
                                    statDefs.RemoveAt(si);
                                    if (statDefs.Count == 0) editingTemplate.StatDefinitions = null;
                                }
                            }
                            ImGui.PopID();
                        }
                    }

                    var plusStatIcon = FontAwesomeIcon.Plus.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        if (ImGui.Button(plusStatIcon + "##add_sd_btn"))
                        {
                            editingTemplate.StatDefinitions ??= new List<StatDefinition>();
                            editingTemplate.StatDefinitions.Add(new StatDefinition());
                        }
                    }
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Models.AddStat"));

                    ImGuiHelpers.ScaledDummy(6f);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(4f);

                    // ── Save / Cancel ──
                    var btnWidth = (fieldWidth - ImGui.GetStyle().ItemSpacing.X) / 2f;
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.6f, 0.25f, 1f));
                    if (ImGui.Button(Loc.Get("Gm.Save") + "##save_tpl", new Vector2(btnWidth, 0)))
                    {
                        if (editingTemplateName != null && editingTemplate.Name != editingTemplateName)
                            session.DeleteTemplate(editingTemplateName);
                        session.SaveTemplate(editingTemplate);
                        editingTemplate = null;
                        editingTemplateName = null;
                    }
                    ImGui.PopStyleColor(2);

                    ImGui.SameLine();

                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.5f, 0.2f, 0.2f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.6f, 0.25f, 0.25f, 1f));
                    if (ImGui.Button(Loc.Get("Gm.Cancel") + "##cancel_tpl", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
                    {
                        editingTemplate = null;
                        editingTemplateName = null;
                    }
                    ImGui.PopStyleColor(2);
                }
                ImGui.EndChild();

                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor();
            }

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            // Saved templates list
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Saved"));
            ImGui.Spacing();

            var templateNames = session.GetTemplateNames();
            if (templateNames.Count == 0)
            {
                ImGui.TextColored(descColor, Loc.Get("Models.None"));
            }
            else
            {
                foreach (var tplName in templateNames)
                {
                    var isDefault = string.Equals(tplName, configuration.DefaultTemplateName, StringComparison.OrdinalIgnoreCase);

                    // Star icon for default template
                    if (isDefault)
                    {
                        var starIcon = FontAwesomeIcon.Star.ToIconString();
                        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                            ImGui.TextColored(MasterEventTheme.AccentColor, starIcon);
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted(Loc.Get("Models.DefaultTooltip"));
                            ImGui.EndTooltip();
                        }
                        ImGui.SameLine();
                    }

                    ImGui.TextUnformatted(tplName);
                    ImGui.SameLine();

                    if (ImGui.Button(Loc.Get("Gm.Load") + "##load_" + tplName))
                    {
                        var loaded = session.LoadTemplate(tplName);
                        if (loaded != null)
                        {
                            session.ApplyTemplate(loaded);
                            session.BroadcastTemplate();
                            session.BroadcastUpdate();

                            configuration.ActiveTemplateName = loaded.Name;
                            configuration.Save();
                        }
                    }
                    ImGui.SameLine();

                    // Edit button
                    var editIcon = FontAwesomeIcon.Pen.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        if (ImGui.Button(editIcon + "##edit_" + tplName))
                        {
                            var loaded = session.LoadTemplate(tplName);
                            if (loaded != null)
                            {
                                editingTemplate = loaded.DeepCopy();
                                editingTemplateName = loaded.Name;
                            }
                        }
                    }
                    ImGui.SameLine();

                    // Export button
                    DrawExportButtonByName(tplName);
                    ImGui.SameLine();

                    // Set as default button (only if not already default)
                    if (!isDefault)
                    {
                        var defaultIcon = FontAwesomeIcon.Star.ToIconString();
                        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        {
                            if (ImGui.Button(defaultIcon + "##default_" + tplName))
                            {
                                configuration.DefaultTemplateName = tplName;
                                configuration.Save();
                            }
                        }
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted(Loc.Get("Models.SetDefault"));
                            ImGui.EndTooltip();
                        }
                        ImGui.SameLine();
                    }

                    // Delete button (disabled for "Standard")
                    if (tplName != "Standard")
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
                        if (ImGui.Button(Loc.Get("Models.Delete") + "##del_" + tplName))
                        {
                            session.DeleteTemplate(tplName);
                            if (session.ActiveTemplate?.Name == tplName)
                            {
                                session.ClearActiveTemplate();
                                configuration.ActiveTemplateName = string.Empty;
                                configuration.Save();
                            }
                            // If deleted template was the default, fall back to Standard
                            if (string.Equals(tplName, configuration.DefaultTemplateName, StringComparison.OrdinalIgnoreCase))
                            {
                                configuration.DefaultTemplateName = "Standard";
                                configuration.Save();
                            }
                        }
                        ImGui.PopStyleColor();
                    }

                    ImGui.Spacing();
                }
            }

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            var importIcon = FontAwesomeIcon.Download.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.AccentColor, importIcon);
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Import"));
            ImGuiHelpers.ScaledDummy(2f);

            if (importInProgress) ImGui.BeginDisabled();
            var importWidth = ImGui.GetContentRegionAvail().X;
            ImGui.SetNextItemWidth(importWidth - 40f * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("##import_code", Loc.Get("Models.ImportCode"), ref importCode, 16);
            ImGui.SameLine();
            var dlIconStr = FontAwesomeIcon.Download.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                var canImport = !string.IsNullOrWhiteSpace(importCode);
                if (!canImport) ImGui.BeginDisabled();
                if (ImGui.Button(dlIconStr + "##do_import"))
                {
                    importInProgress = true;
                    var code = importCode.Trim();
                    _ = Task.Run(async () =>
                    {
                        var template = await SessionManager.ImportTemplateAsync(code, configuration.RelayServerUrl);
                        importInProgress = false;
                        if (template != null)
                        {
                            session.SaveTemplate(template);
                            Plugin.ChatGui.Print(string.Format(Loc.Get("Models.Imported"), template.Name));
                            importCode = string.Empty;
                        }
                        else
                        {
                            Plugin.ChatGui.Print(Loc.Get("Models.ImportError"));
                        }
                    });
                }
                if (!canImport) ImGui.EndDisabled();
            }
            if (importInProgress) ImGui.EndDisabled();
            if (importInProgress)
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), Loc.Get("Models.Importing"));
        }
        ImGui.EndChild();
    }

    private void DrawExportButtonByName(string templateName)
    {
        if (exportInProgress) ImGui.BeginDisabled();

        var exportIcon = FontAwesomeIcon.Upload.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(exportIcon + "##export_" + templateName))
                ImGui.OpenPopup("##export_popup_" + templateName);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Models.ExportTooltip"));
            ImGui.EndTooltip();
        }

        if (exportInProgress) ImGui.EndDisabled();

        if (ImGui.BeginPopup("##export_popup_" + templateName))
        {
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Export"));
            ImGui.Separator();

            ImGui.Checkbox(Loc.Get("Models.ExportPermanent") + "##perm_" + templateName, ref exportPermanent);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Models.ExportPermanentTooltip"));
                ImGui.EndTooltip();
            }

            if (ImGui.Button(Loc.Get("Models.Export") + "##do_export_" + templateName))
            {
                var tpl = session.LoadTemplate(templateName);
                if (tpl != null)
                {
                    exportInProgress = true;
                    lastExportCode = null;
                    var perm = exportPermanent;
                    _ = Task.Run(async () =>
                    {
                        var code = await SessionManager.ExportTemplateAsync(tpl, configuration.RelayServerUrl, perm);
                        exportInProgress = false;
                        if (code != null)
                        {
                            lastExportCode = code;
                            ImGui.SetClipboardText(code);
                            Plugin.ChatGui.Print(Loc.Get("Models.Exported"));
                        }
                        else
                        {
                            Plugin.ChatGui.Print(Loc.Get("Models.ImportError"));
                        }
                    });
                }
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }
}
