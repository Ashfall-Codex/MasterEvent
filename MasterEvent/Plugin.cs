using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.Command;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MasterEvent.Communication;
using MasterEvent.Localization;
using MasterEvent.Services;
using MasterEvent.UI;

namespace MasterEvent;

public sealed class Plugin : IDalamudPlugin
{
    private static IDalamudPluginInterface pluginInterface = null!;
    private static IChatGui chatGuiStatic = null!;
    private static IPluginLog logStatic = null!;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    internal static IClientState ClientState { get; private set; } = null!;
    internal static IPartyList PartyList { get; private set; } = null!;
    internal static ICondition Condition { get; private set; } = null!;
    internal static IPluginLog Log => logStatic;
    internal static IFramework Framework { get; private set; } = null!;
    internal static ITextureProvider TextureProvider { get; private set; } = null!;
    internal static IToastGui ToastGui { get; private set; } = null!;

    internal static IDalamudPluginInterface PluginInterface => pluginInterface;
    internal static IChatGui ChatGui => chatGuiStatic;
    internal static IFontHandle? CustomIconFont { get; private set; }
    internal static IFontHandle? LargeFont { get; private set; }

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
    private readonly RoundAnnouncementOverlay roundAnnouncementOverlay;

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
        IToastGui toastGui)
    {
        Plugin.pluginInterface = pluginInterface;
        Plugin.chatGuiStatic = chatGui;
        Plugin.logStatic = pluginLog;
        TextureProvider = textureProvider;
        ToastGui = toastGui;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.playerState = playerState;
        ClientState = clientState;
        PartyList = partyList;
        Condition = condition;
        Framework = framework;

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Loc.Initialize(Configuration.UiLanguage);
        if (!string.Equals(Configuration.UiLanguage, Loc.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
        {
            Configuration.UiLanguage = Loc.CurrentLanguage;
            Configuration.Save();
        }

        sessionManager = new SessionManager(pluginInterface.GetPluginConfigDirectory());
        sessionManager.GmIsPlayer = Configuration.GmIsPlayer;

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

        relayClient = new RelayClient();
        protocolHandler = new ProtocolHandler(sessionManager);
        sessionManager.SetRelayClient(relayClient);

        relayClient.OnMessageReceived += protocolHandler.HandleMessage;
        relayClient.OnConnected += OnRelayConnected;
        relayClient.OnDisconnected += OnRelayDisconnected;

        partyWatcher = new PartyWatcher(partyList, playerState, framework);

        gmWindow = new GmWindow(sessionManager, Configuration, OnConsentRevoked, OnDebugDisabled);
        playerWindow = new PlayerWindow(sessionManager, playerState);
        configWindow = new ConfigWindow(Configuration, OnConsentRevoked);
        rgpdConsentWindow = new RgpdConsentWindow(Configuration, OnConsentGiven);
        roundAnnouncementOverlay = new RoundAnnouncementOverlay();
        sessionManager.SetRoundOverlay(roundAnnouncementOverlay);

        WindowSystem.AddWindow(gmWindow);
        WindowSystem.AddWindow(playerWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(rgpdConsentWindow);

        partyWatcher.OnPartyJoined += OnPartyJoined;
        partyWatcher.OnPartyLeft += OnPartyLeft;
        partyWatcher.OnLeaderChanged += OnLeaderChanged;
        partyWatcher.OnMembersChanged += OnMembersChanged;
        sessionManager.OnPromotionChanged += OnPromotionChanged;

        framework.Update += OnFrameworkUpdate;

        commandManager.AddHandler(Constants.CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = Loc.Get("Command.HelpMessage"),
        });

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
                    _ = stream.Read(fontData, 0, fontData.Length);
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

        // Show RGPD consent window on first launch if consent not yet given
        if (!Configuration.IsRgpdConsentValid)
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

        relayClient.OnMessageReceived -= protocolHandler.HandleMessage;
        relayClient.OnConnected -= OnRelayConnected;
        relayClient.OnDisconnected -= OnRelayDisconnected;
        relayClient.Dispose();
        partyWatcher.Dispose();
        CustomIconFont?.Dispose();
        LargeFont?.Dispose();
        WindowSystem.RemoveAllWindows();
        commandManager.RemoveHandler(Constants.CommandName);
    }

    private bool initialSyncDone;

    private void OnFrameworkUpdate(IFramework _)
    {
        relayClient.ProcessIncoming();

        if (!initialSyncDone)
        {
            sessionManager.SyncPartyMembers(PartyList, playerState);
            if (sessionManager.PartyMembers.Count > 0)
                initialSyncDone = true;
        }

        if (sessionManager.CanEdit)
        {
            sessionManager.PollWaymarkChanges();
            sessionManager.CheckAutoBroadcast();
        }
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
                if (!Configuration.DebugMode)
                {
                    chatGui.Print(Loc.Get("Chat.DebugDisabled"));
                    break;
                }
                playerWindow.IsOpen = true;
                sessionManager.IsGm = false;
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

        if (partyWatcher.InParty && (partyWatcher.IsLeader || sessionManager.IsPromoted))
        {
            playerWindow.IsOpen = false;
            gmWindow.IsOpen = !gmWindow.IsOpen;
        }
        else if (partyWatcher.InParty)
        {
            gmWindow.IsOpen = false;
            playerWindow.IsOpen = !playerWindow.IsOpen;
        }
        else
        {
            playerWindow.IsOpen = false;
            gmWindow.IsOpen = !gmWindow.IsOpen;
        }

        sessionManager.IsGm = partyWatcher.IsLeader || !partyWatcher.InParty;

        // Retry relay connection if in party but not connected
        if (partyWatcher.InParty && !relayClient.IsConnected && !sessionManager.IsConnected)
        {
            ConnectToRelay();
        }
    }

    private void OnPartyJoined()
    {
        UpdateRole();
        sessionManager.SyncPartyMembers(PartyList, playerState);
        chatGui.Print(string.Format(Loc.Get("Chat.PartyJoined"), sessionManager.IsGm ? Loc.Get("Role.Gm") : Loc.Get("Role.Player")));

        if (!sessionManager.IsGm && Configuration.AutoOpenPlayerWindow)
            playerWindow.IsOpen = true;

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
        _ = relayClient.DisconnectAsync();
        sessionManager.IsConnected = false;
        sessionManager.ConnectedPlayerCount = 0;
        sessionManager.ResetAllPlayerConnections();
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

    private void ConnectToRelay()
    {
        // Block relay connection if RGPD consent not given
        if (!Configuration.IsRgpdConsentValid)
        {
            Plugin.Log.Warning("[MasterEvent] Relay connection blocked: RGPD consent not given.");
            chatGui.Print(Loc.Get("Chat.RgpdRequired"));
            rgpdConsentWindow.IsOpen = true;
            return;
        }

        if (relayClient.IsConnected) return;

        Plugin.Log.Info($"[MasterEvent] Connecting to relay: {Configuration.RelayServerUrl}");
        _ = relayClient.ConnectAsync(Configuration.RelayServerUrl);
    }

    private void SendJoinMessage()
    {
        if (!relayClient.IsConnected || !partyWatcher.InParty) return;

        sessionManager.CacheRestored = false;
        var partyId = partyWatcher.PartyId.ToString();
#pragma warning disable CS0618
        var playerName = ClientState.LocalPlayer?.Name.TextValue ?? "Unknown";
#pragma warning restore CS0618
        var playerHash = GeneratePlayerHash(playerState.ContentId);

        var joinMsg = new RelayMessage
        {
            Type = MessageType.Join,
            PartyId = partyId,
            PlayerName = playerName,
            PlayerHash = playerHash,
            IsLeader = sessionManager.IsGm,
            Version = Constants.PluginVersion,
        };
        _ = relayClient.SendAsync(joinMsg);

        // Non-GM players request the current state from the GM
        if (!sessionManager.IsGm)
            sessionManager.RequestUpdate();
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

#pragma warning disable CS0618
        var playerName = ClientState.LocalPlayer?.Name.TextValue ?? "Debug";
        var worldName = ClientState.LocalPlayer?.HomeWorld.Value.Name.ExtractText();
        if (!string.IsNullOrEmpty(worldName))
            playerName = $"{playerName}@{worldName}";
#pragma warning restore CS0618
        var playerHash = GeneratePlayerHash(playerState.ContentId);

        pendingDebugJoin = new RelayMessage
        {
            Type = MessageType.Join,
            PartyId = "debug-" + playerHash,
            PlayerName = playerName,
            PlayerHash = playerHash,
            IsLeader = sessionManager.IsGm,
            Version = Constants.PluginVersion,
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
    }

    private void OnRelayDisconnected()
    {
        var wasConnected = sessionManager.IsConnected;
        sessionManager.IsConnected = false;
        sessionManager.ResetAllPlayerConnections();
        Plugin.Log.Info("[MasterEvent] Relay disconnected.");

        if (wasConnected && partyWatcher.InParty)
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

    private void UpdateRole()
    {
        sessionManager.IsGm = partyWatcher.IsLeader || !partyWatcher.InParty;
    }

    private static void ToggleWindow(Window window)
    {
        window.IsOpen = !window.IsOpen;
    }

    private void DrawUI()
    {
        WindowSystem.Draw();
        roundAnnouncementOverlay.Draw();
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
