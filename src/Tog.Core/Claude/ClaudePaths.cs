namespace Tog.Core.Claude;

/// <summary>
/// Where Claude Code keeps the files Tog reads.
/// </summary>
/// <remarks>
/// <c>CLAUDE_CONFIG_DIR</c> relocates the whole tree, so nothing here hardcodes
/// <c>~/.claude</c>. Everything is read-only: Tog never writes into
/// Claude's directories.
/// </remarks>
public sealed class ClaudePaths
{
    private readonly string _home;

    public ClaudePaths(IDictionary<string, string?>? env = null, string? home = null)
    {
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configured = env is not null && env.TryGetValue("CLAUDE_CONFIG_DIR", out var v)
            ? v
            : Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");

        Root = !string.IsNullOrWhiteSpace(configured)
            ? configured!
            : Path.Combine(_home, ".claude");

        // Claude keeps its user settings, MCP servers among them, beside the
        // config tree by default and inside it when the tree is moved.
        UserConfigFile = !string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(configured!, ".claude.json")
            : Path.Combine(_home, ".claude.json");
    }

    /// <summary>Claude's user-wide config file, which holds the MCP servers added with <c>--scope user</c>.</summary>
    public string UserConfigFile { get; }

    /// <summary>Claude's config tree.</summary>
    public string Root { get; }

    /// <summary>One JSON file per live session, named for its pid.</summary>
    public string SessionsDir => Path.Combine(Root, "sessions");

    /// <summary>One directory per project, holding that project's transcripts.</summary>
    public string ProjectsDir => Path.Combine(Root, "projects");

    /// <summary>
    /// The directory name Claude derives from a working directory: every
    /// separator, dot and space becomes a dash, so <c>/a/b/.claude/w</c> becomes
    /// <c>-a-b--claude-w</c>.
    /// </summary>
    /// <remarks>
    /// This is a fast path only. <see cref="TranscriptLocator"/> falls back to
    /// scanning the projects directory, because the slug is Claude's private
    /// convention and a session whose transcript we cannot find must degrade to
    /// "no work summary", never to a missing agent.
    /// </remarks>
    public static string ProjectSlug(string cwd)
    {
        var chars = new char[cwd.Length];
        for (var i = 0; i < cwd.Length; i++)
        {
            var c = cwd[i];
            chars[i] = char.IsAsciiLetterOrDigit(c) ? c : '-';
        }

        return new string(chars);
    }
}
