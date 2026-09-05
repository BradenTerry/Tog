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

    /// <summary>One file per worktree holding its unsubmitted review.</summary>
    public string DraftsDir => Path.Combine(Root, "drafts");

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
