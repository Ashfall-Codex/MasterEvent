namespace MasterEvent.Models;

/// Jet demandé par le MJ. StatId est nul pour un jet sans stat comme pour une stat absente de la
/// fiche : StatMissing distingue le second cas, où le MJ a nommé une stat que la fiche n'a pas.
public sealed record RollRequest(string? StatId, string StatName, int Threshold, bool StatMissing = false);
