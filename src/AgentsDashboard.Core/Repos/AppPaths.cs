namespace AgentsDashboard.Core.Repos;

/// <summary>Where the dashboard keeps its own state.</summary>
/// <remarks>
/// Deliberately outside Claude's config tree. The dashboard only ever reads from
/// there, so nothing of its own is mixed into it.
/// </remarks>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agents-dashboard");
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>The agent and worktree last in view. See <c>LastViewStore</c>.</summary>
    public string LastViewFile => Path.Combine(Root, "last-view.json");

    /// <summary>Where the views sit in the panels, and the panels' sizes. See <c>PanelLayoutStore</c>.</summary>
    public string LayoutFile => Path.Combine(Root, "layout.json");

    /// <summary>Each agent's last turn end you had on screen. See <c>SeenTurnsStore</c>.</summary>
    public string SeenTurnsFile => Path.Combine(Root, "seen-turns.json");

    /// <summary>The native window's size and place on screen. See <c>WindowBoundsStore</c>.</summary>
    public string WindowFile => Path.Combine(Root, "window.json");

    /// <summary>One file per worktree holding its unsubmitted review.</summary>
    public string DraftsDir => Path.Combine(Root, "drafts");

    /// <summary>Installed extensions, one folder each.</summary>
    public string ExtensionsDir => Path.Combine(Root, "extensions");

    /// <summary>
    /// Copies of extensions as they were loaded. Loading from a copy keeps the
    /// build output free to be overwritten by the next build.
    /// </summary>
    public string ExtensionCacheDir => Path.Combine(Root, "extension-cache");

    /// <summary>Each extension's own data folder.</summary>
    public string ExtensionDataDir(string id) => Path.Combine(Root, "extension-data", id);

    /// <summary>Where anything outside the app drops a request to open a file in it. See <c>OpenRequests</c>.</summary>
    public string OpenRequestsDir => Path.Combine(Root, "open");

    /// <summary>Where the running app's agent tools are served, for the stdio bridge. See <c>McpLink</c>.</summary>
    public string McpLinkFile => Path.Combine(Root, "mcp-link.json");

    /// <summary>The extension API assembly, for extensions to compile against.</summary>
    public string SdkDir => Path.Combine(Root, "sdk");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DraftsDir);
    }

    /// <summary>
    /// A file name that is unique per worktree path and safe on every platform.
    /// The path is hashed rather than escaped so length limits and case-folding
    /// filesystems cannot collide two different worktrees onto one draft.
    /// </summary>
    public static string KeyFor(string worktreePath)
    {
        var normalized = worktreePath.Replace('\\', '/').TrimEnd('/');
        var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash)[..16];
    }
}
