using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;

namespace MasterEvent.UI.Components;

// Une ligne par acteur, d'une seule hauteur. La zone de gauche choisit l'acteur ; les commandes de
// droite, quand la vue en donne, agissent sans l'ouvrir. Maj + clic sur - ou + compte 5 PV.
public static class ActorRow
{
    public sealed class Model
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public bool NameMuted;
        public bool Selected;
        public bool Dimmed;

        public uint WaymarkIcon;
        // Prend la place du glyphe ; GlyphText passe alors en pastille sur son coin.
        public IDalamudTextureWrap? Portrait;
        public FontAwesomeIcon GlyphIcon = FontAwesomeIcon.User;
        public string? GlyphText;
        public Vector4 GlyphColor = MasterEventTheme.TextSecondary;

        public string? Prefix;
        public string? PrefixTooltip;

        // Infobulle de la zone de gauche, quand ni le préfixe ni le jet attendu n'en montrent une.
        public string? Tooltip;

        // C'est le tour de l'acteur : le préfixe d'initiative passe en couleur d'attente.
        public bool Active;
        public readonly List<(string Text, Vector4 Color)> Pills = [];
        public string? Trailing;
        public Vector4 TrailingColor = MasterEventTheme.MutedTextColor;
        public bool RollPending;
        public bool? Connected;

        public bool HasBar = true;
        public int Hp;
        public int HpMax;
        public int Shield;
        public Vector4 BarColor = MasterEventTheme.PlayerColor;

        // Sans ToggleActed, la coche se lit sans se cliquer.
        public bool? Acted;
        public bool ReserveCheck;
        public Action? ToggleActed;
        public Action<int>? AdjustHp;
        public IVitalEntity? RollEntity;
        public bool RollEnabled = true;
        public string RollTooltip = string.Empty;
        public Action<string?>? Roll;
        public Action? Menu;
        public float MenuWidth;
    }

    public static bool Draw(Model row, HpMode hpMode)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var dl = ImGui.GetWindowDrawList();
        var frame = ImGui.GetFrameHeight();
        var lineHeight = ImGui.GetTextLineHeight();
        var pad = 6f * scale;
        var barHeight = 5f * scale;
        var rounding = MasterEventTheme.RadiusCard * scale;
        var percent = hpMode == HpMode.Percentage;

        var width = ImGui.GetContentRegionAvail().X - MasterEventTheme.ListRightMargin;
        var height = Math.Max(frame, lineHeight + (row.HasBar ? 4f * scale + barHeight : 0f)) + pad * 2f;
        var pos = ImGui.GetCursorScreenPos();

        var stepWidth = row.AdjustHp != null ? frame * 2f + 2f * scale : 0f;
        var rollWidth = row.Roll != null ? Buttons.IconWidth(FontAwesomeIcon.Dice) : 0f;
        var menuWidth = row.Menu != null ? row.MenuWidth : 0f;
        var parts = new[] { stepWidth, rollWidth, menuWidth }.Where(w => w > 0f).ToArray();
        var controlsWidth = parts.Sum() + spacing * Math.Max(0, parts.Length - 1);

        ImGui.PushID(row.Id);

        var background = row.Selected ? MasterEventTheme.ThemeButtonBg : MasterEventTheme.ThemeFrameBg;
        dl.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.GetColorU32(background), rounding);
        // Contour d'accent : la sélection reste visible tant que le détail est ouvert.
        if (row.Selected)
            dl.AddRect(pos, pos + new Vector2(width, height),
                ImGui.GetColorU32(MasterEventTheme.AccentColor with { W = 0.85f }), rounding, ImDrawFlags.None, 1f * scale);

        var fade = row.Dimmed ? 0.5f : 1f;
        var x = pos.X + pad;
        if (row.Acted is { } acted)
        {
            if (row.ToggleActed != null)
            {
                ImGui.SetCursorScreenPos(new Vector2(x, pos.Y + (height - frame) / 2f));
                var value = acted;
                if (ImGui.Checkbox("##acted", ref value))
                    row.ToggleActed();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Scene.Acted"));
            }
            else if (acted)
            {
                DrawIcon(dl, FontAwesomeIcon.Check, new Vector2(x, pos.Y + (height - frame) / 2f), frame,
                    MasterEventTheme.TextDim with { W = fade });
            }
        }
        if (row.Acted != null || row.ReserveCheck)
            x += frame + spacing;

        var mainEnd = pos.X + width - pad - controlsWidth - (controlsWidth > 0f ? spacing : 0f);
        ImGui.SetCursorScreenPos(new Vector2(x - pad / 2f, pos.Y));
        var clicked = ImGui.InvisibleButton("##select", new Vector2(Math.Max(1f, mainEnd - x + pad / 2f), height));
        var rowHovered = ImGui.IsItemHovered();
        var tooltipShown = false;
        if (rowHovered && !row.Selected)
            dl.AddRectFilled(pos, pos + new Vector2(width, height),
                ImGui.GetColorU32(MasterEventTheme.TextStrong with { W = 0.04f }), rounding);

        var glyphSize = frame * 0.9f;
        var glyphMin = new Vector2(x, pos.Y + (height - glyphSize) / 2f);
        DrawGlyph(dl, row, glyphMin, glyphSize, fade, background);

        var textX = glyphMin.X + glyphSize + 8f * scale;
        var line1Y = row.HasBar ? pos.Y + pad : pos.Y + (height - lineHeight) / 2f;

        // Côté droit de la première ligne : attitude ou état, puis la pastille de connexion.
        var right = mainEnd;
        if (row.Connected is { } connected)
        {
            var dot = 3.5f * scale;
            var center = new Vector2(right - dot, line1Y + lineHeight / 2f);
            dl.AddCircleFilled(center, dot,
                ImGui.GetColorU32(connected ? MasterEventTheme.SuccessColor : MasterEventTheme.TextDim));
            right -= dot * 2f + 6f * scale;
        }
        if (row.Trailing != null)
        {
            var size = ImGui.CalcTextSize(row.Trailing);
            dl.AddText(new Vector2(right - size.X, line1Y), ImGui.GetColorU32(row.TrailingColor with { W = fade }), row.Trailing);
            right -= size.X + 8f * scale;
        }

        var cursorX = textX;
        if (row.Prefix != null)
        {
            var prefixColor = row.Active ? MasterEventTheme.PendingColor : MasterEventTheme.TextSecondary;
            dl.AddText(new Vector2(cursorX, line1Y), ImGui.GetColorU32(prefixColor with { W = fade }), row.Prefix);
            var prefixSize = ImGui.CalcTextSize(row.Prefix);
            if (row.PrefixTooltip != null && rowHovered && ImGui.IsMouseHoveringRect(new Vector2(cursorX, line1Y),
                    new Vector2(cursorX + prefixSize.X, line1Y + lineHeight)))
            {
                ImGui.SetTooltip(row.PrefixTooltip);
                tooltipShown = true;
            }
            cursorX += prefixSize.X + 8f * scale;
        }

        var pillsWidth = row.Pills.Sum(p => Pill.Width(p.Text) + 4f * scale);
        var pendingWidth = 0f;
        if (row.RollPending)
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                pendingWidth = ImGui.CalcTextSize(FontAwesomeIcon.Dice.ToIconString()).X + 6f * scale;
        }

        var name = TextFit.Fit(row.Name, right - cursorX - pillsWidth - pendingWidth);
        var nameColor = row.NameMuted ? MasterEventTheme.MutedTextColor : MasterEventTheme.TextStrong;
        dl.AddText(new Vector2(cursorX, line1Y), ImGui.GetColorU32(nameColor with { W = fade }), name);
        cursorX += ImGui.CalcTextSize(name).X + 6f * scale;

        foreach (var (text, color) in row.Pills)
            cursorX += Pill.Draw(dl, new Vector2(cursorX, line1Y), text, color) + 4f * scale;

        if (row.RollPending)
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                dl.AddText(new Vector2(cursorX + 2f * scale, line1Y), ImGui.GetColorU32(MasterEventTheme.PendingColor),
                    FontAwesomeIcon.Dice.ToIconString());
            if (rowHovered && ImGui.IsMouseHoveringRect(new Vector2(cursorX, line1Y),
                    new Vector2(cursorX + pendingWidth, line1Y + lineHeight)))
            {
                ImGui.SetTooltip(Loc.Get("Group.RollPending"));
                tooltipShown = true;
            }
        }

        if (row.HasBar)
        {
            var hpText = percent ? $"{row.Hp}%" : $"{row.Hp}/{row.HpMax}";
            var hpSize = ImGui.CalcTextSize(hpText);
            var barY = line1Y + lineHeight + 4f * scale;
            var barEnd = mainEnd - hpSize.X - 8f * scale;
            var barMax = percent ? 100 : row.HpMax;
            DrawBar(dl, new Vector2(textX, barY), Math.Max(0f, barEnd - textX), barHeight, row, barMax, fade);
            dl.AddText(new Vector2(mainEnd - hpSize.X, barY + (barHeight - hpSize.Y) / 2f),
                ImGui.GetColorU32(MasterEventTheme.TextSecondary with { W = fade }), hpText);
        }

        // Contrôles : - et + collés, le dé, puis le menu propre à la ligne.
        var controlX = pos.X + width - pad - controlsWidth;
        var controlY = pos.Y + (height - frame) / 2f;
        if (row.AdjustHp != null)
        {
            var step = ImGui.GetIO().KeyShift ? 5 : 1;
            ImGui.SetCursorScreenPos(new Vector2(controlX, controlY));
            if (StepButton(FontAwesomeIcon.Minus, "hp_down", Loc.Get(percent ? "Scene.HpDownPct" : "Scene.HpDown"), frame))
                row.AdjustHp(-step);
            ImGui.SetCursorScreenPos(new Vector2(controlX + frame + 2f * scale, controlY));
            if (StepButton(FontAwesomeIcon.Plus, "hp_up", Loc.Get(percent ? "Scene.HpUpPct" : "Scene.HpUp"), frame))
                row.AdjustHp(step);
            controlX += stepWidth + spacing;
        }

        if (row.Roll != null)
        {
            ImGui.SetCursorScreenPos(new Vector2(controlX, controlY));
            bool roll;
            using (ImRaii.Disabled(!row.RollEnabled))
                roll = Buttons.QuietIcon(FontAwesomeIcon.Dice, "roll", row.RollTooltip);
            if (!row.RollEnabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(row.RollTooltip);

            if (roll)
            {
                if (row.RollEntity?.Stats is { Count: > 0 }) ImGui.OpenPopup("##row_roll");
                else row.Roll(null);
            }

            if (row.RollEntity != null && ImGui.BeginPopup("##row_roll"))
            {
                DiceControls.DrawRollStatMenu(row.RollEntity, row.Id, statId => row.Roll(statId), withHeader: true);
                ImGui.EndPopup();
            }
            controlX += rollWidth + spacing;
        }

        if (row.Menu != null)
        {
            ImGui.SetCursorScreenPos(new Vector2(controlX, controlY));
            row.Menu();
        }

        if (rowHovered && !tooltipShown && row.Tooltip != null)
            ImGui.SetTooltip(row.Tooltip);

        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + height));
        ImGui.Dummy(new Vector2(width, 0f));
        ImGui.PopID();
        return clicked;
    }

    private static void DrawGlyph(ImDrawListPtr dl, Model row, Vector2 min, float size, float fade, Vector4 background)
    {
        var max = min + new Vector2(size, size);
        if (row.WaymarkIcon != 0)
        {
            var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(row.WaymarkIcon)).GetWrapOrEmpty();
            dl.AddImage(wrap.Handle, min, max, Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, fade)));
            return;
        }

        if (row.Portrait != null)
        {
            DrawPortrait(dl, row.Portrait, min, size, fade);
            if (row.GlyphText != null)
                DrawBadge(dl, row.GlyphText, row.GlyphColor, max, size, fade, background);
            return;
        }

        var rounding = MasterEventTheme.RadiusControl * ImGuiHelpers.GlobalScale;
        dl.AddRectFilled(min, max, ImGui.GetColorU32(row.GlyphColor with { W = 0.16f * fade }), rounding);

        if (row.GlyphText != null)
        {
            var textSize = ImGui.CalcTextSize(row.GlyphText);
            dl.AddText(min + (new Vector2(size, size) - textSize) / 2f, ImGui.GetColorU32(row.GlyphColor with { W = fade }),
                row.GlyphText);
            return;
        }

        DrawIcon(dl, row.GlyphIcon, min, size, row.GlyphColor with { W = fade });
    }

    // Portrait du joueur, ou à défaut le même carré teinté qu'une ligne d'acteur.
    public static void DrawAvatar(ImDrawListPtr dl, IDalamudTextureWrap? portrait, Vector2 min, float size,
        Vector4 color, float alpha = 1f)
    {
        if (portrait != null)
        {
            DrawPortrait(dl, portrait, min, size, alpha);
            return;
        }

        var rounding = MasterEventTheme.RadiusControl * ImGuiHelpers.GlobalScale;
        dl.AddRectFilled(min, min + new Vector2(size, size), ImGui.GetColorU32(color with { W = 0.16f * alpha }), rounding);
        DrawIcon(dl, FontAwesomeIcon.User, min, size, color with { W = alpha });
    }

    public static void DrawPortrait(ImDrawListPtr dl, IDalamudTextureWrap portrait, Vector2 min, float size, float alpha = 1f)
    {
        var (uv0, uv1) = UmbraPortraitCache.CoverUv(portrait.Width, portrait.Height, size, size);
        var rounding = MasterEventTheme.RadiusControl * ImGuiHelpers.GlobalScale;
        dl.AddImageRounded(portrait.Handle, min, min + new Vector2(size, size), uv0, uv1,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)), rounding);
    }

    // Lettre du sous-groupe à cheval sur le coin bas droit, cerclée du fond de la ligne pour se
    // détacher du portrait.
    private static void DrawBadge(ImDrawListPtr dl, string text, Vector4 color, Vector2 corner, float glyphSize,
        float fade, Vector4 background)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * 0.66f;
        var textSize = ImGui.CalcTextSize(text) * (fontSize / ImGui.GetFontSize());
        var height = Math.Max(glyphSize * 0.5f, textSize.Y + 1f * scale);
        var width = Math.Max(height, textSize.X + 4f * scale);
        var max = corner + new Vector2(3f * scale, 3f * scale);
        var min = max - new Vector2(width, height);
        var ring = 1.5f * scale;

        dl.AddRectFilled(min - new Vector2(ring, ring), max + new Vector2(ring, ring),
            ImGui.GetColorU32(background with { W = fade }), height / 2f + ring);
        dl.AddRectFilled(min, max, ImGui.GetColorU32(color with { W = fade }), height / 2f);
        dl.AddText(font, fontSize, min + (new Vector2(width, height) - textSize) / 2f,
            ImGui.GetColorU32(MasterEventTheme.ThemeWindowBg with { W = fade }), text);
    }

    // Icône centrée dans un carré de côté `size`.
    private static void DrawIcon(ImDrawListPtr dl, FontAwesomeIcon icon, Vector2 min, float size, Vector4 color)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            var text = icon.ToIconString();
            var iconSize = ImGui.CalcTextSize(text);
            dl.AddText(min + (new Vector2(size, size) - iconSize) / 2f, ImGui.GetColorU32(color), text);
        }
    }

    private static void DrawBar(ImDrawListPtr dl, Vector2 pos, float width, float height, Model row, int barMax,
        float fade)
    {
        if (width <= 0f) return;

        var rounding = height / 2f;
        dl.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f)), rounding);

        var max = barMax > 0 ? barMax : 100;
        var ratio = Math.Clamp((float)row.Hp / max, 0f, 1f);
        if (ratio > 0f)
            dl.AddRectFilled(pos, pos + new Vector2(width * ratio, height), ImGui.GetColorU32(row.BarColor with { W = fade }), rounding);

        if (row.Shield <= 0) return;
        var shieldRatio = Math.Clamp((float)row.Shield / max, 0f, 1f - ratio);
        if (shieldRatio > 0f)
            dl.AddRectFilled(pos + new Vector2(width * ratio, 0f), pos + new Vector2(width * (ratio + shieldRatio), height),
                ImGui.GetColorU32(MasterEventTheme.ShieldOverlayColor with { W = 0.7f * fade }), rounding);
    }

    private static bool StepButton(FontAwesomeIcon icon, string id, string tooltip, float size)
    {
        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            clicked = ImGui.Button(icon.ToIconString() + "##" + id, new Vector2(size, size));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }
}
