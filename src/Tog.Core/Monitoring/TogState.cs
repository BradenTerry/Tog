using Tog.Core.Model;

namespace Tog.Core.Monitoring;

/// <summary>
/// The current picture, and a signal when it changes.
/// </summary>
/// <remarks>
/// One writer (the monitor) and many readers (every open view). Views subscribe
/// to <see cref="Changed"/> and re-read <see cref="Snapshot"/>; the snapshot is
/// immutable, so a view that is part way through rendering one is never handed a
/// half-updated model.
/// </remarks>
public sealed class TogState
{
    private volatile TogSnapshot _snapshot = new();

    /// <summary>Raised after a new snapshot is published. Runs on the monitor's thread.</summary>
    public event Action? Changed;

    public TogSnapshot Snapshot => _snapshot;

    public void Publish(TogSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke();
    }

    /// <summary>The worktree at this path, or null when it is not on screen.</summary>
    public WorktreeView? Worktree(string path) =>
        _snapshot.Repos
            .SelectMany(r => r.Worktrees)
            .FirstOrDefault(w => string.Equals(w.Worktree.Path, path, StringComparison.Ordinal));

    public RepoView? Repo(string root) =>
        _snapshot.Repos.FirstOrDefault(r => string.Equals(r.Root, root, StringComparison.Ordinal));
}
