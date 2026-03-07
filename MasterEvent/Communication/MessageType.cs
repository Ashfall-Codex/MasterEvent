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
}
