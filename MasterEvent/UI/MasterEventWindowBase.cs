using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace MasterEvent.UI;

public abstract class MasterEventWindowBase(string name, ImGuiWindowFlags flags = ImGuiWindowFlags.None, bool forceMainWindow = false)
    : Window(name, flags, forceMainWindow)
{

    protected virtual MasterEventTheme.GlassLevel WindowGlassLevel => MasterEventTheme.GlassLevel.Regular;

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
        MasterEventTheme.DrawGlassSheen(
            ImGui.GetWindowDrawList(),
            pos,
            pos + ImGui.GetWindowSize(),
            MasterEventTheme.RadiusWindow * ImGuiHelpers.GlobalScale,
            alpha);
    }

    protected abstract void DrawContents();
}
