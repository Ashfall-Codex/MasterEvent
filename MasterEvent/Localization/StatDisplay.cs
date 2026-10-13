using MasterEvent.Models;

namespace MasterEvent.Localization;

// Affichage de la valeur d'une statistique. En mode additif, c'est un bonus (« +5 ») ; en mode seuil,
// c'est le nombre à atteindre : y accoler un « + » le ferait lire comme un bonus qui n'existe pas.
public static class StatDisplay
{
    public static string Signed(int value) => value >= 0 ? $"+{value}" : value.ToString();

    // Valeur d'une stat selon la résolution du modèle actif.
    public static string Stat(int value)
        => VitalLabels.ActiveTemplate is { StatResolution: StatResolution.Target }
            ? value.ToString()
            : Signed(value);
}
