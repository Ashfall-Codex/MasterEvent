using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI;


public static class MasterEventTheme
{

    public enum GlassLevel
    {
        Regular,
        Clear,
        Opaque,
    }
    public const float MinOpacity = 0.30f;
    public const float MaxGlassOpacity = 1f;
    private const float MinBlurStrength = 1f;
    private const float MaxBlurStrength = 4.5f;
    private const float BlurFadeRange = 0.25f;
    private const float ClearBaseAlpha = 0.55f;
    private const float MinResolvedAlpha = 0.18f;

    private static Configuration? glassConfig;
    public static void AttachConfiguration(Configuration configuration) => glassConfig = configuration;
    public static BackdropBlur? Backdrop { get; set; }
    private static float GlassSetting => Math.Clamp(glassConfig?.UiGlass ?? 1f, 0f, 1f);
    public static float GlassOpacity
    {
        get
        {
            if (glassConfig is null) return 1f;
            return MinOpacity + (MaxGlassOpacity - MinOpacity) * GlassSetting;
        }
    }

    public static float BlurStrength => MinBlurStrength + (MaxBlurStrength - MinBlurStrength) * GlassSetting;
    public static float BlurOpacity => glassConfig is null ? 0f : Math.Clamp(GlassSetting / BlurFadeRange, 0f, 1f);

    public static float GlassFromOpacity(float opacity)
        => Math.Clamp((opacity - MinOpacity) / (MaxGlassOpacity - MinOpacity), 0f, 1f);

    public static float GlassAlpha(GlassLevel level)
    {
        if (level == GlassLevel.Opaque) return 1f;

        var baseAlpha = level == GlassLevel.Clear ? ClearBaseAlpha : 1f;
        return Math.Clamp(baseAlpha * GlassOpacity, MinResolvedAlpha, 1f);
    }

    private const float CardBaseAlpha = 0.45f;
    private const float CardCompensation = 0.45f;
    public static float CardAlpha(GlassLevel level = GlassLevel.Regular)
    {
        var window = GlassAlpha(level);
        return Math.Clamp(CardBaseAlpha + (1f - window) * CardCompensation, 0f, 1f);
    }

    public static Vector4 WithAlpha(Vector4 color, float alpha) => color with { W = alpha };


    public static readonly Vector4 AccentColor = new(0xE6 / 255f, 0x45 / 255f, 0x45 / 255f, 1f);
    public static readonly Vector4 AccentHoverColor = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 1f);
    public static readonly Vector4 ThemeWindowBg = new(0.11f, 0.11f, 0.11f, 1f);
    public static readonly Vector4 ThemeChildBg = new(0f, 0f, 0f, 0f);
    public static readonly Vector4 ThemeBorder = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 1f);
    public static readonly Vector4 ThemeSeparator = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 0.60f);
    public static readonly Vector4 ThemeTitleBar = new(0x3D / 255f, 0x1F / 255f, 0x1F / 255f, 1f);
    public static readonly Vector4 ThemeFrameBg = new(0.135f, 0.113f, 0.113f, 1f);
    public static readonly Vector4 ThemeFrameBgHovered = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 0.7f);
    public static readonly Vector4 ThemeFrameBgActive = new(0x68 / 255f, 0x36 / 255f, 0x36 / 255f, 1f);
    public static readonly Vector4 ThemeButtonBg = new(0x3D / 255f, 0x1F / 255f, 0x1F / 255f, 1f);
    public static readonly Vector4 ThemeButtonHovered = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 1f);
    public static readonly Vector4 ThemeButtonActive = new(0x68 / 255f, 0x36 / 255f, 0x36 / 255f, 1f);
    public static readonly Vector4 ThemeHeaderBg = new(0x1C / 255f, 0x1C / 255f, 0x1C / 255f, 1f);
    public static readonly Vector4 ThemeHeaderHovered = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 0.7f);
    public static readonly Vector4 ThemeHeaderActive = new(0x68 / 255f, 0x36 / 255f, 0x36 / 255f, 1f);
    public static readonly Vector4 ThemeScrollbarBg = new(0.08f, 0.08f, 0.08f, 0.50f);
    public static readonly Vector4 ThemeScrollbarGrab = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 1f);
    public static readonly Vector4 ThemeScrollbarHover = new(0x68 / 255f, 0x36 / 255f, 0x36 / 255f, 1f);
    public static readonly Vector4 ThemeScrollbarActive = new(0x80 / 255f, 0x45 / 255f, 0x45 / 255f, 1f);
    public static readonly Vector4 ThemeTabNormal = new(0x1C / 255f, 0x1C / 255f, 0x1C / 255f, 0.90f);
    public static readonly Vector4 ThemeTabHovered = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 1f);
    public static readonly Vector4 ThemeTabActive = new(0x68 / 255f, 0x36 / 255f, 0x36 / 255f, 1f);
    public static readonly Vector4 AttitudeHostile = new(1.0f, 0.2f, 0.2f, 1f);
    public static readonly Vector4 AttitudeNeutral = new(1.0f, 0.75f, 0.2f, 1f);
    public static readonly Vector4 AttitudeFriendly = new(0.3f, 0.8f, 0.3f, 1f);
    // ── Texte ──
    // Ces gris portent un léger biais chaud : sur le fond rouge sombre du thème, un gris
    // neutre tire visiblement vers le vert. Trois niveaux couvrent tous les usages relevés.
    public static readonly Vector4 TextStrong = new(1f, 0.98f, 0.98f, 1f);
    public static readonly Vector4 TextSecondary = new(0.72f, 0.70f, 0.70f, 1f);
    public static readonly Vector4 MutedTextColor = new(0.62f, 0.59f, 0.59f, 1f);
    // Relevé pour atteindre 5,2:1 sur le fond de fenêtre : à 0,52 les astuces tombaient sous 4,5:1.
    public static readonly Vector4 TextDim = new(0.58f, 0.55f, 0.55f, 1f);

    // Texte couleur d'accent (titres de carte, liens, action teintée). L'accent lui-même, trop
    // sombre en texte (4,3:1), reste pour les fonds.
    public static readonly Vector4 AccentText = new(0xF0 / 255f, 0x6A / 255f, 0x6A / 255f, 1f);

    // En attente d'une décision (demande d'accès, approbation du MJ).
    public static readonly Vector4 PendingColor = new(0.9f, 0.75f, 0.3f, 1f);

    // Identité d'un PNJ (violet), distincte des joueurs (bleu) et des marqueurs (couleur
    // d'attitude) : un PNJ en accent rouge se confondrait avec un ennemi.
    public static readonly Vector4 NpcColor = new(0.68f, 0.50f, 0.92f, 1f);

    // Bonus temporaire, qui s'éteint au fil des tours.
    public static readonly Vector4 TempModifierColor = new(0.8f, 0.6f, 1f, 1f);

    // Sous-groupes d'un lobby, de A à H : mêmes couleurs côté MJ et côté joueur.
    public static readonly Vector4[] GroupColors =
    [
        new(0.4f, 0.7f, 1.0f, 1f),
        new(1.0f, 0.6f, 0.3f, 1f),
        new(0.5f, 0.9f, 0.5f, 1f),
        new(0.9f, 0.5f, 0.9f, 1f),
        new(1.0f, 0.9f, 0.4f, 1f),
        new(0.4f, 0.9f, 0.9f, 1f),
        new(1.0f, 0.5f, 0.5f, 1f),
        new(0.7f, 0.7f, 0.7f, 1f),
    ];

    public static Vector4 GroupColor(string label)
        => label.Length == 1 && label[0] is >= 'A' and <= 'H' ? GroupColors[label[0] - 'A'] : MutedTextColor;

    // ── État ──
    // Volontairement distincts de l'accent, qui désigne déjà l'élément courant :
    // un rouge qui veut dire « ici » et « attention » à la fois ne dit plus rien.
    public static readonly Vector4 WarningColor = new(0.95f, 0.55f, 0.15f, 1f);
    public static readonly Vector4 SuccessColor = new(0.20f, 0.80f, 0.20f, 1f);
    public static readonly Vector4 SuccessDimColor = new(0.22f, 0.55f, 0.24f, 1f);
    public static readonly Vector4 DangerColor = new(0.90f, 0.32f, 0.32f, 1f);

    // Bouton destructeur : fin de combat, départ d'alliance, suppression. Il doit se
    // distinguer d'un bouton ordinaire sans emprunter l'accent, qui désigne l'élément courant.
    public static readonly Vector4 DangerButtonBg = new(0.60f, 0.15f, 0.15f, 1f);
    public static readonly Vector4 DangerButtonHovered = new(0.70f, 0.20f, 0.20f, 1f);

    // Identité d'un joueur, par opposition à un PNJ ou à un marqueur.
    public static readonly Vector4 PlayerColor = new(0.227f, 0.604f, 1f, 0.8f);
    public static readonly Vector4 MpBarColor = new(0.2f, 0.4f, 0.9f, 1f);
    public static readonly Vector4 ShieldOverlayColor = new(0.6f, 0.85f, 1f, 0.7f);
    public static readonly Vector4 ThemeSliderGrab = new(0xE6 / 255f, 0x45 / 255f, 0x45 / 255f, 1f);
    public static readonly Vector4 ThemeSliderGrabActive = new(0xFF / 255f, 0x6B / 255f, 0x6B / 255f, 1f);
    public static readonly Vector4 ThemeCheckMark = new(0xFF / 255f, 0x6B / 255f, 0x6B / 255f, 1f);

    public const int ThemeColorCount = 26;
    public const int ThemeStyleVarCount = 5;
    public const float RadiusWindow = 10f;
    public const float RadiusCard = 6f;
    public const float RadiusControl = 4f;
    public static void PushTheme(float glassAlpha = 1f)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, RadiusWindow * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, RadiusCard * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, RadiusCard * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, RadiusControl * scale);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WithAlpha(ThemeWindowBg, glassAlpha));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ThemeChildBg);
        ImGui.PushStyleColor(ImGuiCol.Border, ThemeBorder);
        ImGui.PushStyleColor(ImGuiCol.Separator, ThemeSeparator);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, WithAlpha(ThemeTitleBar, glassAlpha));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, WithAlpha(ThemeTitleBar, glassAlpha));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, WithAlpha(ThemeTitleBar, glassAlpha));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, ThemeFrameBg);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ThemeFrameBgHovered);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ThemeFrameBgActive);
        ImGui.PushStyleColor(ImGuiCol.Button, ThemeButtonBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ThemeButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ThemeButtonActive);
        ImGui.PushStyleColor(ImGuiCol.Header, ThemeHeaderBg);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ThemeHeaderHovered);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ThemeHeaderActive);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, ThemeScrollbarBg);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, ThemeScrollbarGrab);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, ThemeScrollbarHover);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, ThemeScrollbarActive);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, ThemeSliderGrab);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, ThemeSliderGrabActive);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, ThemeCheckMark);
        ImGui.PushStyleColor(ImGuiCol.Tab, ThemeTabNormal);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, ThemeTabHovered);
        ImGui.PushStyleColor(ImGuiCol.TabActive, ThemeTabActive);
    }

    public static void PopTheme()
    {
        ImGui.PopStyleColor(ThemeColorCount);
        ImGui.PopStyleVar(ThemeStyleVarCount);
    }

    public static void DrawGlassSheen(ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding, float alpha)
    {
        if (alpha <= 0f) return;

        var height = max.Y - min.Y;
        if (height <= 2f || max.X - min.X <= 2f) return;

        var mid = new Vector2(max.X, min.Y + height * 0.42f);
        var lit = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.05f * alpha));
        var off = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0f));

        drawList.PushClipRect(min, max, true);
        drawList.AddRectFilledMultiColor(min, mid, lit, lit, off, off);
        drawList.PopClipRect();

        var inset = Math.Min(rounding, (max.X - min.X) / 2f);
        drawList.AddLine(
            new Vector2(min.X + inset, min.Y + 0.5f),
            new Vector2(max.X - inset, min.Y + 0.5f),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.14f * alpha)),
            1f);
    }
}
