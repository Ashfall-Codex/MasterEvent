using System.Numerics;
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
        MasterEventTheme.PushTheme(MasterEventTheme.GlassAlpha(WindowGlassLevel));
    }

    public override void PostDraw()
    {
        MasterEventTheme.PopTheme();
    }

    public sealed override void Draw()
    {
        DrawWindowSheen();
        DrawContents();
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
