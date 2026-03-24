using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
    // ────────── Profiles content ──────────

    private void DrawProfilesContent()
    {
        if (ImGui.BeginChild("##profiles_scroll", Vector2.Zero))
        {
            if (editingProfile != null)
                DrawProfileEditor();
            else
                DrawProfilesMain();
        }
        ImGui.EndChild();
    }

    private void DrawProfilesMain()
    {
        var availWidth = ImGui.GetContentRegionAvail().X;

        // Header
        ImGuiHelpers.ScaledDummy(6f);
        var iconStr = FontAwesomeIcon.Scroll.ToIconString();
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
        var titleSz = ImGui.CalcTextSize(Loc.Get("Player.Sheet"));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Player.Sheet"));
        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        //Créer un profil
        var templateNames = session.GetTemplateNames();
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Player.CreateProfile"));
        ImGuiHelpers.ScaledDummy(2f);

        ImGui.SetNextItemWidth(availWidth);
        ImGui.InputTextWithHint("##profile_name", Loc.Get("Player.ProfileName"), ref newProfileName, 64);

        ImGui.SetNextItemWidth(availWidth);
        if (ImGui.BeginCombo("##tpl_select", string.IsNullOrEmpty(selectedTemplateName) ? Loc.Get("Player.SelectTemplate") : selectedTemplateName))
        {
            foreach (var tplName in templateNames.Where(t => ImGui.Selectable(t, t == selectedTemplateName)))
            {
                selectedTemplateName = tplName;
            }
            ImGui.EndCombo();
        }

        var canCreate = !string.IsNullOrWhiteSpace(newProfileName) && !string.IsNullOrEmpty(selectedTemplateName);
        if (!canCreate) ImGui.BeginDisabled();
        if (ImGui.Button(Loc.Get("Player.CreateProfile") + "##do_create", new Vector2(availWidth, 0)))
        {
            var tpl = session.LoadTemplate(selectedTemplateName);
            if (tpl != null)
            {
                var profile = PlayerSheet.FromTemplate(tpl, newProfileName.Trim());
                editingProfile = profile;
                editingDirty = true;
                newProfileName = string.Empty;
            }
        }
        if (!canCreate) ImGui.EndDisabled();

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // Mes profils
        DrawGmProfilesList();
    }

    private void DrawGmProfilesList()
    {
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Player.MyProfiles"));
        ImGuiHelpers.ScaledDummy(2f);

        var sheetNames = session.GetPlayerSheetNames();
        if (sheetNames.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Player.NoProfiles"));
            return;
        }

        string? toDelete = null;
        foreach (var name in sheetNames)
        {
            var sheet = session.LoadPlayerSheet(name);
            if (sheet == null) continue;

            var isDefault = configuration.DefaultSheetName == name;
            var userIcon = FontAwesomeIcon.User.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(new Vector4(0.227f, 0.604f, 1f, 0.8f), userIcon);
            ImGui.SameLine();
            ImGui.TextUnformatted($"{name} - {sheet.TemplateName}");

            var btnSize = new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight());

            // Définir par défaut
            var starIcon = FontAwesomeIcon.Star.ToIconString();
            var starColor = isDefault
                ? new Vector4(1f, 0.85f, 0.2f, 1f)
                : new Vector4(1f, 1f, 1f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, starColor);
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(starIcon + $"##default_{name}", btnSize))
                {
                    configuration.DefaultSheetName = isDefault ? null : name;
                    configuration.Save();
                }
            }
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(isDefault ? Loc.Get("Player.UnsetDefault") : Loc.Get("Player.SetDefault"));
                ImGui.EndTooltip();
            }
            ImGui.SameLine();

            // Charger
            var loadIcon = FontAwesomeIcon.Play.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(loadIcon + $"##load_{name}", btnSize))
                {
                    session.ApplyPlayerSheet(sheet);
                    Plugin.ChatGui.Print(string.Format(Loc.Get("Player.ProfileLoaded"), name));
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Player.LoadSheet"));
                ImGui.EndTooltip();
            }
            ImGui.SameLine();

            // Éditer
            var editBtnIcon = FontAwesomeIcon.Pen.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(editBtnIcon + $"##edit_{name}", btnSize))
                {
                    editingProfile = sheet.DeepCopy();
                    editingDirty = false;
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Player.EditProfile"));
                ImGui.EndTooltip();
            }
            ImGui.SameLine();

            // Supprimer
            var trashIcon = FontAwesomeIcon.Trash.ToIconString();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(trashIcon + $"##del_{name}", btnSize))
                    toDelete = name;
            }
            ImGui.PopStyleColor();

            ImGui.Spacing();
        }

        if (toDelete != null)
        {
            session.DeletePlayerSheet(toDelete);
            if (configuration.DefaultSheetName == toDelete)
            {
                configuration.DefaultSheetName = null;
                configuration.Save();
            }
            Plugin.ChatGui.Print(Loc.Get("Player.ProfileDeleted"));
        }
    }

    private void DrawProfileEditor()
    {
        var profile = editingProfile!;
        var fieldWidth = ImGui.GetContentRegionAvail().X;

        // Titre
        var penIcon = FontAwesomeIcon.Pen.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.AccentColor, penIcon);
        ImGui.SameLine();
        var dirtyMarker = editingDirty ? " *" : "";
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Player.EditProfile"));
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.227f, 0.604f, 1f, 0.8f), profile.Name + dirtyMarker);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"[{profile.TemplateName}]");

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        //  PV
        var heartIcon = FontAwesomeIcon.Heart.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.AttitudeHostile, heartIcon);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.HpMax"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
        var hpMax = profile.HpMax;
        if (ImGui.InputInt("##prof_hpmax", ref hpMax))
        {
            if (hpMax < 1) hpMax = 1;
            profile.HpMax = hpMax;
            if (profile.Hp > hpMax) profile.Hp = hpMax;
            editingDirty = true;
        }

        //  PE
        var linkedTpl = session.LoadTemplate(profile.TemplateName);
        var profMpDisabled = linkedTpl != null && !linkedTpl.ShowMpBar;
        if (profMpDisabled) ImGui.BeginDisabled();
        var magicIcon = FontAwesomeIcon.Magic.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.MpBarColor, magicIcon);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.MpMax"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
        var mpMax = profile.MpMax;
        if (ImGui.InputInt("##prof_mpmax", ref mpMax))
        {
            if (mpMax < 1) mpMax = 1;
            profile.MpMax = mpMax;
            if (profile.Mp > mpMax) profile.Mp = mpMax;
            editingDirty = true;
        }
        if (profMpDisabled) ImGui.EndDisabled();

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // Stats
        if (profile.Stats != null && profile.Stats.Count > 0)
        {
            var chartIcon = FontAwesomeIcon.ChartBar.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.AccentColor, chartIcon);
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Stats"));
            ImGuiHelpers.ScaledDummy(2f);

            var labelWidth = 0f;
            foreach (var stat in profile.Stats)
            {
                var w = ImGui.CalcTextSize(stat.Name).X;
                if (w > labelWidth) labelWidth = w;
            }
            labelWidth += 8f * ImGuiHelpers.GlobalScale;

            foreach (var stat in profile.Stats)
            {
                ImGui.TextUnformatted(stat.Name);
                ImGui.SameLine(labelWidth);
                ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                var mod = stat.Modifier;
                if (ImGui.InputInt($"##pstat_{stat.Id}", ref mod))
                {
                    stat.Modifier = mod;
                    editingDirty = true;
                }
                ImGui.SameLine();
                var modStr = mod >= 0 ? $"+{mod}" : mod.ToString();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"({modStr})");
            }
        }

        // Compteurs
        if (profile.Counters != null && profile.Counters.Count > 0)
        {
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            var listIcon = FontAwesomeIcon.ListUl.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.AccentColor, listIcon);
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Counters"));
            ImGuiHelpers.ScaledDummy(2f);

            foreach (var counter in profile.Counters)
            {
                ImGui.TextUnformatted(counter.Name);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(60f * ImGuiHelpers.GlobalScale);
                var val = counter.Value;
                if (ImGui.InputInt($"##pcnt_{counter.Name}", ref val))
                {
                    if (val < 0) val = 0;
                    if (val > counter.Max) val = counter.Max;
                    counter.Value = val;
                    editingDirty = true;
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"/ {counter.Max}");
            }
        }

        // Boutons
        ImGuiHelpers.ScaledDummy(8f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        var btnWidth = (fieldWidth - ImGui.GetStyle().ItemSpacing.X * 2) / 3f;

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.6f, 0.25f, 1f));
        if (ImGui.Button(Loc.Get("Gm.Save") + "##save_prof", new Vector2(btnWidth, 0)))
        {
            session.SavePlayerSheet(profile);
            Plugin.ChatGui.Print(string.Format(Loc.Get("Player.ProfileSaved"), profile.Name));
            editingProfile = null;
        }
        ImGui.PopStyleColor(2);

        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.35f, 0.6f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.4f, 0.7f, 1f));
        if (ImGui.Button(Loc.Get("Player.LoadSheet") + "##apply_prof", new Vector2(btnWidth, 0)))
        {
            session.SavePlayerSheet(profile);
            session.ApplyPlayerSheet(profile);
            Plugin.ChatGui.Print(string.Format(Loc.Get("Player.ProfileLoaded"), profile.Name));
            editingProfile = null;
        }
        ImGui.PopStyleColor(2);

        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.5f, 0.2f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.6f, 0.25f, 0.25f, 1f));
        if (ImGui.Button(Loc.Get("Gm.Cancel") + "##back_prof", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
        {
            editingProfile = null;
        }
        ImGui.PopStyleColor(2);
    }
}
