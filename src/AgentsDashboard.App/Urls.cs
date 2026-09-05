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

    public static bool ShowsWorktree(string relativeUrl, string path) =>
        WorktreeRoute.Shows(relativeUrl, path);
}
