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
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Onglet « Fiches » : fiches de personnage, règles de la salle et modèles réunis sur une seule page.
// Une fiche est une instance d'un modèle ; les séparer obligeait à changer d'onglet pour voir l'effet
// d'une modification, et dupliquait l'import par code.
public sealed partial class GmWindow
{
    private enum SheetsCreateMode { None, Sheet, Template }

    // Deux catégories : les personnages (fiches) et les modèles (règles). Le joueur ne voit que la première.
    private enum SheetsCategory { Characters, Templates }

    private SheetsCategory sheetsCategory;
    private SheetsCreateMode sheetsCreateMode;
    private int editingTemplateSheetCount;
    private bool modelsImportFailed;
    private const string TemplateEditorUnsubscribeId = "tpl_editor_unsub";
    private static string SheetDeleteId(string name) => "sheet_del_" + name;
    private static string TemplateDeleteId(string name) => "tpl_del_" + name;
    private static string UnsubscribeId(string name) => "tpl_unsub_" + name;
    private static string SharedRemoveId(string code) => "shared_del_" + code;

    private void DrawSheetsContent()
    {
        if (editingProfile != null)
        {
            DrawSheetEditorPage();
            return;
        }

        if (editingTemplate != null)
        {
            if (HasGmAccess())
            {
                DrawTemplateEditorPage();
                return;
            }

            // Droits MJ perdus pendant l'édition : on referme proprement.
            editingTemplate = null;
            editingTemplateName = null;
        }

        if (ImGui.BeginChild("##sheets_scroll", Vector2.Zero))
            DrawSheetsMain();
        ImGui.EndChild();
    }

    private void DrawSheetsMain()
    {
        var gmAccess = HasGmAccess();

        LayoutControls.DrawTabHeader(FontAwesomeIcon.Scroll, Loc.Get("Sheets.Title"), Loc.Get("Sheets.Subtitle"));

        if (!gmAccess) sheetsCategory = SheetsCategory.Characters;

        var characters = sheetsCategory == SheetsCategory.Characters;
        var wanted = characters ? SheetsCreateMode.Sheet : SheetsCreateMode.Template;
        var creating = sheetsCreateMode == wanted;

        var addLabel = creating
            ? Loc.Get("Gm.Cancel")
            : "+  " + Loc.Get(characters ? "Sheets.NewSheet" : "Sheets.NewTemplate");

        var labels = gmAccess
            ? new[] { Loc.Get("Sheets.CategoryCharacters"), Loc.Get("Sheets.CategoryTemplates") }
            : null;
        var leftText = gmAccess ? null : Loc.Get("Sheets.CategoryCharacters");

        var (selected, addClicked, _) = DrawSegmentedToolbar("sheets_bar", labels, (int)sheetsCategory,
            addLabel, creating, leftText);

        if (selected != (int)sheetsCategory)
        {
            sheetsCategory = (SheetsCategory)selected;
            sheetsCreateMode = SheetsCreateMode.None;
            characters = sheetsCategory == SheetsCategory.Characters;
            wanted = characters ? SheetsCreateMode.Sheet : SheetsCreateMode.Template;
            creating = false;
        }

        if (addClicked)
        {
            sheetsCreateMode = creating ? SheetsCreateMode.None : wanted;
            creating = !creating;
        }

        ImGuiHelpers.ScaledDummy(8f);
        DrawTabNotice(Tab.Profiles);

        // Le panneau de création s'ouvre sous la barre, dans sa propre carte.
        if (creating)
        {
            LayoutControls.BeginCard(
                Loc.Get(characters ? "Sheets.NewSheet" : "Sheets.NewTemplate"),
                characters ? FontAwesomeIcon.UserPlus : FontAwesomeIcon.Plus);
            if (characters) DrawNewSheetPanel();
            else DrawNewTemplatePanel();
            LayoutControls.EndCard();
        }

        if (characters)
        {
            DrawSheetsInGameCard();
            DrawMySheetsCard(gmAccess);
        }
        else
        {
            DrawRulesCard();
            DrawSharedCodesCard();
        }
    }

    private static int DrawSegmentedControl(string id, string[] labels, int selected, float? fixedWidth = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = fixedWidth ?? ImGui.GetContentRegionAvail().X - 12f * scale;
        var height = ImGui.GetFrameHeight() * 1.2f;
        var inset = 3f * scale;
        var origin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        ImGui.Dummy(new Vector2(width, height));
        var end = ImGui.GetCursorScreenPos();

        dl.AddRectFilled(origin, origin + new Vector2(width, height),
            ImGui.GetColorU32(MasterEventTheme.TextStrong with { W = 0.07f }), height * 0.5f);

        var segmentWidth = (width - inset * 2f) / labels.Length;
        for (var i = 0; i < labels.Length; i++)
        {
            var min = origin + new Vector2(inset + segmentWidth * i, inset);
            var size = new Vector2(segmentWidth, height - inset * 2f);

            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"##{id}_{i}", size)) selected = i;
            var hovered = ImGui.IsItemHovered();

            var active = i == selected;
            if (active)
            {
                dl.AddRectFilled(min, min + size, ImGui.GetColorU32(MasterEventTheme.AccentColor with { W = 0.75f }),
                    size.Y * 0.5f);
            }
            else if (hovered)
            {
                dl.AddRectFilled(min, min + size, ImGui.GetColorU32(MasterEventTheme.TextStrong with { W = 0.08f }), size.Y * 0.5f);
            }

            var textSize = ImGui.CalcTextSize(labels[i]);
            var color = active ? MasterEventTheme.TextStrong : MasterEventTheme.TextSecondary;
            dl.AddText(min + (size - textSize) * 0.5f, ImGui.GetColorU32(color), labels[i]);
        }

        ImGui.SetCursorScreenPos(end);
        return selected;
    }

    // Barre d'outils commune à Fiches et Terrain : commutateur segmenté à gauche (ou un simple titre
    // quand il n'y a qu'une catégorie), l'unique action principale à droite.
    private static (int Selected, bool AddClicked, Vector2 AddMax) DrawSegmentedToolbar(
        string id, string[]? labels, int selected, string addLabel, bool addQuiet = false, string? leftText = null)
    {
        var style = ImGui.GetStyle();
        var totalWidth = ImGui.GetContentRegionAvail().X - CardRightMargin;
        var height = ImGui.GetFrameHeight() * 1.2f;
        var addWidth = ImGui.CalcTextSize(addLabel).X + style.FramePadding.X * 4f;
        var origin = ImGui.GetCursorScreenPos();

        if (labels != null)
        {
            var filterWidth = totalWidth - addWidth - style.ItemSpacing.X;
            selected = DrawSegmentedControl(id, labels, selected, filterWidth);
        }
        else if (leftText != null)
        {
            var textHeight = ImGui.GetTextLineHeight();
            ImGui.SetCursorScreenPos(origin + new Vector2(0, (height - textHeight) / 2f));
            ImGui.TextColored(MasterEventTheme.TextSecondary, leftText);
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X + totalWidth - addWidth, origin.Y));
        var clicked = addQuiet
            ? QuietButton(addLabel + "##" + id + "_add", addWidth, height)
            : PrimaryButton(addLabel + "##" + id + "_add", addWidth, height);
        var addMax = ImGui.GetItemRectMax();

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height));
        return (selected, clicked, addMax);
    }

    private static bool PrimaryButton(string label, float width = 0f, float height = 0f)
        => Buttons.Primary(label, width, height);

    private static bool TintedButton(string label, float width = 0f)
        => Buttons.Tinted(label, width);

    private static bool QuietButton(string label, float width = 0f, float height = 0f)
        => Buttons.Quiet(label, width, height);

    private static bool QuietIconButton(FontAwesomeIcon icon, string id, string tooltip)
    {
        return Buttons.QuietIcon(icon, id, tooltip);
    }

    private static readonly TimeSpan TabNoticeLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TabErrorLifetime = TimeSpan.FromSeconds(12);
    private Tab tabNoticeTab;
    private string? tabNoticeText;
    private bool tabNoticeIsError;
    private long tabNoticeAt;

    private void ShowTabNotice(Tab tab, string text, bool isError = false)
    {
        tabNoticeTab = tab;
        tabNoticeIsError = isError;
        tabNoticeAt = Environment.TickCount64;
        tabNoticeText = text;
    }

    private void DrawTabNotice(Tab tab)
    {
        if (tabNoticeText is not { } text || tabNoticeTab != tab) return;

        var lifetime = tabNoticeIsError ? TabErrorLifetime : TabNoticeLifetime;
        var age = (Environment.TickCount64 - tabNoticeAt) / lifetime.TotalMilliseconds;
        if (age >= 1d)
        {
            tabNoticeText = null;
            return;
        }

        var alpha = (float)(1d - age * age);
        if (tabNoticeIsError)
            LayoutControls.DrawNotice(text, MasterEventTheme.DangerColor, FontAwesomeIcon.ExclamationTriangle, alpha);
        else
            LayoutControls.DrawNotice(text, MasterEventTheme.SuccessColor, FontAwesomeIcon.CheckCircle, alpha);
        ImGuiHelpers.ScaledDummy(4f);
    }


    private void DrawNewSheetPanel()
    {
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.SetNextItemWidth(LayoutControls.CardContentWidth);
        ImGui.InputTextWithHint("##profile_name", Loc.Get("Sheets.SheetNameHint"), ref newProfileName, 64);

        var templateNames = session.GetTemplateNames();
        if (string.IsNullOrEmpty(selectedTemplateName) || !templateNames.Contains(selectedTemplateName))
            selectedTemplateName = PreferredTemplateName(templateNames);

        ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.PickTemplate"));
        DrawTemplateChips(templateNames);

        ImGuiHelpers.ScaledDummy(2f);
        var canCreate = !string.IsNullOrWhiteSpace(newProfileName) && !string.IsNullOrEmpty(selectedTemplateName);
        ImGui.BeginDisabled(!canCreate);
        if (PrimaryButton(Loc.Get("Sheets.CreateSheet") + "##do_create_sheet", LayoutControls.CardContentWidth))
        {
            var tpl = session.LoadTemplate(selectedTemplateName);
            if (tpl != null)
            {
                editingProfile = PlayerSheet.FromTemplate(tpl, newProfileName.Trim());
                editingDirty = true;
                newProfileName = string.Empty;
                sheetsCreateMode = SheetsCreateMode.None;
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawNewTemplatePanel()
    {
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.SetNextItemWidth(LayoutControls.CardContentWidth);
        ImGui.InputTextWithHint("##new_template_name", Loc.Get("Sheets.TemplateNameHint"), ref newTemplateName, 64);

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(newTemplateName));
        if (PrimaryButton(Loc.Get("Sheets.CreateTemplate") + "##do_create_template", LayoutControls.CardContentWidth))
        {
            var template = EventTemplate.CreateDefault();
            template.Name = newTemplateName.Trim();
            editingTemplate = template;
            editingTemplateName = template.Name;
            editingTemplateSheetCount = 0;
            newTemplateName = string.Empty;
            sheetsCreateMode = SheetsCreateMode.None;
        }
        ImGui.EndDisabled();
    }

    private void DrawCodeImportRow()
    {
        var importLabel = Loc.Get("Sheets.ImportCode");
        var buttonWidth = TextButtonWidth(importLabel);
        var inputWidth = LayoutControls.CardContentWidth - buttonWidth - ImGui.GetStyle().ItemSpacing.X;

        ImGui.BeginDisabled(importInProgress);
        ImGui.SetNextItemWidth(Math.Max(inputWidth, 80f * ImGuiHelpers.GlobalScale));
        if (ImGui.InputTextWithHint("##import_code", Loc.Get("Models.ImportCode"), ref importCode, 16))
            modelsImportFailed = false;
        ImGui.SameLine();

        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(importCode));
        if (TintedButton(importLabel + "##do_import", buttonWidth))
        {
            importInProgress = true;
            modelsImportedName = null;
            modelsImportFailed = false;
            var code = importCode.Trim();
            _ = Task.Run(async () =>
            {
                var template = await SessionManager.ImportTemplateAsync(code, configuration.RelayServerUrl);
                importInProgress = false;
                if (template != null)
                {
                    session.SaveTemplate(template);
                    importCode = string.Empty;
                    modelsImportedName = template.Name;
                }
                else
                {
                    modelsImportFailed = true;
                }
            });
        }
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        if (importInProgress)
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Models.Importing"));
        else if (modelsImportFailed)
            ImGui.TextColored(MasterEventTheme.DangerColor, Loc.Get("Models.ImportError"));
        else if (modelsImportedName != null)
            ImGui.TextColored(MasterEventTheme.SuccessColor, string.Format(Loc.Get("Models.Imported"), modelsImportedName));
    }

    private string PreferredTemplateName(IReadOnlyList<string> names)
    {
        if (session.ActiveTemplate is { } active && names.Contains(active.Name)) return active.Name;
        if (names.Contains(configuration.DefaultTemplateName)) return configuration.DefaultTemplateName;
        return names.FirstOrDefault() ?? string.Empty;
    }

    // Modèles sous forme de pastilles : un clic au lieu d'ouvrir une liste déroulante.
    private void DrawTemplateChips(IReadOnlyList<string> names)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var maxWidth = LayoutControls.CardContentWidth;
        var lineWidth = 0f;

        for (var i = 0; i < names.Count; i++)
        {
            var width = TextButtonWidth(names[i]);
            if (i > 0)
            {
                if (lineWidth + spacing + width <= maxWidth)
                {
                    ImGui.SameLine();
                    lineWidth += spacing;
                }
                else
                {
                    lineWidth = 0f;
                }
            }

            if (ToggleButton(names[i] + "##chip_" + i, names[i] == selectedTemplateName))
                selectedTemplateName = names[i];
            lineWidth += width;
        }
    }

    private static bool ToggleButton(string label, bool active, float width = 0f)
    {
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, MasterEventTheme.AccentColor with { W = 0.55f });
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MasterEventTheme.AccentColor with { W = 0.7f });
        }

        var clicked = ImGui.Button(label, new Vector2(width, 0));
        if (active) ImGui.PopStyleColor(2);
        return clicked;
    }

    // ─── Ma fiche en jeu ─────────────────────────────────────────────────────────────────

    private void DrawSheetsInGameCard()
    {
        LayoutControls.BeginCard(Loc.Get("Sheets.InGame"), FontAwesomeIcon.User);

        var loadedName = session.LoadedSheetName;
        var loaded = loadedName != null ? session.LoadPlayerSheet(loadedName) : null;

        ImGui.AlignTextToFramePadding();
        if (loaded != null)
        {
            ImGui.TextColored(MasterEventTheme.TextStrong, loaded.Name);
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.TextDim, $"[{loaded.TemplateName}]");
        }
        else
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.NoneLoaded"));
        }

        var me = session.PartyMembers.FirstOrDefault(p => p.Hash == session.LocalPlayerHash);
        if (me != null)
        {
            var vitals = $"{VitalLabels.Hp} {me.Hp}/{me.HpMax}";
            if (session.ShowMpBar) vitals += $"    {VitalLabels.Mp} {me.Mp}/{me.MpMax}";
            ImGui.TextColored(MasterEventTheme.MutedTextColor, vitals);
        }

        ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.RoomRules") + " :");
        ImGui.SameLine();
        if (session.ActiveTemplate is { } rules)
            ImGui.TextColored(MasterEventTheme.TextStrong, rules.Name);
        else
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.NoRules"));

        LayoutControls.EndCard();
    }

    // ─── Mes fiches ──────────────────────────────────────────────────────────────────────

    private void DrawMySheetsCard(bool gmAccess)
    {
        var names = session.GetPlayerSheetNames();
        LayoutControls.BeginCard(string.Format(Loc.Get("Sheets.MySheetsFmt"), names.Count), FontAwesomeIcon.Users);

        if (names.Count == 0)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Player.NoProfiles"));
        }
        else
        {
            foreach (var name in names)
            {
                var sheet = session.LoadPlayerSheet(name);
                if (sheet != null) DrawSheetRow(name, sheet);
            }
        }

        // Le joueur n'a pas de catégorie « Modèles » : il importe un modèle par code depuis ici.
        if (!gmAccess)
        {
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);
            DrawCodeImportRow();
        }

        LayoutControls.EndCard();
    }

    private void DrawSheetRow(string name, PlayerSheet sheet)
    {
        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var isDefault = configuration.GetDefaultSheetName() == name;
        var isLoaded = session.LoadedSheetName == name;
        var rowStartX = ImGui.GetCursorPosX();

        ImGui.PushID("sheet_" + name);

        var deleteId = SheetDeleteId(name);
        if (ConfirmInline.IsOpen(deleteId))
        {
            if (ConfirmInline.DrawQuestion(deleteId, string.Format(Loc.Get("Sheets.ConfirmDeleteFmt"), name),
                    Loc.Get("Sheets.Delete")))
            {
                session.DeletePlayerSheet(name);
                if (configuration.GetDefaultSheetName() == name)
                {
                    configuration.SetDefaultSheetName(null);
                    configuration.Save();
                }
                ShowTabNotice(Tab.Profiles, string.Format(Loc.Get("Player.ProfileDeleted"), name));
            }

            ImGui.PopID();
            ImGui.Spacing();
            return;
        }

        var leftWidth = 0f;
        ImGui.AlignTextToFramePadding();
        if (DrawPortraitThumbnail(name))
        {
            leftWidth += ImGui.GetFrameHeight() + 4f * ImGuiHelpers.GlobalScale;
        }
        else
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.PlayerColor, FontAwesomeIcon.User.ToIconString());
            ImGui.SameLine();
            leftWidth += IconWidth(FontAwesomeIcon.User) + spacing;
        }

        ImGui.TextUnformatted(name);
        leftWidth += ImGui.CalcTextSize(name).X;

        var tag = $"[{sheet.TemplateName}]";
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.TextDim, tag);
        leftWidth += spacing + ImGui.CalcTextSize(tag).X;

        if (isLoaded)
        {
            var label = Loc.Get("Sheets.Loaded");
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.SuccessColor, label);
            leftWidth += spacing + ImGui.CalcTextSize(label).X;
        }

        if (isDefault)
        {
            ImGui.SameLine();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.AccentText, FontAwesomeIcon.Star.ToIconString());
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Sheets.StartupTooltip"));
            leftWidth += spacing + IconWidth(FontAwesomeIcon.Star);
        }

        var loadLabel = Loc.Get("Sheets.Load");
        var editLabel = Loc.Get("Sheets.Edit");
        var buttonsWidth = TextButtonWidth(loadLabel) + spacing + TextButtonWidth(editLabel)
                           + spacing + IconButtonWidth(FontAwesomeIcon.EllipsisH);
        AlignRowRight(rowStartX, leftWidth, buttonsWidth);

        // Charger est l'action la plus fréquente d'une ligne ; une fiche déjà chargée le montre déjà.
        var load = isLoaded ? QuietButton(loadLabel + "##load") : TintedButton(loadLabel + "##load");
        if (load)
        {
            session.ApplyPlayerSheet(sheet);
            ShowTabNotice(Tab.Profiles, string.Format(Loc.Get("Player.ProfileLoaded"), name));
        }
        ImGui.SameLine();

        if (QuietButton(editLabel + "##edit"))
        {
            editingProfile = sheet.DeepCopy();
            editingDirty = false;

            // Un profil créé avant l'ajout d'une stat au modèle ne la montrerait jamais :
            // on réaligne la copie en édition, la sauvegarde la fera persister.
            if (session.LoadTemplate(editingProfile.TemplateName) is { } linked
                && TemplateSyncHelper.SyncSheetWithTemplate(editingProfile, linked).HasChanges)
                editingDirty = true;
        }
        ImGui.SameLine();

        if (QuietIconButton(FontAwesomeIcon.EllipsisH, "menu", Loc.Get("Sheets.More")))
            ImGui.OpenPopup("##sheet_menu");

        if (ImGui.BeginPopup("##sheet_menu"))
        {
            var startupLabel = isDefault ? Loc.Get("Sheets.UnsetStartup") : Loc.Get("Sheets.SetStartup");
            if (ImGui.Selectable(startupLabel))
            {
                configuration.SetDefaultSheetName(isDefault ? null : name);
                configuration.Save();
            }

            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.DangerColor);
            if (ImGui.Selectable(Loc.Get("Sheets.Delete")))
                ConfirmInline.Open(deleteId);
            ImGui.PopStyleColor();
            ImGui.EndPopup();
        }

        ImGui.PopID();
        ImGui.Spacing();
    }

    // ─── Règles et modèles (MJ) ──────────────────────────────────────────────────────────

    private void DrawRulesCard()
    {
        LayoutControls.BeginCard(Loc.Get("Sheets.Rules"), FontAwesomeIcon.FileAlt);

        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;

        // Règles actuellement appliquées à la salle.
        var rowStartX = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.RoomRules") + " :");
        ImGui.SameLine();
        if (session.ActiveTemplate is { } active)
        {
            ImGui.TextColored(MasterEventTheme.TextStrong, active.Name);
            var leftWidth = ImGui.CalcTextSize(Loc.Get("Sheets.RoomRules") + " :").X + spacing
                            + ImGui.CalcTextSize(active.Name).X;

            var rebroadcast = Loc.Get("Sheets.Rebroadcast");
            var clear = Loc.Get("Sheets.Deactivate");
            AlignRowRight(rowStartX, leftWidth, TextButtonWidth(rebroadcast) + spacing + TextButtonWidth(clear));

            if (QuietButton(rebroadcast + "##rebroadcast"))
            {
                session.BroadcastTemplate();
                session.BroadcastUpdate();
            }
            ImGui.SameLine();
            if (QuietButton(clear + "##deactivate"))
            {
                session.ClearActiveTemplate();
                configuration.SetActiveTemplateName(string.Empty);
                configuration.Save();
            }
        }
        else
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.NoRules"));
        }

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        var all = session.GetTemplateNames()
            .Select(n => (Name: n, Template: session.LoadTemplate(n)))
            .Where(t => t.Template != null)
            .ToList();

        if (all.Count == 0)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Models.None"));
        }
        else
        {
            foreach (var (name, template) in all.Where(t => !t.Template!.IsSubscription))
                DrawOwnTemplateRow(name, template!);

            var subscribed = all.Where(t => t.Template!.IsSubscription).ToList();
            if (subscribed.Count > 0)
            {
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.TextColored(MasterEventTheme.AccentText, Loc.Get("Models.SubscribedSection"));
                ImGui.Spacing();
                foreach (var (name, template) in subscribed)
                    DrawSubscribedTemplateRow(name, template!);
            }
        }

        // Import d'un modèle par code, discret en pied de carte.
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
        DrawCodeImportRow();

        // Résultat du dernier partage : le code est aussi copié dans le presse-papiers.
        if (exportInProgress)
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Models.Exporting"));
        if (lastExportCode != null)
        {
            ImGuiHelpers.ScaledDummy(2f);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(MasterEventTheme.SuccessColor, string.Format(Loc.Get("Models.ExportCode"), lastExportCode));
            ImGui.SameLine();
            if (QuietIconButton(FontAwesomeIcon.Copy, "copy_export_code", Loc.Get("Models.SharedCopy")))
                ImGui.SetClipboardText(lastExportCode);
        }

        LayoutControls.EndCard();
    }

    private int CountSheetsUsing(string templateName)
        => session.GetPlayerSheetNames().Count(n =>
            string.Equals(session.LoadPlayerSheet(n)?.TemplateName, templateName, StringComparison.Ordinal));

    private void DrawOwnTemplateRow(string tplName, EventTemplate tpl)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var isDefault = string.Equals(tplName, configuration.DefaultTemplateName, StringComparison.OrdinalIgnoreCase);
        var isActive = string.Equals(session.ActiveTemplate?.Name, tplName, StringComparison.Ordinal);
        var canDelete = tplName != "Standard";
        var rowStartX = ImGui.GetCursorPosX();

        ImGui.PushID("tpl_" + tplName);

        // Suppression demandée depuis le menu : la ligne devient la question.
        var deleteId = TemplateDeleteId(tplName);
        if (ConfirmInline.IsOpen(deleteId))
        {
            if (ConfirmInline.DrawQuestion(deleteId, string.Format(Loc.Get("Sheets.ConfirmDeleteFmt"), tplName),
                    Loc.Get("Sheets.Delete")))
            {
                session.DeleteTemplate(tplName);
                if (session.ActiveTemplate?.Name == tplName)
                {
                    session.ClearActiveTemplate();
                    configuration.SetActiveTemplateName(string.Empty);
                    configuration.Save();
                }
                if (isDefault)
                {
                    configuration.DefaultTemplateName = "Standard";
                    configuration.Save();
                }
            }

            ImGui.PopID();
            ImGui.Spacing();
            return;
        }

        var leftWidth = 0f;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(tplName);
        leftWidth += ImGui.CalcTextSize(tplName).X;

        if (isActive)
        {
            var label = Loc.Get("Sheets.RoomBadge");
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.SuccessColor, label);
            leftWidth += spacing + ImGui.CalcTextSize(label).X;
        }

        if (!string.IsNullOrEmpty(tpl.SourceCode))
        {
            var label = string.Format(Loc.Get("Sheets.PublishedFmt"), tpl.SourceVersion);
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.MutedTextColor, label);
            leftWidth += spacing + ImGui.CalcTextSize(label).X;
        }

        if (isDefault)
        {
            ImGui.SameLine();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.AccentText, FontAwesomeIcon.Star.ToIconString());
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Models.DefaultTooltip"));
            leftWidth += spacing + IconWidth(FontAwesomeIcon.Star);
        }

        var activateLabel = isActive ? Loc.Get("Sheets.Active") : Loc.Get("Sheets.Activate");
        var editLabel = Loc.Get("Sheets.Edit");
        var buttonsWidth = TextButtonWidth(activateLabel) + spacing + TextButtonWidth(editLabel)
                           + spacing + IconButtonWidth(FontAwesomeIcon.EllipsisH);
        AlignRowRight(rowStartX, leftWidth, buttonsWidth);

        // Activer est l'action la plus fréquente ; un modèle déjà actif l'affiche en texte, sans bouton.
        if (isActive)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(MasterEventTheme.SuccessColor, activateLabel);
        }
        else if (TintedButton(activateLabel + "##activate"))
        {
            session.ApplyTemplate(tpl);
            session.BroadcastTemplate();
            session.BroadcastUpdate();
            configuration.SetActiveTemplateName(tpl.Name);
            configuration.Save();

            // Le modèle activé charge la première fiche qui l'utilise.
            session.LoadFirstSheetFor(tpl.Name);
        }
        ImGui.SameLine();

        if (QuietButton(editLabel + "##edit"))
        {
            editingTemplate = tpl.DeepCopy();
            editingTemplateName = tpl.Name;
            editingTemplateSheetCount = CountSheetsUsing(tpl.Name);
        }
        ImGui.SameLine();

        if (QuietIconButton(FontAwesomeIcon.EllipsisH, "menu", Loc.Get("Sheets.More")))
            ImGui.OpenPopup("##tpl_menu");

        var alreadyShared = session.GetSharedTemplates().Any(x => x.TemplateName == tplName);
        var openExport = false;

        if (ImGui.BeginPopup("##tpl_menu"))
        {
            ImGui.BeginDisabled(exportInProgress || alreadyShared);
            if (ImGui.Selectable(Loc.Get("Models.Share")))
                openExport = true;
            ImGui.EndDisabled();
            if (alreadyShared && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Loc.Get("Models.ExportAlreadyShared"));

            ImGui.BeginDisabled(isDefault);
            if (ImGui.Selectable(Loc.Get("Models.SetDefault")))
            {
                configuration.DefaultTemplateName = tplName;
                configuration.Save();
            }
            ImGui.EndDisabled();

            if (canDelete)
            {
                ImGui.Separator();
                ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.DangerColor);
                if (ImGui.Selectable(Loc.Get("Sheets.Delete")))
                    ConfirmInline.Open(deleteId);
                ImGui.PopStyleColor();
            }
            ImGui.EndPopup();
        }

        // La fenêtre de partage s'ouvre au niveau de la ligne, pas dans le menu qui vient de se refermer.
        DrawExportPopup(tplName, openExport);

        ImGui.PopID();
        ImGui.Spacing();
    }

    // Modèle abonné (importé d'un créateur) : lecture seule, on peut l'activer ou s'en désabonner.
    private void DrawSubscribedTemplateRow(string tplName, EventTemplate tpl)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var isActive = string.Equals(session.ActiveTemplate?.Name, tplName, StringComparison.Ordinal);
        var rowStartX = ImGui.GetCursorPosX();
        var version = $"v{tpl.SourceVersion}";

        ImGui.PushID("sub_" + tplName);

        // Désabonnement demandé : la ligne devient la question.
        var unsubscribeId = UnsubscribeId(tplName);
        if (ConfirmInline.IsOpen(unsubscribeId))
        {
            if (ConfirmInline.DrawQuestion(unsubscribeId, string.Format(Loc.Get("Models.UnsubscribeConfirmFmt"), tplName),
                    Loc.Get("Models.Unsubscribe")))
                UnsubscribeTemplate(tplName);

            ImGui.PopID();
            ImGui.Spacing();
            return;
        }

        ImGui.AlignTextToFramePadding();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.PendingColor, FontAwesomeIcon.Lock.ToIconString());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Format(Loc.Get("Models.ReadOnlyBanner"), tpl.SourceCode, tpl.SourceVersion));
        ImGui.SameLine();

        ImGui.TextUnformatted(tplName);
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.MutedTextColor, version);

        var leftWidth = IconWidth(FontAwesomeIcon.Lock) + spacing + ImGui.CalcTextSize(tplName).X
                        + spacing + ImGui.CalcTextSize(version).X;
        if (isActive)
        {
            var label = Loc.Get("Sheets.RoomBadge");
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.SuccessColor, label);
            leftWidth += spacing + ImGui.CalcTextSize(label).X;
        }

        var activateLabel = isActive ? Loc.Get("Sheets.Active") : Loc.Get("Sheets.Activate");
        var unsubLabel = Loc.Get("Models.Unsubscribe");
        AlignRowRight(rowStartX, leftWidth, TextButtonWidth(activateLabel) + spacing + TextButtonWidth(unsubLabel));

        if (isActive)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(MasterEventTheme.SuccessColor, activateLabel);
        }
        else if (TintedButton(activateLabel + "##activate"))
        {
            session.ApplyTemplate(tpl);
            session.BroadcastTemplate();
            session.BroadcastUpdate();
            configuration.SetActiveTemplateName(tpl.Name);
            configuration.Save();

            // Le modèle activé charge la première fiche qui l'utilise.
            session.LoadFirstSheetFor(tpl.Name);
        }
        ImGui.SameLine();

        if (Buttons.Destructive(unsubLabel + "##unsub"))
            ConfirmInline.Open(unsubscribeId);

        ImGui.PopID();
        ImGui.Spacing();
    }

    // Retire un modèle abonné ; s'il réglait la salle, la salle n'a plus de règles.
    private void UnsubscribeTemplate(string tplName)
    {
        session.DeleteTemplate(tplName);
        if (session.ActiveTemplate?.Name == tplName)
        {
            session.ClearActiveTemplate();
            configuration.SetActiveTemplateName(string.Empty);
            configuration.Save();
        }
    }

    // Codes des modèles que ce MJ a publiés.
    private void DrawSharedCodesCard()
    {
        var sharedTemplates = session.GetSharedTemplates();
        if (sharedTemplates.Count > 0)
        {
            LayoutControls.BeginCard(Loc.Get("Models.SharedTitle"), FontAwesomeIcon.ShareAlt);

            string? toRemove = null;
            foreach (var shared in sharedTemplates)
            {
                ImGui.PushID("shared_" + shared.Code);

                // Retrait demandé : la ligne devient la question.
                var removeId = SharedRemoveId(shared.Code);
                if (ConfirmInline.IsOpen(removeId))
                {
                    if (ConfirmInline.DrawQuestion(removeId,
                            string.Format(Loc.Get("Models.SharedRemoveConfirmFmt"), shared.Code),
                            Loc.Get("Models.SharedRemoveYes")))
                        toRemove = shared.Code;

                    ImGui.PopID();
                    ImGui.Spacing();
                    continue;
                }

                // Icône permanent/temporaire
                var typeIcon = shared.Permanent
                    ? FontAwesomeIcon.Lock.ToIconString()
                    : FontAwesomeIcon.Clock.ToIconString();
                var typeColor = shared.Permanent
                    ? MasterEventTheme.SuccessColor
                    : MasterEventTheme.PendingColor;
                ImGui.AlignTextToFramePadding();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(typeColor, typeIcon);
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(shared.Permanent ? Loc.Get("Models.SharedPermanent") : Loc.Get("Models.SharedTemporary"));
                    ImGui.EndTooltip();
                }
                ImGui.SameLine();

                ImGui.TextUnformatted(shared.TemplateName);
                ImGui.SameLine();
                ImGui.TextColored(MasterEventTheme.TextDim, shared.Code);
                ImGui.SameLine();

                // Copier le code
                if (QuietIconButton(FontAwesomeIcon.Copy, "copy_shared", Loc.Get("Models.SharedCopy")))
                    ImGui.SetClipboardText(shared.Code);
                ImGui.SameLine();

                // Retirer de la liste, après confirmation : le code ne se retrouve plus ensuite.
                if (RemoveIconButton())
                    ConfirmInline.Open(removeId);

                ImGui.PopID();
                ImGui.Spacing();
            }

            if (toRemove != null)
                session.RemoveSharedTemplate(toRemove);
            LayoutControls.EndCard();
        }

        // Corbeille en rouge discret ; l'infobulle garde la couleur du texte ordinaire.
        static bool RemoveIconButton()
        {
            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.DangerColor);
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MasterEventTheme.AccentColor with { W = 0.25f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, MasterEventTheme.AccentColor with { W = 0.4f });
            bool clicked;
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                clicked = ImGui.Button(FontAwesomeIcon.Trash.ToIconString() + "##del_shared");
            ImGui.PopStyleColor(4);

            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Models.SharedRemove"));
            return clicked;
        }
    }

    // ─── Éditeurs en pleine page ─────────────────────────────────────────────────────────

    // Fil d'Ariane : retour vers la liste, nom de l'élément édité, pastille de modèle et état.
    private bool DrawEditorBreadcrumb(string current, string? tag, bool dirty, string? root = null)
    {
        var back = false;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            back = ImGui.Button(FontAwesomeIcon.ArrowLeft.ToIconString() + "##editor_back");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Sheets.BackTooltip"));

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MasterEventTheme.TextDim, (root ?? Loc.Get("Sheets.Title")) + " /");
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.TextStrong, current);

        if (!string.IsNullOrEmpty(tag))
        {
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.TextDim, $"[{tag}]");
        }

        if (dirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.WarningColor, "* " + Loc.Get("Sheets.Modified"));
        }

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);
        return back;
    }

    // Hauteur réservée à la barre d'enregistrement, fixe sous la zone défilante.
    private static float EditorBarHeight
        => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 3f + ImGui.GetStyle().WindowPadding.Y;

    private void DrawTemplateEditorPage()
    {
        if (editingTemplate == null) return;

        if (DrawEditorBreadcrumb(editingTemplateName ?? editingTemplate.Name, null, false))
        {
            // Une question de désabonnement laissée ouverte ne doit pas réapparaître au prochain modèle.
            if (ConfirmInline.IsOpen(TemplateEditorUnsubscribeId)) ConfirmInline.Close();
            editingTemplate = null;
            editingTemplateName = null;
            return;
        }

        // Le résultat d'une publication arrive pendant que l'éditeur reste ouvert.
        DrawTabNotice(Tab.Profiles);

        // La question de désabonnement prend une ligne de plus dans la barre.
        var barHeight = EditorBarHeight;
        if (ConfirmInline.IsOpen(TemplateEditorUnsubscribeId))
            barHeight += ImGui.GetTextLineHeightWithSpacing();

        if (ImGui.BeginChild("##tpl_editor_scroll", new Vector2(0, -barHeight), false))
            DrawTemplateEditorBody();
        ImGui.EndChild();

        ImGui.Separator();
        DrawTemplateEditorBar();
    }

    private void DrawTemplateEditorBody()
    {
        if (editingTemplate == null) return;
        var tpl = editingTemplate;

        DrawTemplateBanner(tpl);
        ImGuiHelpers.ScaledDummy(4f);

        // Tout le contenu d'édition est désactivé pour les abonnements.
        ImGui.BeginDisabled(tpl.IsSubscription);

        DrawTemplateIdentityCard(tpl);
        DrawTemplateBarsCard(tpl);
        DrawTemplateRollsCard(tpl);
        DrawTemplateSceneCard(tpl);
        DrawTemplateStatsCard(tpl);
        DrawTemplateCountersCard(tpl);

        ImGui.EndDisabled();
    }

    // Bandeau d'état : abonnement (lecture seule) ou modèle publié (propriétaire).
    private static void DrawTemplateBanner(EventTemplate tpl)
    {
        if (tpl.IsSubscription)
        {
            LayoutControls.DrawNotice(
                string.Format(Loc.Get("Models.ReadOnlyBanner"), tpl.SourceCode, tpl.SourceVersion),
                MasterEventTheme.PendingColor, FontAwesomeIcon.Lock);
        }
        else if (!string.IsNullOrEmpty(tpl.SourceCode))
        {
            LayoutControls.DrawNotice(
                string.Format(Loc.Get("Models.PublishedBanner"), tpl.SourceCode, tpl.SourceVersion),
                MasterEventTheme.SuccessColor, FontAwesomeIcon.CloudUploadAlt);
        }
    }

    private static void DrawTemplateIdentityCard(EventTemplate tpl)
    {
        LayoutControls.BeginCard(Loc.Get("Models.Name"), FontAwesomeIcon.Pen);

        ImGui.SetNextItemWidth(LayoutControls.CardContentWidth);
        var name = tpl.Name;
        if (ImGui.InputTextWithHint("##tpl_name", Loc.Get("Sheets.TemplateNameHint"), ref name, 64))
            tpl.Name = name;

        LayoutControls.EndCard();
    }

    // Ligne « titre à gauche, commutateur segmenté à droite », comme un réglage d'iOS.
    private static int DrawTitledSegments(FontAwesomeIcon icon, Vector4 iconColor, string title, string id,
        string[] labels, int selected, float segmentsWidth)
    {
        var origin = ImGui.GetCursorScreenPos();
        var cardWidth = LayoutControls.CardContentWidth;
        var rowHeight = ImGui.GetFrameHeight() * 1.2f;

        ImGui.SetCursorScreenPos(origin + new Vector2(0, (rowHeight - ImGui.GetTextLineHeight()) / 2f));
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(iconColor, icon.ToIconString());
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.TextStrong, title);

        ImGui.SetCursorScreenPos(new Vector2(origin.X + cardWidth - segmentsWidth, origin.Y));
        selected = DrawSegmentedControl(id, labels, selected, segmentsWidth);
        ImGui.SetCursorScreenPos(new Vector2(origin.X, ImGui.GetCursorScreenPos().Y));
        return selected;
    }

    // Deux champs numériques côte à côte, chacun avec son libellé au-dessus.
    private static void DrawNumberPair(string idLeft, string labelLeft, ref int left,
        string idRight, string labelRight, ref int right, int min, int max, bool disabled = false)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var half = (LayoutControls.CardContentWidth - spacing) / 2f;
        var startX = ImGui.GetCursorPosX();

        ImGui.BeginDisabled(disabled);
        ImGui.TextColored(MasterEventTheme.TextSecondary, labelLeft);
        ImGui.SameLine();
        ImGui.SetCursorPosX(startX + half + spacing);
        ImGui.TextColored(MasterEventTheme.TextSecondary, labelRight);

        ImGui.SetNextItemWidth(half);
        if (ImGui.InputInt(idLeft, ref left)) left = Math.Clamp(left, min, max);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(half);
        if (ImGui.InputInt(idRight, ref right)) right = Math.Clamp(right, min, max);
        ImGui.EndDisabled();
    }

    private static void DrawTemplateBarsCard(EventTemplate tpl)
    {
        LayoutControls.BeginCard(Loc.Get("Sheets.SectionBars"), FontAwesomeIcon.Heart);

        var segmentsWidth = Math.Min(LayoutControls.CardContentWidth * 0.62f, 240f * ImGuiHelpers.GlobalScale);
        var modeLabels = new[] { Loc.Get("Config.HpMode.Points"), Loc.Get("Config.HpMode.Percentage") };

        // ── Barre de vie ──
        var hpMode = DrawTitledSegments(FontAwesomeIcon.Heart, MasterEventTheme.AttitudeHostile, VitalLabels.Hp,
            "tpl_hp_mode", modeLabels, tpl.HpMode == HpMode.Points ? 0 : 1, segmentsWidth);
        tpl.HpMode = hpMode == 0 ? HpMode.Points : HpMode.Percentage;

        ImGuiHelpers.ScaledDummy(4f);
        var hpMax = tpl.DefaultHpMax;
        var hpPlayerMax = tpl.DefaultPlayerHpMax;
        DrawNumberPair("##tpl_hp_max", Loc.Get("Sheets.MaxNpc"), ref hpMax,
            "##tpl_player_hp_max", Loc.Get("Sheets.MaxPlayers"), ref hpPlayerMax, 1, 99999);
        tpl.DefaultHpMax = hpMax;
        tpl.DefaultPlayerHpMax = hpPlayerMax;

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Template.HpLabel"));
        ImGui.SetNextItemWidth(LayoutControls.CardContentWidth);
        var hpLabel = tpl.HpLabel ?? string.Empty;
        if (ImGui.InputTextWithHint("##tpl_hp_label", Loc.Get("Marker.Hp"), ref hpLabel, 24))
            tpl.HpLabel = string.IsNullOrWhiteSpace(hpLabel) ? null : hpLabel;

        ImGuiHelpers.ScaledDummy(4f);
        var shield = tpl.ShowShield;
        if (ToggleSwitch.Draw("##tpl_shield", Loc.Get("Config.ShowShield"), ref shield))
            tpl.ShowShield = shield;

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // ── Seconde barre (Ether par défaut) ──
        var showMp = tpl.ShowMpBar;
        if (ToggleSwitch.Draw("##tpl_mp_bar", Loc.Get("Config.ShowMpBar"), ref showMp))
            tpl.ShowMpBar = showMp;

        var mpOff = !tpl.ShowMpBar;
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.BeginDisabled(mpOff);

        var mpMode = DrawTitledSegments(FontAwesomeIcon.Magic, MasterEventTheme.MpBarColor, VitalLabels.Mp,
            "tpl_mp_mode", modeLabels, tpl.MpMode == HpMode.Points ? 0 : 1, segmentsWidth);
        tpl.MpMode = mpMode == 0 ? HpMode.Points : HpMode.Percentage;

        ImGuiHelpers.ScaledDummy(4f);
        var mpMax = tpl.DefaultMpMax;
        var mpPlayerMax = tpl.DefaultPlayerMpMax;
        DrawNumberPair("##tpl_mp_max", Loc.Get("Sheets.MaxNpc"), ref mpMax,
            "##tpl_player_mp_max", Loc.Get("Sheets.MaxPlayers"), ref mpPlayerMax, 1, 99999);
        tpl.DefaultMpMax = mpMax;
        tpl.DefaultPlayerMpMax = mpPlayerMax;

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Template.MpLabel"));
        ImGui.SetNextItemWidth(LayoutControls.CardContentWidth);
        var mpLabel = tpl.MpLabel ?? string.Empty;
        if (ImGui.InputTextWithHint("##tpl_mp_label", Loc.Get("Marker.Mp"), ref mpLabel, 24))
            tpl.MpLabel = string.IsNullOrWhiteSpace(mpLabel) ? null : mpLabel;

        ImGui.EndDisabled();

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Template.LabelHint"));

        LayoutControls.EndCard();
    }

    private static void DrawTemplateRollsCard(EventTemplate tpl)
    {
        LayoutControls.BeginCard(Loc.Get("Sheets.SectionRolls"), FontAwesomeIcon.Dice);

        // ── Formule ──
        DrawDiceFormulaRow(tpl);
        ImGuiHelpers.ScaledDummy(6f);

        var segmentsWidth = Math.Min(LayoutControls.CardContentWidth * 0.62f, 260f * ImGuiHelpers.GlobalScale);

        // ── Résolution des stats : la valeur s'ajoute au dé, ou lui sert de seuil ──
        var resolution = DrawTitledSegments(FontAwesomeIcon.Bullseye, MasterEventTheme.TextSecondary,
            Loc.Get("Models.StatResolution"), "tpl_resolution",
            new[] { Loc.Get("Models.StatResolutionModifier"), Loc.Get("Models.StatResolutionTarget") },
            tpl.StatResolution == StatResolution.Target ? 1 : 0, segmentsWidth);
        tpl.StatResolution = resolution == 1 ? StatResolution.Target : StatResolution.Modifier;

        // L'explication du mode choisi, affichée plutôt que cachée dans une infobulle.
        ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.MutedTextColor);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + LayoutControls.CardContentWidth);
        ImGui.TextUnformatted(Loc.Get(tpl.StatResolution == StatResolution.Target
            ? "Models.StatResolutionTargetTooltip"
            : "Models.StatResolutionModifierTooltip"));
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        ImGuiHelpers.ScaledDummy(6f);

        // ── Sens du dé : en mode seuil, il dit s'il faut faire moins ou plus ──
        var direction = DrawTitledSegments(FontAwesomeIcon.ExchangeAlt, MasterEventTheme.TextSecondary,
            Loc.Get("Sheets.DieDirection"), "tpl_direction",
            new[] { Loc.Get("Sheets.HighWins"), Loc.Get("Sheets.LowWins") },
            tpl.RollLowerIsBetter ? 1 : 0, segmentsWidth);
        tpl.RollLowerIsBetter = direction == 1;

        DrawResolutionPreview(tpl);

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // ── Réussites et échecs critiques ──
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.TextSecondary, FontAwesomeIcon.Star.ToIconString());
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.TextStrong, Loc.Get("Models.Criticals"));

        var critSuccess = tpl.CriticalSuccessThreshold;
        var critFailure = tpl.CriticalFailureThreshold;
        DrawNumberPair("##tpl_crit_success", Loc.Get("Models.CritSuccessLabel"), ref critSuccess,
            "##tpl_crit_failure", Loc.Get("Models.CritFailureLabel"), ref critFailure, 0, int.MaxValue);
        tpl.CriticalSuccessThreshold = critSuccess;
        tpl.CriticalFailureThreshold = critFailure;

        // Résumé en langage naturel de la règle courante
        var successText = tpl.CriticalSuccessThreshold > 0
            ? string.Format(Loc.Get(tpl.RollLowerIsBetter ? "Models.CritSuccessPreviewLow" : "Models.CritSuccessPreviewHigh"),
                tpl.CriticalSuccessThreshold)
            : Loc.Get("Models.CritSuccessDefault");
        var failureText = tpl.CriticalFailureThreshold > 0
            ? string.Format(Loc.Get(tpl.RollLowerIsBetter ? "Models.CritFailurePreviewLow" : "Models.CritFailurePreviewHigh"),
                tpl.CriticalFailureThreshold)
            : Loc.Get("Models.CritFailureDefault");

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.MutedTextColor);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + LayoutControls.CardContentWidth);
        ImGui.TextUnformatted("• " + successText);
        ImGui.TextUnformatted("• " + failureText);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        LayoutControls.EndCard();
    }

    // « 1 dé de 100 faces » : les deux champs de la formule, sur une ligne.
    private static void DrawDiceFormulaRow(EventTemplate tpl)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.TextStrong, FontAwesomeIcon.Dice.ToIconString());
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.TextStrong, Loc.Get("Dice.Formula"));
        DiceFormulaEditor.Draw(tpl, "tpl");
    }

    // Initiative et déplacement : deux réglages avancés, regroupés.
    private static void DrawTemplateSceneCard(EventTemplate tpl)
    {
        LayoutControls.BeginCard(Loc.Get("Sheets.SectionInitiative"), FontAwesomeIcon.SortNumericDown);

        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Models.InitiativeStat"));
        ImGui.SetNextItemWidth(LayoutControls.CardContentWidth);
        var statDefs = tpl.StatDefinitions ?? [];
        var initName = statDefs.FirstOrDefault(sd => sd.Id == tpl.InitiativeStatId)?.Name
                       ?? Loc.Get("Models.InitiativeNone");
        if (ImGui.BeginCombo("##tpl_init_stat", initName))
        {
            if (ImGui.Selectable(Loc.Get("Models.InitiativeNone"), tpl.InitiativeStatId == null))
                tpl.InitiativeStatId = null;
            foreach (var sd in statDefs)
                if (ImGui.Selectable(sd.Name + "##init_" + sd.Id, sd.Id == tpl.InitiativeStatId))
                    tpl.InitiativeStatId = sd.Id;
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Models.InitiativeStatHint"));

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Models.MovementQuota"));
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        var quota = tpl.MovementQuota;
        if (ImGui.InputInt("##tpl_move_quota", ref quota))
            tpl.MovementQuota = Math.Clamp(quota, 0, 999);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Models.MovementQuotaHint"));

        // La stat de déplacement n'a de sens que si un quota existe.
        if (tpl.MovementQuota > 0)
        {
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Models.MovementStat"));
            ImGui.SetNextItemWidth(LayoutControls.CardContentWidth);
            var moveName = statDefs.FirstOrDefault(sd => sd.Id == tpl.MovementStatId)?.Name
                           ?? Loc.Get("Models.InitiativeNone");
            if (ImGui.BeginCombo("##tpl_move_stat", moveName))
            {
                if (ImGui.Selectable(Loc.Get("Models.InitiativeNone"), tpl.MovementStatId == null))
                    tpl.MovementStatId = null;
                foreach (var sd in statDefs)
                    if (ImGui.Selectable(sd.Name + "##move_" + sd.Id, sd.Id == tpl.MovementStatId))
                        tpl.MovementStatId = sd.Id;
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Models.MovementStatHint"));
        }

        LayoutControls.EndCard();
    }

    private void DrawTemplateStatsCard(EventTemplate tpl)
    {
        var count = tpl.StatDefinitions?.Count ?? 0;
        LayoutControls.BeginCard($"{Loc.Get("Models.Stats")} ({count})", FontAwesomeIcon.ChartBar);

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var valueWidth = 90f * ImGuiHelpers.GlobalScale;
        var removeWidth = IconButtonWidth(FontAwesomeIcon.Times);
        var nameWidth = LayoutControls.CardContentWidth - valueWidth - removeWidth - spacing * 2f;

        if (tpl.StatDefinitions is { Count: > 0 } defs)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Stat.Name"));
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, nameWidth - ImGui.CalcTextSize(Loc.Get("Stat.Name")).X) + spacing);
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.DefaultValue"));

            for (var i = 0; i < defs.Count; i++)
            {
                var sd = defs[i];
                ImGui.PushID(1000 + i);

                ImGui.SetNextItemWidth(nameWidth);
                var name = sd.Name;
                if (ImGui.InputTextWithHint("##sd_name", Loc.Get("Stat.Name"), ref name, 32))
                    sd.Name = name;
                ImGui.SameLine();

                ImGui.SetNextItemWidth(valueWidth);
                var value = sd.DefaultValue;
                if (ImGui.InputInt("##sd_value", ref value))
                    sd.DefaultValue = Math.Clamp(value, -9999, 9999);
                ImGui.SameLine();

                if (QuietIconButton(FontAwesomeIcon.Times, "sd_del", Loc.Get("Sheets.Delete")))
                {
                    defs.RemoveAt(i);
                    if (defs.Count == 0) tpl.StatDefinitions = null;
                    ImGui.PopID();
                    break;
                }

                ImGui.PopID();
            }
        }
        else
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.NoStats"));
        }

        ImGuiHelpers.ScaledDummy(4f);
        if (QuietButton("+  " + Loc.Get("Models.AddStat") + "##add_stat"))
        {
            tpl.StatDefinitions ??= [];
            tpl.StatDefinitions.Add(new StatDefinition());
        }

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextColored(MasterEventTheme.TextDim,
            string.Format(Loc.Get("Sheets.UsedByFmt"), editingTemplateSheetCount));

        LayoutControls.EndCard();
    }

    private static void DrawTemplateCountersCard(EventTemplate tpl)
    {
        var count = tpl.CounterDefinitions?.Count ?? 0;
        LayoutControls.BeginCard($"{Loc.Get("Models.Counters")} ({count})", FontAwesomeIcon.ListUl);

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var scale = ImGuiHelpers.GlobalScale;
        var maxWidth = 80f * scale;
        var colorWidth = ImGui.GetFrameHeight() * 1.4f;
        var removeWidth = IconButtonWidth(FontAwesomeIcon.Times);
        var nameWidth = LayoutControls.CardContentWidth - maxWidth - colorWidth - removeWidth - spacing * 3f;

        if (tpl.CounterDefinitions is { Count: > 0 } defs)
        {
            for (var i = 0; i < defs.Count; i++)
            {
                var cd = defs[i];
                ImGui.PushID(i);

                ImGui.SetNextItemWidth(nameWidth);
                var name = cd.Name;
                if (ImGui.InputTextWithHint("##cd_name", Loc.Get("Stat.Name"), ref name, 32))
                    cd.Name = name;
                ImGui.SameLine();

                ImGui.SetNextItemWidth(maxWidth);
                var max = cd.DefaultMax;
                if (ImGui.InputInt("##cd_max", ref max))
                    cd.DefaultMax = Math.Max(1, max);
                ImGui.SameLine();

                var color = new Vector3(cd.ColorR, cd.ColorG, cd.ColorB);
                if (ImGui.ColorEdit3("##cd_color", ref color, ImGuiColorEditFlags.NoInputs))
                {
                    cd.ColorR = color.X;
                    cd.ColorG = color.Y;
                    cd.ColorB = color.Z;
                }
                ImGui.SameLine();

                if (QuietIconButton(FontAwesomeIcon.Times, "cd_del", Loc.Get("Sheets.Delete")))
                {
                    defs.RemoveAt(i);
                    if (defs.Count == 0) tpl.CounterDefinitions = null;
                    ImGui.PopID();
                    break;
                }

                ImGui.PopID();
            }
        }
        else
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.NoCounters"));
        }

        ImGuiHelpers.ScaledDummy(4f);
        if (QuietButton("+  " + Loc.Get("Models.AddCounter") + "##add_counter"))
        {
            tpl.CounterDefinitions ??= [];
            tpl.CounterDefinitions.Add(new CounterDefinition());
        }

        LayoutControls.EndCard();
    }

    // Barre de l'éditeur de modèle : Annuler au début, l'action principale à la fin.
    private void DrawTemplateEditorBar()
    {
        if (editingTemplate == null) return;

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var scale = ImGuiHelpers.GlobalScale;
        var rowStart = ImGui.GetCursorPosX();
        var rowWidth = ImGui.GetContentRegionAvail().X - CardRightMargin;

        // Abonnement : lecture seule. Se désabonner au début, après confirmation ; Fermer à la fin.
        if (editingTemplate.IsSubscription)
        {
            if (ConfirmInline.IsOpen(TemplateEditorUnsubscribeId))
            {
                var question = string.Format(Loc.Get("Models.UnsubscribeConfirmFmt"), editingTemplate.Name);
                if (ConfirmInline.DrawQuestion(TemplateEditorUnsubscribeId, question, Loc.Get("Models.Unsubscribe")))
                {
                    UnsubscribeTemplate(editingTemplate.Name);
                    editingTemplate = null;
                    editingTemplateName = null;
                }
                return;
            }

            var unsubLabel = Loc.Get("Models.Unsubscribe");
            if (Buttons.Destructive(unsubLabel + "##unsub_tpl", TextButtonWidth(unsubLabel) + 12f * scale))
                ConfirmInline.Open(TemplateEditorUnsubscribeId);

            var closeLabel = Loc.Get("Sheets.Close");
            var closeWidth = TextButtonWidth(closeLabel) + 24f * scale;
            AlignBarEnd(rowStart, rowWidth, closeWidth);
            if (QuietButton(closeLabel + "##close_tpl", closeWidth))
            {
                editingTemplate = null;
                editingTemplateName = null;
            }
            return;
        }

        // Modèle local : Annuler, puis « Publier la mise à jour » pour le propriétaire, puis Sauvegarder.
        var isOwnerPublished = !string.IsNullOrEmpty(editingTemplate.SourceCode);
        var cancelLabel = Loc.Get("Gm.Cancel");
        var publishLabel = Loc.Get("Models.Publish");
        var saveLabel = Loc.Get("Gm.Save");

        var cancelWidth = TextButtonWidth(cancelLabel) + 12f * scale;
        var publishWidth = isOwnerPublished ? TextButtonWidth(publishLabel) + 12f * scale : 0f;
        var saveWidth = SaveButtonWidth(saveLabel, rowWidth - cancelWidth - spacing
                                                   - (isOwnerPublished ? publishWidth + spacing : 0f));

        if (QuietButton(cancelLabel + "##cancel_tpl", cancelWidth))
        {
            editingTemplate = null;
            editingTemplateName = null;
            return;
        }

        AlignBarEnd(rowStart, rowWidth, (isOwnerPublished ? publishWidth + spacing : 0f) + saveWidth);

        if (isOwnerPublished)
        {
            if (TintedButton(publishLabel + "##publish_tpl", publishWidth))
            {
                // Capture des références non-null pour le closure (editingTemplate est le field).
                var tpl = editingTemplate!;
                var localName = editingTemplateName;
                _ = Task.Run(async () =>
                {
                    var leaderToken = configuration.EnsureLeaderToken();
                    var newVersion = await SessionManager.PublishTemplateUpdateAsync(tpl, configuration.RelayServerUrl, leaderToken);
                    if (newVersion != null)
                    {
                        tpl.SourceVersion = newVersion.Value;
                        if (localName != null && tpl.Name != localName)
                            session.DeleteTemplate(localName);
                        session.SaveTemplate(tpl);
                        ShowTabNotice(Tab.Profiles, string.Format(Loc.Get("Models.Published"), tpl.Name, newVersion.Value));
                    }
                    else
                    {
                        ShowTabNotice(Tab.Profiles, Loc.Get("Models.PublishError"), isError: true);
                    }
                });
            }
            ImGui.SameLine();
        }

        if (PrimaryButton(saveLabel + "##save_tpl", saveWidth))
        {
            var previousName = editingTemplateName;
            if (previousName != null && editingTemplate.Name != previousName)
                session.DeleteTemplate(previousName);
            session.SaveTemplate(editingTemplate);

            // Le modèle enregistré est celui qui est actif : ses nouvelles règles
            // s'appliquent tout de suite, au MJ comme aux joueurs.
            if (session.RefreshActiveTemplate(editingTemplate, previousName))
            {
                configuration.SetActiveTemplateName(editingTemplate.Name);
                configuration.Save();
            }

            // Les profils créés avec ce modèle reçoivent ses nouvelles stats.
            var syncReports = session.SyncSheetsWithTemplate(editingTemplate, previousName);
            ShowTabNotice(Tab.Profiles, syncReports.Count switch
            {
                0 => string.Format(Loc.Get("Sheets.TemplateSavedFmt"), editingTemplate.Name),
                1 => string.Format(Loc.Get("Sheets.SyncedOneFmt"), syncReports[0].Sheet),
                _ => string.Format(Loc.Get("Sheets.SyncedManyFmt"), syncReports.Count,
                    string.Join(", ", syncReports.Select(r => r.Sheet))),
            });

            editingTemplate = null;
            editingTemplateName = null;
        }
    }

    // Bouton principal d'une barre d'édition : généreux, sans déborder de la place restante.
    private static float SaveButtonWidth(string label, float available)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var preferred = Math.Max(TextButtonWidth(label) + 24f * scale, 120f * scale);
        return Math.Max(TextButtonWidth(label), Math.Min(preferred, available));
    }

    // Pousse le groupe de fin d'une barre contre le bord droit, s'il y a la place.
    private static void AlignBarEnd(float rowStart, float rowWidth, float endWidth)
    {
        ImGui.SameLine();
        var target = rowStart + rowWidth - endWidth;
        if (target > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(target);
    }

    // Illustre la règle choisie par des exemples chiffrés, calculés par le même moteur que les jets réels.
    // En mode seuil, deux jets placés de part et d'autre de la cible montrent dans quel sens on réussit.
    private static void DrawResolutionPreview(EventTemplate tpl)
    {
        var stat = tpl.StatDefinitions?.FirstOrDefault();
        var statName = !string.IsNullOrWhiteSpace(stat?.Name) ? stat!.Name : Loc.Get("Sheets.PreviewStatFallback");
        var statValue = stat?.DefaultValue ?? 20;
        var faces = Math.Max(DiceEngine.GetMax(tpl.DiceFormula), 2);

        string Line(int rawRoll)
        {
            var outcome = DiceEngine.Resolve(tpl, rawRoll, statValue, 0);
            if (outcome.Target is not { } target)
                return string.Format(Loc.Get("Sheets.PreviewModifierFmt"), rawRoll, statName, statValue, outcome.Total);

            var verdict = Loc.Get(outcome.Success == true ? "Sheets.PreviewSuccess" : "Sheets.PreviewFailure");
            return string.Format(Loc.Get("Sheets.PreviewTargetFmt"), rawRoll, statName, target, verdict);
        }

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.MutedTextColor);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + LayoutControls.CardContentWidth);
        ImGui.TextUnformatted(Loc.Get("Sheets.PreviewTitle") + " :");

        if (tpl.StatResolution == StatResolution.Target)
        {
            // Un jet sous la cible et un jet au-dessus : l'un réussit, l'autre non, quel que soit le sens.
            var gap = Math.Max(1, faces / 10);
            var below = Math.Clamp(statValue - gap, 1, faces);
            var above = Math.Clamp(statValue + gap, 1, faces);
            if (below == above) above = Math.Min(faces, below + 1);

            ImGui.TextUnformatted("• " + Line(below));
            ImGui.TextUnformatted("• " + Line(above));
        }
        else
        {
            ImGui.TextUnformatted("• " + Line(Math.Max(1, (int)Math.Round(faces * 0.8))));
        }

        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private void DrawSheetEditorPage()
    {
        if (editingProfile == null) return;

        var profile = editingProfile;
        if (DrawEditorBreadcrumb(profile.Name, profile.TemplateName, editingDirty))
        {
            editingProfile = null;
            return;
        }

        if (ImGui.BeginChild("##sheet_editor_scroll", new Vector2(0, -EditorBarHeight), false))
            DrawSheetEditorBody(profile);
        ImGui.EndChild();

        ImGui.Separator();
        DrawSheetEditorBar(profile);
    }

    // Filtre de la liste de stats d'une fiche, partagé : un seul éditeur est ouvert à la fois.
    private static string sheetStatsFilter = string.Empty;

    private void DrawSheetEditorBody(PlayerSheet profile)
    {
        var template = session.LoadTemplate(profile.TemplateName);
        var mpOff = template != null && !template.ShowMpBar;
        var targetMode = template is { StatResolution: StatResolution.Target };

        // ── Vitaux : la vie, et la seconde barre seulement si le modèle de la fiche s'en sert ──
        LayoutControls.BeginCard(Loc.Get("Sheets.SectionVitals"), FontAwesomeIcon.Heart);

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var vitalsColumn = mpOff ? LayoutControls.CardContentWidth : (LayoutControls.CardContentWidth - spacing) / 2f;
        var startX = ImGui.GetCursorPosX();

        ImGui.AlignTextToFramePadding();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.AttitudeHostile, FontAwesomeIcon.Heart.ToIconString());
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.TextSecondary, VitalLabels.HpMax);
        if (!mpOff)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(startX + vitalsColumn + spacing);
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.MpBarColor, FontAwesomeIcon.Magic.ToIconString());
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.TextSecondary, VitalLabels.MpMax);
        }

        ImGui.SetNextItemWidth(vitalsColumn);
        var hpMax = profile.HpMax;
        if (ImGui.InputInt("##prof_hpmax", ref hpMax))
        {
            profile.HpMax = Math.Max(1, hpMax);
            if (profile.Hp > profile.HpMax) profile.Hp = profile.HpMax;
            editingDirty = true;
        }

        if (!mpOff)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(vitalsColumn);
            var mpMax = profile.MpMax;
            if (ImGui.InputInt("##prof_mpmax", ref mpMax))
            {
                profile.MpMax = Math.Max(1, mpMax);
                if (profile.Mp > profile.MpMax) profile.Mp = profile.MpMax;
                editingDirty = true;
            }
        }

        LayoutControls.EndCard();

        // ── Statistiques : deux colonnes, filtre au-delà de quelques stats ──
        if (profile.Stats is { Count: > 0 } stats)
        {
            LayoutControls.BeginCard($"{Loc.Get("Models.Stats")} ({stats.Count})", FontAwesomeIcon.ChartBar);

            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.MutedTextColor);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + LayoutControls.CardContentWidth);
            ImGui.TextUnformatted(Loc.Get(targetMode ? "Sheets.StatsHintTarget" : "Sheets.StatsHintModifier"));
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
            ImGuiHelpers.ScaledDummy(4f);

            if (stats.Count > VitalsControls.StatFilterThreshold)
            {
                ImGui.SetNextItemWidth(LayoutControls.CardContentWidth);
                ImGui.InputTextWithHint("##sheet_stats_filter", Loc.Get("Models.StatsFilter"), ref sheetStatsFilter, 64);
                ImGuiHelpers.ScaledDummy(2f);
            }

            var shown = VitalsControls.Filtered(stats, sheetStatsFilter).ToList();
            if (shown.Count == 0)
            {
                ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheets.NoStatMatch"));
            }
            else
            {
                var scale = ImGuiHelpers.GlobalScale;
                var cellWidth = (LayoutControls.CardContentWidth - spacing * 2f) / 2f;
                var inputWidth = 64f * scale;
                var labelMax = cellWidth - inputWidth - spacing;
                var gridX = ImGui.GetCursorPosX();

                for (var i = 0; i < shown.Count; i++)
                {
                    var stat = shown[i];
                    var column = i % 2;
                    if (column == 1)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(gridX + cellWidth + spacing * 2f);
                    }

                    // Libellé tronqué plutôt que de déborder sur le champ.
                    var label = stat.Name;
                    while (label.Length > 3 && ImGui.CalcTextSize(label).X > labelMax)
                        label = label[..^1];
                    if (label.Length < stat.Name.Length) label = label.TrimEnd() + "...";

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(label);
                    if (label.Length != stat.Name.Length && ImGui.IsItemHovered()) ImGui.SetTooltip(stat.Name);

                    ImGui.SameLine();
                    ImGui.SetCursorPosX(gridX + column * (cellWidth + spacing * 2f) + cellWidth - inputWidth);
                    ImGui.SetNextItemWidth(inputWidth);
                    var value = stat.Modifier;
                    if (ImGui.InputInt($"##pstat_{stat.Id}", ref value, 0, 0))
                    {
                        stat.Modifier = Math.Clamp(value, -9999, 9999);
                        editingDirty = true;
                    }
                }
            }

            LayoutControls.EndCard();
        }

        // ── Compteurs ──
        if (profile.Counters is { Count: > 0 } counters)
        {
            LayoutControls.BeginCard($"{Loc.Get("Models.Counters")} ({counters.Count})", FontAwesomeIcon.ListUl);

            var valueWidth = 90f * ImGuiHelpers.GlobalScale;
            foreach (var counter in counters)
            {
                ImGui.PushID("cnt_" + counter.Id);

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(counter.Name);
                ImGui.SameLine();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f,
                    LayoutControls.CardContentWidth - valueWidth - ImGui.CalcTextSize($"/ {counter.Max}").X
                    - ImGui.CalcTextSize(counter.Name).X - spacing * 2f));
                ImGui.SetNextItemWidth(valueWidth);
                var val = counter.Value;
                if (ImGui.InputInt("##value", ref val, 0, 0))
                {
                    counter.Value = Math.Clamp(val, 0, counter.Max);
                    editingDirty = true;
                }
                ImGui.SameLine();
                ImGui.TextColored(MasterEventTheme.TextDim, $"/ {counter.Max}");

                ImGui.PopID();
            }

            LayoutControls.EndCard();
        }
    }

    // Barre d'enregistrement : Annuler au début ; à la fin « Sauvegarder et charger » puis
    // l'unique action pleine.
    private void DrawSheetEditorBar(PlayerSheet profile)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var scale = ImGuiHelpers.GlobalScale;
        var rowStart = ImGui.GetCursorPosX();
        var rowWidth = ImGui.GetContentRegionAvail().X - CardRightMargin;

        var saveLabel = Loc.Get("Gm.Save");
        var applyLabel = Loc.Get("Sheets.SaveAndLoad");
        var cancelLabel = Loc.Get("Gm.Cancel");

        var cancelWidth = TextButtonWidth(cancelLabel) + 12f * scale;
        var applyWidth = TextButtonWidth(applyLabel) + 12f * scale;
        var saveWidth = SaveButtonWidth(saveLabel, rowWidth - cancelWidth - applyWidth - spacing * 2f);

        if (QuietButton(cancelLabel + "##back_prof", cancelWidth))
        {
            editingProfile = null;
            return;
        }

        AlignBarEnd(rowStart, rowWidth, applyWidth + spacing + saveWidth);

        if (TintedButton(applyLabel + "##apply_prof", applyWidth))
        {
            session.SavePlayerSheet(profile);
            session.ApplyPlayerSheet(profile);
            ShowTabNotice(Tab.Profiles, string.Format(Loc.Get("Player.ProfileLoaded"), profile.Name));
            editingProfile = null;
        }
        ImGui.SameLine();

        if (PrimaryButton(saveLabel + "##save_prof", saveWidth))
        {
            session.SavePlayerSheet(profile);
            ShowTabNotice(Tab.Profiles, string.Format(Loc.Get("Player.ProfileSaved"), profile.Name));
            editingProfile = null;
        }
    }

    private void DrawExportPopup(string templateName, bool open)
    {
        if (open) ImGui.OpenPopup("##export_popup_" + templateName);

        if (ImGui.BeginPopup("##export_popup_" + templateName))
        {
            ImGui.TextColored(MasterEventTheme.AccentText, Loc.Get("Models.Export"));
            ImGui.Separator();

            ImGui.Checkbox(Loc.Get("Models.ExportPermanent") + "##perm_" + templateName, ref exportPermanent);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Models.ExportPermanentTooltip"));
                ImGui.EndTooltip();
            }

            if (PrimaryButton(Loc.Get("Models.Export") + "##do_export_" + templateName))
            {
                var tpl = session.LoadTemplate(templateName);
                if (tpl != null)
                {
                    exportInProgress = true;
                    lastExportCode = null;
                    var perm = exportPermanent;
                    _ = Task.Run(async () =>
                    {
                        var leaderToken = configuration.EnsureLeaderToken();
                        var result = await SessionManager.ExportTemplateAsync(tpl, configuration.RelayServerUrl, perm, leaderToken);
                        exportInProgress = false;
                        if (result != null)
                        {
                            lastExportCode = result.Code;
                            ImGui.SetClipboardText(result.Code);
                            ShowTabNotice(Tab.Profiles, Loc.Get("Models.Exported"));
                            session.AddSharedTemplate(new SharedTemplate
                            {
                                Code = result.Code,
                                TemplateName = tpl.Name,
                                Permanent = perm,
                            });
                            tpl.SourceCode = result.Code;
                            tpl.SourceVersion = result.Version;
                            tpl.IsSubscription = false;
                            session.SaveTemplate(tpl);
                        }
                        else
                        {
                            ShowTabNotice(Tab.Profiles, Loc.Get("Models.ExportError"), isError: true);
                        }
                    });
                }
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private static float TextButtonWidth(string label)
        => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;

    private static float IconButtonWidth(FontAwesomeIcon icon)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            return ImGui.CalcTextSize(icon.ToIconString()).X + ImGui.GetStyle().FramePadding.X * 2f;
    }

    private static float IconWidth(FontAwesomeIcon icon)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            return ImGui.CalcTextSize(icon.ToIconString()).X;
    }

    private static void AlignRowRight(float rowStartX, float leftWidth, float buttonsWidth)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var target = rowStartX + LayoutControls.CardContentWidth - buttonsWidth;
        if (rowStartX + leftWidth + spacing <= target)
            ImGui.SameLine(target);
    }

}
