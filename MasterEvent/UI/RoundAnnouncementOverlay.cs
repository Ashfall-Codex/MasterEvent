using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
namespace MasterEvent.UI;


public sealed class RoundAnnouncementOverlay
{
    private string text = string.Empty;
    private DateTime showUntil = DateTime.MinValue;
    private DateTime showStart = DateTime.MinValue;
    // Couleur du texte (sans alpha, il est appliqué dynamiquement). Défaut : or pour les annonces de tour.
    private static readonly Vector3 GoldRgb = new(0.92f, 0.80f, 0.36f);
    // Rouge rubis pour les annonces MJ libres.
    public static readonly Vector3 RubyRgb = new(0.78f, 0.15f, 0.22f);
    private Vector3 textColorRgb = GoldRgb;
    // Durée étendue pour les annonces MJ libres (plus longues à lire qu'un "Tour 3").
    private float currentHoldDuration = DefaultHoldDuration;
    private const float FadeInDuration = 0.3f;
    private const float DefaultHoldDuration = 2.0f;
    private const float FadeOutDuration = 0.5f;

    // Affiche un message au centre de l'écran.
    // color : couleur RGB (alpha géré automatiquement). Null = or par défaut.
    // holdDurationSeconds : durée d'affichage à pleine opacité (avant fade-out).
    public void Show(string message, Vector3? color = null, float holdDurationSeconds = DefaultHoldDuration)
    {
        text = message;
        textColorRgb = color ?? GoldRgb;
        currentHoldDuration = holdDurationSeconds;
        showStart = DateTime.UtcNow;
        showUntil = showStart.AddSeconds(FadeInDuration + holdDurationSeconds + FadeOutDuration);
    }

    public void Draw()
    {
        var now = DateTime.UtcNow;
        if (now >= showUntil) return;

        var elapsed = (float)(now - showStart).TotalSeconds;

        float alpha;
        if (elapsed < FadeInDuration)
            alpha = elapsed / FadeInDuration;
        else if (elapsed < FadeInDuration + currentHoldDuration)
            alpha = 1f;
        else
            alpha = 1f - (elapsed - FadeInDuration - currentHoldDuration) / FadeOutDuration;

        alpha = Math.Clamp(alpha, 0f, 1f);
        if (alpha <= 0f) return;

        var viewport = ImGui.GetMainViewport();
        var center = viewport.GetCenter();

        var textColor = new Vector4(textColorRgb.X, textColorRgb.Y, textColorRgb.Z, alpha);
        var shadowColor = new Vector4(0f, 0f, 0f, alpha * 0.8f);

        var fontHandle = Plugin.LargeFont;
        if (fontHandle == null) return;

        using (fontHandle.Push())
        {
            // Largeur max raisonnable pour éviter un texte qui traverse tout l'écran.
            // 60% de la viewport = assez pour des annonces longues, mais reste lisible.
            var maxWidth = viewport.Size.X * 0.6f;
            var lines = WrapTextToWidth(text, maxWidth);

            // Hauteur d'une ligne (CalcTextSize sur "Mg" est un bon proxy pour la hauteur typo).
            var lineHeight = ImGui.CalcTextSize("Mg").Y;
            var totalHeight = lineHeight * lines.Count;

            // Bloc de texte centré verticalement autour de 28% de la hauteur viewport.
            var startY = viewport.Size.Y * 0.28f - totalHeight / 2f;

            var dl = ImGui.GetForegroundDrawList();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var lineSize = ImGui.CalcTextSize(line);
                var linePos = new Vector2(
                    center.X - lineSize.X / 2f,
                    startY + i * lineHeight);

                dl.AddText(linePos + new Vector2(2f, 2f), ImGui.GetColorU32(shadowColor), line);
                dl.AddText(linePos, ImGui.GetColorU32(textColor), line);
            }
        }
    }

    // Découpe un texte en lignes qui tiennent dans maxWidth.
    // Respecte les \n explicites, wrappe par mot, et si un mot seul dépasse (ex : "AAAA..." sans espace),
    // le coupe caractère par caractère pour éviter un débordement hors écran.
    private static List<string> WrapTextToWidth(string text, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            if (string.IsNullOrEmpty(paragraph))
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                var separator = current.Length == 0 ? string.Empty : " ";
                var candidate = current + separator + word;

                // Cas nominal : le mot tient sur la ligne courante
                if (ImGui.CalcTextSize(candidate).X <= maxWidth)
                {
                    current.Append(separator);
                    current.Append(word);
                    continue;
                }

                // Flush la ligne courante, elle est complète
                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                // Le mot entier tient sur une nouvelle ligne vide
                if (ImGui.CalcTextSize(word).X <= maxWidth)
                {
                    current.Append(word);
                    continue;
                }

                // Fallback : mot plus large que maxWidth → on coupe caractère par caractère
                foreach (var c in word)
                {
                    var chunkCandidate = current.ToString() + c;
                    if (current.Length == 0 || ImGui.CalcTextSize(chunkCandidate).X <= maxWidth)
                    {
                        current.Append(c);
                    }
                    else
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                        current.Append(c);
                    }
                }
            }
            if (current.Length > 0) lines.Add(current.ToString());
        }
        return lines;
    }
}
