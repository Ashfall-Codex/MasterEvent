using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MasterEvent.Models;

namespace MasterEvent.Services;

// Suivi du quota de déplacement du joueur local, en yalms.
public sealed class MovementTracker
{
    // Sous ce seuil, on est dans le bruit de la position renvoyée par le jeu, pas dans un déplacement voulu.
    private const float NoiseThreshold = 0.02f;

    // Au-delà, ce n'est pas une marche mais une téléportation.
    private const float TeleportThreshold = 15f;
    private const float TrailPointSpacing = 0.4f;
    private const int MaxTrailPoints = 400;

    private Vector3 anchor;
    private Vector3 last;
    private bool tracking;
    private readonly List<Vector3> trail = new();
    public Vector3 Anchor => anchor;
    public IReadOnlyList<Vector3> Trail => trail;
    public Vector3 Head => last;
    public float Consumed { get; private set; }
    public bool IsTracking => tracking;
    public static float ResolveMax(EventTemplate? template, PlayerData? player)
    {
        if (template is not { MovementQuota: > 0 }) return 0f;

        var total = (float)template.MovementQuota;
        if (template.MovementStatId is { } statId && player?.Stats is { } stats)
        {
            var stat = stats.FirstOrDefault(s => s.Id == statId);
            if (stat != null) total += stat.Modifier;
        }

        total += player?.MoveBonus ?? 0f;

        return MathF.Max(0f, total);
    }

    public float Remaining(float max) => MathF.Max(0f, max - Consumed);
    public bool IsExceeded(float max) => max > 0f && Consumed > max;
    public void Tick(bool shouldTrack, Vector3 position)
    {
        if (!shouldTrack)
        {
            tracking = false;
            return;
        }

        if (!tracking)
        {
            Reset(position);
            return;
        }

        var step = HorizontalDistance(position, last);

        if (step > TeleportThreshold)
        {
            Reset(position);
            return;
        }

        if (step < NoiseThreshold) return;

        var distBefore = HorizontalDistance(last, anchor);
        var distNow = HorizontalDistance(position, anchor);

        Consumed += step;

        if (distNow < distBefore) Consumed -= distBefore - distNow;

        if (Consumed < 0f) Consumed = 0f;
        last = position;
        RecordTrailPoint(position);
    }

    public void Reset(Vector3 position)
    {
        anchor = position;
        last = position;
        Consumed = 0f;
        tracking = true;

        trail.Clear();
        trail.Add(position);
    }

    private void RecordTrailPoint(Vector3 position)
    {
        if (trail.Count == 0)
        {
            trail.Add(position);
            return;
        }

        if (trail.Count >= MaxTrailPoints) return;

        if (HorizontalDistance(position, trail[^1]) >= TrailPointSpacing)
            trail.Add(position);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
