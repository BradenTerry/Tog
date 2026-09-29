using AgentsDashboard.Core.Extensions;

namespace AgentsDashboard.App.Extensions;

/// <summary>How Settings names an extension's source and status, shared by its list and its own page.</summary>
public static class ExtensionLabels
{
    public static string Name(FoundExtension found) => found.Manifest?.Name ?? found.Id;

    public static string SourceName(ExtensionSource source) => source switch
    {
        ExtensionSource.Installed => "installed",
        ExtensionSource.InFolder => "folder",
        ExtensionSource.Linked => "dev",
        _ => "this run",
    };

    public static string SourceTip(ExtensionSource source) => source switch
    {
        ExtensionSource.Installed => "A copy in the dashboard's extensions folder",
        ExtensionSource.Linked => "A folder you linked. Reloaded on every build.",
        ExtensionSource.InFolder => "In one of your extension folders. Reloaded on every build.",
        _ => "Passed with --extension for this run only",
    };

    public static string StatusName(ExtensionStatus status) => status switch
    {
        ExtensionStatus.Loaded => "on",
        ExtensionStatus.NeedsConsent => "changed",
        ExtensionStatus.Failed => "failed",
        ExtensionStatus.Invalid => "invalid",
        _ => "off",
    };

    public static string StatusClass(ExtensionStatus status) => status switch
    {
        ExtensionStatus.Loaded => "active",
        ExtensionStatus.Failed or ExtensionStatus.Invalid => "danger",
        ExtensionStatus.NeedsConsent => "accent",
        _ => "",
    };
}
