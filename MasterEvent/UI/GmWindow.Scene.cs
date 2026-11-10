using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services.Npc;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Onglet « Scène » : joueurs, terrain et combat sur un même écran. Terrain, Groupe et Tours étaient
// séparés, et le MJ passait de l'un à l'autre sans arrêt pour mener une scène.
public sealed partial class GmWindow
{
    private enum SceneSelection { None, Player, Marker, Npc }

    private SceneSelection sceneSelection;
    private string? sceneSelectedPlayer;
    private WaymarkId sceneSelectedMarker;
    private Guid sceneSelectedNpc;
    private int sceneNarrowList = 1;
    private int sceneCombatView;
    private bool sceneWasCombat;
    private int sceneSeenSpawnSerial;
    private WaymarkId? scenePlacingWaymark;
    private long scenePlacingSince;

    // Largeurs minimales lisibles : en dessous, la vue passe en onglets et le détail en pleine page.
    private const float SceneListMinWidth = 320f;
    private const float ScenePlayersMinWidth = 180f;

    // Placement annulé en jeu : passé ce délai, une balise posée autrement n'ouvre pas sa carte d'elle-même.
    private const long ScenePlacingTimeoutMs = 30_000;

    private void DrawSceneContent()
    {
        var gmAccess = HasGmAccess();

        if (!session.CanEdit && !gmAccess)
        {
            var avail = ImGui.GetContentRegionAvail();
            var text = Loc.Get("Gm.PlayerViewLocked");
            var textSz = ImGui.CalcTextSize(text);
            ImGui.SetCursorPos(new Vector2(
                ImGui.GetCursorPosX() + (avail.X - textSz.X) / 2f,
                ImGui.GetCursorPosY() + (avail.Y - textSz.Y) / 2f));
            ImGui.TextColored(MasterEventTheme.TextDim, text);
            return;
        }

        npcManager?.PruneDead();
        var npcAvailable = gmAccess && npcManager != null;

        if (terrainPage != TerrainPage.List)
        {
            // Le PNJ créé (ou des droits perdus) referme la page et ramène à la scène.
            if (npcAvailable && terrainSpawnSerial == terrainSpawnSerialAtEntry)
            {
                if (terrainPage == TerrainPage.NewNpc) DrawNewNpcPage();
                else DrawNewCreaturePage();
                return;
            }

            terrainPage = TerrainPage.List;
        }

        FollowNewSceneActors();

        DrawSceneBar(gmAccess, gmAccess && session.CurrentTurnState is { IsActive: true });
        DrawNpcNotices();

        // Relu après la barre : « Terminer le combat » vient peut-être d'y effacer l'état des tours.
        var state = gmAccess ? session.CurrentTurnState : null;
        var combat = state is { IsActive: true };
        if (combat && !sceneWasCombat) sceneCombatView = 0;
        sceneWasCombat = combat;

        ValidateSceneSelection(gmAccess);

        var scale = ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = ImGui.GetContentRegionAvail().X;
        var hasSelection = sceneSelection != SceneSelection.None;

        // Deux colonnes hors combat pour le MJ ; en combat, une liste à la fois, au choix.
        var columns = gmAccess && !combat;
        var listNeed = columns ? (ScenePlayersMinWidth + SceneListMinWidth) * scale + spacing : SceneListMinWidth * scale;
        var inspectorWidth = Math.Clamp(width * 0.38f, 270f * scale, 380f * scale);
        var listWidth = width - (hasSelection ? inspectorWidth + spacing : 0f);
        var wide = listWidth >= listNeed;

        if (combat && (wide || !hasSelection))
        {
            var labels = new[] { Loc.Get("Scene.TurnOrder"), Loc.Get("Group.Players"), Loc.Get("Terrain.Title") };
            sceneCombatView = DrawSegmentedControl("scene_combat_view", labels, sceneCombatView);
            ImGuiHelpers.ScaledDummy(4f);
        }

        if (!wide)
        {
            DrawSceneNarrow(gmAccess, npcAvailable, combat ? state : null, hasSelection);
            return;
        }

        if (combat) DrawSceneCombatView(listWidth, state!, npcAvailable);
        else DrawSceneColumns(listWidth, gmAccess, npcAvailable);

        if (!hasSelection) return;

        ImGui.SameLine();
        if (ImGui.BeginChild("##scene_inspector", new Vector2(inspectorWidth, 0)))
        {
            DrawSceneInspectorHeader(narrow: false);
            DrawSceneInspector();
        }
        ImGui.EndChild();
    }

    // Un PNJ qui vient d'apparaître, ou une balise que le MJ vient de poser, s'ouvre dans le détail :
    // c'est là qu'on le nomme et qu'on règle ses PV.
    private void FollowNewSceneActors()
    {
        if (terrainSpawnSerial != sceneSeenSpawnSerial)
        {
            sceneSeenSpawnSerial = terrainSpawnSerial;
            if (npcSelected != null)
                SelectScene(SceneSelection.Npc, npc: npcSelected.NetworkId, toggle: false);
        }

        if (scenePlacingWaymark is not { } placing) return;

        if (session.CurrentMarkers[placing].IsVisible)
        {
            scenePlacingWaymark = null;
            SelectScene(SceneSelection.Marker, marker: placing, toggle: false);
        }
        else if (Environment.TickCount64 - scenePlacingSince > ScenePlacingTimeoutMs)
        {
            scenePlacingWaymark = null;
        }
    }

    private void FollowWaymarkPlacement(WaymarkId id)
    {
        scenePlacingWaymark = id;
        scenePlacingSince = Environment.TickCount64;
    }

    private void DrawSceneNarrow(bool gmAccess, bool npcAvailable, TurnState? combatState, bool hasSelection)
    {
        if (hasSelection)
        {
            DrawSceneInspectorHeader(narrow: true);
            if (ImGui.BeginChild("##scene_inspector", Vector2.Zero))
                DrawSceneInspector();
            ImGui.EndChild();
            return;
        }

        if (combatState != null)
        {
            DrawSceneCombatView(ImGui.GetContentRegionAvail().X, combatState, npcAvailable);
            return;
        }

        if (gmAccess)
        {
            var labels = new[] { Loc.Get("Group.Players"), Loc.Get("Terrain.Title") };
            sceneNarrowList = DrawSegmentedControl("scene_narrow", labels, sceneNarrowList);
            ImGuiHelpers.ScaledDummy(4f);
        }

        if (ImGui.BeginChild("##scene_narrow_list", Vector2.Zero))
        {
            if (gmAccess && sceneNarrowList == 0) DrawScenePlayers();
            else DrawSceneTerrain(npcAvailable);
        }
        ImGui.EndChild();
    }

    private void DrawSceneCombatView(float width, TurnState state, bool npcAvailable)
    {
        switch (sceneCombatView)
        {
            case 1:
                if (ImGui.BeginChild("##scene_players", new Vector2(width, 0)))
                    DrawScenePlayers();
                ImGui.EndChild();
                break;
            case 2:
                if (ImGui.BeginChild("##scene_terrain", new Vector2(width, 0)))
                    DrawSceneTerrain(npcAvailable);
                ImGui.EndChild();
                break;
            default:
                DrawSceneCombat(width, state);
                break;
        }
    }

    private void DrawSceneColumns(float width, bool gmAccess, bool npcAvailable)
    {
        if (!gmAccess)
        {
            if (ImGui.BeginChild("##scene_terrain", new Vector2(width, 0)))
                DrawSceneTerrain(npcAvailable);
            ImGui.EndChild();
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var playersWidth = Math.Clamp(width * 0.36f, 180f * scale, 260f * scale);

        if (ImGui.BeginChild("##scene_players", new Vector2(playersWidth, 0)))
            DrawScenePlayers();
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("##scene_terrain", new Vector2(width - playersWidth - spacing, 0)))
            DrawSceneTerrain(npcAvailable);
        ImGui.EndChild();
    }

    private static void DrawSceneListHeader(string title, string? count, Action? trailing = null, float trailingWidth = 0f)
    {
        var rowStart = ImGui.GetCursorPosX();
        var avail = ImGui.GetContentRegionAvail().X;

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MasterEventTheme.TextStrong, title);
        if (count != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.MutedTextColor, count);
        }

        if (trailing != null)
        {
            ImGui.SameLine(rowStart + avail - trailingWidth - CardRightMargin);
            trailing();
        }
        else
        {
            // Même hauteur de ligne qu'avec un bouton, pour que les colonnes restent alignées.
            ImGui.SameLine();
            ImGui.Dummy(new Vector2(0, ImGui.GetFrameHeight()));
        }

        ImGuiHelpers.ScaledDummy(2f);
    }

    private void DrawScenePlayers()
    {
        // Copie : retirer un joueur du lobby modifie PartyMembers en pleine itération.
        var players = session.PartyMembers.Where(p => !p.IsGm || session.GmIsPlayer).ToList();
        DrawSceneListHeader(Loc.Get("Group.Players"), players.Count > 0 ? players.Count.ToString() : null);

        if (players.Count == 0)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Group.NoPlayers"));
            return;
        }

        foreach (var player in players)
        {
            var row = new SceneRow
            {
                Id = "pl_" + player.Hash,
                Name = player.Name,
                NameMuted = !player.IsConnected,
                Selected = sceneSelection == SceneSelection.Player && sceneSelectedPlayer == player.Hash,
                GlyphIcon = FontAwesomeIcon.User,
                GlyphColor = MasterEventTheme.PlayerColor,
                Badge = player.CanEdit ? Loc.Get("Group.CoGm") : null,
                RollPending = session.IsAwaitingRoll(player.Hash),
                Connected = player.IsConnected,
                Hp = player.Hp,
                HpMax = player.HpMax,
                Shield = session.ShowShield ? player.Shield : 0,
                BarColor = MasterEventTheme.PlayerColor,
            };

            // En lobby, l'étiquette du sous-groupe remplace l'icône : c'est elle qui situe le joueur.
            if (session.IsLobbyMode && player.GroupLabel != null)
            {
                row.GlyphText = player.GroupLabel;
                row.GlyphColor = MasterEventTheme.GroupColor(player.GroupLabel);
            }

            if (DrawSceneRow(row))
                SelectScene(SceneSelection.Player, player: player.Hash);
        }
    }

    private void DrawSceneTerrain(bool npcAvailable)
    {
        var addLabel = "+  " + Loc.Get("Terrain.Add");
        DrawSceneListHeader(Loc.Get("Scene.OnField"), null, () =>
        {
            if (Buttons.Tinted(addLabel + "##scene_add"))
                ImGui.OpenPopup("##scene_add_popup");
            sceneAddAnchor = ImGui.GetItemRectMax();
            DrawSceneAddPopup(npcAvailable);
        }, Buttons.TextWidth(addLabel));

        if (npcAvailable)
        {
            var labels = new[]
            {
                Loc.Get("Terrain.FilterAll"),
                Loc.Get("Terrain.FilterMarkers"),
                Loc.Get("Terrain.FilterNpcs"),
                Loc.Get("Terrain.FilterCreatures"),
            };
            terrainFilter = (TerrainFilter)DrawSegmentedControl("scene_filter", labels, (int)terrainFilter);
            ImGuiHelpers.ScaledDummy(4f);
        }
        else
        {
            terrainFilter = TerrainFilter.Markers;
        }

        if (ImGui.BeginChild("##scene_terrain_rows", Vector2.Zero))
            DrawSceneTerrainRows(npcAvailable);
        ImGui.EndChild();
    }

    private void DrawSceneTerrainRows(bool npcAvailable)
    {
        var showMarkers = terrainFilter is TerrainFilter.All or TerrainFilter.Markers;
        var showNpcs = npcAvailable && terrainFilter is TerrainFilter.All or TerrainFilter.Npcs;
        var showCreatures = npcAvailable && terrainFilter is TerrainFilter.All or TerrainFilter.Creatures;

        var markerCount = CountPlacedMarkers();
        var npcs = npcAvailable ? npcManager!.Instances.ToArray() : [];
        if (markerCount == 0 && npcs.Length == 0)
        {
            LayoutControls.DrawEmptyState(FontAwesomeIcon.MapMarkerAlt,
                Loc.Get("Terrain.EmptyTitle"), Loc.Get("Terrain.EmptyHint"));
            return;
        }

        var drawn = 0;
        if (showMarkers)
        {
            for (var i = 0; i < Constants.WaymarkCount; i++)
            {
                var id = (WaymarkId)i;
                var marker = session.CurrentMarkers[id];
                if (!marker.HasData) continue;

                DrawSceneMarkerRow(id, marker);
                drawn++;
            }
        }

        // Copie : retirer un PNJ modifie la liste pendant le parcours.
        var shown = npcs.Where(n => n.IsCreature ? showCreatures : showNpcs).ToArray();
        foreach (var npc in shown)
        {
            DrawSceneNpcRow(npc);
            drawn++;
        }

        if (drawn == 0)
        {
            var empty = terrainFilter switch
            {
                TerrainFilter.Markers => "Gm.NoMarkers",
                TerrainFilter.Creatures => "Creature.Empty",
                _ => "Npc.Empty",
            };
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get(empty));
            return;
        }

        if (shown.Length == 0) return;

        ImGuiHelpers.ScaledDummy(4f);
        var prefix = terrainFilter == TerrainFilter.Creatures ? "Creature" : "Npc";
        var question = shown.Length == 1
            ? Loc.Get(prefix + ".DespawnAllQuestionOne")
            : string.Format(Loc.Get(prefix + ".DespawnAllQuestionFmt"), shown.Length);
        if (ConfirmInline.Draw("scene_despawn_all", Loc.Get("Npc.DespawnAll"), question, Loc.Get("Npc.DespawnAll")))
        {
            foreach (var npc in shown)
                npcManager!.Despawn(npc);
            npcLastInfo = Loc.Get("Npc.AllDespawned");
            npcLastError = null;
        }
    }

    private void DrawSceneMarkerRow(WaymarkId id, MarkerData marker)
    {
        var named = !string.IsNullOrEmpty(marker.Name);
        var row = new SceneRow
        {
            Id = "mk_" + (int)id,
            Name = named ? marker.Name : string.Format(Loc.Get("Scene.MarkerFmt"), id.ToLabel()),
            NameMuted = !named,
            Selected = sceneSelection == SceneSelection.Marker && sceneSelectedMarker == id,
            Dimmed = !marker.IsVisible,
            WaymarkIcon = id.ToIconId(),
            Badge = marker.IsBoss ? Loc.Get("Marker.Boss") : null,
            Trailing = AttitudeLabel(marker.Attitude),
            TrailingColor = AttitudeColor(marker.Attitude),
            Hp = marker.Hp,
            HpMax = marker.HpMax,
            Shield = session.ShowShield ? marker.Shield : 0,
            BarColor = AttitudeColor(marker.Attitude),
            AdjustHp = delta => marker.Hp = ClampSceneHp(marker.Hp + delta, marker.HpMax),
            RollEntity = marker,
            RollEnabled = named,
            RollTooltip = named ? Loc.Get("Npc.Roll") : Loc.Get("Marker.RollDisabled"),
            Roll = statId => session.RollDiceWithStat(id, statId),
        };

        if (DrawSceneRow(row))
            SelectScene(SceneSelection.Marker, marker: id);
    }

    private void DrawSceneNpcRow(NpcInstance npc)
    {
        var row = new SceneRow
        {
            Id = "npc_" + npc.NetworkId,
            Name = npc.DisplayName,
            Selected = sceneSelection == SceneSelection.Npc && sceneSelectedNpc == npc.NetworkId,
            Dimmed = !npc.IsAlive,
            GlyphIcon = npc.IsCreature ? FontAwesomeIcon.Dragon : FontAwesomeIcon.UserFriends,
            GlyphColor = MasterEventTheme.NpcColor,
            Badge = npc.IsBoss ? Loc.Get("Marker.Boss") : null,
            Trailing = npc.IsAlive ? AttitudeLabel(npc.Attitude) : Loc.Get("Npc.Dead"),
            TrailingColor = npc.IsAlive ? AttitudeColor(npc.Attitude) : MasterEventTheme.DangerColor,
            Hp = npc.Hp,
            HpMax = npc.HpMax,
            Shield = session.ShowShield ? npc.Shield : 0,
            BarColor = AttitudeColor(npc.Attitude),
            AdjustHp = delta =>
            {
                npc.Hp = ClampSceneHp(npc.Hp + delta, npc.HpMax);
                npcManager?.NotifyChanged();
            },
            RollEntity = npc,
            RollTooltip = Loc.Get("Npc.Roll"),
            Roll = statId => session.RollDiceFor(npc, statId),
        };

        if (DrawSceneRow(row))
            SelectScene(SceneSelection.Npc, npc: npc.NetworkId);
    }

    private int ClampSceneHp(int value, int max)
        => Math.Clamp(value, 0, session.HpMode == HpMode.Percentage ? 100 : Math.Max(max, 0));

    private string SceneHpText(int hp, int max)
        => session.HpMode == HpMode.Percentage ? $"{hp}%" : $"{hp}/{max}";

    private static string AttitudeLabel(Attitude attitude) => attitude switch
    {
        Attitude.Hostile => Loc.Get("Attitude.Hostile"),
        Attitude.Friendly => Loc.Get("Attitude.Friendly"),
        _ => Loc.Get("Attitude.Neutral"),
    };

    private static Vector4 AttitudeColor(Attitude attitude) => attitude switch
    {
        Attitude.Hostile => MasterEventTheme.AttitudeHostile,
        Attitude.Friendly => MasterEventTheme.AttitudeFriendly,
        _ => MasterEventTheme.AttitudeNeutral,
    };

    // ── Sélection et détail ─────────────────────────────────────────────

    private void SelectScene(SceneSelection kind, string? player = null, WaymarkId marker = default, Guid npc = default,
        bool toggle = true)
    {
        var same = kind == sceneSelection && kind switch
        {
            SceneSelection.Player => sceneSelectedPlayer == player,
            SceneSelection.Marker => sceneSelectedMarker == marker,
            SceneSelection.Npc => sceneSelectedNpc == npc,
            _ => true,
        };

        // Un second clic sur la ligne choisie referme le détail : une autre façon de le masquer.
        if (toggle && same)
        {
            ClearSceneSelection();
            return;
        }

        CloseSceneKickQuestion();
        sceneSelection = kind;
        sceneSelectedPlayer = player;
        sceneSelectedMarker = marker;
        sceneSelectedNpc = npc;
    }

    private void ClearSceneSelection()
    {
        CloseSceneKickQuestion();
        sceneSelection = SceneSelection.None;
        sceneSelectedPlayer = null;
    }

    // La question « Retirer X du lobby ? » appartient au détail de X : elle ne doit pas y revenir plus tard.
    private void CloseSceneKickQuestion()
    {
        if (sceneSelectedPlayer != null && ConfirmInline.IsOpen("group_kick_" + sceneSelectedPlayer))
            ConfirmInline.Close();
    }

    private void ValidateSceneSelection(bool gmAccess)
    {
        var valid = sceneSelection switch
        {
            SceneSelection.Player => gmAccess && session.PartyMembers.Any(p => p.Hash == sceneSelectedPlayer),
            SceneSelection.Marker => session.CurrentMarkers[sceneSelectedMarker].HasData,
            SceneSelection.Npc => gmAccess && npcManager?.FindByNetworkId(sceneSelectedNpc) != null,
            _ => true,
        };
        if (!valid) ClearSceneSelection();
    }

    private string SceneSelectionTitle() => sceneSelection switch
    {
        SceneSelection.Player => session.PartyMembers.FirstOrDefault(p => p.Hash == sceneSelectedPlayer)?.Name ?? string.Empty,
        SceneSelection.Marker => session.CurrentMarkers[sceneSelectedMarker] is { Name.Length: > 0 } marker
            ? marker.Name
            : string.Format(Loc.Get("Scene.MarkerFmt"), sceneSelectedMarker.ToLabel()),
        SceneSelection.Npc => npcManager?.FindByNetworkId(sceneSelectedNpc)?.DisplayName ?? string.Empty,
        _ => string.Empty,
    };

    private void DrawSceneInspectorHeader(bool narrow)
    {
        if (narrow)
        {
            if (DrawEditorBreadcrumb(SceneSelectionTitle(), null, false, Loc.Get("Sidebar.Scene")))
                ClearSceneSelection();
            return;
        }

        var closeWidth = Buttons.IconWidth(FontAwesomeIcon.Times);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - closeWidth - CardRightMargin);
        if (Buttons.QuietIcon(FontAwesomeIcon.Times, "scene_close_detail", Loc.Get("Scene.CloseDetail")))
            ClearSceneSelection();
    }

    private void DrawSceneInspector()
    {
        switch (sceneSelection)
        {
            case SceneSelection.Player
                when session.PartyMembers.FirstOrDefault(p => p.Hash == sceneSelectedPlayer) is { } player:
                DrawScenePlayerInspector(player);
                break;

            case SceneSelection.Marker:
                var id = sceneSelectedMarker;
                MarkerCard.DrawEdit(id, session.CurrentMarkers[id],
                    onPlace: () => ShowTerrainHint(string.Format(Loc.Get("Terrain.PlaceWaymarkFmt"), id.ToLabel())),
                    onClear: () => session.ClearMarker(id),
                    onMove: () => session.MoveMarker(id),
                    onRoll: statId => session.RollDiceWithStat(id, statId),
                    hpMode: session.HpMode,
                    showShield: session.ShowShield,
                    showMpBar: session.ShowMpBar,
                    mpMode: session.MpMode);
                break;

            case SceneSelection.Npc when npcManager?.FindByNetworkId(sceneSelectedNpc) is { } npc:
                DrawNpcCard(npc);
                break;
        }
    }

    private void DrawScenePlayerInspector(PlayerData player)
    {
        LayoutControls.BeginCard(player.Name, FontAwesomeIcon.User);

        if (session.IsLobbyMode && player.GroupLabel != null)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(MasterEventTheme.GroupColor(player.GroupLabel), $"[{player.GroupLabel}]");
            ImGui.SameLine();
        }
        ImGui.AlignTextToFramePadding();
        DrawGroupConnection(player.IsConnected);
        if (player.CanEdit)
        {
            ImGui.SameLine();
            DrawGroupPill(Loc.Get("Group.CoGm"), MasterEventTheme.AccentText);
        }

        if (session.CanEdit)
        {
            var vitals = new PlayerVitals(player, session);
            var vitalsId = "insp_" + player.Hash;
            VitalsEditor.DrawBars(vitals, vitalsId, session.HpMode, session.ShowShield, session.ShowMpBar, session.MpMode);

            if (player.Stats is { Count: > 0 } stats)
            {
                var statLine = string.Join("   ", stats.Select(st => $"{st.Name} {StatDisplay.Stat(st.Modifier)}"));
                ImGui.TextColored(MasterEventTheme.TextSecondary, statLine);
            }

            VitalsEditor.DrawCounters(vitals, vitalsId);
            VitalsEditor.DrawTempModifier(vitals, vitalsId);
        }

        if (session.IsAwaitingRoll(player.Hash))
            ImGui.TextColored(MasterEventTheme.PendingColor, Loc.Get("Group.RollPending"));

        // Mêmes droits que l'ancien écran Groupe : jet pour qui peut modifier, menu pour le vrai MJ.
        var canAskRoll = !player.IsGm && session.CanEdit && session.IsConnected;
        var hasMenu = session.IsGm && !player.IsGm;
        var kickId = "group_kick_" + player.Hash;

        if (canAskRoll || hasMenu)
        {
            ImGuiHelpers.ScaledDummy(2f);
            var rowStart = ImGui.GetCursorPosX();

            if (canAskRoll && Buttons.Tinted(Loc.Get("Group.AskRoll") + "##insp_ask_" + player.Hash))
                RollRequestWindowRef?.Open(player);

            if (hasMenu)
            {
                if (canAskRoll) AlignGroupActions(rowStart, Buttons.IconWidth(FontAwesomeIcon.EllipsisH));
                if (Buttons.QuietIcon(FontAwesomeIcon.EllipsisH, "member_menu_" + player.Hash, Loc.Get("Sheets.More")))
                    ImGui.OpenPopup("##member_menu_" + player.Hash);
                DrawGroupMemberMenu(player, kickId);
            }
        }

        if (ConfirmInline.DrawQuestion(kickId, string.Format(Loc.Get("Lobby.RemoveConfirmFmt"), player.Name),
                Loc.Get("Lobby.RemoveConfirm")))
            session.KickLobbyPlayer(player.Hash);

        LayoutControls.EndCard();
    }

    // ── Combat ──────────────────────────────────────────────────────────

    private void DrawSceneCombat(float width, TurnState state)
    {
        if (ImGui.BeginChild("##scene_combat", new Vector2(width, 0)))
        {
            DrawSceneListHeader(Loc.Get("Scene.TurnOrder"), null);
            ImGui.TextColored(MasterEventTheme.MutedTextColor, string.Format(Loc.Get("Turns.SubtitleFmt"), state.DiceMax));
            ImGuiHelpers.ScaledDummy(2f);

            var scale = ImGuiHelpers.GlobalScale;
            var active = session.ActiveTurnIndex;
            var seenGroups = new HashSet<string>();
            for (var i = 0; i < state.Entries.Count; i++)
            {
                var entry = state.Entries[i];

                if (entry.GroupId != null && seenGroups.Add(entry.GroupId)
                    && state.Groups.FirstOrDefault(g => g.Id == entry.GroupId) is { } group)
                    DrawGroupHeader(group, state, i);

                var grouped = entry.GroupId != null;
                if (grouped) ImGui.Indent(16f * scale);
                DrawSceneTurnRow(state, entry, i, active);
                if (grouped) ImGui.Unindent(16f * scale);
            }
        }
        ImGui.EndChild();
    }

    private void DrawSceneTurnRow(TurnState state, TurnEntry entry, int index, int activeIndex)
    {
        var activeEntry = activeIndex >= 0 && activeIndex < state.Entries.Count ? state.Entries[activeIndex] : null;
        var isActive = activeEntry != null
                       && (activeIndex == index || (entry.GroupId != null && activeEntry.GroupId == entry.GroupId));
        var grouped = entry.GroupId != null;

        var row = new SceneRow
        {
            Id = "turn_" + index,
            Name = entry.Name,
            Prefix = entry.Initiative.ToString(),
            PrefixTooltip = InitiativeTooltip(entry),
            Dimmed = state.HasEntryActed(entry),
            Status = isActive ? Loc.Get("Scene.TheirTurn") : null,
            Selected = IsSceneSelected(entry),
            Acted = grouped ? null : entry.HasActed,
            ReserveCheck = grouped,
            ToggleActed = () => session.ToggleHasActed(index),
            Menu = () => DrawSceneTurnMenu(state, entry, index),
            MenuWidth = Buttons.IconWidth(FontAwesomeIcon.EllipsisH),
        };

        if (ResolveTurnEntity(entry) is { } entity)
        {
            if (entry.WaymarkIndex is { } wi)
            {
                row.WaymarkIcon = ((WaymarkId)wi).ToIconId();
            }
            else
            {
                row.GlyphIcon = entity is NpcInstance { IsCreature: true } ? FontAwesomeIcon.Dragon : FontAwesomeIcon.UserFriends;
                row.GlyphColor = MasterEventTheme.NpcColor;
            }

            row.Hp = entity.Hp;
            row.HpMax = entity.HpMax;
            row.Shield = session.ShowShield ? entity.Shield : 0;
            row.BarColor = AttitudeColor(entity.Attitude);
            row.Badge = entity.IsBoss ? Loc.Get("Marker.Boss") : null;
            row.AdjustHp = delta =>
            {
                entity.Hp = ClampSceneHp(entity.Hp + delta, entity.HpMax);
                if (entity is NpcInstance) npcManager?.NotifyChanged();
            };
            row.RollEntity = entity;
            row.RollTooltip = Loc.Get("Turns.RollFor");
            row.Roll = statId => session.RollDiceFor(entity, statId);
        }
        else if (entry.PlayerHash is { } hash && session.PartyMembers.FirstOrDefault(p => p.Hash == hash) is { } player)
        {
            row.GlyphIcon = FontAwesomeIcon.User;
            row.GlyphColor = MasterEventTheme.PlayerColor;
            row.Hp = player.Hp;
            row.HpMax = player.HpMax;
            row.Shield = session.ShowShield ? player.Shield : 0;
            row.BarColor = MasterEventTheme.PlayerColor;
            row.AdjustHp = delta => session.SetPlayerHp(hash, ClampSceneHp(player.Hp + delta, player.HpMax));
        }
        else
        {
            row.GlyphIcon = FontAwesomeIcon.User;
            row.GlyphColor = MasterEventTheme.TextDim;
            row.HasBar = false;
        }

        if (DrawSceneRow(row))
            SelectTurnEntry(entry);
    }

    private static string? InitiativeTooltip(TurnEntry entry)
    {
        if (entry.InitiativeRoll <= 0) return null;
        if (entry.InitiativeStatName == null)
            return $"{Loc.Get("Turns.InitRoll")}: {entry.InitiativeRoll}";

        var modifier = entry.InitiativeModifier >= 0 ? $"+{entry.InitiativeModifier}" : entry.InitiativeModifier.ToString();
        return $"{Loc.Get("Turns.InitRoll")}: {entry.InitiativeRoll} ({entry.InitiativeStatName} {modifier}) = {entry.Initiative}";
    }

    private void DrawSceneTurnMenu(TurnState state, TurnEntry entry, int index)
    {
        if (Buttons.QuietIcon(FontAwesomeIcon.EllipsisH, "turn_menu", Loc.Get("Sheets.More")))
            ImGui.OpenPopup("##scene_turn_menu");

        if (!ImGui.BeginPopup("##scene_turn_menu")) return;

        // Monter et descendre restent dans un même groupe, ou entre deux participants seuls.
        var canUp = index > 0 && state.Entries[index - 1].GroupId == entry.GroupId;
        if (ImGui.Selectable(Loc.Get("Turns.MoveUp"), false, canUp ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled))
            session.MoveParticipantUp(index);

        var canDown = index < state.Entries.Count - 1 && state.Entries[index + 1].GroupId == entry.GroupId;
        if (ImGui.Selectable(Loc.Get("Turns.MoveDown"), false, canDown ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled))
            session.MoveParticipantDown(index);

        if (ImGui.Selectable(Loc.Get("Turns.Reroll")))
            session.RerollInitiative(index);

        DrawMergeMenuItem(state, entry, index);

        ImGui.Separator();
        ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.DangerColor);
        if (ImGui.Selectable(Loc.Get("Turns.Remove")))
            session.RemoveTurnParticipant(index);
        ImGui.PopStyleColor();
        ImGui.EndPopup();
    }

    private bool IsSceneSelected(TurnEntry entry)
    {
        if (entry.WaymarkIndex is { } wi)
            return sceneSelection == SceneSelection.Marker && (int)sceneSelectedMarker == wi;
        if (entry.NpcId is { } npcId)
            return sceneSelection == SceneSelection.Npc && Guid.TryParse(npcId, out var id) && id == sceneSelectedNpc;
        return entry.PlayerHash is { } hash && sceneSelection == SceneSelection.Player && sceneSelectedPlayer == hash;
    }

    private void SelectTurnEntry(TurnEntry entry)
    {
        if (entry.WaymarkIndex is { } wi && wi >= 0 && wi < Constants.WaymarkCount)
            SelectScene(SceneSelection.Marker, marker: (WaymarkId)wi);
        else if (entry.NpcId is { } npcId && Guid.TryParse(npcId, out var id))
            SelectScene(SceneSelection.Npc, npc: id);
        else if (entry.PlayerHash is { } hash)
            SelectScene(SceneSelection.Player, player: hash);
    }

    // ── Ligne compacte ──────────────────────────────────────────────────

    private sealed class SceneRow
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public bool NameMuted;
        public bool Selected;
        public bool Dimmed;

        public uint WaymarkIcon;
        public FontAwesomeIcon GlyphIcon = FontAwesomeIcon.User;
        public string? GlyphText;
        public Vector4 GlyphColor = MasterEventTheme.TextSecondary;

        public string? Prefix;
        public string? PrefixTooltip;
        public string? Badge;
        public string? Status;
        public string? Trailing;
        public Vector4 TrailingColor = MasterEventTheme.MutedTextColor;
        public bool RollPending;
        public bool? Connected;

        public bool HasBar = true;
        public int Hp;
        public int HpMax;
        public int Shield;
        public Vector4 BarColor = MasterEventTheme.PlayerColor;

        public bool? Acted;
        public bool ReserveCheck;
        public Action? ToggleActed;
        public Action<int>? AdjustHp;
        public IVitalEntity? RollEntity;
        public bool RollEnabled = true;
        public string RollTooltip = string.Empty;
        public Action<string?>? Roll;
        public Action? Menu;
        public float MenuWidth;
    }

    // Une ligne par acteur : la zone de gauche choisit l'acteur, les boutons de droite agissent sans
    // ouvrir le détail. Maj + clic sur - ou + compte 5 PV.
    private bool DrawSceneRow(SceneRow row)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var dl = ImGui.GetWindowDrawList();
        var frame = ImGui.GetFrameHeight();
        var lineHeight = ImGui.GetTextLineHeight();
        var pad = 6f * scale;
        var barHeight = 5f * scale;
        var rounding = MasterEventTheme.RadiusCard * scale;

        var width = ImGui.GetContentRegionAvail().X - CardRightMargin;
        var height = Math.Max(frame, lineHeight + (row.HasBar ? 4f * scale + barHeight : 0f)) + pad * 2f;
        var pos = ImGui.GetCursorScreenPos();

        var stepWidth = row.AdjustHp != null ? frame * 2f + 2f * scale : 0f;
        var rollWidth = row.Roll != null ? Buttons.IconWidth(FontAwesomeIcon.Dice) : 0f;
        var menuWidth = row.Menu != null ? row.MenuWidth : 0f;
        var parts = new[] { stepWidth, rollWidth, menuWidth }.Where(w => w > 0f).ToArray();
        var controlsWidth = parts.Sum() + spacing * Math.Max(0, parts.Length - 1);

        ImGui.PushID(row.Id);

        var background = row.Selected ? MasterEventTheme.ThemeButtonBg : MasterEventTheme.ThemeFrameBg;
        dl.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.GetColorU32(background), rounding);
        // Contour d'accent : la sélection reste visible tant que le détail est ouvert.
        if (row.Selected)
            dl.AddRect(pos, pos + new Vector2(width, height),
                ImGui.GetColorU32(MasterEventTheme.AccentColor with { W = 0.85f }), rounding, ImDrawFlags.None, 1f * scale);

        var x = pos.X + pad;
        if (row.Acted is { } acted)
        {
            ImGui.SetCursorScreenPos(new Vector2(x, pos.Y + (height - frame) / 2f));
            var value = acted;
            if (ImGui.Checkbox("##acted", ref value))
                row.ToggleActed?.Invoke();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Scene.Acted"));
        }
        if (row.Acted != null || row.ReserveCheck)
            x += frame + spacing;

        var mainEnd = pos.X + width - pad - controlsWidth - (controlsWidth > 0f ? spacing : 0f);
        ImGui.SetCursorScreenPos(new Vector2(x - pad / 2f, pos.Y));
        var clicked = ImGui.InvisibleButton("##select", new Vector2(Math.Max(1f, mainEnd - x + pad / 2f), height));
        var rowHovered = ImGui.IsItemHovered();
        if (rowHovered && !row.Selected)
            dl.AddRectFilled(pos, pos + new Vector2(width, height),
                ImGui.GetColorU32(MasterEventTheme.TextStrong with { W = 0.04f }), rounding);

        var fade = row.Dimmed ? 0.5f : 1f;
        var glyphSize = frame * 0.9f;
        var glyphMin = new Vector2(x, pos.Y + (height - glyphSize) / 2f);
        DrawSceneGlyph(dl, row, glyphMin, glyphSize, fade);

        var textX = glyphMin.X + glyphSize + 8f * scale;
        var line1Y = row.HasBar ? pos.Y + pad : pos.Y + (height - lineHeight) / 2f;

        // Côté droit de la première ligne : attitude ou état, puis la pastille de connexion.
        var right = mainEnd;
        if (row.Connected is { } connected)
        {
            var dot = 3.5f * scale;
            var center = new Vector2(right - dot, line1Y + lineHeight / 2f);
            dl.AddCircleFilled(center, dot,
                ImGui.GetColorU32(connected ? MasterEventTheme.SuccessColor : MasterEventTheme.TextDim));
            right -= dot * 2f + 6f * scale;
        }
        if (row.Trailing != null)
        {
            var size = ImGui.CalcTextSize(row.Trailing);
            dl.AddText(new Vector2(right - size.X, line1Y), ImGui.GetColorU32(row.TrailingColor with { W = fade }), row.Trailing);
            right -= size.X + 8f * scale;
        }

        var cursorX = textX;
        if (row.Prefix != null)
        {
            var prefixColor = row.Status != null ? MasterEventTheme.PendingColor : MasterEventTheme.TextSecondary;
            dl.AddText(new Vector2(cursorX, line1Y), ImGui.GetColorU32(prefixColor with { W = fade }), row.Prefix);
            var prefixSize = ImGui.CalcTextSize(row.Prefix);
            if (row.PrefixTooltip != null && rowHovered && ImGui.IsMouseHoveringRect(new Vector2(cursorX, line1Y),
                    new Vector2(cursorX + prefixSize.X, line1Y + lineHeight)))
                ImGui.SetTooltip(row.PrefixTooltip);
            cursorX += prefixSize.X + 8f * scale;
        }

        var pills = new List<(string Text, Vector4 Color)>();
        if (row.Badge != null) pills.Add((row.Badge, MasterEventTheme.AccentText));
        if (row.Status != null) pills.Add((row.Status, MasterEventTheme.PendingColor));
        var pillsWidth = pills.Sum(p => ScenePillWidth(p.Text) + 4f * scale);
        var pendingWidth = 0f;
        if (row.RollPending)
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                pendingWidth = ImGui.CalcTextSize(FontAwesomeIcon.Dice.ToIconString()).X + 6f * scale;
        }

        var name = FitText(row.Name, right - cursorX - pillsWidth - pendingWidth);
        var nameColor = row.NameMuted ? MasterEventTheme.MutedTextColor : MasterEventTheme.TextStrong;
        dl.AddText(new Vector2(cursorX, line1Y), ImGui.GetColorU32(nameColor with { W = fade }), name);
        cursorX += ImGui.CalcTextSize(name).X + 6f * scale;

        foreach (var (text, color) in pills)
            cursorX += DrawScenePill(dl, new Vector2(cursorX, line1Y), text, color) + 4f * scale;

        if (row.RollPending)
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                dl.AddText(new Vector2(cursorX + 2f * scale, line1Y), ImGui.GetColorU32(MasterEventTheme.PendingColor),
                    FontAwesomeIcon.Dice.ToIconString());
            if (rowHovered && ImGui.IsMouseHoveringRect(new Vector2(cursorX, line1Y),
                    new Vector2(cursorX + pendingWidth, line1Y + lineHeight)))
                ImGui.SetTooltip(Loc.Get("Group.RollPending"));
        }

        if (row.HasBar)
        {
            var hpText = SceneHpText(row.Hp, row.HpMax);
            var hpSize = ImGui.CalcTextSize(hpText);
            var barY = line1Y + lineHeight + 4f * scale;
            var barEnd = mainEnd - hpSize.X - 8f * scale;
            var barMax = session.HpMode == HpMode.Percentage ? 100 : row.HpMax;
            DrawSceneHpBar(dl, new Vector2(textX, barY), Math.Max(0f, barEnd - textX), barHeight, row, barMax, fade);
            dl.AddText(new Vector2(mainEnd - hpSize.X, barY + (barHeight - hpSize.Y) / 2f),
                ImGui.GetColorU32(MasterEventTheme.TextSecondary with { W = fade }), hpText);
        }

        // Contrôles : - et + collés, le dé, puis le menu propre à la ligne.
        var controlX = pos.X + width - pad - controlsWidth;
        var controlY = pos.Y + (height - frame) / 2f;
        if (row.AdjustHp != null)
        {
            var step = ImGui.GetIO().KeyShift ? 5 : 1;
            var percent = session.HpMode == HpMode.Percentage;
            ImGui.SetCursorScreenPos(new Vector2(controlX, controlY));
            if (SceneStepButton(FontAwesomeIcon.Minus, "hp_down", Loc.Get(percent ? "Scene.HpDownPct" : "Scene.HpDown"), frame))
                row.AdjustHp(-step);
            ImGui.SetCursorScreenPos(new Vector2(controlX + frame + 2f * scale, controlY));
            if (SceneStepButton(FontAwesomeIcon.Plus, "hp_up", Loc.Get(percent ? "Scene.HpUpPct" : "Scene.HpUp"), frame))
                row.AdjustHp(step);
            controlX += stepWidth + spacing;
        }

        if (row.Roll != null)
        {
            ImGui.SetCursorScreenPos(new Vector2(controlX, controlY));
            bool roll;
            using (ImRaii.Disabled(!row.RollEnabled))
                roll = Buttons.QuietIcon(FontAwesomeIcon.Dice, "roll", row.RollTooltip);
            if (!row.RollEnabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(row.RollTooltip);

            if (roll)
            {
                if (row.RollEntity?.Stats is { Count: > 0 }) ImGui.OpenPopup("##row_roll");
                else row.Roll(null);
            }

            if (row.RollEntity != null && ImGui.BeginPopup("##row_roll"))
            {
                DiceControls.DrawRollStatMenu(row.RollEntity, row.Id, statId => row.Roll(statId), withHeader: true);
                ImGui.EndPopup();
            }
            controlX += rollWidth + spacing;
        }

        if (row.Menu != null)
        {
            ImGui.SetCursorScreenPos(new Vector2(controlX, controlY));
            row.Menu();
        }

        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + height));
        ImGui.Dummy(new Vector2(width, 0f));
        ImGui.PopID();
        return clicked;
    }

    private static void DrawSceneGlyph(ImDrawListPtr dl, SceneRow row, Vector2 min, float size, float fade)
    {
        var max = min + new Vector2(size, size);
        if (row.WaymarkIcon != 0)
        {
            var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(row.WaymarkIcon)).GetWrapOrEmpty();
            dl.AddImage(wrap.Handle, min, max, Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, fade)));
            return;
        }

        var rounding = MasterEventTheme.RadiusControl * ImGuiHelpers.GlobalScale;
        dl.AddRectFilled(min, max, ImGui.GetColorU32(row.GlyphColor with { W = 0.16f * fade }), rounding);

        if (row.GlyphText != null)
        {
            var textSize = ImGui.CalcTextSize(row.GlyphText);
            dl.AddText(min + (new Vector2(size, size) - textSize) / 2f, ImGui.GetColorU32(row.GlyphColor with { W = fade }),
                row.GlyphText);
            return;
        }

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            var icon = row.GlyphIcon.ToIconString();
            var iconSize = ImGui.CalcTextSize(icon);
            dl.AddText(min + (new Vector2(size, size) - iconSize) / 2f, ImGui.GetColorU32(row.GlyphColor with { W = fade }), icon);
        }
    }

    private static void DrawSceneHpBar(ImDrawListPtr dl, Vector2 pos, float width, float height, SceneRow row, int barMax,
        float fade)
    {
        if (width <= 0f) return;

        var rounding = height / 2f;
        dl.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f)), rounding);

        var max = barMax > 0 ? barMax : 100;
        var ratio = Math.Clamp((float)row.Hp / max, 0f, 1f);
        if (ratio > 0f)
            dl.AddRectFilled(pos, pos + new Vector2(width * ratio, height), ImGui.GetColorU32(row.BarColor with { W = fade }), rounding);

        if (row.Shield <= 0) return;
        var shieldRatio = Math.Clamp((float)row.Shield / max, 0f, 1f - ratio);
        if (shieldRatio > 0f)
            dl.AddRectFilled(pos + new Vector2(width * ratio, 0f), pos + new Vector2(width * (ratio + shieldRatio), height),
                ImGui.GetColorU32(MasterEventTheme.ShieldOverlayColor with { W = 0.7f * fade }), rounding);
    }

    private static float ScenePillWidth(string text)
        => ImGui.CalcTextSize(text).X + 10f * ImGuiHelpers.GlobalScale;

    private static float DrawScenePill(ImDrawListPtr dl, Vector2 pos, string text, Vector4 color)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = ImGui.CalcTextSize(text);
        var width = size.X + 10f * scale;
        dl.AddRectFilled(pos - new Vector2(0f, 1f * scale), pos + new Vector2(width, size.Y + 1f * scale),
            ImGui.GetColorU32(color with { W = 0.16f }), (size.Y + 2f * scale) / 2f);
        dl.AddText(pos + new Vector2(5f * scale, 0f), ImGui.GetColorU32(color), text);
        return width;
    }

    private static bool SceneStepButton(FontAwesomeIcon icon, string id, string tooltip, float size)
    {
        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            clicked = ImGui.Button(icon.ToIconString() + "##" + id, new Vector2(size, size));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    // Coupe un nom trop long en finissant par trois points, sans caractère d'ellipse.
    private static string FitText(string text, float maxWidth)
    {
        if (maxWidth <= 0f) return string.Empty;
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;

        const string dots = "...";
        var dotsWidth = ImGui.CalcTextSize(dots).X;
        var end = text.Length;
        while (end > 0 && ImGui.CalcTextSize(text[..end]).X + dotsWidth > maxWidth)
            end--;
        return end == 0 ? dots : text[..end].TrimEnd() + dots;
    }
}
