using AgentsDashboard.Core.Platform;

namespace AgentsDashboard.Core.Git;

/// <summary>
/// Tells you, after a quiet moment, which files in a worktree have changed on
/// disk, so the diff and the open files follow an agent's edits without a
/// refresh.
/// </summary>
/// <remarks>
/// <para>
/// The monitor's git status is not enough for this. It counts files, and an
/// agent's second edit to a file already modified changes no count at all.
/// </para>
/// <para>
/// Changes are gathered until the worktree has been quiet for a moment and then
/// reported once, as a set: an agent writing a file is several events, and a
/// build or a checkout is thousands, and each report costs a diff read.
/// </para>
/// <para>
/// A linked worktree's <c>.git</c> is a file naming its git directory elsewhere,
/// under the main repository's <c>.git/worktrees</c>. The index and HEAD live
/// there, and they are what move when the agent stages or commits, so that
/// directory is watched as well. Inside any git directory only those two
/// matter; objects and logs change on every command and mean nothing here.
/// </para>
/// </remarks>
public sealed class WorktreeChanges : IDisposable
{
    /// <summary>How long the worktree has to be quiet before the gathered changes are reported.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    /// <summary>Folders whose churn is build output or tooling, not work anyone reviews.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".vs", ".idea",
    };

    private readonly string _root;
    private readonly Action<IReadOnlySet<string>> _changed;
    private readonly List<PathWatcher> _watchers = [];
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Timer _timer;
    private bool _disposed;

    /// <summary>
    /// Starts watching. <paramref name="changed"/> is called on a pool thread
    /// with worktree-relative paths, forward slashes; a change to the index or
    /// HEAD is reported as <c>.git</c>.
    /// </summary>
    public WorktreeChanges(string worktreePath, Action<IReadOnlySet<string>> changed)
    {
        _root = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar);
        _changed = changed;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        Add(_root, recursive: true);
        if (GitDirectoryOf(_root) is { } gitDir && !IsInside(gitDir, _root))
        {
            Add(gitDir, recursive: false);
        }
    }

    /// <summary>
    /// Whether a changed path is worth reporting, and as what: a worktree path, or
    /// <c>.git</c> for the index or HEAD. Null for anything else.
    /// </summary>
    public static string? Classify(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || relative == ".")
        {
            // Outside the worktree: the linked git directory, whose files are
            // taken by name.
            var name = Path.GetFileName(fullPath);
            return name is "index" or "HEAD" ? ".git" : null;
        }

        var segments = relative.Split('/');
        if (segments[0] == ".git")
        {
            return segments.Length == 2 && segments[1] is "index" or "HEAD" ? ".git" : null;
        }

        return segments.Any(Ignored.Contains) ? null : relative;
    }

    /// <summary>The git directory of a linked worktree, from the "gitdir:" line its .git file holds.</summary>
    public static string? GitDirectoryOf(string worktreePath)
    {
        var dotGit = Path.Combine(worktreePath, ".git");
        if (!File.Exists(dotGit))
        {
            return null;
        }

        try
        {
            var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (line is null)
            {
                return null;
            }

            var path = line["gitdir:".Length..].Trim();
            var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(worktreePath, path));
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Add(string path, bool recursive)
    {
        try
        {
            // A lost event is handled as "everything".
            _watchers.Add(PathWatcher.Watch(path, recursive, Note, () => Mark(".git")));
        }
        catch (IOException)
        {
            // A worktree that was removed. The refresh button still works.
        }
    }

    private void Note(string fullPath)
    {
        if (Classify(_root, fullPath) is { } relative)
        {
            Mark(relative);
        }
    }

    private void Mark(string relative)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending.Add(relative);
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        HashSet<string> changed;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            changed = new HashSet<string>(_pending, StringComparer.Ordinal);
            _pending.Clear();
        }

        _changed(changed);
    }

    private static bool IsInside(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _timer.Dispose();
    }
}
