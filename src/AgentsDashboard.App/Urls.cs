using AgentsDashboard.Core.Model;

namespace AgentsDashboard.App;

/// <summary>The app's URLs, built in one place.</summary>
/// <remarks>
/// The construction and parsing live in <see cref="WorktreeRoute"/>, which has no
/// UI dependency and is unit tested: getting worktree URLs wrong is subtle,
/// because linked worktrees sit inside the primary one and so share its prefix.
/// </remarks>
public static class Urls
{
    public static string Worktree(string path, string? tab = null) => WorktreeRoute.For(path, tab);

    /// <summary>The Files tab of a worktree, showing one file at an optional line.</summary>
    public static string File(string worktreePath, string relativeFile, int? line = null) =>
        WorktreeRoute.ForFile(worktreePath, relativeFile, line);

    public static bool ShowsWorktree(string relativeUrl, string path) =>
        WorktreeRoute.Shows(relativeUrl, path);

    /// <summary>The agent view, optionally open on one agent and one of its tabs.</summary>
    public static string Chat(string? sessionId = null, string? tab = null) =>
        sessionId is null ? "chat"
        : tab is null ? "chat/" + Uri.EscapeDataString(sessionId)
        : "chat/" + Uri.EscapeDataString(sessionId) + "/" + tab;
}
