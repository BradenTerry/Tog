using System.Collections.Concurrent;

namespace Tog.Core.Repos;

/// <summary>
/// Paths with every symbolic link in them resolved, for comparing an agent's
/// folder with the worktrees git reports.
/// </summary>
/// <remarks>
/// Git reports a worktree by its real path, and an agent keeps the folder it was
/// started in as typed, so on macOS an agent in <c>/tmp/repo</c> sits in a
/// worktree git calls <c>/private/tmp/repo</c> and matched none. The agent's
/// folder is not rewritten: Claude finds a conversation by the exact folder it
/// started in, so resuming depends on it staying as it was. Only comparisons go
/// through here.
/// <para>
/// Cached, since the monitor compares every agent with every worktree each
/// tick. Only a path that exists is cached: one that does not yet may be created
/// later, as a worktree or a link.
/// </para>
/// </remarks>
public static class RealPaths
{
    private const int MaxLinks = 16;

    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string Resolve(string path)
    {
        if (path.Length == 0 || !Path.IsPathRooted(path))
        {
            return path;
        }

        if (Cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var real = Walk(Path.GetFullPath(path), 0);
        if (Directory.Exists(real) || File.Exists(real))
        {
            Cache[path] = real;
        }

        return real;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it, once both are resolved.</summary>
    public static bool IsUnder(string path, string root)
    {
        path = Resolve(path).TrimEnd(Path.DirectorySeparatorChar);
        root = Resolve(root).TrimEnd(Path.DirectorySeparatorChar);
        return root.Length > 0
            && (string.Equals(path, root, Comparison)
                || path.StartsWith(root + Path.DirectorySeparatorChar, Comparison));
    }

    /// <summary>
    /// Case-insensitive on Windows and macOS, where the filesystem is, so a
    /// drive letter written <c>c:</c> by one tool and <c>C:</c> by another still
    /// matches; ordinal on Linux. See <see cref="RepoDiscovery.PathComparer"/>.
    /// </summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Rebuilds the path a component at a time, replacing each link with where it
    /// points. A link's target can itself run through links, so it is walked
    /// again, up to a limit that stops a loop.
    /// </summary>
    private static string Walk(string path, int depth)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var real = root;
        try
        {
            foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                real = Path.Combine(real, part);
                if (depth < MaxLinks
                    && new DirectoryInfo(real) is { LinkTarget: not null } link
                    && link.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    real = Walk(target.FullName, depth + 1);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }

        return real;
    }
}
