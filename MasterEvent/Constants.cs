using System.Reflection;

namespace MasterEvent;

public static class Constants
{
    public const string PluginName = "MasterEvent";
    public const string CommandName = "/masterevent";
    public const string CommandAlias = "/me";
    public const int MaxNameLength = 26;
    public const int WaymarkCount = 8; // A, B, C, D, 1, 2, 3, 4
    public const string DefaultRelayUrl = "ws://83.228.223.246:8765";

    public static readonly string PluginVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "0.0.0";

    public const string DiscordUrl = "https://discord.gg/placeholder";
    public const string GitHubUrl = "https://github.com/kedaewyn/MasterEvent";
    public const string ChangelogUrl = "https://github.com/kedaewyn/MasterEvent/releases";
}
