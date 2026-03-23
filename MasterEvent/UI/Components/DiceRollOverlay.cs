using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
namespace MasterEvent.UI.Components;

public sealed class DiceRollOverlay
{
    private string rollerName = string.Empty;
    private string? statName;
    private int finalResult;
    private int rawRoll;
    private int diceMax;
    private int displayedNumber;
    private DateTime animStart = DateTime.MinValue;
    private DateTime animEnd = DateTime.MinValue;
    private DateTime lastTick = DateTime.MinValue;

    // Étapes d'animation des modificateurs (max 2 : stat puis temp)
    private readonly (int value, int runningTotal, bool isTemp)[] modSteps = new (int, int, bool)[2];
    private int modStepCount;

    private bool IsCriticalSuccess => rawRoll >= diceMax;
    private bool IsCriticalFail => rawRoll <= 1;
    private bool IsCritical => IsCriticalSuccess || IsCriticalFail;

    // Durées des phases de base
    private const float FadeInDuration = 0.25f;
    private const float RollDuration = 1.4f;
    private const float RevealDuration = 0.3f;
    private const float HoldDuration = 1.0f;
    private const float FadeOutDuration = 0.4f;

    // Durées d'une étape de modificateur (show + absorb + pop)
    private const float ModShowDuration = 0.35f;
    private const float ModAbsorbDuration = 0.45f;
    private const float ModPopDuration = 0.2f;
    private const float StepDuration = ModShowDuration + ModAbsorbDuration + ModPopDuration;

    // Seuils fixes
    private const float RollStart = FadeInDuration;
    private const float RevealStart = RollStart + RollDuration;
    private const float StepsStart = RevealStart + RevealDuration;

    // Vitesse de rotation du dé
    private const float MaxSpeed = 6f;
    private const float MinSpeed = 0.3f;

    // Géométrie de l'icosaèdre
    private static readonly Vector3[] DieVertices;
    private static readonly int[][] DieFaces;

    static DiceRollOverlay()
    {
        var phi = (1f + MathF.Sqrt(5f)) / 2f;
        var len = MathF.Sqrt(1f + phi * phi);
        var a = 1f / len;
        var b = phi / len;

        DieVertices =
        [
            new(0, a, b), new(0, a, -b), new(0, -a, b), new(0, -a, -b),
            new(a, b, 0), new(a, -b, 0), new(-a, b, 0), new(-a, -b, 0),
            new(b, 0, a), new(b, 0, -a), new(-b, 0, a), new(-b, 0, -a),
        ];

        DieFaces =
        [
            [0, 2, 8],   [0, 8, 4],   [0, 4, 6],   [0, 6, 10],  [0, 10, 2],
            [1, 3, 11],  [1, 11, 6],  [1, 6, 4],   [1, 4, 9],   [1, 9, 3],
            [2, 5, 8],   [2, 7, 5],   [2, 10, 7],
            [3, 9, 5],   [3, 5, 7],   [3, 7, 11],
            [4, 8, 9],   [6, 11, 10],
            [5, 9, 8],   [7, 10, 11],
        ];
    }

    private readonly (int faceIndex, float avgZ)[] faceSortBuffer = new (int, float)[DieFaces.Length];

    // Message chat en attente, affiché à la fin de l'animation
    private string? pendingChatMessage;

    public bool IsAnimating => DateTime.UtcNow < animEnd;

    // Enregistre un message à afficher dans le chat à la fin de l'animation.
    public void DeferChatMessage(string message)
    {
        pendingChatMessage = message;
    }

    private void FlushPendingChat()
    {
        if (pendingChatMessage == null) return;
        Plugin.ChatGui.Print(pendingChatMessage);
        pendingChatMessage = null;
    }

    // Déclenche l'animation. statMod = bonus de stat, tempMod = bonus/malus temporaire.
    public void Show(string roller, int result, int max, int raw, int statMod = 0, int tempMod = 0, string? stat = null)
    {
        // Si une animation précédente avait un message en attente, l'afficher maintenant
        FlushPendingChat();

        rollerName = roller;
        finalResult = result;
        diceMax = max;
        rawRoll = raw;
        statName = stat;
        displayedNumber = 0;
        animStart = DateTime.UtcNow;
        lastTick = DateTime.MinValue;

        // Construire la liste des étapes de modificateur
        modStepCount = 0;
        var running = raw;
        if (statMod != 0)
        {
            running += statMod;
            modSteps[modStepCount++] = (statMod, running, false);
        }
        if (tempMod != 0)
        {
            running += tempMod;
            modSteps[modStepCount++] = (tempMod, running, true);
        }

        var extra = modStepCount * StepDuration;
        animEnd = animStart.AddSeconds(FadeInDuration + RollDuration + RevealDuration + extra + HoldDuration + FadeOutDuration);
    }

    public void Draw()
    {
        var now = DateTime.UtcNow;
        if (now >= animEnd)
        {
            FlushPendingChat();
            return;
        }

        var elapsed = (float)(now - animStart).TotalSeconds;

        // Seuils dynamiques
        var allStepsEnd = StepsStart + modStepCount * StepDuration;
        var holdStart = allStepsEnd;
        var fadeOutStart = holdStart + HoldDuration;

        // Opacité globale
        float alpha;
        if (elapsed < FadeInDuration)
            alpha = elapsed / FadeInDuration;
        else if (elapsed < fadeOutStart)
            alpha = 1f;
        else
            alpha = 1f - (elapsed - fadeOutStart) / FadeOutDuration;
        alpha = Math.Clamp(alpha, 0f, 1f);
        if (alpha <= 0f) return;

        // Nombre affiché : roulette -> jet brut -> intermédiaires -> total
        UpdateDisplayedNumber(now, elapsed, allStepsEnd);

        var viewport = ImGui.GetMainViewport();
        var viewportSize = viewport.Size;
        var viewportPos = viewport.Pos;
        var center = viewport.GetCenter();
        var dl = ImGui.GetForegroundDrawList();

        // La couleur critique n'apparaît qu'après la révélation pour garder le suspens
        var revealed = elapsed >= RevealStart;
        var accentColor = revealed ? GetAccentColor(alpha) : new Vector4(0.92f, 0.80f, 0.36f, alpha);

        // Fond assombri (teinté lors des critiques)
        if (IsCritical && revealed)
        {
            var tint = IsCriticalSuccess
                ? new Vector4(0f, 0.03f, 0f, 0.6f * alpha)
                : new Vector4(0.04f, 0f, 0f, 0.6f * alpha);
            dl.AddRectFilled(viewportPos, viewportPos + viewportSize, ImGui.GetColorU32(tint));
        }
        else
        {
            dl.AddRectFilled(viewportPos, viewportPos + viewportSize,
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f * alpha)));
        }

        var shadowColor = new Vector4(0f, 0f, 0f, alpha * 0.8f);
        var subtitleColor = new Vector4(0.75f, 0.70f, 0.60f, alpha * 0.9f);
        var dieCenter = new Vector2(center.X, center.Y - viewportSize.Y * 0.02f);
        var numberCenter = new Vector2(center.X, center.Y - viewportSize.Y * 0.04f);

        float dieAlpha;
        if (elapsed < RevealStart)
            dieAlpha = alpha;
        else if (elapsed < StepsStart)
            dieAlpha = alpha * (1f - 0.7f * (elapsed - RevealStart) / RevealDuration);
        else if (modStepCount > 0 && elapsed < allStepsEnd)
        {
            // Fond subtil pendant les animations de modificateurs, disparaît à la fin
            var stepsRemaining = allStepsEnd - elapsed;
            dieAlpha = stepsRemaining < ModPopDuration
                ? alpha * 0.25f * (stepsRemaining / ModPopDuration)
                : alpha * 0.25f;
        }
        else
            dieAlpha = 0f;

        if (dieAlpha > 0.01f)
        {
            var dieSize = viewportSize.Y * 0.09f;
            DrawDie(dl, dieCenter, dieSize, elapsed, dieAlpha);
        }

        // Effets critiques après la révélation
        if (IsCritical && revealed)
            DrawCriticalEffects(dl, numberCenter, viewportSize, elapsed, alpha);

        var fontHandle = Plugin.LargeFont;
        if (fontHandle == null) return;

        var numberText = displayedNumber > 0 ? displayedNumber.ToString() : "?";

        if (elapsed < RevealStart)
        {
            // Pendant le roll : nombre sur le dé
            using (fontHandle.Push())
            {
                var font = ImGui.GetFont();
                var fontSize = font.FontSize * 0.55f;
                var fullSize = ImGui.CalcTextSize(numberText);
                var scaledSize = fullSize * 0.55f;
                var numPos = new Vector2(dieCenter.X - scaledSize.X / 2f, dieCenter.Y - scaledSize.Y / 2f);

                dl.AddText(font, fontSize, numPos + new Vector2(2f, 2f),
                    ImGui.GetColorU32(new Vector4(0f, 0f, 0f, alpha * 0.8f)), numberText);
                dl.AddText(font, fontSize, numPos, ImGui.GetColorU32(accentColor), numberText);
            }
        }
        else
        {
            // Après la révélation : nombre plein format avec effets de scale
            var scale = ComputeNumberScale(elapsed);

            using (fontHandle.Push())
            {
                var font = ImGui.GetFont();
                var fontSize = font.FontSize * scale;
                var baseSize = ImGui.CalcTextSize(numberText);
                var scaledSize = baseSize * scale;
                var numberPos = new Vector2(numberCenter.X - scaledSize.X / 2f, numberCenter.Y - scaledSize.Y / 2f);

                dl.AddText(font, fontSize, numberPos + new Vector2(3f, 3f),
                    ImGui.GetColorU32(shadowColor), numberText);
                dl.AddText(font, fontSize, numberPos, ImGui.GetColorU32(accentColor), numberText);
            }

            // Animation de chaque étape de modificateur
            for (var step = 0; step < modStepCount; step++)
            {
                var stepStart = StepsStart + step * StepDuration;
                var stepAbsorbStart = stepStart + ModShowDuration;
                var stepPopStart = stepAbsorbStart + ModAbsorbDuration;
                var stepEnd = stepStart + StepDuration;

                if (elapsed < stepStart || elapsed >= stepEnd) continue;

                var (value, _, isTemp) = modSteps[step];
                var modSign = value > 0 ? $"+{value}" : value.ToString();
                var modLabel = isTemp
                    ? (value >= 0 ? "Bonus temp." : "Malus temp.")
                    : (statName ?? "Stat");
                var modText = $"{modLabel} {modSign}";
                var modColorBase = value >= 0
                    ? new Vector3(0.5f, 0.9f, 0.5f)
                    : new Vector3(0.9f, 0.4f, 0.4f);

                var modStartPos = new Vector2(numberCenter.X, numberCenter.Y + viewportSize.Y * 0.055f);

                float modAlpha, modScale;
                Vector2 modPos;

                if (elapsed < stepAbsorbStart)
                {
                    var showProgress = (elapsed - stepStart) / ModShowDuration;
                    modAlpha = alpha * Math.Clamp(showProgress * 2.5f, 0f, 1f);
                    modScale = 0.45f;
                    modPos = modStartPos;
                }
                else if (elapsed < stepPopStart)
                {
                    var absorbProgress = (elapsed - stepAbsorbStart) / ModAbsorbDuration;
                    var eased = absorbProgress * absorbProgress * absorbProgress;
                    modPos = Vector2.Lerp(modStartPos, numberCenter, eased);
                    modScale = 0.45f * (1f - eased * 0.7f);
                    modAlpha = alpha * (1f - eased);
                }
                else
                {
                    modAlpha = 0f;
                    modScale = 0f;
                    modPos = numberCenter;
                }

                if (modAlpha > 0.01f)
                {
                    using (fontHandle.Push())
                    {
                        var font = ImGui.GetFont();
                        var fontSize = font.FontSize * modScale;
                        var fullSize = ImGui.CalcTextSize(modText);
                        var scaledModSize = fullSize * modScale;
                        var drawPos = new Vector2(modPos.X - scaledModSize.X / 2f, modPos.Y - scaledModSize.Y / 2f);

                        dl.AddText(font, fontSize, drawPos + new Vector2(1f, 1f),
                            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, modAlpha * 0.6f)), modText);
                        dl.AddText(font, fontSize, drawPos,
                            ImGui.GetColorU32(new Vector4(modColorBase.X, modColorBase.Y, modColorBase.Z, modAlpha)),
                            modText);
                    }
                }

                // Flash blanc à chaque fusion
                if (elapsed >= stepPopStart && elapsed < stepPopStart + 0.1f)
                {
                    var flashProgress = (elapsed - stepPopStart) / 0.1f;
                    var flashAlpha = alpha * 0.3f * (1f - flashProgress);
                    dl.AddCircleFilled(numberCenter, viewportSize.Y * 0.04f * (1f + flashProgress),
                        ImGui.GetColorU32(new Vector4(1f, 1f, 1f, flashAlpha)), 24);
                }
            }

            // Label critique (affiché une fois le total visible)
            if (IsCritical && elapsed >= holdStart)
            {
                var critLabel = IsCriticalSuccess ? "Réussite critique !" : "Échec critique !";
                var critLabelColor = IsCriticalSuccess
                    ? new Vector4(0.3f, 1f, 0.3f, alpha * 0.95f)
                    : new Vector4(1f, 0.25f, 0.25f, alpha * 0.95f);
                var labelSize = ImGui.CalcTextSize(critLabel);
                var labelPos = new Vector2(center.X - labelSize.X / 2f, center.Y + viewportSize.Y * 0.02f);

                dl.AddText(labelPos + new Vector2(1f, 1f),
                    ImGui.GetColorU32(new Vector4(0f, 0f, 0f, alpha * 0.7f)), critLabel);
                dl.AddText(labelPos, ImGui.GetColorU32(critLabelColor), critLabel);
            }
        }

        // Sous-titre (position fixe, juste au-dessus de la ligne du bas)
        var subtitle = statName != null ? $"{rollerName} - {statName}" : rollerName;
        var lineBottomY = center.Y + viewportSize.Y * 0.10f;

        if (fontHandle != null)
        {
            using (fontHandle.Push())
            {
                var font = ImGui.GetFont();
                var fontSize = font.FontSize * 0.35f;
                var fullSize = ImGui.CalcTextSize(subtitle);
                var scaledSize = fullSize * 0.35f;
                var subtitlePos = new Vector2(center.X - scaledSize.X / 2f, lineBottomY - scaledSize.Y - 6f);

                dl.AddText(font, fontSize, subtitlePos + new Vector2(1f, 1f),
                    ImGui.GetColorU32(new Vector4(0f, 0f, 0f, alpha * 0.6f)), subtitle);
                dl.AddText(font, fontSize, subtitlePos, ImGui.GetColorU32(subtitleColor), subtitle);
            }
        }

        // Lignes décoratives
        var lineHalfWidth = viewportSize.X * 0.12f;
        var lineVec = IsCritical && revealed
            ? new Vector4(accentColor.X, accentColor.Y, accentColor.Z, alpha * 0.4f)
            : new Vector4(0.92f, 0.80f, 0.36f, alpha * 0.3f);
        var lineColor = ImGui.GetColorU32(lineVec);
        var lineTop = center.Y - viewportSize.Y * 0.12f;
        var lineBottom = center.Y + viewportSize.Y * 0.10f;
        dl.AddLine(new Vector2(center.X - lineHalfWidth, lineTop),
            new Vector2(center.X + lineHalfWidth, lineTop), lineColor, 1.5f);
        dl.AddLine(new Vector2(center.X - lineHalfWidth, lineBottom),
            new Vector2(center.X + lineHalfWidth, lineBottom), lineColor, 1.5f);
    }

    // Met à jour le nombre affiché selon la phase courante
    private void UpdateDisplayedNumber(DateTime now, float elapsed, float allStepsEnd)
    {
        if (elapsed >= RollStart && elapsed < RevealStart)
        {
            var rollProgress = (elapsed - RollStart) / RollDuration;
            var tickInterval = 0.03f + 0.17f * (rollProgress * rollProgress);
            if (lastTick == DateTime.MinValue || (float)(now - lastTick).TotalSeconds >= tickInterval)
            {
                displayedNumber = Random.Shared.Next(1, diceMax + 1);
                lastTick = now;
            }
        }
        else if (elapsed >= RevealStart && modStepCount > 0 && elapsed < allStepsEnd)
        {
            var stepElapsed = elapsed - StepsStart;
            if (stepElapsed < 0)
            {
                // Encore dans la phase reveal
                displayedNumber = rawRoll;
            }
            else
            {
                var currentStep = Math.Min((int)(stepElapsed / StepDuration), modStepCount - 1);
                var inStep = stepElapsed - currentStep * StepDuration;
                var popStart = ModShowDuration + ModAbsorbDuration;

                if (inStep >= popStart)
                    displayedNumber = modSteps[currentStep].runningTotal;
                else if (currentStep > 0)
                    displayedNumber = modSteps[currentStep - 1].runningTotal;
                else
                    displayedNumber = rawRoll;
            }
        }
        else if (elapsed >= RevealStart)
        {
            displayedNumber = finalResult;
        }
    }

    // Calcule le scale du nombre principal (pop au reveal, vibration pendant absorb, pop au changement)
    private float ComputeNumberScale(float elapsed)
    {
        // Pop au reveal initial
        if (elapsed < StepsStart)
        {
            var revealProgress = (elapsed - RevealStart) / RevealDuration;
            var popIntensity = IsCritical ? 0.25f : 0.15f;
            return 1f + popIntensity * MathF.Sin(revealProgress * MathF.PI);
        }

        // Effets par étape de modificateur
        for (var step = 0; step < modStepCount; step++)
        {
            var stepStart = StepsStart + step * StepDuration;
            var stepAbsorbStart = stepStart + ModShowDuration;
            var stepPopStart = stepAbsorbStart + ModAbsorbDuration;
            var stepEnd = stepStart + StepDuration;

            if (elapsed >= stepAbsorbStart && elapsed < stepPopStart)
            {
                var absorbProgress = (elapsed - stepAbsorbStart) / ModAbsorbDuration;
                return 1f + MathF.Sin(absorbProgress * MathF.PI * 6f) * 0.02f * absorbProgress;
            }
            if (elapsed >= stepPopStart && elapsed < stepEnd)
            {
                var popProgress = (elapsed - stepPopStart) / ModPopDuration;
                return 1f + 0.12f * MathF.Sin(popProgress * MathF.PI);
            }
        }

        return 1f;
    }

    private Vector4 GetAccentColor(float alpha)
    {
        if (IsCriticalSuccess) return new Vector4(0.3f, 1f, 0.3f, alpha);
        if (IsCriticalFail) return new Vector4(1f, 0.25f, 0.25f, alpha);
        return new Vector4(0.92f, 0.80f, 0.36f, alpha);
    }

    private void DrawCriticalEffects(ImDrawListPtr dl, Vector2 critCenter, Vector2 viewportSize, float elapsed, float alpha)
    {
        var t = elapsed - RevealStart;
        var pulse = 0.7f + 0.3f * MathF.Sin(t * 4f);
        var glowBase = IsCriticalSuccess ? new Vector3(0.2f, 0.9f, 0.2f) : new Vector3(0.9f, 0.15f, 0.15f);

        var baseRadius = viewportSize.Y * 0.08f * pulse;
        for (var ring = 2; ring >= 0; ring--)
        {
            var radius = baseRadius * (1f + ring * 0.5f);
            dl.AddCircleFilled(critCenter, radius,
                ImGui.GetColorU32(new Vector4(glowBase.X, glowBase.Y, glowBase.Z, alpha * 0.12f / (1f + ring))), 32);
        }

        const int rayCount = 12;
        var rayLen = viewportSize.Y * 0.10f * pulse;
        var rot = t * 0.5f;
        for (var i = 0; i < rayCount; i++)
        {
            var angle = rot + i * (MathF.PI * 2f / rayCount);
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var inner = viewportSize.Y * 0.04f;
            dl.AddLine(critCenter + dir * inner, critCenter + dir * rayLen,
                ImGui.GetColorU32(new Vector4(glowBase.X, glowBase.Y, glowBase.Z, alpha * 0.25f)), 1.5f);
        }
    }

    private void DrawDie(ImDrawListPtr dl, Vector2 screenCenter, float size, float time, float alpha)
    {
        var angle = ComputeAngle(time);
        Span<Vector3> transformed = stackalloc Vector3[DieVertices.Length];
        Span<Vector2> projected = stackalloc Vector2[DieVertices.Length];

        for (var i = 0; i < DieVertices.Length; i++)
        {
            var v = DieVertices[i];
            v = RotateZ(v, angle * 0.3f);
            v = RotateY(v, angle);
            v = RotateX(v, angle * 0.6f);
            transformed[i] = v;
            projected[i] = new Vector2(screenCenter.X + v.X * size, screenCenter.Y - v.Y * size);
        }

        var visibleCount = 0;
        for (var i = 0; i < DieFaces.Length; i++)
        {
            var face = DieFaces[i];
            var e1 = transformed[face[1]] - transformed[face[0]];
            var e2 = transformed[face[2]] - transformed[face[0]];
            if (e1.X * e2.Y - e1.Y * e2.X <= 0) continue;
            var avgZ = (transformed[face[0]].Z + transformed[face[1]].Z + transformed[face[2]].Z) / 3f;
            faceSortBuffer[visibleCount++] = (i, avgZ);
        }

        Array.Sort(faceSortBuffer, 0, visibleCount,
            Comparer<(int faceIndex, float avgZ)>.Create((a, b) => a.avgZ.CompareTo(b.avgZ)));

        var edgeColor = ImGui.GetColorU32(new Vector4(0.92f, 0.80f, 0.36f, alpha * 0.5f));
        var edgeHighlight = ImGui.GetColorU32(new Vector4(0.92f, 0.80f, 0.36f, alpha * 0.85f));

        for (var i = 0; i < visibleCount; i++)
        {
            var (faceIndex, avgZ) = faceSortBuffer[i];
            var face = DieFaces[faceIndex];
            var p0 = projected[face[0]];
            var p1 = projected[face[1]];
            var p2 = projected[face[2]];
            var bright = 0.5f + 0.5f * Math.Clamp((avgZ + 1f) / 2f, 0f, 1f);

            dl.AddTriangleFilled(p0, p1, p2,
                ImGui.GetColorU32(new Vector4(0.14f * bright, 0.07f * bright, 0.07f * bright, alpha * 0.9f)));
            dl.AddTriangle(p0, p1, p2, i >= visibleCount - 3 ? edgeHighlight : edgeColor, 1.2f);
        }
    }

    private static float ComputeAngle(float elapsed)
    {
        if (elapsed <= 0) return 0;
        if (elapsed <= RollStart) return elapsed * MaxSpeed;

        var angleAtRollStart = RollStart * MaxSpeed;
        if (elapsed <= RevealStart)
        {
            var u = (elapsed - RollStart) / RollDuration;
            var sr = MaxSpeed - MinSpeed;
            return angleAtRollStart + RollDuration * (MaxSpeed * u - sr * u * u * u / 3f);
        }

        var srf = MaxSpeed - MinSpeed;
        var angleAtReveal = angleAtRollStart + RollDuration * (MaxSpeed - srf / 3f);
        return angleAtReveal + MinSpeed * (elapsed - RevealStart);
    }

    private static Vector3 RotateX(Vector3 v, float a)
    {
        var c = MathF.Cos(a); var s = MathF.Sin(a);
        return new Vector3(v.X, v.Y * c - v.Z * s, v.Y * s + v.Z * c);
    }
    private static Vector3 RotateY(Vector3 v, float a)
    {
        var c = MathF.Cos(a); var s = MathF.Sin(a);
        return new Vector3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
    }
    private static Vector3 RotateZ(Vector3 v, float a)
    {
        var c = MathF.Cos(a); var s = MathF.Sin(a);
        return new Vector3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
    }
}
