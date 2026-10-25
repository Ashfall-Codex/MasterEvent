using Dalamud.Bindings.ImGui;
using MasterEvent.Localization;

namespace MasterEvent.UI.Components;

// Confirmation en ligne d'une action destructive. Le premier clic remplace le bouton par la
// question, « Annuler » et le verbe précis en rouge plein ; une alerte modale couperait la scène
// en plein jeu. Une seule confirmation est ouverte à la fois dans toute l'interface.
public static class ConfirmInline
{
    private static string? openId;

    public static bool IsOpen(string id) => openId == id;

    public static void Open(string id) => openId = id;

    public static void Close() => openId = null;

    // Vrai au clic sur le verbe de confirmation. `confirmLabel` reprend le verbe de l'action
    // (« Retirer », « Fermer le lobby »), jamais un « OK » ou un « Confirmer ».
    public static bool Draw(string id, string actionLabel, string question, string confirmLabel)
    {
        if (openId != id)
        {
            if (Buttons.Destructive(actionLabel + "##" + id)) openId = id;
            return false;
        }

        return DrawQuestion(id, question, confirmLabel);
    }

    // Pour un déclencheur qui n'est pas un bouton (entrée de menu) : l'appelant fait Open(id).
    public static bool DrawQuestion(string id, string question, string confirmLabel)
    {
        if (openId != id) return false;

        ImGui.TextColored(MasterEventTheme.TextSecondary, question);
        if (Buttons.Quiet(Loc.Get("Gm.Cancel") + "##" + id + "_cancel"))
        {
            openId = null;
            return false;
        }

        ImGui.SameLine();
        if (!Buttons.DestructiveFilled(confirmLabel + "##" + id + "_confirm")) return false;

        openId = null;
        return true;
    }
}
