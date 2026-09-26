using AgentsDashboard.Core.Claude;
using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Platform;
using AgentsDashboard.Core.Repos;

namespace AgentsDashboard.Core.Monitoring;

/// <summary>
/// The one loop that keeps the dashboard current.
/// </summary>
/// <remarks>
/// <para>
/// Three cadences, because the three sources cost wildly different amounts.
/// Reading the session registry is a directory listing and a stat per file, so it
/// runs every second and agent status is effectively live. Listing worktrees and
/// reading git status spawn git, so they run on their own slower timers.
/// </para>
/// <para>
/// Git status is read only for worktrees a view has asked for through
/// <see cref="Watch"/>, which in practice is the one agent on screen. Nothing
/// shows the status of any other worktree, and reading every worktree of every
/// repository on a timer was a steady stream of git processes whose output was
/// thrown away.
/// </para>
/// <para>
/// It is also one loop rather than several, so nothing can interleave two
/// half-finished refreshes into one snapshot, and so the whole app has a single
/// place where its polling rate is decided.
/// </para>
/// </remarks>
public sealed class MonitorService : IAsyncDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WorktreeInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How stale a watched worktree's status may get. The status is also re-read
    /// whenever an agent in it changes state, which is when files usually change,
    /// so this only has to catch edits made outside an agent.
    /// </summary>
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(10);

    private readonly DashboardState _state;
    private readonly SettingsStore _settings;
    private readonly IAgentSessionSource _registry;
    private readonly TranscriptLocator _locator;
    private readonly TranscriptReader _transcripts;
    private readonly SubagentReader _subagents;
    private readonly RepoDiscovery _discovery;
    private readonly WorktreeLister _worktrees;
    private readonly StatusReader _status;
    private readonly INotifier _notifier;
    private readonly IClock _clock;
    private readonly WaitingWatch _waitingWatch = new();

    private readonly Dictionary<string, IReadOnlyList<WorktreeInfo>> _worktreeCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (GitStatusInfo? Status, DateTimeOffset At)> _statusCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _repoNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentStatus> _lastStatus = new(StringComparer.Ordinal);

    /// <summary>Worktree paths views are showing, counted, since two windows can show one agent.</summary>
    private readonly Dictionary<string, int> _watched = new(StringComparer.Ordinal);
    private readonly Lock _watchGate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset _worktreesRefreshedAt = DateTimeOffset.MinValue;
    private IReadOnlyList<string> _repoRoots = [];

    public MonitorService(
        DashboardState state,
        SettingsStore settings,
        IAgentSessionSource registry,
        TranscriptLocator locator,
        TranscriptReader transcripts,
        SubagentReader subagents,
        RepoDiscovery discovery,
        WorktreeLister worktrees,
        StatusReader status,
        INotifier notifier,
        IClock? clock = null)
    {
        _state = state;
        _settings = settings;
        _registry = registry;
        _locator = locator;
        _transcripts = transcripts;
        _subagents = subagents;
        _discovery = discovery;
        _worktrees = worktrees;
        _status = status;
        _notifier = notifier;
        _clock = clock ?? new SystemClock();
    }

    /// <summary>Set when the last pass threw, so the UI can say the loop is unhealthy.</summary>
    public string? LastError { get; private set; }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Force the next pass to re-list worktrees rather than reuse the cache.</summary>
    public void InvalidateWorktrees() => _worktreesRefreshedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Keep the git status of a worktree current until the returned handle is
    /// disposed. Only watched worktrees carry a status in the snapshot.
    /// </summary>
    public IDisposable Watch(string worktreePath)
    {
        lock (_watchGate)
        {
            _watched[worktreePath] = _watched.GetValueOrDefault(worktreePath) + 1;
        }

        return new Watcher(this, worktreePath);
    }

    private void Unwatch(string worktreePath)
    {
        lock (_watchGate)
        {
            if (_watched.TryGetValue(worktreePath, out var count) && count > 1)
            {
                _watched[worktreePath] = count - 1;
            }
            else
            {
                _watched.Remove(worktreePath);
            }
        }
    }

    private sealed class Watcher(MonitorService owner, string path) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unwatch(path);
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
                LastError = null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // A failed pass must not stop the loop: the next one usually
                // succeeds, and a dashboard that quietly stopped updating is worse
                // than one that missed a tick.
                LastError = e.Message;
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var settings = _settings.Load();
        var sessions = _registry.Read();

        var live = sessions.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        _locator.Forget(live);
        _transcripts.Forget(live);

        var enriched = sessions.Select(Enrich).ToList();

        await RefreshWorktreesAsync(enriched, settings, ct).ConfigureAwait(false);

        await RefreshStatusAsync(enriched, ct).ConfigureAwait(false);

        var snapshot = Compose(enriched);
        _state.Publish(snapshot);

        if (settings.NotifyOnWaiting)
        {
            foreach (var agent in _waitingWatch.Take(snapshot.Waiting))
            {
                _notifier.AgentWaiting(agent);
            }
        }
        else
        {
            // Keep the watch's memory current so turning notifications back on does
            // not announce everything that is already blocked.
            _waitingWatch.Take(snapshot.Waiting);
        }
    }

    private AgentSession Enrich(AgentSession session)
    {
        var transcript = _locator.Locate(session.SessionId, session.Cwd);
        var facts = transcript is null
            ? new TranscriptFacts(null, [], null, null)
            : _transcripts.Read(session.SessionId, transcript);

        // Background work started before the current agent process began was
        // killed with the one before it, and nothing records that: no notice for
        // a command, a leftover file for a subagent. Left in, it would read as
        // running forever.
        var since = session.ProcessStartedAt ?? DateTimeOffset.MinValue;
        return session with
        {
            Summary = facts.Summary,
            LastPrompt = facts.LastPrompt,
            LastReply = facts.LastReply,
            Skills = facts.Skills,
            Subagents = _subagents.Read(session.SessionId, session.Cwd).Where(s => s.StartedAt >= since).ToList(),
            BackgroundCommands = (facts.BackgroundCommands ?? []).Where(c => c.StartedAt >= since).ToList(),
        };
    }

    private async Task RefreshWorktreesAsync(
        IReadOnlyList<AgentSession> sessions,
        Settings settings,
        CancellationToken ct)
    {
        var due = _clock.Now - _worktreesRefreshedAt >= WorktreeInterval;

        // A session working somewhere that is not one of the worktrees we know
        // about means a worktree was created since the last listing, which is
        // exactly what `claude -w` does. Re-list immediately rather than leaving
        // the agent unplaced for up to the poll interval.
        var known = _worktreeCache.Values.SelectMany(w => w).Select(w => w.Path).ToList();
        var unplaced = sessions.Any(s => !known.Any(k => IsUnder(s.Cwd, k)));

        if (!due && !unplaced)
        {
            return;
        }

        _worktreesRefreshedAt = _clock.Now;
        _repoRoots = await _discovery.DiscoverAsync(sessions, settings, ct).ConfigureAwait(false);

        foreach (var stale in _worktreeCache.Keys.Where(k => !_repoRoots.Contains(k, StringComparer.Ordinal)).ToList())
        {
            _worktreeCache.Remove(stale);
            _repoNames.Remove(stale);
        }

        foreach (var root in _repoRoots)
        {
            ct.ThrowIfCancellationRequested();
            _worktreeCache[root] = await _worktrees.ListAsync(root, ct).ConfigureAwait(false);
            _repoNames[root] = RepoDiscovery.NameOf(root);
        }
    }

    /// <summary>
    /// Re-reads git status for the watched worktrees: on a timer, and at once when
    /// an agent working in one changes state, since a turn ending is when its
    /// edits land.
    /// </summary>
    private async Task RefreshStatusAsync(IReadOnlyList<AgentSession> sessions, CancellationToken ct)
    {
        HashSet<string> watched;
        lock (_watchGate)
        {
            watched = _watched.Keys.ToHashSet(StringComparer.Ordinal);
        }

        var moved = sessions
            .Where(s => _lastStatus.TryGetValue(s.SessionId, out var was) && was != s.Status)
            .Select(s => s.Cwd)
            .ToList();

        _lastStatus.Clear();
        foreach (var session in sessions)
        {
            _lastStatus[session.SessionId] = session.Status;
        }

        var now = _clock.Now;

        foreach (var worktree in _worktreeCache.Values.SelectMany(w => w))
        {
            ct.ThrowIfCancellationRequested();

            if (!watched.Contains(worktree.Path))
            {
                continue;
            }

            var fresh = _statusCache.TryGetValue(worktree.Path, out var cached) && now - cached.At < StatusInterval;
            if (fresh && !moved.Any(cwd => IsUnder(cwd, worktree.Path)))
            {
                continue;
            }

            _statusCache[worktree.Path] = (
                await _status.ReadAsync(worktree.Path, ct).ConfigureAwait(false),
                now);
        }

        foreach (var gone in _statusCache.Keys.Where(k => !watched.Contains(k)).ToList())
        {
            _statusCache.Remove(gone);
        }
    }

    private DashboardSnapshot Compose(IReadOnlyList<AgentSession> sessions)
    {
        var repos = new List<RepoView>(_repoRoots.Count);
        var waiting = new List<WaitingAgent>();

        foreach (var root in _repoRoots)
        {
            if (!_worktreeCache.TryGetValue(root, out var infos))
            {
                continue;
            }

            var repoName = _repoNames.GetValueOrDefault(root, RepoDiscovery.NameOf(root));
            var views = new List<WorktreeView>(infos.Count);

            foreach (var info in infos)
            {
                // Longest match wins, so a session inside a nested worktree lands on
                // that worktree rather than on the repo root it also sits under.
                var agents = sessions
                    .Where(s => Owner(s.Cwd, infos) == info.Path)
                    .OrderBy(s => s.StartedAt)
                    .ToList();

                views.Add(new WorktreeView
                {
                    Worktree = info,
                    RepoRoot = root,
                    Status = _statusCache.GetValueOrDefault(info.Path).Status,
                    Agents = agents,
                });

                waiting.AddRange(agents
                    .Where(a => a.Status == AgentStatus.Waiting)
                    .Select(a => new WaitingAgent(a, root, repoName, info.Path, info.Name, info.Branch)));
            }

            repos.Add(new RepoView { Root = root, Name = repoName, Worktrees = views });
        }

        // Longest-blocked first: the agent that has been stuck the longest is the
        // one costing the most, and it is the one a glance should land on.
        waiting.Sort((a, b) => a.Session.StatusSince.CompareTo(b.Session.StatusSince));

        return new DashboardSnapshot
        {
            Repos = repos,
            Waiting = waiting,
            TakenAt = _clock.Now,
        };
    }

    private static string? Owner(string cwd, IReadOnlyList<WorktreeInfo> worktrees) =>
        worktrees
            .Where(w => IsUnder(cwd, w.Path))
            .MaxBy(w => w.Path.Length)
            ?.Path;

    private static bool IsUnder(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = root.EndsWith('/') || root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return path.StartsWith(prefix, StringComparison.Ordinal)
               || path.StartsWith(root + "/", StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _cts?.Dispose();
    }
}
