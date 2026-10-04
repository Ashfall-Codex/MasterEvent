using System.Collections.Generic;

namespace MasterEvent.Models;

public interface IVitalEntity
{
    /// Nom affiché dans l'interface et annoncé au chat lors d'un jet.
    string EntityName { get; }

    int Hp { get; set; }
    int HpMax { get; set; }
    int Shield { get; set; }
    Attitude Attitude { get; set; }
    bool IsBoss { get; set; }

    /// Bonus ou malus ponctuel, ajouté à tous les jets de l'entité.
    int TempModifier { get; set; }

    List<StatValue>? Stats { get; set; }
    List<CustomCounter>? Counters { get; set; }

    /// Dernier jet, conservé pour l'affichage uniquement, jamais transmis sur le réseau.
    int LastRollResult { get; set; }
    int LastRollMax { get; set; }

    /// Faux pour un marqueur vierge ou un PNJ sans vitalité : l'interface n'affiche alors pas de barre.
    bool HasVitals { get; }
}
