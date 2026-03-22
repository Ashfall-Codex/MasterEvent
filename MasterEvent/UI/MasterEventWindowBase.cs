using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MasterEvent.UI;

/// <summary>
/// Base window class that automatically applies the MasterEvent red theme.
/// All plugin windows should inherit from this class.
/// </summary>
public abstract class MasterEventWindowBase(string name, ImGuiWindowFlags flags = ImGuiWindowFlags.None, bool forceMainWindow = false)
    : Window(name, flags, forceMainWindow)
{

    public override void PreDraw()
    {
        MasterEventTheme.PushTheme();
    }

    public override void PostDraw()
    {
        MasterEventTheme.PopTheme();
    }

    public sealed override void Draw()
    {
        DrawContents();
    }

    protected abstract void DrawContents();
}
