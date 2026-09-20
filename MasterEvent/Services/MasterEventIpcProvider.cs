using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace MasterEvent.Services;

public sealed class MasterEventIpcProvider : IDisposable
{
    private const int Version = 1;

    private readonly ICallGateProvider<int> versionProvider;
    private readonly ICallGateProvider<bool> handlesSheetsProvider;

    public MasterEventIpcProvider(IDalamudPluginInterface pluginInterface)
    {
        versionProvider = pluginInterface.GetIpcProvider<int>("MasterEvent.Profile.Version");
        versionProvider.RegisterFunc(() => Version);

        handlesSheetsProvider = pluginInterface.GetIpcProvider<bool>("MasterEvent.Profile.HandlesSheets");
        handlesSheetsProvider.RegisterFunc(() => true);
    }

    public void Dispose()
    {
        versionProvider.UnregisterFunc();
        handlesSheetsProvider.UnregisterFunc();
    }
}
