using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace MasterEvent.Services.Npc;


public static class EmoteCatalog
{
    public readonly record struct Entry(ushort Id, string Name);

    private static List<Entry>? cache;
    private static Dictionary<ushort, string>? namesById;

    public static IReadOnlyList<Entry> Entries => Load();

    public static string NameOf(ushort id)
    {
        Load();
        return namesById!.TryGetValue(id, out var name) ? name : $"#{id}";
    }

    private static List<Entry> Load()
    {
        if (cache != null) return cache;

        var entries = new List<Entry>();
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Emote>();
            foreach (var row in sheet)
            {
                if (row.RowId == 0) continue;

                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (row.ActionTimeline[0].ValueNullable is null) continue;

                entries.Add(new Entry((ushort)row.RowId, name));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[EmoteCatalog] Chargement des emotes impossible : {ex.Message}");
        }

        cache = entries
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        namesById = cache.ToDictionary(e => e.Id, e => e.Name);
        return cache;
    }
}
