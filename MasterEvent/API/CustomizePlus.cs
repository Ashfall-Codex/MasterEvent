using System;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace MasterEvent.API;

public sealed class CustomizePlus
{
    private const int SupportedBreakingVersion = 6;

    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)> setTemporaryProfile;
    private readonly ICallGateSubscriber<Guid, int> deleteTemporaryProfile;

    public CustomizePlus(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        apiVersion = pi.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        setTemporaryProfile = pi.GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
        deleteTemporaryProfile = pi.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.DeleteTemporaryProfileByUniqueId");
    }

    // Lu à chaque usage : Customize+ n'annonce pas son démarrage par IPC.
    public bool Available
    {
        get
        {
            try
            {
                return apiVersion.InvokeFunc().Item1 == SupportedBreakingVersion;
            }
            catch
            {
                return false;
            }
        }
    }

    // Le profil d'un MCDF est le JSON de Customize+ encodé en base64.
    public Guid? TryApplyProfile(ushort objectIndex, string base64Profile)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Profile));
            var (code, id) = setTemporaryProfile.InvokeFunc(objectIndex, json);
            log.Info($"[MasterEvent] CustomizePlus.SetTemporaryProfileOnCharacter(idx={objectIndex}) : {code}");
            return code == 0 ? id : null;
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] CustomizePlus.SetTemporaryProfileOnCharacter a levé : {ex.Message}");
            return null;
        }
    }

    public void DeleteProfile(Guid profileId)
    {
        try
        {
            deleteTemporaryProfile.InvokeFunc(profileId);
        }
        catch (Exception ex)
        {
            log.Warning($"[MasterEvent] CustomizePlus.DeleteTemporaryProfileByUniqueId a levé : {ex.Message}");
        }
    }
}
