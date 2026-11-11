using System.Numerics;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.UI.Components;

// L'attitude se dit par un mot et une couleur, les mêmes dans toutes les vues.
public static class AttitudeStyle
{
    public static string Label(Attitude attitude) => attitude switch
    {
        Attitude.Hostile => Loc.Get("Attitude.Hostile"),
        Attitude.Friendly => Loc.Get("Attitude.Friendly"),
        _ => Loc.Get("Attitude.Neutral"),
    };

    public static Vector4 Color(Attitude attitude) => attitude switch
    {
        Attitude.Hostile => MasterEventTheme.AttitudeHostile,
        Attitude.Friendly => MasterEventTheme.AttitudeFriendly,
        _ => MasterEventTheme.AttitudeNeutral,
    };
}
