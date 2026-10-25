using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace MasterEvent.UI.Components;

// Les styles de bouton de toute l'interface. Une vue n'a qu'une action principale (Primary) ;
// les autres sont teintées, discrètes, ou destructives. Le rouge plein, couleur de l'accent, est
// réservé à l'action principale et à l'étape de confirmation d'une action destructive.
public static class Buttons
{
    public static bool Primary(string label, float width = 0f, float height = 0f)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, MasterEventTheme.AccentColor with { W = 0.8f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MasterEventTheme.AccentColor with { W = 0.95f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, MasterEventTheme.AccentColor);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
        var clicked = ImGui.Button(label, new Vector2(width, height));
        ImGui.PopStyleColor(4);
        return clicked;
    }

    // Action la plus fréquente d'une ligne : sans fond, texte d'accent.
    public static bool Tinted(string label, float width = 0f, float height = 0f)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.AccentText);
        var clicked = Quiet(label, width, height);
        ImGui.PopStyleColor();
        return clicked;
    }

    // Action secondaire : sans fond au repos, un voile d'accent au survol.
    public static bool Quiet(string label, float width = 0f, float height = 0f)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MasterEventTheme.AccentColor with { W = 0.25f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, MasterEventTheme.AccentColor with { W = 0.4f });
        var clicked = ImGui.Button(label, new Vector2(width, height));
        ImGui.PopStyleColor(3);
        return clicked;
    }

    // Action destructive (retirer, fermer, terminer) : texte rouge sans fond. Elle passe par une
    // confirmation quand elle est rare ou irréversible, voir ConfirmInline.
    public static bool Destructive(string label, float width = 0f, float height = 0f)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.DangerColor);
        var clicked = Quiet(label, width, height);
        ImGui.PopStyleColor();
        return clicked;
    }

    // Réponse favorable face à une action destructive (Accepter à côté de Refuser) : texte vert,
    // pour que les deux réponses ne se lisent pas de la même couleur.
    public static bool Positive(string label, float width = 0f, float height = 0f)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.SuccessColor);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MasterEventTheme.SuccessColor with { W = 0.2f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, MasterEventTheme.SuccessColor with { W = 0.35f });
        var clicked = ImGui.Button(label, new Vector2(width, height));
        ImGui.PopStyleColor(4);
        return clicked;
    }

    // Seulement à l'étape de confirmation : là, le rouge plein dit bien ce qui va se passer.
    public static bool DestructiveFilled(string label, float width = 0f, float height = 0f)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, MasterEventTheme.DangerButtonBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MasterEventTheme.DangerButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, MasterEventTheme.DangerButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
        var clicked = ImGui.Button(label, new Vector2(width, height));
        ImGui.PopStyleColor(4);
        return clicked;
    }

    public static bool QuietIcon(FontAwesomeIcon icon, string id, string tooltip)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MasterEventTheme.AccentColor with { W = 0.25f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, MasterEventTheme.AccentColor with { W = 0.4f });
        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            clicked = ImGui.Button(icon.ToIconString() + "##" + id);
        ImGui.PopStyleColor(3);

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    public static float TextWidth(string label)
        => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;

    // Largeur d'un bouton principal, plus généreux qu'un bouton ordinaire.
    public static float PrimaryWidth(string label)
        => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 4f;

    public static float IconWidth(FontAwesomeIcon icon)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            return ImGui.CalcTextSize(icon.ToIconString()).X + ImGui.GetStyle().FramePadding.X * 2f;
    }
}
