using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using ClientFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace MasterEvent.Services;


public sealed unsafe class TacticalCameraService : IDisposable
{
    private readonly Configuration configuration;
    private readonly SessionManager session;

    private const float SnapDirV = -1.0f;
    private const float WideDirVMin = -1.45f;
    private const float WideDirVMax = 1.45f;
    private const float SnapDistance = 50f;
    private const float WideMaxDistance = 80f;
    private const float SnapSideOffset = -MathF.PI / 2f - MathF.PI / 6f;

    private const string AutoRotateSig = "E8 ?? ?? ?? ?? 48 8B CB 85 C0 0F 84 ?? ?? ?? ?? 83 E8 01";
    private const byte AutoRotateDisabled = 4;
    private delegate byte GetCameraAutoRotateModeDelegate(Camera* camera, ClientFramework* framework);
    private readonly Hook<GetCameraAutoRotateModeDelegate>? autoRotateHook;

    private bool applied;
    private bool hasSaved;
    private float savedDirVMin;
    private float savedDirVMax;
    private float savedMaxDistance;

    public TacticalCameraService(Configuration configuration, SessionManager session,
        ISigScanner sigScanner, IGameInteropProvider interop)
    {
        this.configuration = configuration;
        this.session = session;

        try
        {
            var address = sigScanner.ScanText(AutoRotateSig);
            if (address == nint.Zero)
            {
                Plugin.Log.Warning("[MasterEvent] Caméra tactique : signature anti-recentrage introuvable.");
            }
            else
            {
                autoRotateHook = interop.HookFromAddress<GetCameraAutoRotateModeDelegate>(address, AutoRotateDetour);
                autoRotateHook.Enable();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Caméra tactique : hook anti-recentrage indisponible : {ex.Message}");
        }
    }


    private bool ShouldBeActive()
        => configuration.TacticalCamera
           || (configuration.TacticalCameraAutoCombat && session.CurrentTurnState is { IsActive: true });

    private byte AutoRotateDetour(Camera* camera, ClientFramework* framework)
        => ShouldBeActive()
            ? AutoRotateDisabled
            : autoRotateHook!.Original(camera, framework);

    public void Tick()
    {
        var cam = GetCamera();
        if (cam == null)
        {
            applied = false;
            return;
        }

        var desired = ShouldBeActive();

        if (desired && !applied)
        {
            Enable(cam);
            applied = true;
        }
        else if (!desired && applied)
        {
            Restore(cam);
            applied = false;
        }
        else if (desired)
        {

            ApplyBounds(cam);
        }
    }

    private void Enable(Camera* cam)
    {
        if (!hasSaved)
        {
            savedDirVMin = cam->DirVMin;
            savedDirVMax = cam->DirVMax;
            savedMaxDistance = cam->MaxDistance;
            hasSaved = true;
        }

        ApplyBounds(cam);
        cam->DirV = SnapDirV;
        cam->Distance = SnapDistance;

        var local = Plugin.ObjectTable.LocalPlayer;
        if (local != null)
            cam->DirH = local.Rotation + SnapSideOffset;
    }

    private static void ApplyBounds(Camera* cam)
    {
        if (cam->DirVMin > WideDirVMin) cam->DirVMin = WideDirVMin;
        if (cam->DirVMax < WideDirVMax) cam->DirVMax = WideDirVMax;
        if (cam->MaxDistance < WideMaxDistance) cam->MaxDistance = WideMaxDistance;
    }

    private void Restore(Camera* cam)
    {
        if (!hasSaved) return;

        cam->DirVMin = savedDirVMin;
        cam->DirVMax = savedDirVMax;
        cam->MaxDistance = savedMaxDistance;

        if (cam->DirV < savedDirVMin) cam->DirV = savedDirVMin;
        if (cam->DirV > savedDirVMax) cam->DirV = savedDirVMax;
        if (cam->Distance > savedMaxDistance) cam->Distance = savedMaxDistance;
    }

    private static Camera* GetCamera()
    {
        var manager = CameraManager.Instance();
        return manager == null ? null : manager->Camera;
    }

    public void Dispose()
    {
        autoRotateHook?.Dispose();
        if (!applied) return;
        var cam = GetCamera();
        if (cam != null) Restore(cam);
        applied = false;
    }
}
