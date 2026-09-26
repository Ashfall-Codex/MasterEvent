using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace MasterEvent.Services;

public sealed class MasterEventIpcProvider : IDisposable
{
    private const int Version = 2;

    private readonly ICallGateProvider<int> versionProvider;
    private readonly ICallGateProvider<bool> handlesSheetsProvider;
    private readonly ICallGateProvider<uint, bool> handlesSheetForProvider;
    public MasterEventIpcProvider(IDalamudPluginInterface pluginInterface, Func<uint, bool> handlesSheetFor)
    {
        versionProvider = pluginInterface.GetIpcProvider<int>("MasterEvent.Profile.Version");
        versionProvider.RegisterFunc(() => Version);

        handlesSheetsProvider = pluginInterface.GetIpcProvider<bool>("MasterEvent.Profile.HandlesSheets");
        handlesSheetsProvider.RegisterFunc(() => true);

        handlesSheetForProvider = pluginInterface.GetIpcProvider<uint, bool>("MasterEvent.Profile.HandlesSheetFor");
        handlesSheetForProvider.RegisterFunc(handlesSheetFor);
    }

    public void Dispose()
    {
        versionProvider.UnregisterFunc();
        handlesSheetsProvider.UnregisterFunc();
        handlesSheetForProvider.UnregisterFunc();
    }
}
