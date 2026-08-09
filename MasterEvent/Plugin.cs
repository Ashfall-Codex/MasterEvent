using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using MasterEvent.Communication;
using MasterEvent.Localization;
using MasterEvent.Services;
using MasterEvent.Services.Npc;
using MasterEvent.UI;
using MasterEvent.UI.Components;

namespace MasterEvent;

public sealed class Plugin : IDalamudPlugin
{
    private static IDalamudPluginInterface pluginInterface = null!;
    private static IChatGui chatGuiStatic = null!;
    private static IPluginLog logStatic = null!;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    internal static IObjectTable ObjectTable { get; private set; } = null!;
    internal static IPartyList PartyList { get; private set; } = null!;
    internal static ICondition Condition { get; private set; } = null!;
    internal static IPluginLog Log => logStatic;
    internal static IFramework Framework { get; private set; } = null!;
    internal static ITextureProvider TextureProvider { get; private set; } = null!;
    internal static IToastGui ToastGui { get; private set; } = null!;
    internal static IClientState ClientState { get; private set; } = null!;
    internal static IDataManager DataManager { get; private set; } = null!;
    internal static IGameGui GameGui { get; private set; } = null!;
    internal static IGameConfig GameConfig { get; private set; } = null!;

    internal static IDalamudPluginInterface PluginInterface => pluginInterface;
    internal static IChatGui ChatGui => chatGuiStatic;
    internal static IFontHandle? CustomIconFont { get; private set; }
    internal static IFontHandle? LargeFont { get; private set; }


    internal static FileDialogManager FileDialogManager { get; } = new();

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("MasterEvent");

    private readonly IPlayerState playerState;
    private readonly SessionManager sessionManager;
    private readonly PartyWatcher partyWatcher;
    private readonly RelayClient relayClient;
    private readonly ProtocolHandler protocolHandler;
    private readonly GmWindow gmWindow;
    private readonly PlayerWindow playerWindow;
    private readonly ConfigWindow configWindow;
    private readonly RgpdConsentWindow rgpdConsentWindow;
    private readonly SetupAssistantWindow setupAssistantWindow;
    private readonly RoundAnnouncementOverlay roundAnnouncementOverlay;
    private readonly DiceRollOverlay diceRollOverlay;
    private readonly TacticalOverlay tacticalOverlay;
    private readonly PlayerToggleButton playerToggleButton;
    private readonly NpcManager npcManager;
    private readonly NpcSyncCoordinator npcSyncCoordinator;
    private readonly TacticalCameraService tacticalCameraService;
    private readonly CombatNamePlateService combatNamePlateService;
    private readonly PlayDeadService playDeadService;
    private readonly CloudSyncService cloudSyncService;
    private readonly NotesStore notesStore;
    private readonly NotesWindow notesWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IPlayerState playerState,
        IPartyList partyList,
        ICondition condition,
        IChatGui chatGui,
        IPluginLog pluginLog,
        IFramework framework,
        ITextureProvider textureProvider,
        IToastGui toastGui,
        IObjectTable objectTable,
        IDataManager dataManager,
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop,
        IGameGui gameGui,
        IGameConfig gameConfig,
        INamePlateGui namePlateGui)
    {
        Plugin.pluginInterface = pluginInterface;
        Plugin.chatGuiStatic = chatGui;
        Plugin.logStatic = pluginLog;
        ClientState = clientState;
        DataManager = dataManager;
        TextureProvider = textureProvider;
        ToastGui = toastGui;
        GameGui = gameGui;
        GameConfig = gameConfig;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.playerState = playerState;
        ObjectTable = objectTable;
        PartyList = partyList;
        Condition = condition;
        Framework = framework;

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.Migrate()) Configuration.Save();

        Loc.Initialize(Configuration.UiLanguage);
        if (!string.Equals(Configuration.UiLanguage, Loc.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
        {
            Configuration.UiLanguage = Loc.CurrentLanguage;
            Configuration.Save();
        }

        sessionManager = new SessionManager(pluginInterface.GetPluginConfigDirectory())
        {
            GmIsPlayer = Configuration.GmIsPlayer,
            ShowDiceAnimation = Configuration.ShowDiceAnimation,
            DiceAnimationSpeed = Configuration.DiceAnimationSpeed,
        };

        // Load active template (or default) to initialize game-rule settings
        var activeTemplateName = Configuration.ActiveTemplateName;
        var activeTemplate = sessionManager.LoadTemplate(activeTemplateName)
                             ?? sessionManager.LoadTemplate(Configuration.DefaultTemplateName)
                             ?? sessionManager.GetOrCreateDefaultTemplate();
        sessionManager.ApplyTemplate(activeTemplate);

        // Persist in case the default was just created
        if (Configuration.ActiveTemplateName != activeTemplate.Name)
        {
            Configuration.ActiveTemplateName = activeTemplate.Name;
            Configuration.Save();
        }

        notesStore = new NotesStore(pluginInterface.GetPluginConfigDirectory());
        cloudSyncService = new CloudSyncService(Configuration, pluginInterface.GetPluginConfigDirectory(), notesStore);
        sessionManager.CloudSync = cloudSyncService;
        notesStore.OnFlushed = () => cloudSyncService.QueueNotePush();

        diceRollOverlay = new DiceRollOverlay();
        relayClient = new RelayClient();
        protocolHandler = new ProtocolHandler(sessionManager, diceRollOverlay, Configuration, relayClient);
        sessionManager.SetRelayClient(relayClient);
        sessionManager.SetWeatherService(new WeatherService(sigScanner, gameInterop));

        relayClient.OnMessageReceived += protocolHandler.HandleMessage;
        relayClient.OnConnected += OnRelayConnected;
        relayClient.OnDisconnected += OnRelayDisconnected;

        partyWatcher = new PartyWatcher(partyList, playerState, framework);

        var npcSpawnGuard = new NpcSpawnGuard(condition, clientState);
        npcManager = new NpcManager(npcSpawnGuard, clientState, condition, framework, pluginLog);
        npcSyncCoordinator = new NpcSyncCoordinator(npcManager, clientState, pluginLog,
            () => sessionManager.BroadcastUpdate());
        sessionManager.NpcSyncProvider = npcSyncCoordinator.BuildPayload;
        sessionManager.OnRemoteNpcSync = npcSyncCoordinator.ApplyRemote;

        gmWindow = new GmWindow(sessionManager, Configuration, OnConsentRevoked, OnDebugDisabled,
            EnableAllianceMode, DisableAllianceMode);
        gmWindow.SetNpcManager(npcManager);
        playerWindow = new PlayerWindow(sessionManager, playerState, Configuration,
            JoinAllianceRoom, LeaveAllianceRoom);
        gmWindow.PlayerWindowRef = playerWindow;

        notesWindow = new NotesWindow(notesStore, Configuration);
        gmWindow.NotesWindowRef = notesWindow;

        playerToggleButton = new PlayerToggleButton(Configuration)
        {
            PlayerWindowRef = playerWindow,
            NotesWindowRef = notesWindow,
            IsInSession = () => partyWatcher.InParty || sessionManager.IsAllianceMode,
        };
        configWindow = new ConfigWindow(Configuration, OnConsentRevoked);
        rgpdConsentWindow = new RgpdConsentWindow(Configuration, OnConsentGiven);
        setupAssistantWindow = new SetupAssistantWindow(sessionManager, Configuration, playerState, () =>
        {
            Configuration.SetupCompleted = true;
            Configuration.Save();
            if (!Configuration.IsRgpdConsentValid)
                rgpdConsentWindow.IsOpen = true;
            else
                gmWindow.IsOpen = true;
        });
        gmWindow.SetupAssistantRef = setupAssistantWindow;
        roundAnnouncementOverlay = new RoundAnnouncementOverlay();
        sessionManager.SetRoundOverlay(roundAnnouncementOverlay);
        sessionManager.SetDiceRollOverlay(diceRollOverlay);
        tacticalOverlay = new TacticalOverlay(sessionManager, Configuration);
        tacticalCameraService = new TacticalCameraService(Configuration, sessionManager, sigScanner, gameInterop);
        combatNamePlateService = new CombatNamePlateService(Configuration, sessionManager, namePlateGui);
        playDeadService = new PlayDeadService(Configuration, sessionManager);

        WindowSystem.AddWindow(gmWindow);
        WindowSystem.AddWindow(playerWindow);
        WindowSystem.AddWindow(notesWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(rgpdConsentWindow);
        WindowSystem.AddWindow(setupAssistantWindow);

        partyWatcher.OnPartyJoined += OnPartyJoined;
        partyWatcher.OnPartyLeft += OnPartyLeft;
        partyWatcher.OnLeaderChanged += OnLeaderChanged;
        partyWatcher.OnMembersChanged += OnMembersChanged;
        sessionManager.OnPromotionChanged += OnPromotionChanged;
        sessionManager.OnAllianceKicked = () => LeaveAllianceRoom();
        sessionManager.OnAllianceInvite = code => JoinAllianceRoom(code);
        sessionManager.OnAllianceDisband = () => LeaveAllianceRoom();
        sessionManager.OnRejoinRequested = SwitchRoom;
        sessionManager.OnLobbyMoved = code => JoinAllianceRoom(code);
        condition.ConditionChange += OnConditionChange;

        instanceSuppressed = condition[ConditionFlag.BoundByDuty]
                             || condition[ConditionFlag.BoundByDuty56]
                             || condition[ConditionFlag.BoundByDuty95];

        // Restaurer le mode alliance si un code était persisté (auto-rejoin après reload/crash)
        if (!string.IsNullOrEmpty(Configuration.AllianceRoomCode))
        {
            sessionManager.AllianceRoomCode = Configuration.AllianceRoomCode;
            Plugin.Log.Info($"[MasterEvent] Alliance restaurée : {Configuration.AllianceRoomCode}");
        }

        framework.Update += OnFrameworkUpdate;

        commandManager.AddHandler(Constants.CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = Loc.Get("Command.HelpMessage"),
        });
        // Alias visibles dans le /help Dalamud pour être découvrables par les utilisateurs.
        foreach (var alias in Constants.CommandAliases)
        {
            commandManager.AddHandler(alias, new CommandInfo(OnCommand)
            {
                HelpMessage = string.Format(Loc.Get("Command.AliasHelp"), Constants.CommandName),
                ShowInHelp = true,
            });
        }

        pluginInterface.UiBuilder.Draw += DrawUI;
        pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;

        // Load custom icon font (baguette glyph at U+E000)
        CustomIconFont = pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        {
            e.OnPreBuild(tk =>
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("MasterEvent.Resources.MasterEventIcons.ttf");
                if (stream != null)
                {
                    var fontData = new byte[stream.Length];
                    stream.ReadExactly(fontData, 0, fontData.Length);
                    tk.AddFontFromMemory(fontData, new SafeFontConfig { SizePx = 40, GlyphRanges = [0xE000, 0xE000, 0] }, "MasterEventIcons");
                }
            });
        });

        LargeFont = pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        {
            e.OnPreBuild(tk =>
            {
                tk.AddDalamudDefaultFont(60);
            });
        });

        // Premier démarrage : l'assistant gère le RGPD
        if (!Configuration.SetupCompleted)
        {
            setupAssistantWindow.IsOpen = true;
        }
        // Sinon, si le RGPD n'est pas valide (consent révoqué après setup), ouvrir la fenêtre RGPD
        else if (!Configuration.IsRgpdConsentValid)
        {
            rgpdConsentWindow.IsOpen = true;
        }
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw -= DrawUI;
        pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;

        partyWatcher.OnPartyJoined -= OnPartyJoined;
        partyWatcher.OnPartyLeft -= OnPartyLeft;
        partyWatcher.OnLeaderChanged -= OnLeaderChanged;
        partyWatcher.OnMembersChanged -= OnMembersChanged;
        sessionManager.OnPromotionChanged -= OnPromotionChanged;
        Condition.ConditionChange -= OnConditionChange;

        relayClient.OnMessageReceived -= protocolHandler.HandleMessage;
        relayClient.OnConnected -= OnRelayConnected;
        relayClient.OnDisconnected -= OnRelayDisconnected;
        relayClient.Dispose();
        tacticalCameraService.Dispose();
        combatNamePlateService.Dispose();
        // Avant le service cloud : le flush final doit pouvoir mettre le texte en file.
        notesStore.Dispose();
        cloudSyncService.Dispose();
        npcSyncCoordinator.Dispose();
        npcManager.Dispose();
        sessionManager.DisposeWeatherService();
        partyWatcher.Dispose();
        CustomIconFont?.Dispose();
        LargeFont?.Dispose();
        WindowSystem.RemoveAllWindows();
        commandManager.RemoveHandler(Constants.CommandName);
        foreach (var alias in Constants.CommandAliases)
            commandManager.RemoveHandler(alias);
    }

    private bool initialSyncDone;
    private bool defaultSheetApplied;
    private bool instanceSuppressed;

    private void OnFrameworkUpdate(IFramework _)
    {
        relayClient.ProcessIncoming();

        // Maintient/restaure la caméra tactique selon Configuration.TacticalCamera.
        tacticalCameraService.Tick();
        combatNamePlateService.Tick();
        playDeadService.Tick();
        // Synchronisation cloud : le service décide lui-même s'il y a quelque chose à faire
        // Avant la synchro : un flush du bloc-notes met sa mise en file d'attente à disposition
        // du tick cloud qui suit immédiatement, plutôt qu'au tour d'après.
        TickMovementQuota();

        notesStore.Tick();
        cloudSyncService.Tick();

        if (!initialSyncDone)
        {
            sessionManager.SyncPartyMembers(PartyList, playerState);
            if (sessionManager.PartyMembers.Count > 0)
                initialSyncDone = true;
        }

        // Charger la fiche par défaut au démarrage
        if (initialSyncDone && !defaultSheetApplied)
        {
            defaultSheetApplied = true;
            var defaultName = Configuration.DefaultSheetName;
            if (!string.IsNullOrEmpty(defaultName))
            {
                var sheet = sessionManager.LoadPlayerSheet(defaultName);
                if (sheet != null)
                    sessionManager.ApplyPlayerSheet(sheet);
            }
        }

        if (sessionManager.CanEdit)
        {
            sessionManager.PollWaymarkChanges();
            sessionManager.CheckAutoBroadcast();
        }

        // Maintenir l'heure éorzéenne à la valeur d'override chaque frame
        sessionManager.TickTimeOverride();
    }

    private void OnCommand(string command, string args)
    {
        var trimmedArgs = args.Trim().ToLowerInvariant();

        switch (trimmedArgs)
        {
            case "config":
                ToggleWindow(configWindow);
                break;
            case "help":
                chatGui.Print(Loc.Get("Command.Help.Title"));
                chatGui.Print(Loc.Get("Command.Help.Main"));
                chatGui.Print(Loc.Get("Command.Help.Config"));
                break;
            case "joueur":
                playerWindow.IsOpen = !playerWindow.IsOpen;
                break;
            case "overlay":
                Configuration.ShowTacticalOverlay = !Configuration.ShowTacticalOverlay;
                Configuration.Save();
                chatGui.Print(Loc.Get(Configuration.ShowTacticalOverlay
                    ? "Chat.TacticalOverlayOn"
                    : "Chat.TacticalOverlayOff"));
                break;
            case "camera":
                Configuration.TacticalCamera = !Configuration.TacticalCamera;
                Configuration.Save();
                chatGui.Print(Loc.Get(Configuration.TacticalCamera
                    ? "Chat.TacticalCameraOn"
                    : "Chat.TacticalCameraOff"));
                break;
            case "mj":
                if (!Configuration.DebugMode)
                {
                    chatGui.Print(Loc.Get("Chat.DebugDisabled"));
                    break;
                }
                playerWindow.IsOpen = false;
                sessionManager.IsGm = true;
                break;
            case "connect":
                if (!Configuration.DebugMode)
                {
                    chatGui.Print(Loc.Get("Chat.DebugDisabled"));
                    break;
                }
                DebugConnect();
                break;
            case "disconnect":
                if (!Configuration.DebugMode)
                {
                    chatGui.Print(Loc.Get("Chat.DebugDisabled"));
                    break;
                }
                _ = relayClient.DisconnectAsync();
                sessionManager.IsConnected = false;
                sessionManager.ConnectedPlayerCount = 0;
                sessionManager.ResetAllPlayerConnections();
                chatGui.Print(Loc.Get("Chat.Disconnected"));
                break;
            default:
                ToggleMainWindow();
                break;
        }
    }

    private void ToggleMainWindow()
    {
        // If RGPD consent not given, show consent window instead
        if (!Configuration.IsRgpdConsentValid)
        {
            rgpdConsentWindow.IsOpen = true;
            return;
        }

        gmWindow.IsOpen = !gmWindow.IsOpen;

        UpdateRole();

        // Retry relay connection if in party (or alliance mode) but not connected
        if ((partyWatcher.InParty || sessionManager.IsAllianceMode) && !relayClient.IsConnected && !sessionManager.IsConnected)
        {
            ConnectToRelay();
        }
    }

    private void OnPartyJoined()
    {
        UpdateRole();

        // En mode alliance, renseigner le groupe local pour le badge
        if (sessionManager.IsAllianceMode && sessionManager.LocalGroupId == null)
        {
            sessionManager.LocalGroupId = partyWatcher.PartyId.ToString();
            sessionManager.AssignLocalGroup();
        }

        sessionManager.SyncPartyMembers(PartyList, playerState);
        chatGui.Print(string.Format(Loc.Get("Chat.PartyJoined"), sessionManager.IsGm ? Loc.Get("Role.Gm") : Loc.Get("Role.Player")));

        if (!sessionManager.IsGm && Configuration.AutoOpenPlayerWindow)
            playerWindow.IsOpen = true;

        // Connecter au relay (en mode alliance, reconnecter à la room persistée)
        ConnectToRelay();
    }

    private void OnPartyLeft()
    {
        sessionManager.IsGm = true;
        sessionManager.IsPromoted = false;
        sessionManager.SyncPartyMembers(PartyList, playerState);
        if (playerWindow.IsOpen)
        {
            playerWindow.IsOpen = false;
            if (Configuration.AutoOpenPlayerWindow)
                gmWindow.IsOpen = true;
        }
        chatGui.Print(Loc.Get("Chat.PartyLeft"));

        // En mode alliance, ne pas déconnecter le relay
        if (!sessionManager.IsAllianceMode)
        {
            _ = relayClient.DisconnectAsync();
            sessionManager.IsConnected = false;
            sessionManager.ConnectedPlayerCount = 0;
            sessionManager.ResetAllPlayerConnections();
        }
    }

    private void OnLeaderChanged()
    {
        var wasGm = sessionManager.IsGm;
        sessionManager.ClearAllPromotions();
        UpdateRole();
        sessionManager.SyncPartyMembers(PartyList, playerState);
        if (wasGm != sessionManager.IsGm)
        {
            if (sessionManager.IsGm)
            {
                playerWindow.IsOpen = false;
                if (Configuration.AutoOpenPlayerWindow)
                    gmWindow.IsOpen = true;
                chatGui.Print(Loc.Get("Chat.NowGm"));
            }
            else
            {
                gmWindow.IsOpen = false;
                if (Configuration.AutoOpenPlayerWindow)
                    playerWindow.IsOpen = true;
                chatGui.Print(Loc.Get("Chat.NowPlayer"));
            }
        }
    }

    private void OnMembersChanged()
    {
        sessionManager.SyncPartyMembers(PartyList, playerState);
        PublishRoster();
    }

    private void OnPromotionChanged(bool promoted)
    {
        if (promoted)
        {
            playerWindow.IsOpen = false;
            if (Configuration.AutoOpenPlayerWindow)
                gmWindow.IsOpen = true;
        }
        else
        {
            gmWindow.IsOpen = false;
            if (Configuration.AutoOpenPlayerWindow)
                playerWindow.IsOpen = true;
        }
    }

    private static bool IsInDuty()
    {
        return Condition[ConditionFlag.BoundByDuty]
            || Condition[ConditionFlag.BoundByDuty56]
            || Condition[ConditionFlag.BoundByDuty95];
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag is not (ConditionFlag.BoundByDuty or ConditionFlag.BoundByDuty56 or ConditionFlag.BoundByDuty95))
            return;

        // Si l'option est désactivée, ne pas interférer avec la connexion
        if (!Configuration.SuppressInInstance)
            return;

        var inDuty = IsInDuty();

        if (inDuty && !instanceSuppressed)
        {
            // Entrée en instance : déconnecter proprement, sans tentative de reconnexion
            instanceSuppressed = true;

            if (relayClient.IsConnected || sessionManager.IsConnected)
            {
                _ = relayClient.DisconnectAsync();
                sessionManager.IsConnected = false;
                sessionManager.ConnectedPlayerCount = 0;
                sessionManager.ResetAllPlayerConnections();
                chatGui.Print(Loc.Get("Chat.InstanceSuspended"));
            }
        }
        else if (!inDuty && instanceSuppressed)
        {
            // Sortie d'instance : reconnecter si en groupe
            instanceSuppressed = false;

            if ((partyWatcher.InParty || sessionManager.IsAllianceMode) && !relayClient.IsConnected)
            {
                chatGui.Print(Loc.Get("Chat.InstanceResumed"));
                ConnectToRelay();
            }
        }
    }

    private readonly MovementTracker movementTracker = new();
    private DateTime lastMovementBroadcast = DateTime.MinValue;
    private float lastBroadcastRemaining = -1f;
    private bool wasTrackingMovement;
    private const double MovementBroadcastIntervalSeconds = 1.0;
    private const float MovementBroadcastEpsilon = 0.5f;
    private void TickMovementQuota()
    {
        var local = sessionManager.PartyMembers.FirstOrDefault(p => p.Hash == sessionManager.LocalPlayerHash);
        var max = MovementTracker.ResolveMax(sessionManager.ActiveTemplate, local);

        var shouldTrack = max > 0f
                          && sessionManager.CurrentTurnState is { IsActive: true }
                          && sessionManager.IsLocalPlayerTurn
                          && ObjectTable.LocalPlayer is not null;

        movementTracker.Tick(shouldTrack, ObjectTable.LocalPlayer?.Position ?? default);

        if (local == null) return;

        local.MoveMax = shouldTrack ? max : 0f;
        local.MoveLeft = shouldTrack ? movementTracker.Remaining(max) : 0f;

        if (!shouldTrack)
        {
            if (wasTrackingMovement)
            {
                wasTrackingMovement = false;
                lastBroadcastRemaining = -1f;
                sessionManager.SendPlayerStatUpdate();
            }
            return;
        }

        if (!wasTrackingMovement)
        {
            wasTrackingMovement = true;
            lastBroadcastRemaining = -1f;
        }

        var now = DateTime.UtcNow;
        if ((now - lastMovementBroadcast).TotalSeconds < MovementBroadcastIntervalSeconds) return;
        if (MathF.Abs(local.MoveLeft - lastBroadcastRemaining) < MovementBroadcastEpsilon) return;

        lastMovementBroadcast = now;
        lastBroadcastRemaining = local.MoveLeft;
        sessionManager.SendPlayerStatUpdate();
    }

    private void ConnectToRelay()
    {
        if (!Configuration.IsRgpdConsentValid)
        {
            Plugin.Log.Warning("[MasterEvent] Relay connection blocked: RGPD consent not given.");
            chatGui.Print(Loc.Get("Chat.RgpdRequired"));
            rgpdConsentWindow.IsOpen = true;
            return;
        }

        if (instanceSuppressed) return;
        if (relayClient.IsConnected) return;

        Plugin.Log.Info($"[MasterEvent] Connecting to relay: {Configuration.RelayServerUrl}");
        _ = relayClient.ConnectAsync(Configuration.RelayServerUrl);
    }

    private void SendJoinMessage()
    {
        if (!relayClient.IsConnected || (!partyWatcher.InParty && !sessionManager.IsAllianceMode)) return;

        sessionManager.CacheRestored = false;
        var playerName = ObjectTable.LocalPlayer?.Name.ToString() ?? "Unknown";
        var playerHash = GeneratePlayerHash(playerState.ContentId);

        // Protocole 2 : la party réelle voyage toujours dans `partyId`, et le lobby dans
        // `lobbyCode`. C'est ce couple qui alimente l'index de découverte du relais, donc ce
        // qui permet aux membres d'un sous-groupe de suivre leur chef dans une alliance sans
        // jamais connaître le code. En 1.x, `partyId` portait le code et l'information de la
        // party d'origine était perdue.
        var partyId = partyWatcher.InParty
            ? partyWatcher.PartyId.ToString()
            // Hors groupe, PartyId vaut 0 pour tout le monde : l'utiliser comme clé ferait
            // collisionner tous les joueurs solo dans une même salle et dans l'index.
            : $"solo-{playerHash}";

        // En mode alliance, transmettre le vrai party ID comme groupId pour identifier le groupe d'origine
        var groupId = sessionManager.IsAllianceMode ? partyId : null;

        // Un seul candidat au leadership par room. En alliance, c'est le créateur : sans ça
        // chaque chef de sous-groupe revendiquerait la room avec son propre jeton, et le premier
        // arrivé la verrouillerait jusqu'à expiration — y compris contre le vrai MJ.
        var claimsLeadership = sessionManager.IsAllianceMode
            ? Configuration.AllianceIsCreator
            : sessionManager.IsGm;

        var joinMsg = new RelayMessage
        {
            Type = MessageType.Join,
            PartyId = partyId,
            PlayerName = playerName,
            PlayerHash = playerHash,
            IsLeader = claimsLeadership,
            Version = Constants.PluginVersion,
            GroupId = groupId,
            LeaderToken = claimsLeadership ? Configuration.EnsureLeaderToken() : null,
            Protocol = ProtocolVersion.Lobby,
            LobbyCode = sessionManager.AllianceRoomCode,
            Roster = BuildPartyRoster(playerHash),
        };
        _ = relayClient.SendAsync(joinMsg);

        // Non-GM players request the current state from the GM
        if (!sessionManager.IsGm)
            sessionManager.RequestUpdate();
    }
    private string[] BuildPartyRoster(string localHash)
    {
        var roster = new List<string> { localHash };

        foreach (var member in PartyList)
        {
            if (member == null) continue;

            var hash = GeneratePlayerHash(member.ContentId);
            if (!roster.Contains(hash))
                roster.Add(hash);
        }

        return roster.ToArray();
    }

    private void PublishRoster()
    {
        if (!relayClient.IsConnected || !sessionManager.IsConnected) return;

        var localHash = GeneratePlayerHash(playerState.ContentId);
        _ = relayClient.SendAsync(new RelayMessage
        {
            Type = MessageType.RosterUpdate,
            Roster = BuildPartyRoster(localHash),
        });
    }


    private void SwitchRoom()
    {
        sessionManager.IsConnected = false;
        sessionManager.ConnectedPlayerCount = 0;
        sessionManager.ResetAllPlayerConnections();

        if (relayClient.IsConnected)
            SendJoinMessage();
        else
            ConnectToRelay();
    }

    internal static string GeneratePlayerHash(ulong contentId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(contentId.ToString()));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private RelayMessage? pendingDebugJoin;

    private void DebugConnect()
    {
        if (!Configuration.IsRgpdConsentValid)
        {
            chatGui.Print("[MasterEvent] RGPD consent required.");
            return;
        }

        var playerName = ObjectTable.LocalPlayer?.Name.ToString() ?? "Debug";
        var worldName = ObjectTable.LocalPlayer?.HomeWorld.Value.Name.ExtractText();
        if (!string.IsNullOrEmpty(worldName))
            playerName = $"{playerName}@{worldName}";
        var playerHash = GeneratePlayerHash(playerState.ContentId);

        pendingDebugJoin = new RelayMessage
        {
            Type = MessageType.Join,
            PartyId = "debug-" + playerHash,
            PlayerName = playerName,
            PlayerHash = playerHash,
            IsLeader = sessionManager.IsGm,
            Version = Constants.PluginVersion,
            LeaderToken = sessionManager.IsGm ? Configuration.EnsureLeaderToken() : null,
        };

        _ = relayClient.ConnectAsync(Configuration.RelayServerUrl);
    }

    private void OnRelayConnected()
    {
        Plugin.Log.Info("[MasterEvent] Relay connected.");

        if (pendingDebugJoin != null)
        {
            _ = relayClient.SendAsync(pendingDebugJoin);
            pendingDebugJoin = null;
            Plugin.Log.Info("[MasterEvent] Debug: join message sent.");
            chatGui.Print(Loc.Get("Chat.DebugConnected"));
        }
        else
        {
            SendJoinMessage();
            chatGui.Print(Loc.Get("Chat.Connected"));
        }

        // Vérifie les mises à jour des modèles abonnés (requêtes HTTP /version légères).
        _ = Task.Run(() => sessionManager.CheckAllSubscriptionsAsync(Configuration.RelayServerUrl));
    }

    private void OnRelayDisconnected()
    {
        var wasConnected = sessionManager.IsConnected;
        sessionManager.IsConnected = false;
        sessionManager.ResetAllPlayerConnections();
        Plugin.Log.Info("[MasterEvent] Relay disconnected.");

        // Ne pas afficher "reconnexion en cours" si on a coupé volontairement pour une instance
        if (wasConnected && partyWatcher.InParty && !instanceSuppressed)
            chatGui.Print(Loc.Get("Chat.RelayConnectionLost"));
        else if (Configuration.DebugMode)
            chatGui.Print(Loc.Get("Chat.Disconnected"));
    }

    private void OnConsentGiven()
    {
        Plugin.Log.Info("[MasterEvent] RGPD consent given.");
        if (partyWatcher.InParty)
        {
            ConnectToRelay();
        }
    }

    private void OnConsentRevoked()
    {
        Plugin.Log.Info("[MasterEvent] RGPD consent revoked. Disconnecting from relay.");
        _ = relayClient.DisconnectAsync();
        sessionManager.IsConnected = false;
        sessionManager.ConnectedPlayerCount = 0;
        sessionManager.ResetAllPlayerConnections();
    }

    private void OnDebugDisabled()
    {
        Plugin.Log.Info("[MasterEvent] Debug mode disabled. Cleaning up debug state.");

        // Disconnect debug relay connection
        _ = relayClient.DisconnectAsync();
        sessionManager.IsConnected = false;
        sessionManager.ConnectedPlayerCount = 0;
        sessionManager.ResetAllPlayerConnections();

        // Restore correct role and window based on party state
        playerWindow.IsOpen = false;
        UpdateRole();

        if (partyWatcher.InParty)
        {
            if (sessionManager.IsGm)
                gmWindow.IsOpen = true;
            else
            {
                gmWindow.IsOpen = false;
                playerWindow.IsOpen = true;
            }
            ConnectToRelay();
        }
    }

    private void EnableAllianceMode()
    {
        var allianceCode = SessionManager.GenerateAllianceCode();

        // Inviter les joueurs du groupe actuel avant de se déconnecter
        if (relayClient.IsConnected)
        {
            var inviteMsg = new RelayMessage
            {
                Type = MessageType.AllianceInvite,
                AllianceCode = allianceCode,
            };
            _ = relayClient.SendAsync(inviteMsg);
        }

        sessionManager.AllianceRoomCode = allianceCode;
        sessionManager.LocalGroupId = partyWatcher.PartyId.ToString();
        Configuration.AllianceRoomCode = sessionManager.AllianceRoomCode;
        Configuration.AllianceIsCreator = true;
        Configuration.Save();
        // Assigner le groupe local aux membres existants
        sessionManager.AssignLocalGroup();
        // Le créateur reste MJ : IsAllianceMode neutralise UpdateRole, on fixe le rôle ici.
        sessionManager.IsGm = true;
        SwitchRoom();
        chatGui.Print($"[MasterEvent] {Loc.Get("Alliance.Title")} — {Loc.Get("Alliance.RoomCode")} {sessionManager.AllianceRoomCode}");
    }

    private void DisableAllianceMode()
    {
        // Notifier les joueurs de l'alliance avant de se déconnecter
        if (relayClient.IsConnected && sessionManager.IsAllianceMode)
        {
            var disbandMsg = new RelayMessage
            {
                Type = MessageType.AllianceDisband,
            };
            _ = relayClient.SendAsync(disbandMsg);
        }

        sessionManager.AllianceRoomCode = null;
        sessionManager.LocalGroupId = null;
        sessionManager.ClearAlliancePlayers();
        Configuration.AllianceRoomCode = null;
        Configuration.AllianceIsCreator = false;
        Configuration.Save();

        // L'alliance quittée, le rôle redevient celui du groupe FFXIV.
        UpdateRole();

        if (partyWatcher.InParty)
        {
            SwitchRoom();
        }
        else
        {
            _ = relayClient.DisconnectAsync();
            sessionManager.IsConnected = false;
            sessionManager.ConnectedPlayerCount = 0;
            sessionManager.ResetAllPlayerConnections();
        }
    }

    private void JoinAllianceRoom(string code)
    {
        sessionManager.AllianceRoomCode = code.ToUpperInvariant();
        sessionManager.LocalGroupId = partyWatcher.PartyId.ToString();
        Configuration.AllianceRoomCode = sessionManager.AllianceRoomCode;
        Configuration.AllianceIsCreator = false;
        Configuration.Save();
        // Assigner le groupe local aux membres existants
        sessionManager.AssignLocalGroup();
        // Rejoindre une alliance, c'est y entrer comme joueur : le MJ est le créateur de la
        // salle. Un chef de sous-groupe FFXIV n'a pas d'autorité sur l'alliance.
        sessionManager.IsGm = false;
        SwitchRoom();
        chatGui.Print($"[MasterEvent] {Loc.Get("Alliance.Connected")} {sessionManager.AllianceRoomCode}");
    }

    private void LeaveAllianceRoom()
    {
        DisableAllianceMode();
    }

    private void UpdateRole()
    {
        // En mode alliance, le rôle est arbitré par le relay (cf. HandleJoinConfirm) : un chef
        // de sous-groupe FFXIV n'est pas MJ de l'alliance. Écraser le verdict serveur ici
        // recréerait plusieurs MJ simultanés, chacun revendiquant le leadership avec son propre
        // jeton — c'est ce qui rendait le mode alliance inutilisable.
        if (sessionManager.IsAllianceMode) return;

        sessionManager.IsGm = partyWatcher.IsLeader || !partyWatcher.InParty;
    }

    private static void ToggleWindow(Window window)
    {
        window.IsOpen = !window.IsOpen;
    }

    private void DrawUI()
    {
        WindowSystem.Draw();
        // FileDialogManager.Draw() doit être appelé chaque frame pour que
        // les boîtes de dialogue ouvertes via OpenFileDialog/SaveFileDialog
        // s'affichent. Sans cet appel, OpenFileDialog ne fait rien visible.
        FileDialogManager.Draw();
        roundAnnouncementOverlay.Draw();
        diceRollOverlay.Draw();
        tacticalOverlay.Draw();
        playerToggleButton.Draw();
    }

    private void OnOpenConfigUi()
    {
        configWindow.IsOpen = true;
    }

    private void OnOpenMainUi()
    {
        ToggleMainWindow();
    }
}
