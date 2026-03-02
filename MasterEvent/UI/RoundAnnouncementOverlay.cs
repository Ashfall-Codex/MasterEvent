using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
namespace MasterEvent.UI;


public sealed class RoundAnnouncementOverlay
{
    private string text = string.Empty;
    private DateTime showUntil = DateTime.MinValue;
    private DateTime showStart = DateTime.MinValue;
    private const float FadeInDuration = 0.3f;
    private const float HoldDuration = 2.0f;
    private const float FadeOutDuration = 0.5f;
    private const float TotalDuration = FadeInDuration + HoldDuration + FadeOutDuration;

    public void Show(string message)
    {
        text = message;
        showStart = DateTime.UtcNow;
        showUntil = showStart.AddSeconds(TotalDuration);
    }

    public void Draw()
    {
        var now = DateTime.UtcNow;
        if (now >= showUntil) return;

        var elapsed = (float)(now - showStart).TotalSeconds;

        float alpha;
        if (elapsed < FadeInDuration)
            alpha = elapsed / FadeInDuration;
        else if (elapsed < FadeInDuration + HoldDuration)
            alpha = 1f;
        else
            alpha = 1f - (elapsed - FadeInDuration - HoldDuration) / FadeOutDuration;

        alpha = Math.Clamp(alpha, 0f, 1f);
        if (alpha <= 0f) return;

        var viewport = ImGui.GetMainViewport();
        var center = viewport.GetCenter();

        var goldColor = new Vector4(0.92f, 0.80f, 0.36f, alpha);
        var shadowColor = new Vector4(0f, 0f, 0f, alpha * 0.8f);

        var fontHandle = Plugin.LargeFont;
        if (fontHandle == null) return;

        using (fontHandle.Push())
        {
            var textSize = ImGui.CalcTextSize(text);

            var textPos = new Vector2(
                center.X - textSize.X / 2f,
                viewport.Size.Y * 0.28f - textSize.Y / 2f);

            var dl = ImGui.GetForegroundDrawList();

            dl.AddText(textPos + new Vector2(2f, 2f), ImGui.GetColorU32(shadowColor), text);
            dl.AddText(textPos, ImGui.GetColorU32(goldColor), text);
        }
    }
}
