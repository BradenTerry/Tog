using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Repos;

/// <summary>Which repositories the dashboard shows.</summary>
/// <remarks>
/// Two sources, unioned. Live sessions are the interesting one: an agent
/// started anywhere reveals its repository without the user configuring
/// anything, which is what makes the dashboard useful the first time it opens.
/// The stored list covers the rest, including a repo whose agents have all
/// finished but which you still want on screen.
/// </remarks>
public sealed class RepoDiscovery(WorktreeLister lister)
{
    /// <summary>
    /// Repository roots for the given sessions plus the stored ones, canonicalized
    /// to the main working tree so every worktree of one repo lands under it.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverAsync(
        IReadOnlyList<AgentSession> sessions,
        Settings settings,
        CancellationToken ct = default)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(PathComparer);

        // Distinct cwds first: several agents usually share a worktree, and every
        // resolution is two git calls.
        var candidates = sessions
            .Select(s => s.Cwd)
            .Concat(settings.RepoRoots)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(PathComparer)
            .ToList();

        var resolved = new Dictionary<string, string?>(PathComparer);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!resolved.TryGetValue(candidate, out var root))
            {
                root = await lister.FindPrimaryRootAsync(candidate, ct).ConfigureAwait(false);
                resolved[candidate] = root;
            }

            if (root is null || IsHidden(root, settings))
            {
                continue;
            }

            if (seen.Add(Normalize(root)))
            {
                roots.Add(root);
            }
        }

        roots.Sort((a, b) => string.Compare(NameOf(a), NameOf(b), StringComparison.OrdinalIgnoreCase));
        return roots;
    }

    private static bool IsHidden(string root, Settings settings) =>
        settings.HiddenRoots.Any(h => PathComparer.Equals(Normalize(h), Normalize(root)));

    public static string NameOf(string root) =>
        Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, '/')) is { Length: > 0 } n ? n : root;

    /// <summary>
    /// Trailing separators removed and backslashes folded, so the same directory
    /// written two ways compares equal.
    /// </summary>
    public static string Normalize(string path) =>
        path.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Case-insensitive on Windows and macOS, where the filesystem is; ordinal on
    /// Linux, where two paths differing only in case really are two directories.
    /// </summary>
    public static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
