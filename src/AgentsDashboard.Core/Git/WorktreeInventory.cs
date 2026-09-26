using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Platform;

namespace AgentsDashboard.Core.Git;

/// <summary>Which worktrees a refresh measures.</summary>
public enum Remeasure
{
    /// <summary>Only those never measured.</summary>
    Missing,

    /// <summary>Those never measured or measured longer ago than <see cref="WorktreeInventory.StaleAfter"/>.</summary>
    Stale,

    /// <summary>Every one.</summary>
    All,
}

/// <summary>
/// The measured facts of every worktree, kept between visits to the Worktrees
/// view and measured one at a time in the background.
/// </summary>
/// <remarks>
/// Measuring walks every file of a worktree, and a worktree with node_modules in
/// it is hundreds of thousands of files. So it never runs in the monitor's
/// one-second pass: only when the Worktrees view asks, only for worktrees whose
/// facts are missing or older than <see cref="StaleAfter"/>, and one worktree at
/// a time so it does not compete with the agents for the disk. Singleton, so two
/// windows share one measurement.
/// </remarks>
public sealed class WorktreeInventory(WorktreeCleanup cleanup, IClock clock)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, WorktreeFacts> _facts = new(StringComparer.Ordinal);
    private readonly Queue<(RepoView Repo, WorktreeInfo Worktree)> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _running;
    private string? _measuring;

    /// <summary>A measurement finished or started. Raised off the circuit.</summary>
    public event Action? Changed;

    public WorktreeFacts? Facts(string worktreePath)
    {
        lock (_gate)
        {
            return _facts.GetValueOrDefault(worktreePath);
        }
    }

    /// <summary>The worktree being measured now, if any.</summary>
    public string? Measuring
    {
        get
        {
            lock (_gate)
            {
                return _measuring;
            }
        }
    }

    /// <summary>Whether a worktree is waiting to be measured or being measured.</summary>
    public bool Pending(string worktreePath)
    {
        lock (_gate)
        {
            return _queued.Contains(worktreePath);
        }
    }

    /// <summary>
    /// Queues the worktrees of these repos that <paramref name="which"/> picks.
    /// The view asks for <see cref="Remeasure.Missing"/> on every snapshot, so a
    /// worktree created while it is open is measured, and for
    /// <see cref="Remeasure.Stale"/> only when it is opened: a tab left open in
    /// the background must not walk every worktree on a timer.
    /// </summary>
    public void Refresh(IEnumerable<RepoView> repos, Remeasure which)
    {
        var now = clock.Now;
        var queued = false;
        lock (_gate)
        {
            foreach (var repo in repos)
            {
                foreach (var view in repo.Worktrees)
                {
                    var path = view.Worktree.Path;
                    var due = which switch
                    {
                        Remeasure.All => true,
                        Remeasure.Stale => !_facts.TryGetValue(path, out var known) || now - known.MeasuredAt >= StaleAfter,
                        _ => !_facts.ContainsKey(path),
                    };

                    if (due && _queued.Add(path))
                    {
                        _queue.Enqueue((repo, view.Worktree));
                        queued = true;
                    }
                }
            }

            StartWorker();
        }

        if (queued)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Measures one worktree: again after something was done to it, or, with
    /// <see cref="Remeasure.Missing"/>, only if it never has been.
    /// </summary>
    public void Refresh(RepoView repo, WorktreeInfo worktree, Remeasure which = Remeasure.All)
    {
        lock (_gate)
        {
            if (which != Remeasure.All && _facts.ContainsKey(worktree.Path))
            {
                return;
            }

            if (_queued.Add(worktree.Path))
            {
                _queue.Enqueue((repo, worktree));
            }

            StartWorker();
        }

        Changed?.Invoke();
    }

    /// <summary>Drops what is known about a worktree that is gone.</summary>
    public void Forget(string worktreePath)
    {
        lock (_gate)
        {
            _facts.Remove(worktreePath);
        }

        Changed?.Invoke();
    }

    private void StartWorker()
    {
        // Set and cleared under the lock, not read off the task: a task that has
        // decided to stop is not complete yet, and work queued in that gap would
        // wait for the next refresh.
        if (!_running && _queue.Count > 0)
        {
            _running = true;
            _ = Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        var bases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        while (true)
        {
            RepoView repo;
            WorktreeInfo worktree;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out var next))
                {
                    _measuring = null;
                    _running = false;
                    return;
                }

                (repo, worktree) = next;
                _measuring = worktree.Path;
            }

            Changed?.Invoke();

            WorktreeFacts facts;
            try
            {
                if (!bases.TryGetValue(repo.Root, out var repoBases))
                {
                    var primary = repo.Worktrees.FirstOrDefault(w => w.Worktree.IsPrimary)?.Worktree.Branch;
                    repoBases = await cleanup.BasesAsync(repo.Root, primary).ConfigureAwait(false);
                    bases[repo.Root] = repoBases;
                }

                var others = repo.Worktrees
                    .Select(w => w.Worktree.Path)
                    .Where(p => !string.Equals(p, worktree.Path, StringComparison.Ordinal))
                    .ToList();

                facts = await cleanup.ReadAsync(worktree, repoBases, others, clock.Now).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // One worktree that cannot be read must not stop the rest.
                facts = new WorktreeFacts { MeasuredAt = clock.Now, Error = e.Message };
            }

            lock (_gate)
            {
                _facts[worktree.Path] = facts;
                _queued.Remove(worktree.Path);
            }

            Changed?.Invoke();
        }
    }
}
