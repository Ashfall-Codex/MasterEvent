using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;

namespace MasterEvent.UI;


public sealed class TacticalOverlay
{
    private readonly SessionManager session;
    private readonly Configuration configuration;
    private const float WorldHeadOffset = 2.1f;
    private static readonly Vector4 PlayerColor = new(0.30f, 0.55f, 1f, 1f);

    public TacticalOverlay(SessionManager session, Configuration configuration)
    {
        this.session = session;
        this.configuration = configuration;
    }

    public void Draw()
    {
        if (!configuration.ShowTacticalOverlay) return;
        if (session.CurrentTurnState is not { IsActive: true } state) return;
        if (state.Entries.Count == 0) return;

        DrawActiveGroundMarker(state);
        DrawInitiativeBand(state);
        DrawFloatingHpBars(state);
    }


    private void DrawInitiativeBand(TurnState state)
    {
        var canEdit = session.CanEdit;

        var viewport = ImGui.GetMainViewport();
        var anchor = new Vector2(
            viewport.WorkPos.X + viewport.WorkSize.X * 0.5f,
            viewport.WorkPos.Y + 12f * ImGuiHelpers.GlobalScale);
        ImGui.SetNextWindowPos(anchor, ImGuiCond.Always, new Vector2(0.5f, 0f));

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

        // Le bandeau reste en lecture seule pour les joueurs, sauf pour celui dont c'est le tour :
        // il doit pouvoir clore son action lui-même plutôt que d'attendre que le MJ le fasse.
        if (!canEdit && !session.IsLocalPlayerTurn) flags |= ImGuiWindowFlags.NoInputs;

        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f * scale, 7f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.05f, 0.05f, 0.08f, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Border, MasterEventTheme.AccentColor with { W = 0.4f });

        if (ImGui.Begin("##MasterEventTacticalBand", flags))
        {
            DrawBandHeader(state, canEdit);
            DrawMovementQuota(state);
            ImGuiHelpers.ScaledDummy(3f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(3f);
            DrawBandCards(state, canEdit);
        }
        ImGui.End();

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    private void DrawBandHeader(TurnState state, bool canEdit)
    {
        ImGui.TextColored(MasterEventTheme.AccentColor,
            string.Format(Loc.Get("Tactical.Round"), state.Round));

        if (!canEdit)
        {
            // Joueur dont c'est le tour : un seul bouton, qui ne ferme que son propre tour.
            if (session.IsLocalPlayerTurn)
            {
                ImGui.SameLine();
                if (ImGui.Button(Loc.Get("Tactical.EndMyTurn") + "##tac_end_my_turn"))
                    session.RequestEndOwnTurn();
            }
            return;
        }

        ImGui.SameLine();
        var allActed = state.Entries.All(e => state.HasEntryActed(e));
        if (allActed)
        {
            if (ImGui.Button(Loc.Get("Tactical.NextRound") + "##tac_next_round"))
                session.NextRound();
        }
        else
        {
            if (ImGui.Button(Loc.Get("Tactical.EndTurn") + "##tac_end_turn"))
            {
                var idx = ActiveIndex(state);
                if (idx >= 0) session.ToggleHasActed(idx);
            }
        }
    }

    private void DrawMovementQuota(TurnState state)
    {
        var index = ActiveIndex(state);
        if (index < 0) return;

        var entry = state.Entries[index];
        var (left, max) = ResolveEntryMovement(entry);
        if (max <= 0f) return;
        var ratio = max > 0f ? Math.Clamp(left / max, 0f, 1f) : 0f;

        var color = ratio switch
        {
            <= 0f => new Vector4(0.85f, 0.25f, 0.25f, 1f),
            <= 0.25f => new Vector4(1f, 0.6f, 0.15f, 1f),
            _ => new Vector4(0.45f, 0.75f, 0.95f, 1f),
        };

        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
            string.Format(Loc.Get("Tactical.MovementOf"), CardLabel(entry)));
        ImGui.SameLine();
        ImGui.TextColored(color, string.Format(Loc.Get("Tactical.MovementValue"), left, max));


        if (session.CanEdit && entry.PlayerHash is { } hash)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"-1##mv_minus_{hash}")) session.GrantMovement(hash, -1f);
            ImGui.SameLine();
            if (ImGui.SmallButton($"+1##mv_plus_{hash}")) session.GrantMovement(hash, 1f);
            ImGui.SameLine();
            if (ImGui.SmallButton($"+5##mv_plus5_{hash}")) session.GrantMovement(hash, 5f);

            var granted = session.PartyMembers.FirstOrDefault(p => p.Hash == hash)?.MoveBonus ?? 0f;
            if (MathF.Abs(granted) > 0.01f)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.85f, 0.75f, 0.4f, 1f),
                    granted > 0f ? $"(+{granted:0.#})" : $"({granted:0.#})");
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
                ImGui.TextUnformatted(Loc.Get("Tactical.MovementGrantHint"));
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }

        // Jauge fine sous la ligne : le chiffre exact sert au MJ, la barre au coup d'œil.
        var width = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X - ImGui.GetStyle().WindowPadding.X;
        var barTop = ImGui.GetCursorScreenPos();
        var barH = 4f * ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(barTop, barTop + new Vector2(width, barH),
            ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.18f, 0.9f)), 2f);
        if (ratio > 0f)
            dl.AddRectFilled(barTop, barTop + new Vector2(width * ratio, barH), ImGui.GetColorU32(color), 2f);
        ImGui.Dummy(new Vector2(width, barH));
    }

    private void DrawBandCards(TurnState state, bool canEdit)
    {
        var activeIndex = ActiveIndex(state);
        var scale = ImGuiHelpers.GlobalScale;
        var normalSize = new Vector2(96f * scale, 60f * scale);
        var bigSize = new Vector2(118f * scale, 74f * scale);

        for (var i = 0; i < state.Entries.Count; i++)
        {
            if (i > 0) ImGui.SameLine(0, 4f * scale);
            var isActive = i == activeIndex;
            DrawCard(state, state.Entries[i], i, isActive, canEdit, isActive ? bigSize : normalSize);
        }
    }

    private void DrawCard(TurnState state, TurnEntry entry, int index, bool isActive, bool canEdit, Vector2 size)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##tac_card_{index}", size);

        // Le MJ peut corriger n'importe quelle carte ; un joueur ne peut agir que sur la sienne,
        // et seulement quand c'est son tour.
        var ownsActiveCard = isActive && session.IsLocalPlayerTurn;
        var clicked = (canEdit || ownsActiveCard) && ImGui.IsItemClicked();

        var acted = state.HasEntryActed(entry);
        var (hp, hpMax, shield, attitude, hasData) = ResolveEntryVitals(entry);

        var dl = ImGui.GetWindowDrawList();
        var p2 = pos + size;

        var entityColor = entry.PlayerHash != null ? PlayerColor : AttitudeColor(attitude);

        var bgMul = acted ? 0.12f : isActive ? 0.34f : 0.18f;
        var bg = new Vector4(entityColor.X * bgMul, entityColor.Y * bgMul, entityColor.Z * bgMul, 0.94f);
        dl.AddRectFilled(pos, p2, ImGui.GetColorU32(bg), 6f);

        dl.AddRect(pos, p2, ImGui.GetColorU32(isActive ? entityColor : entityColor with { W = 0.55f }), 6f);

        var nameColor = acted ? new Vector4(0.55f, 0.55f, 0.55f, 1f) : new Vector4(1f, 1f, 1f, 1f);
        var name = TruncateToWidth(CardLabel(entry), size.X - 10f);
        var nameSize = ImGui.CalcTextSize(name);
        var namePos = new Vector2(pos.X + (size.X - nameSize.X) * 0.5f, pos.Y + 6f);
        dl.AddText(namePos + new Vector2(1f, 1f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.8f)), name);
        dl.AddText(namePos, ImGui.GetColorU32(nameColor), name);

        var initText = entry.Initiative.ToString();
        var initSize = ImGui.CalcTextSize(initText);
        dl.AddText(new Vector2(pos.X + (size.X - initSize.X) * 0.5f, pos.Y + size.Y * 0.42f),
            ImGui.GetColorU32(new Vector4(0.85f, 0.82f, 0.5f, 1f)), initText);


        var barW = size.X - 12f;
        var bottom = p2.Y - 6f;

        if (hasData && hpMax > 0)
        {
            const float barH = 7f;
            DrawBarSegment(dl, new Vector2(pos.X + 6f, bottom - barH), barW, barH, hp, hpMax, shield, acted);
            bottom -= barH + 2f;
        }

        var (moveLeft, moveMax) = ResolveEntryMovement(entry);
        if (moveMax > 0f)
        {
            const float moveH = 3f;
            var moveTop = new Vector2(pos.X + 6f, bottom - moveH);
            var moveRatio = Math.Clamp(moveLeft / moveMax, 0f, 1f);

            dl.AddRectFilled(moveTop, moveTop + new Vector2(barW, moveH),
                ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.15f, 0.95f)), 1.5f);

            // Bleu, pour ne pas être confondu avec la barre de PV juste en dessous.
            var moveColor = moveRatio <= 0f
                ? new Vector4(0.85f, 0.25f, 0.25f, 1f)
                : new Vector4(0.42f, 0.70f, 0.95f, acted ? 0.5f : 1f);
            if (moveRatio > 0f)
                dl.AddRectFilled(moveTop, moveTop + new Vector2(barW * moveRatio, moveH),
                    ImGui.GetColorU32(moveColor), 1.5f);
        }

        // Une barre de trois pixels ne porte pas de chiffre : l'infobulle donne la valeur exacte,
        // et au passage le détail d'initiative, qui n'était affiché nulle part.
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(entry.Name);
            if (entry.InitiativeStatName is { } statName)
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
                    $"{statName} : {entry.InitiativeRoll} + {entry.InitiativeModifier} = {entry.Initiative}");
            if (hasData && hpMax > 0)
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"PV {hp} / {hpMax}");
            if (moveMax > 0f)
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
                    string.Format(Loc.Get("Tactical.MovementValue"), moveLeft, moveMax));
            ImGui.EndTooltip();
        }

        if (clicked)
        {
            // Le joueur passe par la demande relayée : le relais rejetterait un `turnUpdate`
            // venant d'un non-leader.
            if (canEdit) session.ToggleHasActed(index);
            else session.RequestEndOwnTurn();
        }
    }


    // Repère au sol de l'acteur courant. Le cercle est échantillonné en espace monde puis projeté
    // point par point : une ellipse tracée directement à l'écran resterait plate et « collée » à
    // la caméra, alors que celle-ci suit la perspective et l'inclinaison du sol.
    private void DrawActiveGroundMarker(TurnState state)
    {
        var index = ActiveIndex(state);
        if (index < 0) return;

        var entry = state.Entries[index];
        if (ResolveEntryWorldPosition(entry) is not { } feet) return;

        const int segments = 40;
        const float radius = 0.85f;

        Span<Vector2> points = stackalloc Vector2[segments];
        Span<bool> onScreen = stackalloc bool[segments];

        for (var i = 0; i < segments; i++)
        {
            var angle = MathF.Tau * i / segments;
            var world = feet + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            onScreen[i] = Plugin.GameGui.WorldToScreen(world, out var screen);
            points[i] = screen;
        }

        // Battement lent : le repère doit rester visible dans un décor chargé sans clignoter.
        var pulse = 0.72f + 0.28f * MathF.Sin((float)(DateTime.UtcNow.TimeOfDay.TotalSeconds * 2.4));

        var (_, _, _, attitude, _) = ResolveEntryVitals(entry);
        var baseColor = entry.PlayerHash != null ? PlayerColor : AttitudeColor(attitude);

        var dl = ImGui.GetForegroundDrawList();
        var thickness = 3.5f * ImGuiHelpers.GlobalScale;

        // Segment par segment plutôt qu'en polyligne fermée : quand l'acteur touche le bord de
        // l'écran, une partie des points ne se projette pas et un anneau fermé relierait deux
        // extrémités arbitraires en travers de l'image.
        for (var i = 0; i < segments; i++)
        {
            var j = (i + 1) % segments;
            if (!onScreen[i] || !onScreen[j]) continue;

            dl.AddLine(points[i], points[j],
                ImGui.GetColorU32(baseColor with { W = pulse }), thickness);
        }
    }

    private void DrawFloatingHpBars(TurnState state)
    {
        var dl = ImGui.GetForegroundDrawList();

        foreach (var entry in state.Entries)
        {
            var worldPos = ResolveEntryWorldPosition(entry);
            if (worldPos is not { } wp) continue;

            var (hp, hpMax, shield, _, hasData) = ResolveEntryVitals(entry);
            if (!hasData || hpMax <= 0) continue;

            var head = wp + new Vector3(0f, WorldHeadOffset, 0f);
            if (!Plugin.GameGui.WorldToScreen(head, out var screen)) continue;

            DrawFloatingBar(dl, screen, entry.Name, hp, hpMax, shield);
        }
    }

    private void DrawFloatingBar(ImDrawListPtr dl, Vector2 anchor, string name,
        int hp, int hpMax, int shield)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var barW = 120f * scale;
        var barH = 15f * scale;

        var barTop = new Vector2(anchor.X - barW * 0.5f, anchor.Y - barH);

        var nameSize = ImGui.CalcTextSize(name);
        var namePos = new Vector2(anchor.X - nameSize.X * 0.5f, barTop.Y - nameSize.Y - 2f);
        dl.AddText(namePos + new Vector2(1f, 1f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f)), name);
        dl.AddText(namePos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), name);

        DrawBarSegment(dl, barTop, barW, barH, hp, hpMax, shield, acted: false, withText: true);
    }


    private static void DrawBarSegment(ImDrawListPtr dl, Vector2 topLeft, float width, float height,
        int hp, int hpMax, int shield, bool acted, bool withText = false)
    {
        var p2 = topLeft + new Vector2(width, height);
        dl.AddRectFilled(topLeft, p2, ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.12f, 0.95f)), 3f);

        var ratio = Math.Clamp(hp / (float)hpMax, 0f, 1f);
        var color = BarColor(ratio, acted);
        var fillW = width * ratio;
        if (fillW > 0)
            dl.AddRectFilled(topLeft, topLeft + new Vector2(fillW, height), ImGui.GetColorU32(color), 3f);

        if (shield > 0)
        {
            var shieldRatio = Math.Clamp(shield / (float)hpMax, 0f, 1f - ratio);
            var shieldW = width * shieldRatio;
            if (shieldW > 0)
            {
                var sStart = topLeft + new Vector2(fillW, 0f);
                dl.AddRectFilled(sStart, sStart + new Vector2(shieldW, height),
                    ImGui.GetColorU32(MasterEventTheme.ShieldOverlayColor), 3f);
            }
        }

        dl.AddRect(topLeft, p2, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.7f)), 3f);

        if (withText)
        {
            var txt = shield > 0 ? $"{hp} (+{shield}) / {hpMax}" : $"{hp} / {hpMax}";
            var ts = ImGui.CalcTextSize(txt);
            var tp = new Vector2(topLeft.X + (width - ts.X) * 0.5f, topLeft.Y + (height - ts.Y) * 0.5f);
            dl.AddText(tp + new Vector2(1f, 1f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.8f)), txt);
            dl.AddText(tp, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), txt);
        }
    }

    private static Vector4 BarColor(float ratio, bool dimmed)
    {
        var c = ratio switch
        {
            <= 0.10f => new Vector4(0.85f, 0.20f, 0.20f, 1f),   // rouge
            <= 0.20f => new Vector4(1.00f, 0.55f, 0.10f, 1f),   // orange
            _ => new Vector4(0.30f, 0.78f, 0.30f, 1f),          // vert
        };
        if (dimmed) c = new Vector4(c.X * 0.5f, c.Y * 0.5f, c.Z * 0.5f, c.W);
        return c;
    }

    private static Vector4 AttitudeColor(Attitude attitude) => attitude switch
    {
        Attitude.Hostile => MasterEventTheme.AttitudeHostile,
        Attitude.Friendly => MasterEventTheme.AttitudeFriendly,
        _ => MasterEventTheme.AttitudeNeutral,
    };

    private (int hp, int hpMax, int shield, Attitude attitude, bool hasData) ResolveEntryVitals(TurnEntry entry)
    {
        if (entry.IsMarker && entry.WaymarkIndex is { } wi && wi >= 0 && wi < Constants.WaymarkCount)
        {
            var m = session.CurrentMarkers.Markers[wi];
            return (m.Hp, m.HpMax, m.Shield, m.Attitude, m.HasData);
        }

        if (entry.PlayerHash != null)
        {
            var p = session.PartyMembers.FirstOrDefault(x => x.Hash == entry.PlayerHash);
            if (p != null) return (p.Hp, p.HpMax, p.Shield, Attitude.Friendly, true);
        }

        return (0, 0, 0, Attitude.Neutral, false);
    }

    private (float left, float max) ResolveEntryMovement(TurnEntry entry)
    {
        if (entry.PlayerHash is not { } hash) return (0f, 0f);

        var player = session.PartyMembers.FirstOrDefault(p => p.Hash == hash);
        return player is null ? (0f, 0f) : (player.MoveLeft, player.MoveMax);
    }

    private Vector3? ResolveEntryWorldPosition(TurnEntry entry)
    {
        if (entry.IsMarker && entry.WaymarkIndex is { } wi && wi >= 0 && wi < Constants.WaymarkCount)
        {
            var m = session.CurrentMarkers.Markers[wi];
            if (!m.HasData || !m.IsVisible) return null;
            return new Vector3(m.X, m.Y, m.Z);
        }

        if (entry.PlayerHash != null)
            return FindPlayerWorldPosition(entry.Name);

        return null;
    }

    private static Vector3? FindPlayerWorldPosition(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is IPlayerCharacter pc && string.Equals(pc.Name.TextValue, name, StringComparison.Ordinal))
                return pc.Position;
        }
        return null;
    }

    private static string CardLabel(TurnEntry entry)
    {
        if (entry.PlayerHash == null) return entry.Name;
        var space = entry.Name.IndexOf(' ');
        return space > 0 ? entry.Name[..space] : entry.Name;
    }

    private int ActiveIndex(TurnState state)
    {
        for (var i = 0; i < state.Entries.Count; i++)
            if (!state.HasEntryActed(state.Entries[i])) return i;
        return -1;
    }

    private static string TruncateToWidth(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;

        var ellipsis = "…";
        var current = text;
        while (current.Length > 1 && ImGui.CalcTextSize(current + ellipsis).X > maxWidth)
            current = current[..^1];
        return current + ellipsis;
    }
}
