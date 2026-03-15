using System;
using System.Reflection;

namespace MasterEvent;

public static class Constants
{
    public const string PluginName = "MasterEvent";
    public const string CommandName = "/masterevent";
    public const string CommandAlias = "/me";
    public const int MaxNameLength = 26;
    public const int WaymarkCount = 8; // A, B, C, D, 1, 2, 3, 4
    public const string DefaultRelayUrl = "wss://masterevent.ashfall-codex.dev";

    public static readonly Version PluginVersionObj =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public static readonly string PluginVersion = $"{PluginVersionObj.Major}.{PluginVersionObj.Minor}.{PluginVersionObj.Build}";
    public static readonly string PluginBuild = PluginVersionObj.Revision.ToString();

    public const string DiscordUrl = "https://discord.gg/2zJB7DjAs9";
    public const string GitHubUrl = "https://github.com/kedaewyn/MasterEvent";
    public const string ChangelogUrl = "https://github.com/kedaewyn/MasterEvent/releases";
}
