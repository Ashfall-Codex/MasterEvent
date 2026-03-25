using System;
using System.Collections.Generic;
using Dalamud;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MasterEvent.Services;


// Utilise Weatherman IPC pour les données (listes météo par zone, noms) et applique les changements via patch mémoire direct (même méthode que Weatherman).
public class WeatherService : IDisposable
{

    private readonly ICallGateSubscriber<Dictionary<byte, string>> ipcGetWeathers;
    private readonly ICallGateSubscriber<Dictionary<ushort, (List<byte> WeatherList, string EnvbFile)>> ipcGetWeatherList;
    private readonly ICallGateSubscriber<bool> ipcIsPluginEnabled;
    private readonly nint weatherPatchAddr;
    private readonly byte[]? weatherOrigBytes;
    private bool weatherPatchEnabled;

    // Adresse du début du patch temps de Weatherman et de la valeur imm32
    private readonly nint timePatchAddr;
    private nint timeValueAddr;
    public bool IsWeathermanTimePatchActive { get; private set; }
    public bool IsWeatherPatchActive => weatherPatchEnabled;

    // Vérifie si Weatherman est installé et actif via IPC.
    public bool IsWeathermanInstalled
    {
        get
        {
            try { return ipcIsPluginEnabled.InvokeFunc(); }
            catch { return false; }
        }
    }
    private uint activeTimeOverride; // 0 = pas d'override

    private readonly Dictionary<byte, uint> weatherIcons = new();
    public static readonly Dictionary<byte, string> FallbackWeathers = new()
    {
        { 1, "Ciel dégagé" },
        { 2, "Beau temps" },
        { 3, "Couvert" },
        { 4, "Pluie" },
        { 7, "Brouillard" },
        { 8, "Orage" },
        { 9, "Tempête de sable" },
        { 14, "Neige" },
        { 15, "Blizzard" },
        { 16, "Canicule" },
    };

    public const uint SecondsInDay = 60 * 60 * 24;

    // Signature de la fonction de rendu météo (identique à Weatherman pour compatibilité 100%)
    private const string WeatherRenderSig = "48 89 5C 24 ?? 57 48 83 EC 30 80 B9 ?? ?? ?? ?? ?? 49 8B F8 0F 29 74 24 ?? 48 8B D9 0F 28 F1";
    private const int WeatherPatchOffset = 0x55;
    // Signature du patch temps de Weatherman — on ne patche pas nous-même,
    // on localise juste l'adresse pour écrire la valeur dans le patch existant de Weatherman.
    private const string TimeRenderSig = "48 89 5C 24 ?? 57 48 83 EC 30 4C 8B 15";
    private const int TimePatchOffset = 0x19;
    private const int TimeValueOffsetInPatch = 3; // Les 4 bytes imm32 commencent à l'offset 3 du patch
    public WeatherService(IDalamudPluginInterface pluginInterface, ISigScanner sigScanner)
    {
        ipcGetWeathers = pluginInterface.GetIpcSubscriber<Dictionary<byte, string>>("Weatherman.DataGetWeathers");
        ipcGetWeatherList = pluginInterface.GetIpcSubscriber<Dictionary<ushort, (List<byte>, string)>>("Weatherman.DataGetWeatherList");
        ipcIsPluginEnabled = pluginInterface.GetIpcSubscriber<bool>("Weatherman.IsPluginEnabled");
        try
        {
            var funcAddr = sigScanner.ScanText(WeatherRenderSig);
            weatherPatchAddr = funcAddr + WeatherPatchOffset;
            SafeMemory.ReadBytes(weatherPatchAddr, 4, out weatherOrigBytes);
            Plugin.Log.Info($"[MasterEvent] Weather render patch address found: {weatherPatchAddr:X}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MasterEvent] Weather render signature scan failed: {ex.Message}");
            weatherPatchAddr = nint.Zero;
        }

        // Localiser l'adresse du patch temps de Weatherman (vérification différée de l'activation)
        try
        {
            var timeFuncAddr = sigScanner.ScanText(TimeRenderSig);
            timePatchAddr = timeFuncAddr + TimePatchOffset;
            Plugin.Log.Info($"[MasterEvent] Time render function found at {timeFuncAddr:X}");
            // Tenter la détection immédiate (Weatherman peut déjà être chargé)
            TryDetectTimePatch();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Time signature scan failed (Weatherman needed for time): {ex.Message}");
            timePatchAddr = nint.Zero;
        }

        LoadWeatherIcons();
    }


    public Dictionary<byte, string> GetAllWeathers()
    {
        try
        {
            var list = ipcGetWeathers.InvokeFunc();
            if (list is { Count: > 0 }) return list;
        }
        catch { /* Weatherman indisponible */ }

        return FallbackWeathers;
    }

    public Dictionary<byte, string> GetWeathersForCurrentZone()
    {
        var territoryId = Plugin.ClientState.TerritoryType;
        if (territoryId == 0) return GetAllWeathers();

        try
        {
            var zoneWeathers = ipcGetWeatherList.InvokeFunc();
            var allWeathers = ipcGetWeathers.InvokeFunc();

            if (zoneWeathers.TryGetValue(territoryId, out var zoneData)
                && zoneData.WeatherList is { Count: > 0 })
            {
                var result = new Dictionary<byte, string>();
                foreach (var id in zoneData.WeatherList)
                {
                    if (allWeathers.TryGetValue(id, out var name))
                        result[id] = name;
                }
                if (result.Count > 0) return result;
            }
        }
        catch { /* Weatherman indisponible pour les données zone */ }

        return GetAllWeathers();
    }

    public uint GetWeatherIconId(byte weatherId)
    {
        return weatherIcons.GetValueOrDefault(weatherId, 0u);
    }

    // Applique une météo
    public void SetWeather(byte weatherId)
    {
        if (weatherPatchAddr == nint.Zero)
        {
            Plugin.Log.Error("[MasterEvent] Cannot set weather: patch address not found");
            return;
        }

        if (weatherId == 0)
        {
            DisableWeatherPatch();
            return;
        }

        EnableWeatherPatch(weatherId);
    }

    // Vérifie si Weatherman a appliqué son patch temps (peut être appelé plusieurs fois)
    private bool TryDetectTimePatch()
    {
        if (IsWeathermanTimePatchActive) return true;
        if (timePatchAddr == nint.Zero) return false;

        SafeMemory.ReadBytes(timePatchAddr, 3, out var headerBytes);
        if (headerBytes is not [0x49, 0xC7, 0xC1])
        {
            var hex = BitConverter.ToString(headerBytes);
            Plugin.Log.Debug($"[MasterEvent] Weatherman time patch not yet active (bytes: {hex})");
            return false;
        }

        timeValueAddr = timePatchAddr + TimeValueOffsetInPatch;
        IsWeathermanTimePatchActive = true;
        Plugin.Log.Info($"[MasterEvent] Weatherman time patch detected at {timeValueAddr:X}");
        return true;
    }

    // Active un override d'heure éorzéenne. La valeur sera réécrite chaque frame
    public void SetTime(uint eorzeaSeconds)
    {
        // Re-vérifier si Weatherman a activé son patch entre-temps
        if (timeValueAddr == nint.Zero && !TryDetectTimePatch())
        {
            Plugin.Log.Error("[MasterEvent] Cannot set time: Weatherman time patch not found. Enable custom time in Weatherman first.");
            return;
        }

        activeTimeOverride = eorzeaSeconds % SecondsInDay;
        Plugin.Log.Info($"[MasterEvent] Time override set: {activeTimeOverride}s ({SecondsToHour(activeTimeOverride):00}:00)");
    }

    // Désactive l'override de l'heure éorzéenne
    public void ClearTime()
    {
        activeTimeOverride = 0;
        Plugin.Log.Info("[MasterEvent] Time override cleared");
    }

    public void TickTimeOverride()
    {
        if (activeTimeOverride == 0 || timeValueAddr == nint.Zero) return;
        SafeMemory.WriteBytes(timeValueAddr, BitConverter.GetBytes(activeTimeOverride));
    }

    public static unsafe uint GetCurrentEorzeaTimeSeconds()
    {
        try
        {
            var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (fw == null) return 0;
            return Convert.ToUInt32(fw->ClientTime.EorzeaTime % SecondsInDay);
        }
        catch { return 0; }
    }

    public static int SecondsToHour(uint seconds) => (int)(seconds / 3600 % 24);
    public static uint HourToSeconds(int hour) => (uint)(hour * 3600);


    private void EnableWeatherPatch(byte weatherId)
    {
        // Écrire le patch
        var patchBytes = new byte[] { 0xB2, weatherId, 0x90, 0x90 };
        if (SafeMemory.WriteBytes(weatherPatchAddr, patchBytes))
        {
            weatherPatchEnabled = true;

            Plugin.Log.Info($"[MasterEvent] Weather patch enabled: id={weatherId}");
        }
        else
        {
            Plugin.Log.Error("[MasterEvent] Failed to write weather patch bytes");
        }
    }

    private void DisableWeatherPatch()
    {
        if (!weatherPatchEnabled || weatherOrigBytes == null) return;

        if (SafeMemory.WriteBytes(weatherPatchAddr, weatherOrigBytes))
        {
            weatherPatchEnabled = false;

            Plugin.Log.Info("[MasterEvent] Weather patch disabled, original bytes restored");
        }
        else
        {
            Plugin.Log.Error("[MasterEvent] Failed to restore original weather bytes");
        }
    }

    private void LoadWeatherIcons()
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Weather>();
            foreach (var row in sheet)
            {
                var id = (byte)row.RowId;
                var iconId = Convert.ToUInt32(row.Icon);
                if (iconId != 0)
                    weatherIcons[id] = iconId;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[MasterEvent] Failed to load weather icons: {ex.Message}");
        }
    }


    public void Dispose()
    {
        DisableWeatherPatch();
    }
}
