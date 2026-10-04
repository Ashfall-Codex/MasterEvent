namespace MasterEvent.Models;

/// Disposition des boutons de la barre flottante.
public enum ToggleButtonLayout
{
    Vertical = 0,
    Horizontal = 1,

    // Grille de deux colonnes : la forme la plus compacte depuis que la barre compte
    // quatre boutons, une simple ligne ou colonne devenant trop longue.
    Grid = 2,
}
