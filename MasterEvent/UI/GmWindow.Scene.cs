using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;
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
            if (LayoutControls.DrawEmptyState(FontAwesomeIcon.Eye, Loc.Get("Scene.PlayingTitle"),
                    Loc.Get("Scene.PlayingHint"), Loc.Get("Player.ToggleShow")))
                ShowPlayerView();
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
            sceneCombatView = SegmentedControl.Draw("scene_combat_view", labels, sceneCombatView);
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
            sceneNarrowList = SegmentedControl.Draw("scene_narrow", labels, sceneNarrowList);
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

    private void DrawScenePlayers()
    {
        // Copie : retirer un joueur du lobby modifie PartyMembers en pleine itération.
        var players = session.PartyMembers.Where(p => !p.IsGm || session.GmIsPlayer).ToList();
        ListHeader.Draw(Loc.Get("Group.Players"), players.Count > 0 ? players.Count.ToString() : null);

        if (players.Count == 0)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Group.NoPlayers"));
            return;
        }

        foreach (var player in players)
        {
            var row = new ActorRow.Model
            {
                Id = "pl_" + player.Hash,
                Name = player.Name,
                NameMuted = !player.IsConnected,
                Selected = sceneSelection == SceneSelection.Player && sceneSelectedPlayer == player.Hash,
                Portrait = UmbraPortraits?.GetForPlayer(player, session.LocalPlayerHash),
                GlyphIcon = FontAwesomeIcon.User,
                GlyphColor = MasterEventTheme.PlayerColor,
                RollPending = session.IsAwaitingRoll(player.Hash),
                Connected = player.IsConnected,
                Hp = player.Hp,
                HpMax = player.HpMax,
                Shield = session.ShowShield ? player.Shield : 0,
                BarColor = MasterEventTheme.PlayerColor,
            };
            if (player.CanEdit) row.Pills.Add((Loc.Get("Group.CoGm"), MasterEventTheme.AccentText));

            // En lobby, l'étiquette du sous-groupe remplace l'icône, ou se pose sur le portrait : c'est elle
            // qui situe le joueur.
            if (session.IsLobbyMode && player.GroupLabel != null)
            {
                row.GlyphText = player.GroupLabel;
                row.GlyphColor = MasterEventTheme.GroupColor(player.GroupLabel);
            }

            if (ActorRow.Draw(row, session.HpMode))
                SelectScene(SceneSelection.Player, player: player.Hash);
        }
    }

    private void DrawSceneTerrain(bool npcAvailable)
    {
        var addLabel = "+  " + Loc.Get("Terrain.Add");
        ListHeader.Draw(Loc.Get("Scene.OnField"), null, () =>
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
            terrainFilter = (TerrainFilter)SegmentedControl.Draw("scene_filter", labels, (int)terrainFilter);
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
        var listedMarkers = new List<WaymarkId>();
        if (showMarkers)
        {
            for (var i = 0; i < Constants.WaymarkCount; i++)
            {
                var id = (WaymarkId)i;
                var marker = session.CurrentMarkers[id];
                if (!marker.HasData) continue;

                DrawSceneMarkerRow(id, marker);
                listedMarkers.Add(id);
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

        // « Tout retirer » vaut pour tout ce que la liste montre, marqueurs compris.
        ImGuiHelpers.ScaledDummy(4f);
        var question = ClearAllQuestion(listedMarkers.Count, shown.Length);
        if (ConfirmInline.Draw("scene_despawn_all", Loc.Get("Npc.DespawnAll"), question, Loc.Get("Npc.DespawnAll")))
        {
            session.RemoveMarkers(listedMarkers);
            foreach (var npc in shown)
                npcManager!.Despawn(npc);
            npcLastInfo = shown.Length > 0 ? Loc.Get("Npc.AllDespawned") : null;
            npcLastError = null;
        }
    }

    private string ClearAllQuestion(int markers, int npcs)
    {
        if (markers > 0 && npcs > 0)
            return string.Format(Loc.Get("Scene.ClearAllQuestionFmt"), markers + npcs);
        if (markers > 0)
            return markers == 1
                ? Loc.Get("Marker.ClearAllQuestionOne")
                : string.Format(Loc.Get("Marker.ClearAllQuestionFmt"), markers);

        var prefix = terrainFilter == TerrainFilter.Creatures ? "Creature" : "Npc";
        return npcs == 1
            ? Loc.Get(prefix + ".DespawnAllQuestionOne")
            : string.Format(Loc.Get(prefix + ".DespawnAllQuestionFmt"), npcs);
    }

    private void DrawSceneMarkerRow(WaymarkId id, MarkerData marker)
    {
        var named = !string.IsNullOrEmpty(marker.Name);
        var row = new ActorRow.Model
        {
            Id = "mk_" + (int)id,
            Name = named ? marker.Name : string.Format(Loc.Get("Scene.MarkerFmt"), id.ToLabel()),
            NameMuted = !named,
            Selected = sceneSelection == SceneSelection.Marker && sceneSelectedMarker == id,
            Dimmed = !marker.IsVisible,
            WaymarkIcon = id.ToIconId(),
            Trailing = AttitudeStyle.Label(marker.Attitude),
            TrailingColor = AttitudeStyle.Color(marker.Attitude),
            Hp = marker.Hp,
            HpMax = marker.HpMax,
            Shield = session.ShowShield ? marker.Shield : 0,
            BarColor = AttitudeStyle.Color(marker.Attitude),
            AdjustHp = delta => sceneActions.AdjustVital(SceneTarget.Marker(id), VitalField.Hp, delta),
            RollEntity = marker,
            RollEnabled = named,
            RollTooltip = named ? Loc.Get("Npc.Roll") : Loc.Get("Marker.RollDisabled"),
            Roll = statId => sceneActions.Roll(SceneTarget.Marker(id), statId),
        };
        if (marker.IsBoss) row.Pills.Add((Loc.Get("Marker.Boss"), MasterEventTheme.AccentText));

        if (ActorRow.Draw(row, session.HpMode))
            SelectScene(SceneSelection.Marker, marker: id);
    }

    private void DrawSceneNpcRow(NpcInstance npc)
    {
        var row = new ActorRow.Model
        {
            Id = "npc_" + npc.NetworkId,
            Name = npc.DisplayName,
            Selected = sceneSelection == SceneSelection.Npc && sceneSelectedNpc == npc.NetworkId,
            Dimmed = !npc.IsAlive,
            GlyphIcon = npc.IsCreature ? FontAwesomeIcon.Dragon : FontAwesomeIcon.UserFriends,
            GlyphColor = MasterEventTheme.NpcColor,
            Trailing = npc.IsAlive ? AttitudeStyle.Label(npc.Attitude) : Loc.Get("Npc.Dead"),
            TrailingColor = npc.IsAlive ? AttitudeStyle.Color(npc.Attitude) : MasterEventTheme.DangerColor,
            Hp = npc.Hp,
            HpMax = npc.HpMax,
            Shield = session.ShowShield ? npc.Shield : 0,
            BarColor = AttitudeStyle.Color(npc.Attitude),
            AdjustHp = delta => sceneActions.AdjustVital(SceneTarget.Npc(npc.NetworkId), VitalField.Hp, delta),
            RollEntity = npc,
            RollTooltip = Loc.Get("Npc.Roll"),
            Roll = statId => sceneActions.Roll(SceneTarget.Npc(npc.NetworkId), statId),
        };
        if (npc.IsBoss) row.Pills.Add((Loc.Get("Marker.Boss"), MasterEventTheme.AccentText));

        if (ActorRow.Draw(row, session.HpMode))
            SelectScene(SceneSelection.Npc, npc: npc.NetworkId);
    }

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
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - closeWidth - MasterEventTheme.ListRightMargin);
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
                    onRoll: statId => sceneActions.Roll(SceneTarget.Marker(id), statId),
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

        if (UmbraPortraits?.GetForPlayer(player, session.LocalPlayerHash) is { } portrait)
        {
            var side = ImGui.GetFrameHeight();
            ActorRow.DrawPortrait(ImGui.GetWindowDrawList(), portrait, ImGui.GetCursorScreenPos(), side);
            ImGui.Dummy(new Vector2(side, side));
            ImGui.SameLine();
        }

        if (session.IsLobbyMode && player.GroupLabel != null)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(MasterEventTheme.GroupColor(player.GroupLabel), $"[{player.GroupLabel}]");
            ImGui.SameLine();
        }
        ImGui.AlignTextToFramePadding();
        LinkStatus.DrawPresence(player.IsConnected);
        if (player.CanEdit)
        {
            ImGui.SameLine();
            Pill.Item(Loc.Get("Group.CoGm"), MasterEventTheme.AccentText);
        }

        if (session.CanEdit)
        {
            var vitals = new PlayerVitals(player, session);
            var vitalsId = "insp_" + player.Hash;
            VitalsEditor.DrawBars(vitals, vitalsId, session.HpMode, session.ShowShield, session.ShowMpBar, session.MpMode);

            if (player.Stats is { Count: > 0 } stats)
            {
                ImGuiHelpers.ScaledDummy(2f);
                StatGrid.Draw(stats, LayoutControls.CardContentWidth);
                ImGuiHelpers.ScaledDummy(2f);
            }

            VitalsEditor.DrawCounters(vitals, vitalsId);
            VitalsEditor.DrawTempModifierRow(vitals, vitalsId);
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
            ListHeader.Draw(Loc.Get("Scene.TurnOrder"), null);
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

        var row = new ActorRow.Model
        {
            Id = "turn_" + index,
            Name = entry.Name,
            Prefix = entry.Initiative.ToString(),
            PrefixTooltip = TurnText.InitiativeTooltip(entry),
            Dimmed = state.HasEntryActed(entry),
            Active = isActive,
            Selected = IsSceneSelected(entry),
            Acted = grouped ? null : entry.HasActed,
            ReserveCheck = grouped,
            ToggleActed = () => sceneActions.SetActedAt(index, !entry.HasActed),
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
            row.BarColor = AttitudeStyle.Color(entity.Attitude);
            if (entity.IsBoss) row.Pills.Add((Loc.Get("Marker.Boss"), MasterEventTheme.AccentText));
            // Hors PNJ, ResolveTurnEntity n'a rendu que la balise d'un index valide.
            var target = entity is NpcInstance npc
                ? SceneTarget.Npc(npc.NetworkId)
                : SceneTarget.Marker((WaymarkId)entry.WaymarkIndex!.Value);
            row.AdjustHp = delta => sceneActions.AdjustVital(target, VitalField.Hp, delta);
            row.RollEntity = entity;
            row.RollTooltip = Loc.Get("Turns.RollFor");
            row.Roll = statId => sceneActions.Roll(target, statId);
        }
        else if (entry.PlayerHash is { } hash && session.PartyMembers.FirstOrDefault(p => p.Hash == hash) is { } player)
        {
            row.Portrait = UmbraPortraits?.GetForPlayer(player, session.LocalPlayerHash);
            row.GlyphIcon = FontAwesomeIcon.User;
            row.GlyphColor = MasterEventTheme.PlayerColor;
            row.Hp = player.Hp;
            row.HpMax = player.HpMax;
            row.Shield = session.ShowShield ? player.Shield : 0;
            row.BarColor = MasterEventTheme.PlayerColor;
            row.AdjustHp = delta => sceneActions.AdjustVital(SceneTarget.Player(hash), VitalField.Hp, delta);
        }
        else
        {
            row.GlyphIcon = FontAwesomeIcon.User;
            row.GlyphColor = MasterEventTheme.TextDim;
            row.HasBar = false;
        }

        if (isActive) row.Pills.Add((Loc.Get("Scene.TheirTurn"), MasterEventTheme.PendingColor));

        if (ActorRow.Draw(row, session.HpMode))
            SelectTurnEntry(entry);
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
}
