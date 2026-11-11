using Dalamud.Bindings.ImGui;

namespace MasterEvent.UI.Components;

// Coupe un texte trop long en finissant par trois points, jamais par le caractère d'ellipse.
public static class TextFit
{
    private const string Dots = "...";

    // Rend une chaîne vide quand la place manque tout à fait.
    public static string Fit(string text, float maxWidth)
    {
        if (maxWidth <= 0f) return string.Empty;
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;

        var dotsWidth = ImGui.CalcTextSize(Dots).X;
        var end = text.Length;
        while (end > 0 && ImGui.CalcTextSize(text[..end]).X + dotsWidth > maxWidth)
            end--;
        return end == 0 ? Dots : text[..end].TrimEnd() + Dots;
    }

    // Garde au moins une lettre, et dit si le texte a été coupé pour que l'appelant rende le texte
    // entier en infobulle.
    public static string Clip(string text, float maxWidth, out bool clipped)
    {
        clipped = ImGui.CalcTextSize(text).X > maxWidth;
        if (!clipped) return text;

        var end = text.Length;
        while (end > 1 && ImGui.CalcTextSize(text[..end].TrimEnd() + Dots).X > maxWidth) end--;
        return text[..end].TrimEnd() + Dots;
    }
}
