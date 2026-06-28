using System;
using System.Linq;
using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace MasterEvent.Services;


public sealed unsafe class PlayDeadService
{
    private readonly Configuration configuration;
    private readonly SessionManager session;
    private readonly ushort playDeadEmoteId;
    private bool wasDead;

    public PlayDeadService(Configuration configuration, SessionManager session)
    {
        this.configuration = configuration;
        this.session = session;
        playDeadEmoteId = ResolvePlayDeadEmote();
    }

    // Appelé chaque frame. Déclenche l'emote au passage des PV locaux à 0.
    public void Tick()
    {
        if (!configuration.PlayDeadAtZeroHp || playDeadEmoteId == 0)
        {
            wasDead = false;
            return;
        }

        var local = session.PartyMembers.FirstOrDefault(p => p.Hash == session.LocalPlayerHash);
        if (local == null)
        {
            wasDead = false;
            return;
        }

        var isDead = local.HpMax > 0 && local.Hp <= 0;
        if (isDead && !wasDead)
            TryPlayDead();
        wasDead = isDead;
    }

    private void TryPlayDead()
    {
        var agent = AgentEmote.Instance();
        if (agent == null) return;
        if (!agent->CanUseEmote(playDeadEmoteId)) return;
        agent->ExecuteEmote(playDeadEmoteId);
    }


    private static ushort ResolvePlayDeadEmote()
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Emote>(ClientLanguage.English);
            foreach (var row in sheet)
            {
                if (row.TextCommand.ValueNullable is not { } cmd) continue;
                if (IsPlayDead(cmd.Command.ExtractText()) || IsPlayDead(cmd.ShortCommand.ExtractText())
                    || IsPlayDead(cmd.Alias.ExtractText()) || IsPlayDead(cmd.ShortAlias.ExtractText()))
                    return (ushort)row.RowId;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] PlayDead : résolution de l'emote échouée : {ex.Message}");
        }
        return 0;
    }

    private static bool IsPlayDead(string command)
        => string.Equals(command, "/playdead", StringComparison.OrdinalIgnoreCase);
}
