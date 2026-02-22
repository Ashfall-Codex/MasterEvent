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

    public static int PlaceAllWaymarks(MarkerSet markerSet)
    {
        var placed = 0;
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var marker = markerSet.Markers[i];
            if (!marker.IsVisible || (marker.X == 0 && marker.Y == 0 && marker.Z == 0))
                continue;
            if (PlaceWaymark((WaymarkId)i, marker.X, marker.Y, marker.Z))
                placed++;
        }
        return placed;
    }

    public static void SyncWaymarkVisibility(MarkerSet markerSet)
    {
        var states = ReadCurrentWaymarks();
        for (var i = 0; i < Constants.WaymarkCount; i++)
            markerSet.Markers[i].IsVisible = states[i].Active;
    }
}
