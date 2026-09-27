namespace AgentsDashboard.Core.Presentation;

/// <summary>One colour theme the app can be drawn in.</summary>
/// <param name="Id">The value of <c>data-theme</c> on the page, which <c>app.css</c> keys its palette on.</param>
public sealed record Theme(string Id, string Name, string Description);

/// <summary>
/// The themes the settings offer. Each one's colours live in <c>app.css</c>
/// under <c>:root[data-theme="id"]</c>; this list only names them, so adding a
/// theme is a block of variables there and a line here.
/// </summary>
public static class Themes
{
    /// <summary>Not a palette: the page picks Dark or Light from the OS, and follows it when it changes.</summary>
    public const string System = "system";

    public static IReadOnlyList<Theme> All { get; } =
    [
        new(System, "System", "Dark or Light, following your OS"),
        new("dark", "Dark", "The default dark theme"),
        new("light", "Light", "The default light theme"),
        new("nord", "Nord", "Cool blue-grey, dark"),
        new("solarized-light", "Solarized Light", "Warm cream, light"),
    ];

    /// <summary>
    /// The theme to draw for a stored choice. A theme that has since been removed,
    /// or a hand-edited settings file, falls back to System rather than to a
    /// <c>data-theme</c> no stylesheet rule matches.
    /// </summary>
    public static string Resolve(string? id) =>
        All.Any(t => t.Id == id) ? id! : System;
}
