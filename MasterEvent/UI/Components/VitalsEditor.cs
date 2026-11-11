using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;

namespace MasterEvent.UI.Components;

// Ce que l'éditeur de vitales lit et modifie. Un marqueur ou un PNJ s'écrit en direct et la
// synchronisation repère le changement ; un joueur passe par la session, qui borne et diffuse.
public interface IVitalsTarget
{
    int Hp { get; }
    int HpMax { get; }
    int Shield { get; }

    // Couleur de la barre de PV : l'attitude d'un marqueur ou d'un PNJ, le bleu joueur pour un joueur.
    Vector4 HpColor { get; }

    // Faux pour un PNJ : seuls les marqueurs et les joueurs portent des PE.
    bool HasMp { get; }
    int Mp { get; }
    int MpMax { get; }

    List<CustomCounter>? Counters { get; }

    int TempModifier { get; }

    // Faux pour un PNJ : la durée d'un bonus en tours n'existe que pour les marqueurs et les joueurs.
    bool HasTempTurns { get; }
    int TempModTurns { get; }

    void SetHp(int value);

    // Fixe le maximum et remet la jauge au plein.
    void SetHpMax(int value);
    void SetShield(int value);
    void SetMp(int value);
    void SetMpMax(int value);
    void SetTempModifier(int value);
    void SetTempModTurns(int value);
    void ResetTempModifier();

    // Appelé après la modification sur place d'un compteur.
    void CountersChanged();
}

// Marqueur ou PNJ. `marker` n'est renseigné que pour un marqueur, seul à porter des PE et une
// durée de bonus en tours.
public sealed class EntityVitals(IVitalEntity entity, MarkerData? marker) : IVitalsTarget
{
    public int Hp => entity.Hp;
    public int HpMax => entity.HpMax;
    public int Shield => entity.Shield;
    public Vector4 HpColor => AttitudeStyle.Color(entity.Attitude);
    public bool HasMp => marker != null;
    public int Mp => marker?.Mp ?? 0;
    public int MpMax => marker?.MpMax ?? 0;
    public List<CustomCounter>? Counters => entity.Counters;
    public int TempModifier => entity.TempModifier;
    public bool HasTempTurns => marker != null;
    public int TempModTurns => marker?.TempModTurns ?? 0;

    public void SetHp(int value) => entity.Hp = value;

    public void SetHpMax(int value)
    {
        entity.HpMax = value;
        entity.Hp = value;
    }

    public void SetShield(int value) => entity.Shield = value;

    public void SetMp(int value)
    {
        if (marker != null) marker.Mp = value;
    }

    public void SetMpMax(int value)
    {
        if (marker == null) return;
        marker.MpMax = value;
        marker.Mp = value;
    }

    public void SetTempModifier(int value) => entity.TempModifier = value;

    public void SetTempModTurns(int value)
    {
        if (marker != null) marker.TempModTurns = value;
    }

    public void ResetTempModifier()
    {
        entity.TempModifier = 0;
        if (marker != null) marker.TempModTurns = 0;
    }

    public void CountersChanged() { }
}

// Joueur du groupe : chaque modification passe par la session, qui la borne et la diffuse.
public sealed class PlayerVitals(PlayerData player, SessionManager session) : IVitalsTarget
{
    public int Hp => player.Hp;
    public int HpMax => player.HpMax;
    public int Shield => player.Shield;
    public Vector4 HpColor => MasterEventTheme.PlayerColor;
    public bool HasMp => true;
    public int Mp => player.Mp;
    public int MpMax => player.MpMax;
    public List<CustomCounter>? Counters => player.Counters;
    public int TempModifier => player.TempModifier;
    public bool HasTempTurns => true;
    public int TempModTurns => player.TempModTurns;

    public void SetHp(int value) => session.SetPlayerHp(player.Hash, value);
    public void SetHpMax(int value) => session.SetPlayerHpMax(player.Hash, value);
    public void SetShield(int value) => session.SetPlayerShield(player.Hash, value);
    public void SetMp(int value) => session.SetPlayerMp(player.Hash, value);
    public void SetMpMax(int value) => session.SetPlayerMpMax(player.Hash, value);

    public void SetTempModifier(int value)
    {
        player.TempModifier = value;
        session.BroadcastPlayerUpdate();
    }

    public void SetTempModTurns(int value)
    {
        player.TempModTurns = value;
        session.BroadcastPlayerUpdate();
    }

    public void ResetTempModifier()
    {
        player.TempModifier = 0;
        player.TempModTurns = 0;
        session.BroadcastPlayerUpdate();
    }

    public void CountersChanged() => session.BroadcastPlayerUpdate();
}

// Éditeur de vitales commun aux marqueurs, aux PNJ et aux joueurs du groupe : bouclier, PV, PE,
// compteurs et bonus ponctuel. Chaque ligne suit le même gabarit, [-] barre [+] [crayon], à la
// hauteur d'un bouton ; la colonne du crayon reste réservée quand il n'y en a pas, pour que toutes
// les barres commencent et s'arrêtent au même endroit. `id` rend les identifiants ImGui uniques.
public static class VitalsEditor
{
    private const int MaxPoints = 99999;

    // Bouclier, PV puis PE, dans cet ordre.
    public static void DrawBars(IVitalsTarget target, string id, HpMode hpMode, bool showShield,
        bool showMp, HpMode mpMode)
    {
        if (showShield) DrawShieldRow(target, id, hpMode);
        DrawHpRow(target, id, hpMode, showShield);
        if (showMp && target.HasMp) DrawMpRow(target, id, mpMode);
    }

    public static void DrawShieldRow(IVitalsTarget target, string id, HpMode hpMode)
    {
        var shieldMax = hpMode == HpMode.Percentage ? 100 : target.HpMax;

        if (StepButton(FontAwesomeIcon.Minus, $"sh_dec_{id}"))
            target.SetShield(Math.Max(0, target.Shield - 1));
        ImGui.SameLine();
        DrawValueBox($"{Loc.Get("Marker.Shield")}: {target.Shield}", MasterEventTheme.ShieldOverlayColor, BarWidth());
        ImGui.SameLine();
        if (StepButton(FontAwesomeIcon.Plus, $"sh_inc_{id}"))
            target.SetShield(Math.Min(shieldMax, target.Shield + 1));
    }

    public static void DrawHpRow(IVitalsTarget target, string id, HpMode hpMode, bool showShield)
    {
        var hpClampMax = hpMode == HpMode.Percentage ? 100 : target.HpMax;

        if (StepButton(FontAwesomeIcon.Minus, $"hp_dec_{id}"))
            target.SetHp(Math.Max(0, target.Hp - 1));
        ImGui.SameLine();
        HpBar.Draw(target.Hp, target.HpColor, BarWidth(), hpMode, target.HpMax, ImGui.GetFrameHeight(),
            shield: showShield ? target.Shield : 0);
        ImGui.SameLine();
        if (StepButton(FontAwesomeIcon.Plus, $"hp_inc_{id}"))
            target.SetHp(Math.Min(hpClampMax, target.Hp + 1));

        if (hpMode == HpMode.Points)
        {
            ImGui.SameLine();
            DrawMaxEditor("hpmax", id, Loc.Get("Marker.HpMax"), target.HpMax, target.SetHpMax);
        }
    }

    public static void DrawMpRow(IVitalsTarget target, string id, HpMode mpMode)
    {
        var mpClampMax = mpMode == HpMode.Percentage ? 100 : target.MpMax;

        if (StepButton(FontAwesomeIcon.Minus, $"mp_dec_{id}"))
            target.SetMp(Math.Max(0, target.Mp - 1));
        ImGui.SameLine();
        HpBar.DrawMpBar(target.Mp, BarWidth(), mpMode, target.MpMax, ImGui.GetFrameHeight());
        ImGui.SameLine();
        if (StepButton(FontAwesomeIcon.Plus, $"mp_inc_{id}"))
            target.SetMp(Math.Min(mpClampMax, target.Mp + 1));

        if (mpMode == HpMode.Points)
        {
            ImGui.SameLine();
            DrawMaxEditor("mpmax", id, Loc.Get("Marker.MpMax"), target.MpMax, target.SetMpMax);
        }
    }

    public static void DrawCounters(IVitalsTarget target, string id)
    {
        var counters = target.Counters;
        if (counters == null) return;

        for (var ci = 0; ci < counters.Count; ci++)
        {
            var counter = counters[ci];

            if (StepButton(FontAwesomeIcon.Minus, $"cnt_dec_{id}_{ci}"))
            {
                counter.Value = Math.Max(0, counter.Value - 1);
                target.CountersChanged();
            }
            ImGui.SameLine();
            CounterBar.Draw(counter, BarWidth(), ImGui.GetFrameHeight());
            ImGui.SameLine();
            if (StepButton(FontAwesomeIcon.Plus, $"cnt_inc_{id}_{ci}"))
            {
                counter.Value = Math.Min(counter.Max, counter.Value + 1);
                target.CountersChanged();
            }
            ImGui.SameLine();

            var popupId = $"cnt_popup_{id}_{ci}";
            if (Buttons.QuietIcon(FontAwesomeIcon.Pen, $"cnt_edit_{id}_{ci}", Loc.Get("Counter.Edit")))
                ImGui.OpenPopup(popupId);
            if (ImGui.BeginPopup(popupId))
            {
                ImGui.TextUnformatted(Loc.Get("Counter.Edit"));
                ImGui.Separator();
                var cMax = counter.Max;
                ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt($"##cnt_max_{id}_{ci}", ref cMax))
                {
                    if (cMax < 1) cMax = 1;
                    counter.Max = cMax;
                    counter.Value = cMax;
                    target.CountersChanged();
                }
                ImGui.EndPopup();
            }
        }
    }

    // Bouton du bonus ponctuel et sa valeur à côté, sur la ligne que l'appelant a choisie.
    public static void DrawTempModifier(IVitalsTarget target, string id)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button($"{FontAwesomeIcon.Magic.ToIconString()}##temp_edit_{id}"))
                ImGui.OpenPopup($"temp_popup_{id}");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Marker.TempMod"));
            ImGui.EndTooltip();
        }

        if (target.TempModifier != 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(TempColor(target.TempModifier), StatDisplay.Signed(target.TempModifier));
            if (target.TempModTurns > 0)
            {
                ImGui.SameLine(0, 2f * ImGuiHelpers.GlobalScale);
                ImGui.TextColored(MasterEventTheme.TextSecondary, TurnText.TempRounds(target.TempModTurns));
            }
        }

        DrawTempPopup(target, id);
    }

    // Le bonus ponctuel sur une ligne à lui : libellé à gauche, valeur à droite, et sa baguette dans
    // la colonne des crayons.
    public static void DrawTempModifierRow(IVitalsTarget target, string id)
    {
        var style = ImGui.GetStyle();
        var rowStart = ImGui.GetCursorPosX();
        var rowWidth = LayoutControls.RemainingCardWidth;
        var iconX = rowStart + rowWidth - EditButtonWidth();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Marker.TempMod"));

        var value = StatDisplay.Signed(target.TempModifier);
        var turns = target.TempModifier != 0 && target.TempModTurns > 0 ? TurnText.TempRounds(target.TempModTurns) : null;
        var turnsGap = 3f * ImGuiHelpers.GlobalScale;
        var valueWidth = ImGui.CalcTextSize(value).X + (turns != null ? turnsGap + ImGui.CalcTextSize(turns).X : 0f);
        // Coordonnées de SameLine : relatives à la fenêtre, comme GetCursorPosX.
        var labelEnd = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + ImGui.GetScrollX();
        var valueX = Math.Max(labelEnd + style.ItemSpacing.X, iconX - style.ItemSpacing.X - valueWidth);

        ImGui.SameLine(valueX);
        ImGui.TextColored(target.TempModifier == 0 ? MasterEventTheme.TextDim : TempColor(target.TempModifier), value);
        if (turns != null)
        {
            ImGui.SameLine(0, turnsGap);
            ImGui.TextColored(MasterEventTheme.TextSecondary, turns);
        }

        ImGui.SameLine(Math.Max(valueX + valueWidth + style.ItemSpacing.X, iconX));
        if (Buttons.QuietIcon(FontAwesomeIcon.Magic, $"temp_edit_{id}", Loc.Get("Marker.TempMod")))
            ImGui.OpenPopup($"temp_popup_{id}");

        DrawTempPopup(target, id);
    }

    private static Vector4 TempColor(int modifier)
        => modifier > 0 ? MasterEventTheme.SuccessColor : MasterEventTheme.DangerColor;

    private static void DrawTempPopup(IVitalsTarget target, string id)
    {
        if (!ImGui.BeginPopup($"temp_popup_{id}")) return;

        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Marker.TempMod"));
        ImGui.Separator();
        ImGui.TextUnformatted(Loc.Get("Marker.TempModValue"));
        ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
        var tempMod = target.TempModifier;
        if (ImGui.InputInt($"##temp_val_{id}", ref tempMod))
            target.SetTempModifier(tempMod);

        if (target.HasTempTurns)
        {
            ImGui.TextUnformatted(Loc.Get("Marker.TempModTurns"));
            ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
            var tempTurns = target.TempModTurns;
            if (ImGui.InputInt($"##temp_turns_{id}", ref tempTurns))
            {
                if (tempTurns < 0) tempTurns = 0;
                target.SetTempModTurns(tempTurns);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Marker.TempModTurnsHint"));
                ImGui.EndTooltip();
            }
        }

        ImGui.Spacing();
        if (ImGui.SmallButton(Loc.Get("Marker.TempModReset") + $"##temp_reset_{id}"))
            target.ResetTempModifier();
        ImGui.EndPopup();
    }

    // Crayon qui ouvre la saisie du maximum ; `kind` vaut « hpmax » ou « mpmax ».
    private static void DrawMaxEditor(string kind, string id, string label, int current, Action<int> apply)
    {
        var popupId = $"{kind}_popup_{id}";
        if (Buttons.QuietIcon(FontAwesomeIcon.Pen, $"{kind}_edit_{id}", label))
            ImGui.OpenPopup(popupId);

        if (!ImGui.BeginPopup(popupId)) return;

        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
        var edit = current;
        if (ImGui.InputInt($"##{kind}_{id}", ref edit))
        {
            if (edit < 1) edit = 1;
            if (edit > MaxPoints) edit = MaxPoints;
            apply(edit);
        }
        ImGui.EndPopup();
    }

    private static bool StepButton(FontAwesomeIcon icon, string id)
    {
        var size = ImGui.GetFrameHeight();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            return ImGui.Button(icon.ToIconString() + "##" + id, new Vector2(size, size));
    }

    // Valeur sans jauge (le bouclier) dans le même cadre qu'une barre, pour garder l'alignement.
    private static void DrawValueBox(string text, Vector4 color, float width)
    {
        var height = ImGui.GetFrameHeight();
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.GetColorU32(MasterEventTheme.ThemeFrameBg), 3f);
        var size = ImGui.CalcTextSize(text);
        dl.AddText(pos + new Vector2((width - size.X) / 2f, (height - size.Y) / 2f), ImGui.GetColorU32(color), text);
        ImGui.Dummy(new Vector2(width, height));
    }

    private static float EditButtonWidth()
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            return ImGui.CalcTextSize(FontAwesomeIcon.Pen.ToIconString()).X + ImGui.GetStyle().FramePadding.X * 2;
    }

    // Largeur d'une barre entre ses boutons - et +, colonne du crayon comprise à droite.
    private static float BarWidth()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        return LayoutControls.RemainingCardWidth - ImGui.GetFrameHeight() - spacing - (EditButtonWidth() + spacing);
    }
}
