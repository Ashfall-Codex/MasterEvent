using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MasterEvent.Communication;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;
using MasterEvent.Services.Npc;
using MasterEvent.Services.Remote;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Ce que la vue joueur demande au plugin : rejoindre ou quitter un lobby, se reconnecter, ouvrir les autres
// fenêtres. Chacun est facultatif.
public sealed class PlayerWindowCallbacks
{
    public Action<string>? OnJoinAlliance { get; init; }
    public Action? OnLeaveAlliance { get; init; }
    public Action? OnReconnect { get; init; }
    public Action? OpenSheets { get; init; }
    public Action? OpenSettings { get; init; }
    public Action? ToggleMainWindow { get; init; }
}

// Vue joueur : la barre de séance, les avis, puis la scène ou l'ordre du tour à gauche et Ma fiche
// à droite. En fenêtre étroite, un sélecteur choisit l'un ou l'autre ; Ma fiche garde sa place.
public sealed partial class PlayerWindow : MasterEventWindowBase
{
    // Dans l'ordre de résolution : le premier qui s'applique l'emporte.
    private enum PlayerViewState
    {
        GmPreview,
        Instance,
        VersionRejected,
        JoinRejected,
        AwaitingApproval,
        Offline,
        Reconnecting,
        OutOfSession,
        Joining,
        Handover,
        GmAway,
        InSession,
    }

    // 320 px de liste, 270 de fiche et un espacement : en dessous, une seule colonne.
    private const float WideMinWidth = 598f;
    private const float SheetMinWidth = 270f;
    private const float SheetMaxWidth = 380f;
    private const int SegmentList = 0;
    private const int SegmentSheet = 1;

    private readonly SessionManager session;
    private readonly Configuration configuration;
    private readonly NpcManager npcManager;
    private readonly Func<bool> inParty;
    private readonly Func<bool> inInstance;
    private readonly Action<string>? onJoinAlliance;
    private readonly Action? onLeaveAlliance;
    private readonly Action? onReconnect;
    private readonly Action? openSheets;
    private readonly Action? openSettings;
    private readonly Action? toggleMainWindow;

    public UmbraPortraitCache? UmbraPortraits { get; set; }

    // Fiche détachée d'un coéquipier : le plugin crée ces fenêtres après la vue.
    public Action<PlayerData>? OpenPlayerSheet { get; set; }

    public PlayerRemoteBridge? RemoteBridge { get; set; }

    private string selectedSheetName = string.Empty;
    public void ResetSheetSelection()
    {
        selectedSheetName = string.Empty;
        statQuery = string.Empty;
    }

    private string joinCodeInput = string.Empty;
    private int narrowSegment = SegmentSheet;
    private bool wasCombat;
    private bool requestOpenDice;
    private bool expandRequested;
    private int lastDrawnFrame = -1;

    // Relus à chaque image avant le dessin, pour que toutes les parties tranchent de la même façon.
    private PlayerViewState viewState;
    private bool combatShown;
    private bool narrowLayout;

    public PlayerWindow(SessionManager session, Configuration configuration, NpcManager npcManager,
        Func<bool> inParty, Func<bool> inInstance, PlayerWindowCallbacks callbacks)
        : base(WindowTitle(), ImGuiWindowFlags.NoScrollbar)
    {
        this.session = session;
        this.configuration = configuration;
        this.npcManager = npcManager;
        this.inParty = inParty;
        this.inInstance = inInstance;
        onJoinAlliance = callbacks.OnJoinAlliance;
        onLeaveAlliance = callbacks.OnLeaveAlliance;
        onReconnect = callbacks.OnReconnect;
        openSheets = callbacks.OpenSheets;
        openSettings = callbacks.OpenSettings;
        toggleMainWindow = callbacks.ToggleMainWindow;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 320),
            MaximumSize = new Vector2(1100, 1600),
        };
    }

    private static string WindowTitle() => Loc.Get("PlayerView.WindowTitle") + "###MasterEventPlayer";

    // Le titre suit la langue choisie dans les réglages.
    public override void PreDraw()
    {
        WindowName = WindowTitle();
        base.PreDraw();
    }

    // Fermée ou repliée, la vue n'est pas dessinée : la demande de jet passe alors au centre de l'écran.
    public bool IsShowing => IsOpen && ImGui.GetFrameCount() - lastDrawnFrame <= 1;

    // Le bouton dé de la barre flottante : la vue s'ouvre, dépliée, sur sa fenêtre Dés.
    public void OpenDice()
    {
        IsOpen = true;
        Collapsed = false;
        CollapsedCondition = ImGuiCond.Always;
        expandRequested = true;
        requestOpenDice = true;
        BringToFront();
    }

    protected override void DrawContents()
    {
        lastDrawnFrame = ImGui.GetFrameCount();
        if (expandRequested)
        {
            expandRequested = false;
            Collapsed = null;
        }

        viewState = ResolveState();
        combatShown = IsCombatShown(viewState);
        // Ordre du tour au début de chaque combat, Ma fiche quand il se termine. Une coupure masque
        // le combat sans le finir : c'est sa présence qui compte, pas son affichage.
        var combatPresent = session.CurrentTurnState is { IsActive: true, Entries.Count: > 0 };
        if (combatPresent != wasCombat)
        {
            narrowSegment = combatPresent ? SegmentList : SegmentSheet;
            scrolledActiveBlock = null;
        }
        wasCombat = combatPresent;
        narrowLayout = ImGui.GetContentRegionAvail().X < WideMinWidth * ImGuiHelpers.GlobalScale;

        DrawBar();
        DrawNotices();

        if (viewState == PlayerViewState.GmPreview)
        {
            // Le MJ agit dans sa propre fenêtre : pas de fiche, la liste prend toute la place.
            if (ImGui.BeginChild("##player_list", Vector2.Zero))
                DrawLists();
            ImGui.EndChild();
            return;
        }

        if (HasStateContent(viewState)) DrawStateLayout();
        else DrawSessionLayout();
    }

    private PlayerViewState ResolveState()
    {
        var grouped = inParty() || session.IsLobbyMode;
        // Seul, UpdateRole fait de chacun le MJ : sans groupe ni lobby, ce n'est pas un aperçu.
        if (session.IsGm && !session.GmIsPlayer && grouped) return PlayerViewState.GmPreview;
        if (inInstance()) return PlayerViewState.Instance;
        if (session.IsVersionRejected) return PlayerViewState.VersionRejected;
        if (session.JoinRejection != JoinRejection.None) return PlayerViewState.JoinRejected;
        if (session.IsAwaitingApproval) return PlayerViewState.AwaitingApproval;

        switch (session.LinkState)
        {
            case RelayLinkState.Offline:
                return PlayerViewState.Offline;
            case RelayLinkState.Reconnecting:
                return PlayerViewState.Reconnecting;
            case RelayLinkState.ServerOnly:
                return grouped ? PlayerViewState.Joining : PlayerViewState.OutOfSession;
        }

        if (session.HandoverPending) return PlayerViewState.Handover;
        if (!session.CanEdit && session.GmPresence is not { Connected: true }) return PlayerViewState.GmAway;
        return PlayerViewState.InSession;
    }

    // États où la vue n'a pas de séance à montrer : leur message prend la place des listes.
    private static bool HasStateContent(PlayerViewState state) => state is PlayerViewState.Instance
        or PlayerViewState.VersionRejected or PlayerViewState.JoinRejected or PlayerViewState.AwaitingApproval
        or PlayerViewState.Offline or PlayerViewState.OutOfSession or PlayerViewState.Joining;

    // États qui montrent la salle : en séance, ou tels qu'avant une coupure.
    private static bool IsSessionLike(PlayerViewState state) => state is PlayerViewState.InSession
        or PlayerViewState.Handover or PlayerViewState.GmAway or PlayerViewState.Reconnecting
        or PlayerViewState.GmPreview;

    private static bool IsInRoom(PlayerViewState state) => state is PlayerViewState.InSession
        or PlayerViewState.Handover or PlayerViewState.GmAway;

    // Un combat reçu avant une coupure ou une instance peut être fini : il ne s'affiche qu'en séance.
    private bool IsCombatShown(PlayerViewState state) =>
        (IsInRoom(state) || state == PlayerViewState.GmPreview)
        && session.LinkState == RelayLinkState.InSession
        && !inInstance()
        && session.CurrentTurnState is { IsActive: true, Entries.Count: > 0 };

    private void DrawStateLayout()
    {
        if (narrowLayout)
        {
            if (ImGui.BeginChild("##player_scroll", Vector2.Zero))
            {
                DrawStateContent(compact: true);
                ImGuiHelpers.ScaledDummy(8f);
                DrawSheetCard();
            }
            ImGui.EndChild();
            return;
        }

        var (listWidth, sheetWidth) = ColumnWidths(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginChild("##player_list", new Vector2(listWidth, 0)))
            DrawStateContent(compact: false);
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("##player_sheet", new Vector2(sheetWidth, 0)))
            DrawSheetCard();
        ImGui.EndChild();
    }

    private void DrawSessionLayout()
    {
        if (narrowLayout)
        {
            var labels = new[]
            {
                Loc.Get(combatShown ? "Scene.TurnOrder" : "Sidebar.Scene"),
                Loc.Get("PlayerView.MySheet"),
            };
            narrowSegment = SegmentedControl.Draw("player_view", labels, narrowSegment);
            ImGuiHelpers.ScaledDummy(4f);

            if (ImGui.BeginChild("##player_narrow", Vector2.Zero))
            {
                if (narrowSegment == SegmentSheet) DrawSheetCard();
                else DrawLists();
            }
            ImGui.EndChild();
            return;
        }

        var (listWidth, sheetWidth) = ColumnWidths(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginChild("##player_list", new Vector2(listWidth, 0)))
            DrawLists();
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("##player_sheet", new Vector2(sheetWidth, 0)))
            DrawSheetCard();
        ImGui.EndChild();
    }

    private void DrawLists()
    {
        if (combatShown && session.CurrentTurnState is { } turns) DrawCombatList(turns);
        else DrawSceneList();
    }

    private static (float List, float Sheet) ColumnWidths(float width)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var sheet = Math.Clamp(width * 0.38f, SheetMinWidth * scale, SheetMaxWidth * scale);
        return (width - sheet - ImGui.GetStyle().ItemSpacing.X, sheet);
    }

    // Clic sur sa propre ligne : en fenêtre large, Ma fiche est déjà à côté.
    private void FocusMySheet()
    {
        if (narrowLayout) narrowSegment = SegmentSheet;
    }

    private PlayerData? LocalPlayer => session.PartyMembers.FirstOrDefault(p => p.Hash == session.LocalPlayerHash);

    // Le MJ de la scène n'est pas un joueur, sauf s'il participe. Le relais le désigne : le chef du
    // groupe en jeu ne l'est plus forcément après une passation.
    private List<PlayerData> SessionPlayers()
    {
        var gmHash = session.GmPresence?.Hash;
        return session.PartyMembers.Where(p => session.GmIsPlayer || p.Hash != gmHash).ToList();
    }
}
