using System.Collections.Generic;
using System.Linq;
using MasterEvent.Models;

namespace MasterEvent.Services;

public static class TemplateSyncHelper
{
    public record SheetSyncReport(
        int StatsAdded,
        int StatsRemoved,
        int StatsRenamed,
        int CountersAdded,
        int CountersRemoved,
        int CountersRenamed)
    {
        public bool HasChanges =>
            StatsAdded > 0 || StatsRemoved > 0 || StatsRenamed > 0 ||
            CountersAdded > 0 || CountersRemoved > 0 || CountersRenamed > 0;
    }

    // Aligne la fiche avec les définitions du modèle, sans toucher à HpMax/MpMax ni aux valeurs
    public static SheetSyncReport SyncSheetWithTemplate(PlayerSheet sheet, EventTemplate template)
    {
        var statsAdded = 0;
        var statsRenamed = 0;

        var templateStatIds = (template.StatDefinitions ?? new List<StatDefinition>())
            .Select(d => d.Id).ToHashSet();

        sheet.Stats ??= new List<StatValue>();

        // 1. Retirer les stats orphelines (Id plus présent côté template)
        var statsRemoved = sheet.Stats.RemoveAll(s => !templateStatIds.Contains(s.Id));

        // 2. Ajouter les nouvelles stats et renommer celles dont le nom a changé
        if (template.StatDefinitions != null)
        {
            foreach (var def in template.StatDefinitions)
            {
                var existing = sheet.Stats.FirstOrDefault(s => s.Id == def.Id);
                if (existing == null)
                {
                    sheet.Stats.Add(def.ToStatValue());
                    statsAdded++;
                }
                else if (existing.Name != def.Name)
                {
                    existing.Name = def.Name;
                    statsRenamed++;
                }
            }
        }

        //  Counters
        var countersAdded = 0;
        var countersRenamed = 0;

        var templateCounterIds = (template.CounterDefinitions ?? new List<CounterDefinition>())
            .Select(d => d.Id).ToHashSet();

        sheet.Counters ??= new List<CustomCounter>();
        var countersRemoved = sheet.Counters.RemoveAll(c => !templateCounterIds.Contains(c.Id));

        if (template.CounterDefinitions != null)
        {
            foreach (var def in template.CounterDefinitions)
            {
                var existing = sheet.Counters.FirstOrDefault(c => c.Id == def.Id);
                if (existing == null)
                {
                    sheet.Counters.Add(def.ToCounter());
                    countersAdded++;
                }
                else if (existing.Name != def.Name)
                {
                    existing.Name = def.Name;
                    countersRenamed++;
                }
            }
        }

        return new SheetSyncReport(
            statsAdded, statsRemoved, statsRenamed,
            countersAdded, countersRemoved, countersRenamed);
    }
}
