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
