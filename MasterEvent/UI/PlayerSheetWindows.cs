using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MasterEvent.Models;
using MasterEvent.Services;

namespace MasterEvent.UI;


public sealed class PlayerSheetWindows
{
    private readonly WindowSystem windowSystem;
    private readonly SessionManager session;
    private readonly UmbraProfileIpc umbraProfiles;
    private readonly UmbraPortraitCache portraits;
    private readonly Dictionary<string, PlayerSheetWindow> opened = new();

    public PlayerSheetWindows(WindowSystem windowSystem, SessionManager session,
        UmbraProfileIpc umbraProfiles, UmbraPortraitCache portraits)
    {
        this.windowSystem = windowSystem;
        this.session = session;
        this.umbraProfiles = umbraProfiles;
        this.portraits = portraits;
    }

    public void Show(PlayerData player, uint objectId)
    {
        if (opened.TryGetValue(player.Hash, out var existing))
        {
            existing.IsOpen = true;
            ImGui.SetWindowFocus(existing.WindowName);
            return;
        }

        var window = new PlayerSheetWindow(session, umbraProfiles, portraits, player.Hash, player.Name, objectId) { IsOpen = true };
        windowSystem.AddWindow(window);
        opened[player.Hash] = window;
    }


    public void PruneClosed()
    {
        if (opened.Count == 0) return;

        foreach (var (hash, window) in opened.Where(kv => !kv.Value.IsOpen).ToList())
        {
            windowSystem.RemoveWindow(window);
            opened.Remove(hash);
        }
    }

    public void CloseAll()
    {
        foreach (var window in opened.Values)
            windowSystem.RemoveWindow(window);
        opened.Clear();
    }
}
