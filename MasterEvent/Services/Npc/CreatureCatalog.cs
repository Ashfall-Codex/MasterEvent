using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;
using MasterEvent.Models;

namespace MasterEvent.Services.Npc;

// Monstres et animaux que le MJ peut faire apparaître. Le jeu ne relie pas un modèle de combat
// (BNpcBase) à son nom : la table de LuminaSupplemental fait le lien, comme dans Brio et Ktisis.
public static class CreatureCatalog
{
    public sealed record Entry(uint BaseId, uint NameId, string Name, uint ModelCharaId, float Scale);

    private static volatile IReadOnlyList<Entry>? entries;
    private static Task? loading;

    // Null tant que le chargement, lancé au premier appel, n'est pas terminé.
    public static IReadOnlyList<Entry>? Entries
    {
        get
        {
            loading ??= Task.Run(Load);
            return entries;
        }
    }

    public static IEnumerable<Entry> Search(string query)
    {
        var all = Entries;
        if (all == null) return [];
        query = query.Trim();
        return query.Length == 0
            ? all
            : all.Where(e => e.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    public static NpcAppearance.CreatureLook ToLook(Entry entry) => new()
    {
        BNpcBaseId = entry.BaseId,
        BNpcNameId = entry.NameId,
        ModelCharaId = entry.ModelCharaId,
        Scale = entry.Scale,
    };

    // Une apparence venue du réseau ou d'un preset n'est dessinée que si nos propres données du jeu
    // la reconnaissent : le modèle de combat existe et porte bien un corps de monstre.
    public static bool IsValid(NpcAppearance.CreatureLook look)
    {
        try
        {
            return Plugin.DataManager.GetExcelSheet<BNpcBase>().TryGetRow(look.BNpcBaseId, out var row)
                   && row.ModelChara.RowId == look.ModelCharaId
                   && IsCreatureModel(look.ModelCharaId);
        }
        catch
        {
            return false;
        }
    }

    // Monstre ciblé en jeu : le MJ reprend exactement ce qu'il voit, sans passer par le catalogue.
    public static unsafe bool TryCaptureTarget(out NpcAppearance.CreatureLook? look, out string? name)
    {
        look = null;
        name = null;
        if (Plugin.TargetManager.Target is not { } target || target.Address == IntPtr.Zero) return false;
        if (target.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc) return false;

        var chara = (Character*)target.Address;
        var candidate = new NpcAppearance.CreatureLook
        {
            BNpcBaseId = target.BaseId,
            BNpcNameId = chara->NameId,
            ModelCharaId = (uint)chara->ModelContainer.ModelCharaId,
            Scale = chara->GameObject.Scale > 0f ? chara->GameObject.Scale : 1f,
        };
        if (!IsValid(candidate)) return false;

        look = candidate;
        name = target.Name.TextValue;
        return true;
    }

    private static bool IsCreatureModel(uint modelCharaId)
        => Plugin.DataManager.GetExcelSheet<ModelChara>().TryGetRow(modelCharaId, out var model)
           && model.Type is 2 or 3;

    private static void Load()
    {
        try
        {
            var links = CsvLoader.LoadResource<BNpcLink>(CsvLoader.BNpcLinkResourceName, false, out _, out _);
            var bases = Plugin.DataManager.GetExcelSheet<BNpcBase>();
            var names = Plugin.DataManager.GetExcelSheet<BNpcName>();

            // Un même monstre existe en dizaines de variantes de combat : une seule ligne par corps et par nom.
            var seen = new HashSet<(uint, string)>();
            var list = new List<Entry>();
            foreach (var link in links)
            {
                if (!bases.TryGetRow(link.BNpcBaseId, out var row)) continue;
                var modelId = row.ModelChara.RowId;
                if (!IsCreatureModel(modelId)) continue;
                if (!names.TryGetRow(link.BNpcNameId, out var nameRow)) continue;

                var name = Capitalize(nameRow.Singular.ExtractText());
                if (!IsUsableName(name) || !seen.Add((modelId, name))) continue;

                list.Add(new Entry(link.BNpcBaseId, link.BNpcNameId, name, modelId, row.Scale > 0f ? row.Scale : 1f));
            }

            entries = list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Plugin.Log.Info($"[CreatureCatalog] {list.Count} monstres chargés.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[CreatureCatalog] Chargement des monstres impossible : {ex.Message}");
            entries = [];
        }
    }

    // Noms de travail restés dans les données : « ??? », ou « （仮） » (provisoire) des versions japonaises.
    private static bool IsUsableName(string name)
        => name.Any(char.IsLetter) && !name.Contains('仮');

    private static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}
