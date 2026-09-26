namespace AgentsDashboard.Core.Model;

/// <summary>
/// The URL a worktree is shown at, and how to read one back.
/// </summary>
/// <remarks>
/// A worktree is addressed by its filesystem path, which is encoded into one URL
/// segment. That matters for more than escaping: linked worktrees usually live
/// <em>inside</em> the primary one (<c>&lt;repo&gt;/.claude/worktrees/&lt;name&gt;</c>),
/// so the primary's URL is a prefix of every other worktree's. Anything that asks
/// "is this the current page?" by prefix therefore lights up the primary as well,
/// which is why this parses the segment out and compares whole paths.
/// </remarks>
public static class WorktreeRoute
{
    private const string Prefix = "worktree/";

    /// <summary>The URL for a worktree, optionally on a named tab.</summary>
    public static string For(string worktreePath, string? tab = null)
    {
        var url = Prefix + Uri.EscapeDataString(worktreePath);
        return tab is null ? url : url + "/" + tab;
    }

    /// <summary>
    /// The URL for one file of a worktree, optionally scrolled to a line.
    /// </summary>
    /// <remarks>
    /// The file goes in the query string rather than in the path because a path is
    /// already one escaped segment here, and nesting a second escaped path inside
    /// it makes a URL nobody can read in the address bar. <see cref="PathOf"/>
    /// drops the query, so the worktree a page shows is unaffected.
    /// </remarks>
    public static string ForFile(string worktreePath, string relativeFile, int? line = null)
    {
        var url = For(worktreePath, "files") + "?file=" + Uri.EscapeDataString(relativeFile);
        return line is null ? url : url + "&line=" + line;
    }

    /// <summary>
    /// The worktree path a base-relative URL is showing, or null when it is not a
    /// worktree page.
    /// </summary>
    public static string? PathOf(string relativeUrl)
    {
        var path = relativeUrl;

        var query = path.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            path = path[..query];
        }

        path = path.TrimStart('/');
        if (!path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = path[Prefix.Length..];
        var slash = rest.IndexOf('/');
        var segment = slash < 0 ? rest : rest[..slash];

        return segment.Length == 0 ? null : Uri.UnescapeDataString(segment);
    }

    /// <summary>Whether a base-relative URL is showing exactly this worktree.</summary>
    public static bool Shows(string relativeUrl, string worktreePath) =>
        string.Equals(PathOf(relativeUrl), worktreePath, StringComparison.Ordinal);
}
