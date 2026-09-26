using System;
using System.Collections.Generic;
using Dalamud;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using Lumina.Excel.Sheets;

namespace MasterEvent.Services;


public class WeatherService : IDisposable
{
    // Hook météo
    private readonly Hook<UpdateTerritoryWeatherDelegate>? weatherHook;
    private bool weatherOverrideEnabled;

    private uint? overrideTimeSeconds;
    private bool timeOverrideActive;

    // Données météo (Lumina)
    private readonly Dictionary<byte, uint> weatherIcons = new();
    private readonly Dictionary<byte, string> weatherNames = new();
    private readonly Dictionary<uint, Dictionary<byte, string>> territoryWeatherCache = new();

    private const string UpdateWeatherSig = "48 89 5C 24 ?? 55 56 57 48 83 EC ?? 48 8B F9 48 8D 0D";
    private delegate void UpdateTerritoryWeatherDelegate(nint weatherManager);

    public bool IsWeatherOverrideActive => weatherOverrideEnabled;
    public bool IsTimeOverrideActive => timeOverrideActive;
    public bool IsReady { get; }

    public const uint SecondsInDay = 60 * 60 * 24;
    public const long SecondsInMonth = SecondsInDay * 32L;

    public WeatherService(ISigScanner sigScanner, IGameInteropProvider gameInterop)
    {
        var weatherOk = false;
        var timeOk = false;

        // Hook de la fonction de mise à jour météo (approche Brio : no-op detour)
        try
        {
            var weatherAddr = sigScanner.ScanText(UpdateWeatherSig);
            weatherHook = gameInterop.HookFromAddress<UpdateTerritoryWeatherDelegate>(weatherAddr, WeatherDetour);
            weatherOk = true;
            Plugin.Log.Info($"[WeatherService] Hook météo initialisé : {weatherAddr:X}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[WeatherService] Échec du hook météo : {ex.Message}");
        }

        timeOk = true;

        IsReady = weatherOk && timeOk;
        LoadWeatherData();
    }



    // Detour no-op : doit rester une méthode d'instance (delegate pour le hook)
#pragma warning disable CA1822
    private void WeatherDetour(nint weatherManager) { }
#pragma warning restore CA1822


    public unsafe void SetWeather(byte weatherId)
    {
        if (weatherId == 0)
        {
            if (weatherOverrideEnabled && weatherHook != null)
            {
                // 1) Désactiver le hook pour que le jeu reprenne ses updates naturelles
                weatherHook.Disable();
                weatherOverrideEnabled = false;

                // 2) Forcer immédiatement la météo naturelle de la zone dans ActiveWeather,
                // sinon le jeu garde la valeur overridée visible jusqu'au prochain changement de zone.
                // WeatherManager.GetCurrentWeather() retourne la météo attendue pour zone + heure courante.
                var env = EnvManager.Instance();
                var wm = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager.Instance();
                if (env != null && wm != null)
                {
                    var naturalWeather = wm->GetCurrentWeather();
                    env->ActiveWeather = naturalWeather;
                    env->TransitionTime = 0.5f;
                    Plugin.Log.Info($"[WeatherService] Override météo désactivé, météo naturelle restaurée (id={naturalWeather})");
                }
                else
                {
                    Plugin.Log.Info("[WeatherService] Override météo désactivé (EnvManager/WeatherManager indisponible)");
                }
            }
            return;
        }

        var envSet = EnvManager.Instance();
        if (envSet == null)
        {
            Plugin.Log.Error("[WeatherService] EnvManager non disponible");
            return;
        }

        // Écriture directe dans la mémoire du jeu
        envSet->ActiveWeather = weatherId;
        envSet->TransitionTime = 0.5f;

        // Activer le hook pour empêcher le jeu de changer la météo
        if (!weatherOverrideEnabled && weatherHook != null)
        {
            weatherHook.Enable();
            weatherOverrideEnabled = true;
        }

        Plugin.Log.Info($"[WeatherService] Météo définie : id={weatherId}");
    }
    public unsafe void SetTime(uint eorzeaSeconds)
    {
        var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if (fw == null)
        {
            Plugin.Log.Error("[WeatherService] Impossible de définir l'heure : Framework indisponible");
            return;
        }

        var normalized = eorzeaSeconds % SecondsInDay;
        overrideTimeSeconds = normalized;

        var value = BuildTimestamp(fw->ClientTime.EorzeaTime, normalized);
        WriteTime(fw, value);
        timeOverrideActive = true;

        Plugin.Log.Info($"[WeatherService] Heure définie : {normalized}s ({SecondsToHour(normalized):00}:00), " +
                        $"horodatage {value}");
    }

    public unsafe void ClearTime()
    {
        overrideTimeSeconds = null;
        timeOverrideActive = false;

        var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if (fw != null)
            fw->ClientTime.IsEorzeaTimeOverridden = false;

        Plugin.Log.Info("[WeatherService] Override temps désactivé");
    }

    public unsafe void TickTimeOverride()
    {
        if (overrideTimeSeconds is not { } seconds) return;

        var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if (fw == null) return;

        WriteTime(fw, BuildTimestamp(fw->ClientTime.EorzeaTime, seconds));
    }

    // Renseigne les trois champs d'un coup : l'horloge visible et le couple d'override,
    // pour que la valeur survive au prochain recalcul du jeu.
    private static unsafe void WriteTime(
        FFXIVClientStructs.FFXIV.Client.System.Framework.Framework* fw, long value)
    {
        fw->ClientTime.EorzeaTimeOverride = value;
        fw->ClientTime.IsEorzeaTimeOverridden = true;
        fw->ClientTime.EorzeaTime = value;
    }

    private static long BuildTimestamp(long currentEorzeaTime, uint secondsOfDay)
    {
        var inMonth = currentEorzeaTime % SecondsInMonth;
        if (inMonth < 0) inMonth += SecondsInMonth;

        var dayStart = inMonth - inMonth % SecondsInDay;
        var value = dayStart + secondsOfDay % SecondsInDay;

        // Filet : ne jamais écrire zéro, même au premier jour du mois éorzéen.
        return value == 0 ? SecondsInDay : value;
    }

    public Dictionary<byte, string> GetWeathersForCurrentZone()
    {
        var territoryId = Plugin.ClientState.TerritoryType;
        if (territoryId == 0) return GetAllWeathers();

        // Vérifier le cache
        if (territoryWeatherCache.TryGetValue(territoryId, out var cached))
            return cached;

        var result = new Dictionary<byte, string>();

        try
        {
            var territorySheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (territorySheet.TryGetRow(territoryId, out var territory))
            {
                var weatherRateRef = territory.WeatherRate;
                if (weatherRateRef.RowId != 0)
                {
                    var weatherRateSheet = Plugin.DataManager.GetExcelSheet<WeatherRate>();
                    if (weatherRateSheet.TryGetRow(weatherRateRef.RowId, out var weatherRate))
                    {
                        for (var i = 0; i < weatherRate.Weather.Count; i++)
                        {
                            var weatherRef = weatherRate.Weather[i];
                            if (!weatherRef.IsValid || weatherRef.RowId == 0) continue;

                            var id = (byte)weatherRef.RowId;
                            var name = weatherNames.GetValueOrDefault(id, $"Weather {id}");
                            result[id] = name;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[WeatherService] Erreur lecture Lumina zone {territoryId}: {ex.Message}");
        }

        if (result.Count == 0)
            result = GetAllWeathers();

        // Mettre en cache
        territoryWeatherCache[territoryId] = result;
        return result;
    }

    public Dictionary<byte, string> GetAllWeathers()
    {
        return weatherNames.Count > 0 ? new Dictionary<byte, string>(weatherNames) : FallbackWeathers;
    }

    public uint GetWeatherIconId(byte weatherId)
    {
        return weatherIcons.GetValueOrDefault(weatherId, 0u);
    }

    public void ClearWeatherCache()
    {
        territoryWeatherCache.Clear();
    }

    // Lecture du temps

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

    // ── Données statiques de secours ──

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

    // ── Chargement initial des données Lumina ──

    private void LoadWeatherData()
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

                var name = row.Name.ToString();
                if (!string.IsNullOrEmpty(name))
                    weatherNames[id] = name;
            }

            Plugin.Log.Info($"[WeatherService] {weatherNames.Count} météos chargées depuis Lumina");
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[WeatherService] Erreur chargement icônes météo : {ex.Message}");
        }
    }

    // Nettoyage

    // ReSharper disable once RedundantUnsafeContext
    public unsafe void Dispose()
    {
        // Restaurer la météo du jeu si override actif
        if (weatherOverrideEnabled && weatherHook != null)
        {
            var wm = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager.Instance();
            if (wm != null)
                weatherHook.Original((nint)wm);
            weatherHook.Disable();
        }

        // Rendre la main à l'horloge du jeu
        ClearTime();

        weatherHook?.Dispose();
    }
}
