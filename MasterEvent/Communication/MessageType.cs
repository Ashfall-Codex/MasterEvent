namespace MasterEvent.Communication;

public static class MessageType
{
    public const string Join = "join";
    public const string Leave = "leave";
    public const string JoinConfirm = "joinConfirm";
    public const string Update = "update";
    public const string RequestUpdate = "requestUpdate";
    public const string Clear = "clear";
    public const string PlayerJoined = "playerJoined";
    public const string PlayerLeft = "playerLeft";
    public const string VersionMismatch = "versionMismatch";
    public const string Roll = "roll";
    public const string PlayerUpdate = "playerUpdate";
    public const string Promote = "promote";
    public const string TemplateShare = "templateShare";
    public const string CachedState = "cachedState";
    public const string TurnUpdate = "turnUpdate";
    public const string TurnClear = "turnClear";
    public const string StatRoll = "statRoll";
    public const string PlayerStatUpdate = "playerStatUpdate";
    public const string WeatherUpdate = "weatherUpdate";
    public const string TimeUpdate = "timeUpdate";
    public const string AllianceKick = "allianceKick";
    public const string AllianceInvite = "allianceInvite";
    public const string AllianceDisband = "allianceDisband";
    public const string VersionRejected = "versionRejected";
    public const string TemplateUpdated = "templateUpdated";
    public const string GmAnnouncement = "gmAnnouncement";
    public const string JoinRejected = "joinRejected";
    public const string JoinPending = "joinPending";
    public const string JoinAdmitted = "joinAdmitted";
    public const string LobbyPending = "lobbyPending";
    public const string LobbyMoved = "lobbyMoved";
    public const string Admit = "admit";
    public const string Deny = "deny";
    public const string RosterUpdate = "rosterUpdate";

    /// Demande d'un joueur signalant la fin de son propre tour. Le MJ l'applique après contrôle
    /// et rediffuse l'état ; le joueur n'écrit jamais l'état des tours lui-même.
    public const string TurnEndSelf = "turnEndSelf";
}

public static class ProtocolVersion
{
    public const int Legacy = 1;
    public const int Lobby = 2;
}
