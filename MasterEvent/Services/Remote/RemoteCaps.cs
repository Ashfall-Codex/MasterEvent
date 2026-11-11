using System.Collections.Generic;
using MasterEvent.Communication;
using MasterEvent.Communication.Remote;

namespace MasterEvent.Services.Remote;

// Ce que la page peut commander maintenant. La même fonction remplit state.caps et filtre chaque commande
// avant de l'exécuter : aucun droit n'est mis en cache. Thread du jeu.
public static class RemoteCaps
{
    private static readonly IReadOnlyList<string> Gm = RemoteOps.All;
    // Sa copie des marqueurs est figée depuis sa promotion et sans stats : il ne garde que les jets.
    private static readonly IReadOnlyList<string> CoGm = [RemoteOps.Roll];

    public static IReadOnlyList<string> For(SessionManager session)
        => session.IsLobbyMode && !session.IsAwaitingApproval && session.LinkState == RelayLinkState.InSession
            ? ForRole(session)
            : [];

    // Hors séance, un geste changerait l'état local sans rien diffuser : le rôle permettrait l'op, pas le lien.
    // Hors lobby, IsGm vaut vrai pour un joueur seul : le rôle n'y prouve rien.
    public static bool IsOfflineOnly(SessionManager session, string op)
        => session.IsLobbyMode && !session.IsAwaitingApproval && session.LinkState != RelayLinkState.InSession
           && Contains(ForRole(session), op);

    private static IReadOnlyList<string> ForRole(SessionManager session)
    {
        if (session.HasGmAccess) return Gm;
        return session.IsPromoted ? CoGm : [];
    }

    public static bool Contains(IReadOnlyList<string> caps, string op)
    {
        foreach (var cap in caps)
        {
            if (cap == op) return true;
        }
        return false;
    }
}
