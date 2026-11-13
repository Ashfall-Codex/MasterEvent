using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using MasterEvent.Models;

namespace MasterEvent.Waymarks;

public static class WaymarkManager
{
    public readonly record struct WaymarkState(bool Active, Vector3 Position);

    public static unsafe WaymarkState[] ReadCurrentWaymarks()
    {
        var states = new WaymarkState[Constants.WaymarkCount];
        var controller = MarkingController.Instance();
        if (controller == null)
            return states;

        var fieldMarkers = controller->FieldMarkers;
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            ref var fm = ref fieldMarkers[i];
            states[i] = new WaymarkState(fm.Active, fm.Position);
        }

        return states;
    }

    public static unsafe bool IsWaymarkActive(WaymarkId id)
    {
        var controller = MarkingController.Instance();
        if (controller == null)
            return false;

        return controller->FieldMarkers[(int)id].Active;
    }

    public static unsafe byte ClearWaymark(WaymarkId id)
    {
        if (!WaymarkSafetyCheck.CanModifyWaymarks())
            return 4; // combat

        var controller = MarkingController.Instance();
        if (controller == null)
            return 255;

        return controller->ClearFieldMarker((uint)id);
    }

    public static unsafe byte ClearAllWaymarks()
    {
        if (!WaymarkSafetyCheck.CanModifyWaymarks())
            return 4;

        var controller = MarkingController.Instance();
        if (controller == null)
            return 255;

        return controller->ClearFieldMarkers();
    }

    public static unsafe void EnterPlacementMode(WaymarkId id)
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            Plugin.Log.Error("[MasterEvent] EnterPlacementMode: UIModule is null.");
            return;
        }

        var label = id.ToLabel();
        var command = new Utf8String($"/waymark {label}");
        uiModule->ProcessChatBoxEntry(&command);
        command.Dtor();
        Plugin.Log.Info($"[MasterEvent] EnterPlacementMode: sent /waymark {label}.");
    }

    public static unsafe bool PlaceWaymark(WaymarkId id, float x, float y, float z)
    {
        if (!WaymarkSafetyCheck.CanModifyWaymarks()) return false;
        var controller = MarkingController.Instance();
        if (controller == null) return false;
        ref var fm = ref controller->FieldMarkers[(int)id];
        fm.Position = new Vector3(x, y, z);
        fm.Active = true;
        return true;
    }

    // Retire chez nous seul un waymark que nous avions posé, s'il est encore là où nous l'avions mis :
    // replacé ailleurs entre-temps, il ne nous appartient plus. Vrai s'il a été retiré, faux s'il n'y
    // avait rien à retirer, null si le jeu l'interdit pour l'instant.
    public static unsafe bool? HideWaymark(WaymarkId id, Vector3 placedAt)
    {
        if (!WaymarkSafetyCheck.CanModifyWaymarks()) return null;
        var controller = MarkingController.Instance();
        if (controller == null) return null;
        ref var fm = ref controller->FieldMarkers[(int)id];
        if (!fm.Active || Vector3.DistanceSquared(fm.Position, placedAt) >= 0.01f) return false;
        fm.Active = false;
        return true;
    }

    public static void SyncWaymarkVisibility(MarkerSet markerSet)
    {
        var states = ReadCurrentWaymarks();
        for (var i = 0; i < Constants.WaymarkCount; i++)
            markerSet.Markers[i].IsVisible = states[i].Active;
    }
}
