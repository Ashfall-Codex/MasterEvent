using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using System.Collections.Generic;
using MasterEvent.Localization;
using MasterEvent.Models;
using Dalamud.Plugin.Services;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;


public sealed class SetupAssistantWindow : MasterEventWindowBase
{
    private int step;
    private const int StepCount = 7;
    private readonly Action? onFinished;
    private readonly SessionManager session;
    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private string importCode = string.Empty;
    private bool importInProgress;
    private string? importedTemplateName;
    private bool rgpdCheckboxAccepted;
    private EventTemplate? creatingTemplate;
    private bool templateSaved;
    private PlayerSheet? creatingSheet;
    private string sheetName = string.Empty;
    private string sheetTemplateName = string.Empty;
    private bool sheetSaved;
    private bool sheetNameInitialized;

    public SetupAssistantWindow(SessionManager session, Configuration configuration, IPlayerState playerState, Action? onFinished = null)
        : base("MasterEvent - Assistant###MasterEventSetup",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.session = session;
        this.configuration = configuration;
        this.playerState = playerState;
        this.onFinished = onFinished;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(750, 620),
            MaximumSize = new Vector2(750, 620),
        };

        SizeCondition = ImGuiCond.Always;
        Size = new Vector2(750, 620);
        PositionCondition = ImGuiCond.Appearing;
    }

    public override void OnOpen()
    {
        step = 0;

        var viewport = ImGui.GetMainViewport();
        var windowSize = new Vector2(750, 620);
        Position = new Vector2(
            viewport.WorkPos.X + (viewport.WorkSize.X - windowSize.X) / 2f,
            viewport.WorkPos.Y + (viewport.WorkSize.Y - windowSize.Y) / 2f);
    }

    protected override void DrawContents()
    {
        var availWidth = ImGui.GetContentRegionAvail().X;
        var contentFlags = step is 1 or 3 or 4 or 5 ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar;
        if (ImGui.BeginChild("##setup_content", new Vector2(0, -50f * ImGuiHelpers.GlobalScale), false, contentFlags))
        {
            switch (step)
            {
                case 0: DrawStepWelcome(availWidth); break;
                case 1: DrawStepRgpd(availWidth); break;
                case 2:
                    ImGui.SetWindowFontScale(0.92f);
                    DrawStepTemplate(availWidth);
                    ImGui.SetWindowFontScale(1f);
                    break;
                case 3: DrawStepTemplateResult(availWidth); break;
                case 4: DrawStepSheet(availWidth); break;
                case 5: DrawStepDice(availWidth); break;
                case 6: DrawStepDone(availWidth); break;
            }
        }
        ImGui.EndChild();

        DrawBottomBar(availWidth);
    }


    // Étape Bienvenue

    private static void DrawStepWelcome(float availWidth)
    {
        DrawStepHeader(FontAwesomeIcon.Dice, Loc.Get("Guide.Welcome.Title"), Loc.Get("Guide.Welcome.Subtitle"), availWidth);

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth);

        ImGui.TextWrapped(Loc.Get("Guide.Welcome.Intro"));

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Guide.Welcome.FeaturesTitle"));
        ImGuiHelpers.ScaledDummy(3f);

        DrawField(FontAwesomeIcon.FileAlt, MasterEventTheme.AccentColor,
            Loc.Get("Guide.Welcome.Feature.Templates"), Loc.Get("Guide.Welcome.Feature.Templates.Desc"));

        DrawField(FontAwesomeIcon.Scroll, MasterEventTheme.AccentColor,
            Loc.Get("Guide.Welcome.Feature.Sheets"), Loc.Get("Guide.Welcome.Feature.Sheets.Desc"));

        DrawField(FontAwesomeIcon.Dice, new Vector4(1f, 1f, 1f, 1f),
            Loc.Get("Guide.Welcome.Feature.Dice"), Loc.Get("Guide.Welcome.Feature.Dice.Desc"));

        DrawField(FontAwesomeIcon.CloudSunRain, MasterEventTheme.AccentColor,
            Loc.Get("Guide.Welcome.Feature.Weather"), Loc.Get("Guide.Welcome.Feature.Weather.Desc"));

        DrawField(FontAwesomeIcon.Users, MasterEventTheme.AccentColor,
            Loc.Get("Guide.Welcome.Feature.Sync"), Loc.Get("Guide.Welcome.Feature.Sync.Desc"));

        ImGuiHelpers.ScaledDummy(4f);

        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Guide.Welcome.Outro"));

        ImGui.PopTextWrapPos();
    }

    // Étape RGPD

    private void DrawStepRgpd(float availWidth)
    {
        DrawStepHeader(FontAwesomeIcon.ShieldAlt, Loc.Get("Guide.Rgpd.Title"), Loc.Get("Guide.Rgpd.Subtitle"), availWidth);

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth);

        ImGui.TextWrapped(Loc.Get("Rgpd.Consent.Intro"));

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f), Loc.Get("Rgpd.Consent.Reassurance"));
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Rgpd.Consent.DataCollected"));
        ImGuiHelpers.ScaledDummy(2f);

        DrawBullet(null, Loc.Get("Guide.Rgpd.Data1"));
        DrawBullet(null, Loc.Get("Guide.Rgpd.Data2"));
        DrawBullet(null, Loc.Get("Guide.Rgpd.Data3"));
        DrawBullet(null, Loc.Get("Guide.Rgpd.Data4"));
        DrawBullet(null, Loc.Get("Guide.Rgpd.Data5"));
        DrawBullet(null, Loc.Get("Guide.Rgpd.Data6"));

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        if (configuration.IsRgpdConsentValid)
        {
            var checkIcon = FontAwesomeIcon.CheckCircle.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f), checkIcon);
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f), Loc.Get("Guide.Rgpd.Accepted"));
        }
        else
        {
            ImGui.Checkbox(Loc.Get("Rgpd.Consent.Checkbox"), ref rgpdCheckboxAccepted);

            ImGuiHelpers.ScaledDummy(4f);

            if (!rgpdCheckboxAccepted) ImGui.BeginDisabled();

            var btnWidth = 180f * ImGuiHelpers.GlobalScale;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - btnWidth) / 2f);

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.6f, 0.25f, 1f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 12f * ImGuiHelpers.GlobalScale);

            if (ImGui.Button(Loc.Get("Rgpd.Consent.Accept") + "##setup_rgpd_accept", new Vector2(btnWidth, 0)))
            {
                configuration.RgpdConsentGiven = true;
                configuration.RgpdConsentDate = DateTime.Now;
                configuration.AcceptedRgpdVersion = Configuration.ExpectedRgpdVersion;
                configuration.Save();
            }

            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);

            if (!rgpdCheckboxAccepted) ImGui.EndDisabled();
        }

        ImGuiHelpers.ScaledDummy(4f);

        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Rgpd.Consent.Rights"));

        ImGui.PopTextWrapPos();
    }

    // Étape Modèle

    private void DrawStepTemplate(float availWidth)
    {
        DrawStepHeader(FontAwesomeIcon.FileAlt, Loc.Get("Guide.Template.Title"), Loc.Get("Guide.Template.Subtitle"), availWidth);

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth);

        ImGui.TextWrapped(Loc.Get("Guide.Template.Intro"));

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Guide.Template.FieldsTitle"));
        ImGuiHelpers.ScaledDummy(3f);

        DrawField(FontAwesomeIcon.Heart, MasterEventTheme.AttitudeHostile,
            Loc.Get("Guide.Template.Field.Hp"), Loc.Get("Guide.Template.Field.Hp.Desc"));

        DrawField(FontAwesomeIcon.Magic, MasterEventTheme.MpBarColor,
            Loc.Get("Guide.Template.Field.Mp"), Loc.Get("Guide.Template.Field.Mp.Desc"));

        DrawField(FontAwesomeIcon.Dice, new Vector4(1f, 1f, 1f, 1f),
            Loc.Get("Guide.Template.Field.Dice"), Loc.Get("Guide.Template.Field.Dice.Desc"));

        DrawField(FontAwesomeIcon.ChartBar, MasterEventTheme.AccentColor,
            Loc.Get("Guide.Template.Field.Stats"), Loc.Get("Guide.Template.Field.Stats.Desc"));

        DrawField(FontAwesomeIcon.ListUl, MasterEventTheme.AccentColor,
            Loc.Get("Guide.Template.Field.Counters"), Loc.Get("Guide.Template.Field.Counters.Desc"));

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // Import de modèle par code de partage
        var importIcon = FontAwesomeIcon.Download.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.AccentColor, importIcon);
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Guide.Template.Import"));

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), Loc.Get("Guide.Template.Import.Desc"));
        ImGuiHelpers.ScaledDummy(4f);

        if (importInProgress) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(availWidth - 50f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##setup_import_code", Loc.Get("Models.ImportCode"), ref importCode, 16);
        ImGui.SameLine();
        var dlIcon = FontAwesomeIcon.Download.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            var canImport = !string.IsNullOrWhiteSpace(importCode);
            if (!canImport) ImGui.BeginDisabled();
            if (ImGui.Button(dlIcon + "##setup_do_import"))
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
                        importedTemplateName = template.Name;
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

        if (importedTemplateName != null)
        {
            ImGuiHelpers.ScaledDummy(2f);
            var checkIcon = FontAwesomeIcon.CheckCircle.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f), checkIcon);
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f),
                string.Format(Loc.Get("Guide.Template.Imported"), importedTemplateName));
        }

        ImGui.PopTextWrapPos();
    }


    // Résultat modèle (recap ou création)

    private void DrawStepTemplateResult(float availWidth)
    {
        if (importedTemplateName != null)
        {

            DrawStepHeader(FontAwesomeIcon.CheckCircle, Loc.Get("Guide.TemplateResult.ImportedTitle"), Loc.Get("Guide.TemplateResult.Subtitle"), availWidth);

            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth);

            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f),
                string.Format(Loc.Get("Guide.TemplateResult.ImportedIntro"), importedTemplateName));

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            var tpl = session.LoadTemplate(importedTemplateName);
            if (tpl != null)
            {
                ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Guide.TemplateResult.RecapTitle"));
                ImGuiHelpers.ScaledDummy(4f);

                DrawField(FontAwesomeIcon.Heart, MasterEventTheme.AttitudeHostile,
                    Loc.Get("Guide.Template.Field.Hp"),
                    string.Format(Loc.Get("Guide.TemplateResult.HpValue"), tpl.DefaultPlayerHpMax, Loc.Get(tpl.HpMode == HpMode.Points ? "Config.HpMode.Points" : "Config.HpMode.Percentage")));


                if (tpl.ShowMpBar)
                {
                    DrawField(FontAwesomeIcon.Magic, MasterEventTheme.MpBarColor,
                        Loc.Get("Guide.Template.Field.Mp"),
                        string.Format(Loc.Get("Guide.TemplateResult.MpValue"), tpl.DefaultPlayerMpMax));
                }


                DrawField(FontAwesomeIcon.Dice, new Vector4(1f, 1f, 1f, 1f),
                    Loc.Get("Guide.Template.Field.Dice"), tpl.DiceFormula);


                var statCount = tpl.StatDefinitions?.Count ?? 0;
                if (statCount > 0)
                {
                    var statNames = string.Join(", ", tpl.StatDefinitions!.Select(s => s.Name));
                    DrawField(FontAwesomeIcon.ChartBar, MasterEventTheme.AccentColor,
                        string.Format(Loc.Get("Guide.TemplateResult.StatsCount"), statCount), statNames);
                }


                var counterCount = tpl.CounterDefinitions?.Count ?? 0;
                if (counterCount > 0)
                {
                    var counterNames = string.Join(", ", tpl.CounterDefinitions!.Select(c => c.Name));
                    DrawField(FontAwesomeIcon.ListUl, MasterEventTheme.AccentColor,
                        string.Format(Loc.Get("Guide.TemplateResult.CountersCount"), counterCount), counterNames);
                }

                if (tpl.ShowShield)
                {
                    DrawField(FontAwesomeIcon.ShieldAlt, new Vector4(0.5f, 0.7f, 1f, 1f),
                        Loc.Get("Config.ShowShield"), Loc.Get("Guide.TemplateResult.ShieldEnabled"));
                }
            }

            ImGui.PopTextWrapPos();
        }
        else
        {
            // Pas de modèle importé, créer directement ici
            DrawStepHeader(FontAwesomeIcon.FileAlt, Loc.Get("Guide.TemplateResult.CreateTitle"), Loc.Get("Guide.TemplateResult.Subtitle"), availWidth);

            ImGui.SetWindowFontScale(0.92f);

            if (templateSaved)
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth);
                var checkIcon = FontAwesomeIcon.CheckCircle.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f), checkIcon);
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f),
                    string.Format(Loc.Get("Guide.TemplateResult.Saved"), creatingTemplate?.Name ?? ""));
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Guide.TemplateResult.SavedHint"));
                ImGui.PopTextWrapPos();
            }
            else
            {
                creatingTemplate ??= EventTemplate.CreateDefault();

                var fieldWidth = availWidth;
                var labelColor = new Vector4(0.7f, 0.7f, 0.7f, 1f);

                ImGui.TextColored(labelColor, Loc.Get("Models.Name"));
                ImGui.SetNextItemWidth(fieldWidth);
                var tplName = creatingTemplate.Name;
                if (ImGui.InputText("##setup_tpl_name", ref tplName, 64))
                    creatingTemplate.Name = tplName;

                ImGuiHelpers.ScaledDummy(4f);

                var hpModeLabels = new[] { Loc.Get("Config.HpMode.Percentage"), Loc.Get("Config.HpMode.Points") };
                var halfWidth = (fieldWidth - ImGui.GetStyle().ItemSpacing.X) / 2f;
                var secondColX = ImGui.GetCursorPosX() + halfWidth + ImGui.GetStyle().ItemSpacing.X;

                var heartIcon = FontAwesomeIcon.Heart.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(MasterEventTheme.AttitudeHostile, heartIcon);
                ImGui.SameLine();
                ImGui.TextColored(labelColor, Loc.Get("Config.HpMode"));
                ImGui.SameLine();
                ImGui.SetCursorPosX(secondColX);
                var mpDisabled = !creatingTemplate.ShowMpBar;
                if (mpDisabled) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.4f);
                var magicIcon = FontAwesomeIcon.Magic.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(MasterEventTheme.MpBarColor, magicIcon);
                ImGui.SameLine();
                ImGui.TextColored(labelColor, Loc.Get("Config.MpMode"));
                if (mpDisabled) ImGui.PopStyleVar();

                ImGui.SetNextItemWidth(halfWidth);
                var hpModeIdx = (int)creatingTemplate.HpMode;
                if (ImGui.Combo("##setup_hp_mode", ref hpModeIdx, hpModeLabels, hpModeLabels.Length))
                    creatingTemplate.HpMode = (HpMode)hpModeIdx;
                ImGui.SameLine();
                if (mpDisabled) ImGui.BeginDisabled();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                var mpModeIdx = (int)creatingTemplate.MpMode;
                if (ImGui.Combo("##setup_mp_mode", ref mpModeIdx, hpModeLabels, hpModeLabels.Length))
                    creatingTemplate.MpMode = (HpMode)mpModeIdx;
                if (mpDisabled) ImGui.EndDisabled();

                ImGuiHelpers.ScaledDummy(2f);

                var tplShield = creatingTemplate.ShowShield;
                if (ImGui.Checkbox(Loc.Get("Config.ShowShield") + "##setup_shield", ref tplShield))
                    creatingTemplate.ShowShield = tplShield;
                ImGui.SameLine();
                var showMp = creatingTemplate.ShowMpBar;
                if (ImGui.Checkbox(Loc.Get("Config.ShowMpBar") + "##setup_mp_toggle", ref showMp))
                    creatingTemplate.ShowMpBar = showMp;

                ImGuiHelpers.ScaledDummy(2f);
                ImGui.TextColored(labelColor, Loc.Get("Config.HpMax"));
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                var hpMax = creatingTemplate.DefaultHpMax;
                if (ImGui.InputInt("##setup_hp_max", ref hpMax))
                {
                    if (hpMax < 1) hpMax = 1;
                    if (hpMax > 99999) hpMax = 99999;
                    creatingTemplate.DefaultHpMax = hpMax;
                }

                ImGui.TextColored(labelColor, Loc.Get("Config.PlayerHpMax"));
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                var playerHpMax = creatingTemplate.DefaultPlayerHpMax;
                if (ImGui.InputInt("##setup_player_hp_max", ref playerHpMax))
                {
                    if (playerHpMax < 1) playerHpMax = 1;
                    if (playerHpMax > 99999) playerHpMax = 99999;
                    creatingTemplate.DefaultPlayerHpMax = playerHpMax;
                }

                if (mpDisabled) ImGui.BeginDisabled();
                ImGui.TextColored(labelColor, Loc.Get("Config.MpMax"));
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                var mpMax = creatingTemplate.DefaultMpMax;
                if (ImGui.InputInt("##setup_mp_max", ref mpMax))
                {
                    if (mpMax < 1) mpMax = 1;
                    if (mpMax > 99999) mpMax = 99999;
                    creatingTemplate.DefaultMpMax = mpMax;
                }
                if (mpDisabled) ImGui.EndDisabled();

                ImGuiHelpers.ScaledDummy(2f);

                var diceIcon = FontAwesomeIcon.Dice.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), diceIcon);
                ImGui.SameLine();
                ImGui.TextColored(labelColor, Loc.Get("Dice.Formula"));

                DiceFormulaEditor.Draw(creatingTemplate, "setup");

                ImGuiHelpers.ScaledDummy(2f);
                var initIcon = FontAwesomeIcon.SortNumericDown.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), initIcon);
                ImGui.SameLine();
                ImGui.TextColored(labelColor, Loc.Get("Models.InitiativeStat"));
                ImGui.SameLine();
                ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
                var currentInitStatName = Loc.Get("Models.InitiativeNone");
                if (creatingTemplate.InitiativeStatId != null && creatingTemplate.StatDefinitions != null)
                {
                    var initStat = creatingTemplate.StatDefinitions.FirstOrDefault(s => s.Id == creatingTemplate.InitiativeStatId);
                    if (initStat != null) currentInitStatName = initStat.Name;
                }
                if (ImGui.BeginCombo("##setup_init_stat", currentInitStatName))
                {
                    if (ImGui.Selectable(Loc.Get("Models.InitiativeNone"), creatingTemplate.InitiativeStatId == null))
                        creatingTemplate.InitiativeStatId = null;
                    foreach (var sd in (creatingTemplate.StatDefinitions ?? []).Where(sd => ImGui.Selectable(sd.Name, sd.Id == creatingTemplate.InitiativeStatId)))
                        creatingTemplate.InitiativeStatId = sd.Id;
                    ImGui.EndCombo();
                }

                ImGuiHelpers.ScaledDummy(2f);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(2f);

                var listIcon = FontAwesomeIcon.ListUl.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(MasterEventTheme.AccentColor, listIcon);
                ImGui.SameLine();
                ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Counters"));
                ImGui.SameLine();
                var plusCntIcon = FontAwesomeIcon.Plus.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.SmallButton(plusCntIcon + "##setup_add_cnt"))
                    {
                        creatingTemplate.CounterDefinitions ??= new List<CounterDefinition>();
                        creatingTemplate.CounterDefinitions.Add(new CounterDefinition());
                    }
                }

                if (creatingTemplate.CounterDefinitions is { Count: > 0 } counterDefs)
                {
                    for (var i = 0; i < counterDefs.Count; i++)
                    {
                        var cd = counterDefs[i];
                        ImGui.PushID(3000 + i);
                        ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                        var cdName = cd.Name;
                        if (ImGui.InputText("##cd", ref cdName, 32))
                            cd.Name = cdName;
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(50f * ImGuiHelpers.GlobalScale);
                        var cdMax = cd.DefaultMax;
                        if (ImGui.InputInt("##cdmax", ref cdMax))
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
                            if (ImGui.SmallButton(xIcon + "##cd_del"))
                            {
                                counterDefs.RemoveAt(i);
                                if (counterDefs.Count == 0)
                                    creatingTemplate.CounterDefinitions = null;
                            }
                        }
                        ImGui.PopID();
                    }
                }

                ImGuiHelpers.ScaledDummy(2f);

                var chartIcon = FontAwesomeIcon.ChartBar.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(MasterEventTheme.AccentColor, chartIcon);
                ImGui.SameLine();
                ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Stats"));
                ImGui.SameLine();
                var plusStatIcon = FontAwesomeIcon.Plus.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.SmallButton(plusStatIcon + "##setup_add_stat"))
                    {
                        creatingTemplate.StatDefinitions ??= new List<StatDefinition>();
                        creatingTemplate.StatDefinitions.Add(new StatDefinition());
                    }
                }

                if (creatingTemplate.StatDefinitions is { Count: > 0 } statDefs)
                {
                    for (var i = 0; i < statDefs.Count; i++)
                    {
                        var sd = statDefs[i];
                        ImGui.PushID(2000 + i);
                        ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
                        var sdName = sd.Name;
                        if (ImGui.InputTextWithHint("##sd", Loc.Get("Stat.Name"), ref sdName, 32))
                            sd.Name = sdName;
                        ImGui.SameLine();
                        var xIcon2 = FontAwesomeIcon.Times.ToIconString();
                        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        {
                            if (ImGui.SmallButton(xIcon2 + "##sd_del"))
                            {
                                statDefs.RemoveAt(i);
                                if (statDefs.Count == 0)
                                    creatingTemplate.StatDefinitions = null;
                            }
                        }
                        ImGui.PopID();
                    }
                }

                ImGuiHelpers.ScaledDummy(4f);

                var canSave = !string.IsNullOrWhiteSpace(creatingTemplate.Name);
                if (!canSave) ImGui.BeginDisabled();
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.6f, 0.25f, 1f));
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 12f * ImGuiHelpers.GlobalScale);
                var saveBtnWidth = 180f * ImGuiHelpers.GlobalScale;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - saveBtnWidth) / 2f);
                if (ImGui.Button(Loc.Get("Guide.TemplateResult.Save") + "##setup_save_tpl", new Vector2(saveBtnWidth, 0)))
                {
                    session.SaveTemplate(creatingTemplate);
                    importedTemplateName = creatingTemplate.Name;
                    templateSaved = true;
                }
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(2);
                if (!canSave) ImGui.EndDisabled();
            }

            ImGui.SetWindowFontScale(1f);
        }
    }

    // Étape Fiche

    private void DrawStepSheet(float availWidth)
    {
        DrawStepHeader(FontAwesomeIcon.Scroll, Loc.Get("Guide.Sheet.Title"), Loc.Get("Guide.Sheet.Subtitle"), availWidth);

        ImGui.SetWindowFontScale(0.92f);

        if (sheetSaved)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth);
            var checkIcon = FontAwesomeIcon.CheckCircle.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f), checkIcon);
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f),
                string.Format(Loc.Get("Guide.Sheet.Saved"), creatingSheet?.Name ?? ""));
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Guide.Sheet.SavedHint"));
            ImGui.PopTextWrapPos();
        }
        else
        {
            if (!sheetNameInitialized)
            {
                sheetName = Plugin.ObjectTable.LocalPlayer?.Name.ToString() ?? "";
                if (importedTemplateName != null)
                    sheetTemplateName = importedTemplateName;
                sheetNameInitialized = true;
            }

            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth);
            ImGui.TextWrapped(Loc.Get("Guide.Sheet.SetupIntro"));
            ImGui.PopTextWrapPos();

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Player.ProfileName"));
            ImGui.SetNextItemWidth(availWidth);
            ImGui.InputText("##setup_sheet_name", ref sheetName, 64);

            ImGuiHelpers.ScaledDummy(2f);

            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Player.SelectTemplate"));
            var templateNames = session.GetTemplateNames();
            ImGui.SetNextItemWidth(availWidth);
            if (ImGui.BeginCombo("##setup_sheet_tpl", string.IsNullOrEmpty(sheetTemplateName) ? Loc.Get("Player.SelectTemplate") : sheetTemplateName))
            {
                foreach (var tplName in templateNames.Where(tplName =>
                             ImGui.Selectable(tplName, tplName == sheetTemplateName)))
                {
                    sheetTemplateName = tplName;
                    // Recréer la fiche quand on change de modèle
                    var tpl = session.LoadTemplate(tplName);
                    if (tpl != null)
                        creatingSheet = PlayerSheet.FromTemplate(tpl, sheetName.Trim());
                }

                ImGui.EndCombo();
            }

            if (!string.IsNullOrEmpty(sheetTemplateName) && creatingSheet == null)
            {
                var tpl = session.LoadTemplate(sheetTemplateName);
                if (tpl != null)
                    creatingSheet = PlayerSheet.FromTemplate(tpl, sheetName.Trim());
            }

            if (creatingSheet != null)
            {
                creatingSheet.Name = sheetName.Trim();

                ImGuiHelpers.ScaledDummy(4f);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(4f);

                var heartIcon = FontAwesomeIcon.Heart.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(MasterEventTheme.AttitudeHostile, heartIcon);
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.HpMax"));
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                var hpMax = creatingSheet.HpMax;
                if (ImGui.InputInt("##setup_sheet_hp", ref hpMax))
                {
                    if (hpMax < 1) hpMax = 1;
                    creatingSheet.HpMax = hpMax;
                    creatingSheet.Hp = hpMax;
                }

                var linkedTpl = session.LoadTemplate(creatingSheet.TemplateName);
                var mpDisabled = linkedTpl != null && !linkedTpl.ShowMpBar;
                if (mpDisabled) ImGui.BeginDisabled();
                var magicIcon = FontAwesomeIcon.Magic.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(MasterEventTheme.MpBarColor, magicIcon);
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.MpMax"));
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                var mpMax = creatingSheet.MpMax;
                if (ImGui.InputInt("##setup_sheet_mp", ref mpMax))
                {
                    if (mpMax < 1) mpMax = 1;
                    creatingSheet.MpMax = mpMax;
                    creatingSheet.Mp = mpMax;
                }
                if (mpDisabled) ImGui.EndDisabled();

                if (creatingSheet.Stats is { Count: > 0 })
                {
                    ImGuiHelpers.ScaledDummy(2f);
                    var chartIcon = FontAwesomeIcon.ChartBar.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.AccentColor, chartIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Stats"));

                    foreach (var stat in creatingSheet.Stats)
                    {
                        ImGui.TextUnformatted(stat.Name);
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                        var mod = stat.Modifier;
                        if (ImGui.InputInt($"##setup_stat_{stat.Id}", ref mod))
                            stat.Modifier = mod;
                    }
                }

                if (creatingSheet.Counters is { Count: > 0 })
                {
                    ImGuiHelpers.ScaledDummy(2f);
                    var listIcon = FontAwesomeIcon.ListUl.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.AccentColor, listIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Counters"));

                    foreach (var counter in creatingSheet.Counters)
                    {
                        ImGui.TextUnformatted(counter.Name);
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(60f * ImGuiHelpers.GlobalScale);
                        var val = counter.Value;
                        if (ImGui.InputInt($"##setup_cnt_{counter.Id}", ref val))
                        {
                            if (val < 0) val = 0;
                            if (val > counter.Max) val = counter.Max;
                            counter.Value = val;
                        }
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"/ {counter.Max}");
                    }
                }

                ImGuiHelpers.ScaledDummy(6f);

                var canSave = !string.IsNullOrWhiteSpace(creatingSheet.Name);
                if (!canSave) ImGui.BeginDisabled();

                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.6f, 0.25f, 1f));
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 12f * ImGuiHelpers.GlobalScale);
                if (ImGui.Button(Loc.Get("Guide.Sheet.SaveAndApply") + "##setup_save_apply", new Vector2(availWidth, 0)))
                {
                    session.SavePlayerSheet(creatingSheet);
                    session.ApplyPlayerSheet(creatingSheet);
                    configuration.DefaultSheetName = creatingSheet.Name;
                    configuration.Save();
                    sheetSaved = true;
                }
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(2);

                if (!canSave) ImGui.EndDisabled();
            }
        }

        ImGui.SetWindowFontScale(1f);
    }


    // Étape Test

    private void DrawStepDice(float availWidth)
    {
        DrawStepHeader(FontAwesomeIcon.Dice, Loc.Get("Guide.Dice.Title"), Loc.Get("Guide.Dice.Subtitle"), availWidth);

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth);
        ImGui.TextWrapped(Loc.Get("Guide.Dice.Intro"));
        ImGui.PopTextWrapPos();

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        var localHash = Plugin.GeneratePlayerHash(playerState.ContentId);

        var sheetStats = creatingSheet?.Stats;

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var columns = 3;
        var tileSize = (availWidth - spacing * (columns - 1)) / columns;
        var tileH = tileSize * 0.55f;
        var idx = 0;

        DrawDiceTile(Loc.Get("Dice.NoStat"), null, "setup_roll_simple", tileSize, tileH, () =>
            session.RollDiceForPlayer(localHash));
        idx++;

        if (sheetStats is { Count: > 0 })
        {
            foreach (var stat in sheetStats)
            {
                if (idx % columns != 0)
                    ImGui.SameLine();

                var modStr = stat.Modifier >= 0 ? $"+{stat.Modifier}" : stat.Modifier.ToString();
                var statId = stat.Id;

                DrawDiceTile(stat.Name, modStr, "setup_roll_" + stat.Id, tileSize, tileH, () =>
                    session.RollDiceForPlayer(localHash, statId));
                idx++;
            }
        }

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Dice.History"));
        ImGuiHelpers.ScaledDummy(2f);

        if (session.RollHistory.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Dice.NoHistory"));
        }
        else
        {
            for (var i = 0; i < session.RollHistory.Count && i < 5; i++)
            {
                var roll = session.RollHistory[i];
                var rollModStr = roll.Modifier >= 0 ? $"+{roll.Modifier}" : roll.Modifier.ToString();
                var statInfo = roll.StatName != null ? $" [{roll.StatName} {rollModStr}]" : "";
                var breakdown = roll.IndividualRolls is { Length: > 1 }
                    ? string.Join(" + ", roll.IndividualRolls) + " = "
                    : "";
                var line = $"{roll.RollerName}: {breakdown}{roll.RawRoll}/{roll.DiceMax}{statInfo} = {roll.Total}";

                if (i == 0)
                    ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), line);
                else
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), line);
            }
        }
    }

    private static void DrawDiceTile(string line1, string? line2, string id, float w, float h, Action onClick)
    {
        var rounding = 6f * ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, rounding);

        if (ImGui.Button("##" + id, new Vector2(w, h)))
            onClick();

        var btnMin = ImGui.GetItemRectMin();
        var dlst = ImGui.GetWindowDrawList();

        var lineHeight = ImGui.GetFontSize();
        var totalTextH = line2 != null ? lineHeight * 2f + 2f : lineHeight;
        var textY = btnMin.Y + (h - totalTextH) / 2f;

        var sz1 = ImGui.CalcTextSize(line1);
        var x1 = btnMin.X + (w - sz1.X) / 2f;
        dlst.AddText(new Vector2(x1, textY), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), line1);

        if (line2 != null)
        {
            var sz2 = ImGui.CalcTextSize(line2);
            var x2 = btnMin.X + (w - sz2.X) / 2f;
            dlst.AddText(new Vector2(x2, textY + lineHeight + 2f),
                ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.7f, 1f)), line2);
        }

        ImGui.PopStyleVar();
    }

    // Étape Terminé

    private static void DrawStepDone(float availWidth)
    {
        DrawStepHeader(FontAwesomeIcon.CheckCircle, Loc.Get("Guide.Done.Title"), Loc.Get("Guide.Done.Subtitle"), availWidth);

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth);

        ImGui.TextWrapped(Loc.Get("Guide.Done.Intro"));

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Guide.Done.RecapTitle"));
        ImGuiHelpers.ScaledDummy(6f);

        DrawBullet(null, Loc.Get("Guide.Done.Recap1"));
        DrawBullet(null, Loc.Get("Guide.Done.Recap2"));
        DrawBullet(null, Loc.Get("Guide.Done.Recap3"));

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Guide.Done.Hint"));

        ImGui.PopTextWrapPos();
    }


    private void DrawBottomBar(float availWidth)
    {
        var dotRadius = 4f * ImGuiHelpers.GlobalScale;
        var dotSpacing = 14f * ImGuiHelpers.GlobalScale;
        var totalDotsWidth = StepCount * dotRadius * 2f + (StepCount - 1) * dotSpacing;
        var dotsStartX = ImGui.GetCursorScreenPos().X + (availWidth - totalDotsWidth) / 2f;
        var dotsY = ImGui.GetCursorScreenPos().Y + dotRadius + 2f * ImGuiHelpers.GlobalScale;

        ImGui.Dummy(new Vector2(0, dotRadius * 2f + 4f * ImGuiHelpers.GlobalScale));
        var dl = ImGui.GetWindowDrawList();
        for (var i = 0; i < StepCount; i++)
        {
            var cx = dotsStartX + i * (dotRadius * 2f + dotSpacing) + dotRadius;
            var color = i == step
                ? MasterEventTheme.AccentColor
                : i < step
                    ? MasterEventTheme.AccentColor with { W = 0.4f }
                    : new Vector4(0.3f, 0.3f, 0.3f, 1f);
            dl.AddCircleFilled(new Vector2(cx, dotsY), dotRadius, ImGui.GetColorU32(color));
        }

        ImGuiHelpers.ScaledDummy(4f);

        var btnWidth = 120f * ImGuiHelpers.GlobalScale;

        if (step > 0)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 12f * ImGuiHelpers.GlobalScale);
            if (ImGui.Button(Loc.Get("Guide.Nav.Previous") + "##setup_prev", new Vector2(btnWidth, 0)))
                step--;
            ImGui.PopStyleVar();
        }
        else
        {
            ImGui.Dummy(new Vector2(btnWidth, 0));
        }

        ImGui.SameLine();

        var nextBtnX = availWidth - btnWidth;
        if (nextBtnX > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(nextBtnX);

        if (step < StepCount - 1)
        {
            var nextDisabled = (step == 1 && !configuration.IsRgpdConsentValid)
                            || (step == 3 && importedTemplateName == null && !templateSaved)
                            || (step == 4 && !sheetSaved);
            if (nextDisabled) ImGui.BeginDisabled();

            ImGui.PushStyleColor(ImGuiCol.Button, MasterEventTheme.AccentColor with { W = 0.8f });
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MasterEventTheme.AccentColor);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 12f * ImGuiHelpers.GlobalScale);
            if (ImGui.Button(Loc.Get("Guide.Nav.Next") + "##setup_next", new Vector2(btnWidth, 0)))
                step++;
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);

            if (nextDisabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.BeginTooltip();
                var tooltipKey = step switch
                {
                    1 => "Guide.Rgpd.NextDisabled",
                    3 => "Guide.TemplateResult.NextDisabled",
                    _ => "Guide.Sheet.NextDisabled",
                };
                ImGui.TextUnformatted(Loc.Get(tooltipKey));
                ImGui.EndTooltip();
            }

            if (nextDisabled) ImGui.EndDisabled();
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.6f, 0.25f, 1f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 12f * ImGuiHelpers.GlobalScale);
            if (ImGui.Button(Loc.Get("Guide.Nav.Finish") + "##setup_finish", new Vector2(btnWidth, 0)))
            {
                IsOpen = false;
                onFinished?.Invoke();
            }
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
        }
    }


    private static void DrawStepHeader(FontAwesomeIcon icon, string title, string subtitle, float availWidth)
    {
        ImGuiHelpers.ScaledDummy(8f);

        var iconStr = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr);
        const float scale = 1.4f;
        var scaledSz = iconSz * scale;
        var pos = ImGui.GetCursorScreenPos();
        var iconX = pos.X + (availWidth - scaledSz.X) / 2f;
        ImGui.Dummy(new Vector2(0, scaledSz.Y));
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(iconX, pos.Y), ImGui.GetColorU32(MasterEventTheme.AccentColor), iconStr);
        ImGui.PopFont();

        ImGuiHelpers.ScaledDummy(4f);

        var titleSz = ImGui.CalcTextSize(title);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
        ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), title);

        ImGuiHelpers.ScaledDummy(2f);

        var descSz = ImGui.CalcTextSize(subtitle);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - descSz.X) / 2f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), subtitle);

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
    }

    private static void DrawField(FontAwesomeIcon icon, Vector4 iconColor, string label, string description)
    {
        var iconStr = icon.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(iconColor, iconStr);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1f), label);
        ImGui.Indent(24f * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), description);
        ImGui.Unindent(24f * ImGuiHelpers.GlobalScale);
        ImGuiHelpers.ScaledDummy(2f);
    }

    private static void DrawBullet(string? prefix, string text)
    {
        if (prefix != null)
        {
            ImGui.TextColored(MasterEventTheme.AccentColor, prefix);
            ImGui.SameLine();
        }
        else
        {
            var bulletIcon = FontAwesomeIcon.ChevronRight.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.AccentColor, bulletIcon);
            ImGui.SameLine();
        }
        ImGui.TextWrapped(text);
        ImGuiHelpers.ScaledDummy(2f);
    }
}
