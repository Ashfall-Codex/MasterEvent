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
    Attitude Attitude { get; }

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
    public Attitude Attitude => entity.Attitude;
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
    public Attitude Attitude => Attitude.Neutral;
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
// compteurs et bonus ponctuel. Chaque barre laisse à sa droite la largeur d'un crayon, pour que
// toutes s'arrêtent au même endroit. `id` rend les identifiants ImGui uniques par entité.
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
        var pmBtnW = ImGui.GetFrameHeight();
        var shield = target.Shield;
        var shieldMax = hpMode == HpMode.Percentage ? 100 : target.HpMax;

        if (ImGui.Button($"-##sh_dec_{id}", new Vector2(pmBtnW, 0)))
        {
            shield = Math.Max(0, shield - 1);
            target.SetShield(shield);
        }
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.ShieldOverlayColor, $"{Loc.Get("Marker.Shield")}: {target.Shield}");
        ImGui.SameLine();
        if (ImGui.Button($"+##sh_inc_{id}", new Vector2(pmBtnW, 0)))
        {
            shield = Math.Min(shieldMax, shield + 1);
            target.SetShield(shield);
        }
    }

    public static void DrawHpRow(IVitalsTarget target, string id, HpMode hpMode, bool showShield)
    {
        var pmBtnW = ImGui.GetFrameHeight();
        var hp = target.Hp;
        var hpClampMax = hpMode == HpMode.Percentage ? 100 : target.HpMax;

        if (hpMode == HpMode.Points)
        {
            DrawMaxEditor("hpmax", id, Loc.Get("Marker.HpMax"), target.HpMax, target.SetHpMax);
            ImGui.SameLine();
        }

        if (ImGui.Button($"-##hp_dec_{id}", new Vector2(pmBtnW, 0)))
        {
            hp = Math.Max(0, hp - 1);
            target.SetHp(hp);
        }
        ImGui.SameLine();

        HpBar.Draw(target.Hp, target.Attitude, BarWidth(), hpMode, target.HpMax,
            shield: showShield ? target.Shield : 0);
        ImGui.SameLine();

        if (ImGui.Button($"+##hp_inc_{id}", new Vector2(pmBtnW, 0)))
        {
            hp = Math.Min(hpClampMax, hp + 1);
            target.SetHp(hp);
        }
    }

    public static void DrawMpRow(IVitalsTarget target, string id, HpMode mpMode)
    {
        var pmBtnW = ImGui.GetFrameHeight();
        var mp = target.Mp;
        var mpClampMax = mpMode == HpMode.Percentage ? 100 : target.MpMax;

        if (mpMode == HpMode.Points)
        {
            DrawMaxEditor("mpmax", id, Loc.Get("Marker.MpMax"), target.MpMax, target.SetMpMax);
            ImGui.SameLine();
        }

        if (ImGui.Button($"-##mp_dec_{id}", new Vector2(pmBtnW, 0)))
        {
            mp = Math.Max(0, mp - 1);
            target.SetMp(mp);
        }
        ImGui.SameLine();
        HpBar.DrawMpBar(target.Mp, BarWidth(), mpMode, target.MpMax);
        ImGui.SameLine();
        if (ImGui.Button($"+##mp_inc_{id}", new Vector2(pmBtnW, 0)))
        {
            mp = Math.Min(mpClampMax, mp + 1);
            target.SetMp(mp);
        }
    }

    public static void DrawCounters(IVitalsTarget target, string id)
    {
        var counters = target.Counters;
        if (counters == null) return;

        var pmBtnW = ImGui.GetFrameHeight();
        for (var ci = 0; ci < counters.Count; ci++)
        {
            var counter = counters[ci];

            // Crayon à la même place que celui des PV.
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button($"{FontAwesomeIcon.Pen.ToIconString()}##cnt_edit_{id}_{ci}"))
                    ImGui.OpenPopup($"cnt_popup_{id}_{ci}");
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Counter.Edit"));
                ImGui.EndTooltip();
            }
            if (ImGui.BeginPopup($"cnt_popup_{id}_{ci}"))
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
            ImGui.SameLine();

            if (ImGui.Button($"-##cnt_dec_{id}_{ci}", new Vector2(pmBtnW, 0)))
            {
                counter.Value = Math.Max(0, counter.Value - 1);
                target.CountersChanged();
            }
            ImGui.SameLine();
            CounterBar.Draw(counter, BarWidth());
            ImGui.SameLine();
            if (ImGui.Button($"+##cnt_inc_{id}_{ci}", new Vector2(pmBtnW, 0)))
            {
                counter.Value = Math.Min(counter.Max, counter.Value + 1);
                target.CountersChanged();
            }
        }
    }

    // Bouton du bonus ponctuel, sa valeur à côté et le popup d'édition. L'appelant choisit la ligne.
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
            var tempStr = target.TempModifier >= 0 ? $"+{target.TempModifier}" : target.TempModifier.ToString();
            var tempColor = target.TempModifier > 0
                ? MasterEventTheme.SuccessColor
                : MasterEventTheme.DangerColor;
            ImGui.TextColored(tempColor, tempStr);
            if (target.TempModTurns > 0)
            {
                ImGui.SameLine(0, 2f * ImGuiHelpers.GlobalScale);
                ImGui.TextColored(MasterEventTheme.TextSecondary, $"({target.TempModTurns}t)");
            }
        }

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

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button($"{FontAwesomeIcon.Pen.ToIconString()}##{kind}_edit_{id}"))
                ImGui.OpenPopup(popupId);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(label);
            ImGui.EndTooltip();
        }

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

    // Largeur d'une barre entre ses boutons - et +, crayon compris à droite.
    private static float BarWidth()
    {
        var style = ImGui.GetStyle();
        float editBtnW;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            editBtnW = ImGui.CalcTextSize(FontAwesomeIcon.Pen.ToIconString()).X + style.FramePadding.X * 2;

        return ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - style.ItemSpacing.X
               - (editBtnW + style.ItemSpacing.X);
    }
}
