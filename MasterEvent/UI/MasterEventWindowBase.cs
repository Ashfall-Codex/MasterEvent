using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace MasterEvent.UI;

public abstract class MasterEventWindowBase(string name, ImGuiWindowFlags flags = ImGuiWindowFlags.None, bool forceMainWindow = false)
    : Window(name, flags, forceMainWindow)
{

    protected virtual MasterEventTheme.GlassLevel WindowGlassLevel => MasterEventTheme.GlassLevel.Regular;

    protected virtual bool UseBackdropBlur => true;

    // Montrée avec un personnage, la fenêtre lui reste liée : à l'écran titre et à la sélection du
    // personnage, elle se masque, puis revient avec lui. Ouverte par un geste hors personnage, elle s'affiche.
    public bool BoundToCharacter { get; set; }

    public override bool DrawConditions()
    {
        if (!Plugin.HasCharacter) return !BoundToCharacter;
        BoundToCharacter = true;
        return true;
    }

    public bool IsHiddenWithoutCharacter => IsOpen && BoundToCharacter && !Plugin.HasCharacter;

    // Ouverture voulue, par le joueur ou en réponse à son geste : visible tout de suite, même hors personnage.
    public void ShowNow()
    {
        BoundToCharacter = false;
        IsOpen = true;
    }

    // Une fenêtre masquée faute de personnage se montre au lieu de se fermer.
    public void ToggleByUser()
    {
        if (IsHiddenWithoutCharacter) ShowNow();
        else IsOpen = !IsOpen;
    }

    public override void OnClose() => BoundToCharacter = false;

    public override void PreDraw()
    {
        var glass = MasterEventTheme.GlassAlpha(WindowGlassLevel);
        UpdateTitleBarOverlap();
        MasterEventTheme.PushTheme(glass, titleBarOverlapped ? 1f : glass);
    }

    public override void PostDraw()
    {
        MasterEventTheme.PopTheme();
    }

    public sealed override void Draw()
    {
        RecordFootprint();
        opaqueRegions.Clear();
        if (!Flags.HasFlag(ImGuiWindowFlags.NoBackground))
        {
            CollectOpaqueRegions(out var own);
            DrawOverlapBackdrop(own);
        }
        DrawWindowSheen();
        DrawContents();
    }

    // Une fenêtre retirée du WindowSystem (fiche détachée fermée) ne doit plus compter parmi celles du dessous.
    public void ForgetFootprint()
    {
        lock (FootprintsLock)
            Footprints.Remove(this);
    }

    // Une fenêtre du plugin posée sur une autre remplit d'opaque la seule zone qui la recouvre, le reste
    // garde le verre. Position, visibilité et ordre sont relus en direct depuis ImGui à chaque image.
    private static readonly Dictionary<MasterEventWindowBase, WindowFootprint> Footprints = [];
    private static readonly Lock FootprintsLock = new();
    private readonly List<(Vector2 Min, Vector2 Max)> opaqueRegions = [];
    private bool titleBarOverlapped;

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct WindowFootprint(ImGuiWindowPtr Window, float TitleBarHeight);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct LiveFootprint(Vector2 Min, Vector2 Max, float TitleBarHeight, int FocusOrder, bool Resizable);

    private static unsafe bool TryReadLive(in WindowFootprint footprint, out LiveFootprint live)
    {
        live = default;
        var window = footprint.Window;
        if (window.Handle == null) return false;
        if (!(window.Active || window.WasActive) || window.Hidden) return false;
        if (window.DockIsActive && !window.DockTabIsVisible) return false;
        if ((window.Flags & (ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs)) != ImGuiWindowFlags.None) return false;

        var min = window.Pos;
        var max = min + window.Size;
        if (max.X <= min.X || max.Y <= min.Y) return false;
        live = new LiveFootprint(min, max, footprint.TitleBarHeight, window.FocusOrder,
            !window.Collapsed && (window.Flags & ImGuiWindowFlags.NoResize) == ImGuiWindowFlags.None);
        return true;
    }

    private bool TryReadOwn(out LiveFootprint own)
    {
        own = default;
        return Footprints.TryGetValue(this, out var footprint) && TryReadLive(footprint, out own);
    }

    private void UpdateTitleBarOverlap()
    {
        titleBarOverlapped = false;
        lock (FootprintsLock)
        {
            if (!TryReadOwn(out var own) || own.TitleBarHeight <= 0f) return;

            var titleMin = own.Min;
            var titleMax = new Vector2(own.Max.X, MathF.Min(own.Max.Y, own.Min.Y + own.TitleBarHeight));
            foreach (var (other, footprint) in Footprints)
            {
                if (ReferenceEquals(other, this) || !TryReadLive(footprint, out var below) || below.FocusOrder >= own.FocusOrder) continue;
                if (MathF.Min(titleMax.X, below.Max.X) > MathF.Max(titleMin.X, below.Min.X)
                    && MathF.Min(titleMax.Y, below.Max.Y) > MathF.Max(titleMin.Y, below.Min.Y))
                {
                    titleBarOverlapped = true;
                    return;
                }
            }
        }
    }

    private void CollectOpaqueRegions(out LiveFootprint own)
    {
        opaqueRegions.Clear();
        var border = ImGui.GetStyle().WindowBorderSize;
        lock (FootprintsLock)
        {
            if (!TryReadOwn(out own)) return;
            var areaMin = new Vector2(own.Min.X + border, own.Min.Y + MathF.Max(border, own.TitleBarHeight));
            var areaMax = own.Max - new Vector2(border);
            if (areaMax.X <= areaMin.X || areaMax.Y <= areaMin.Y) return;

            // La poignée de redimensionnement reste visible.
            var grip = own.Resizable ? MathF.Floor(ImGui.GetFontSize() * 1.35f) : 0f;
            foreach (var (other, footprint) in Footprints)
            {
                if (ReferenceEquals(other, this) || !TryReadLive(footprint, out var below) || below.FocusOrder >= own.FocusOrder) continue;
                var min = Vector2.Max(areaMin, below.Min);
                var max = Vector2.Min(areaMax, below.Max);
                if (max.X <= min.X || max.Y <= min.Y) continue;
                AddExcludingCorner(min, max, own.Max - new Vector2(grip), own.Max);
            }
        }
    }

    private void AddExcludingCorner(Vector2 min, Vector2 max, Vector2 cornerMin, Vector2 cornerMax)
    {
        var hitsCorner = cornerMax.X > cornerMin.X && max.X > cornerMin.X && min.X < cornerMax.X && max.Y > cornerMin.Y && min.Y < cornerMax.Y;
        if (!hitsCorner)
        {
            opaqueRegions.Add((min, max));
            return;
        }

        if (cornerMin.Y > min.Y)
            opaqueRegions.Add((min, new Vector2(max.X, cornerMin.Y)));
        var bandTop = MathF.Max(min.Y, cornerMin.Y);
        if (cornerMin.X > min.X && max.Y > bandTop)
            opaqueRegions.Add((new Vector2(min.X, bandTop), new Vector2(cornerMin.X, max.Y)));
    }

    // Le fond opaque va jusqu'au bord et épouse l'arrondi de la fenêtre, sous le reflet du verre.
    private void DrawOverlapBackdrop(in LiveFootprint own)
    {
        if (opaqueRegions.Count == 0) return;

        var style = ImGui.GetStyle();
        var border = style.WindowBorderSize;
        var shapeMin = own.Min + new Vector2(border);
        var shapeMax = own.Max - new Vector2(border);
        var rounding = MathF.Max(0f, style.WindowRounding - border);
        var corners = own.TitleBarHeight > 0f ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersAll;
        var color = ImGui.GetColorU32(MasterEventTheme.WithAlpha(MasterEventTheme.ThemeWindowBg, 1f));
        var drawList = ImGui.GetWindowDrawList();

        foreach (var (min, max) in opaqueRegions)
        {
            drawList.PushClipRect(min, max, false);
            drawList.AddRectFilled(shapeMin, shapeMax, color, rounding, corners);
            drawList.PopClipRect();
        }
    }

    // Les fenêtres invisibles (sans fond ou sans entrée) ne comptent pas.
    private unsafe void RecordFootprint()
    {
        var window = ImGuiP.GetCurrentWindow();
        if (window.Handle == null) return;
        if ((window.Flags & (ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs)) != ImGuiWindowFlags.None)
        {
            ForgetFootprint();
            return;
        }

        var titleBar = (window.Flags & ImGuiWindowFlags.NoTitleBar) != ImGuiWindowFlags.None ? 0f : ImGui.GetFrameHeight();
        lock (FootprintsLock)
            Footprints[this] = new WindowFootprint(window, titleBar);
    }

    private void DrawWindowSheen()
    {
        if (Flags.HasFlag(ImGuiWindowFlags.NoBackground)) return;

        var alpha = MasterEventTheme.GlassAlpha(WindowGlassLevel);
        if (alpha >= 1f) return;

        var pos = ImGui.GetWindowPos();
        var rounding = MasterEventTheme.RadiusWindow * ImGuiHelpers.GlobalScale;
        var blurOpacity = MasterEventTheme.BlurOpacity;
        if (UseBackdropBlur && blurOpacity > 0f && MasterEventTheme.Backdrop is { } backdrop)
        {
            backdrop.EnsureRendered();
            backdrop.DrawBehind(pos, pos + ImGui.GetWindowSize(), rounding, blurOpacity);
        }

        MasterEventTheme.DrawGlassSheen(
            ImGui.GetWindowDrawList(),
            pos,
            pos + ImGui.GetWindowSize(),
            rounding,
            alpha);
    }

    protected abstract void DrawContents();
}
