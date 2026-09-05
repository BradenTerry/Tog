using System.Collections.Concurrent;
using System.Text;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Platform;

namespace AgentsDashboard.Core.Testing;

/// <summary>
/// Follows .NET test runs in every watched worktree, live.
/// </summary>
/// <remarks>
/// <para>
/// Two signals, because neither is enough alone. The report file is the
/// authority on results, and since Microsoft.Testing.Platform 2.3.0 it is
/// written as the run progresses rather than at the end, which turns it into a
/// live feed. But it says nothing during restore and build, which on a real
/// solution is most of the wall clock. The process table covers exactly that
/// gap, so a run appears the moment the agent starts it.
/// </para>
/// <para>
/// A classic VSTest project writes its TRX once, at the end. Nothing here has to
/// know the difference: the same code reads it, the results just all arrive in
/// one pass. What the user sees differs, so the Tests view says which kind of
/// project it is rather than leaving a still progress bar unexplained.
/// </para>
/// </remarks>
public sealed class TestRunTracker(IClock? clock = null) : IDisposable
{
    /// <summary>Runs kept per worktree. Older ones are dropped, not archived.</summary>
    private const int HistoryPerWorktree = 20;

    /// <summary>How long a run with no process and no writes waits before it is called aborted.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How often the watched worktrees are walked for report files, on top of the
    /// file watcher.
    /// </summary>
    /// <remarks>
    /// The watcher is the fast path and usually the only one that fires, but it is
    /// allowed to drop events: its buffer can overflow under a busy build, and on
    /// network and container-mounted filesystems it may see nothing at all. A run
    /// the dashboard silently failed to notice is the one failure mode that makes
    /// the whole feature untrustworthy, so a cheap periodic walk backs it up.
    /// </remarks>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    private readonly IClock _clock = clock ?? new SystemClock();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunState> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.Ordinal);
    private readonly List<string> _worktrees = [];

    private bool _disposed;
    private DateTimeOffset _lastScan = DateTimeOffset.MinValue;

    /// <summary>Raised when a run appeared or changed. Fired off the UI thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Point the tracker at a set of worktrees. Watchers for worktrees no longer
    /// in the list are torn down, and their runs are dropped: a worktree that has
    /// gone is not one whose test history is worth keeping.
    /// </summary>
    public void SetWorktrees(IReadOnlyList<string> worktreePaths)
    {
        lock (_gate)
        {
            var wanted = new HashSet<string>(worktreePaths, StringComparer.Ordinal);

            foreach (var gone in _watchers.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                _watchers[gone].Dispose();
                _watchers.Remove(gone);

                foreach (var run in _runs.Where(r => r.Value.WorktreePath == gone).Select(r => r.Key).ToList())
                {
                    _runs.Remove(run);
                }
            }

            foreach (var worktree in wanted.Where(w => !_watchers.ContainsKey(w)))
            {
                StartWatching(worktree);
            }

            _worktrees.Clear();
            _worktrees.AddRange(wanted);
        }
    }

    /// <summary>Runs for one worktree, most recent first.</summary>
    public IReadOnlyList<TestRun> RunsFor(string worktreePath)
    {
        lock (_gate)
        {
            // Most recently active first. Ordering by the run's own start time
            // would put a report whose clock disagrees with this machine's in the
            // wrong place; "what moved last" cannot be wrong.
            return _runs.Values
                .Where(r => string.Equals(r.WorktreePath, worktreePath, StringComparison.Ordinal))
                .OrderByDescending(r => r.LastUpdated)
                .Take(HistoryPerWorktree)
                .Select(r => r.ToModel())
                .ToList();
        }
    }

    /// <summary>
    /// One pass: re-read what changed, look for test processes, and retire runs
    /// that stopped without finishing. Driven by the monitor's tick rather than a
    /// timer of its own, so all the polling in the app shares one cadence.
    /// </summary>
    public void Poll(ITestProcessScanner scanner)
    {
        var changed = false;
        List<string> worktrees;

        lock (_gate)
        {
            worktrees = [.. _worktrees];
        }

        var running = scanner.Scan(worktrees);

        lock (_gate)
        {
            if (_clock.Now - _lastScan >= ScanInterval)
            {
                _lastScan = _clock.Now;
                foreach (var found in worktrees.SelectMany(TrxLocator.Find))
                {
                    _dirty[found] = 0;
                }
            }

            // Anything a watcher flagged, plus every run still believed to be in
            // flight: a file being appended to can coalesce its change events, so
            // an active run is always re-read rather than waited on.
            var toRead = new HashSet<string>(_dirty.Keys, StringComparer.Ordinal);
            _dirty.Clear();

            foreach (var run in _runs.Values.Where(r => !r.Completed && r.TrxPath is not null))
            {
                toRead.Add(run.TrxPath!);
            }

            foreach (var path in toRead)
            {
                changed |= ReadTrx(path);
            }

            changed |= ReconcileProcesses(running);
            changed |= RetireStalled();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private void StartWatching(string worktreePath)
    {
        // Whatever is already there is read on the first poll; the periodic scan
        // and the watcher between them cover everything after that.
        foreach (var existing in TrxLocator.Find(worktreePath))
        {
            _dirty[existing] = 0;
        }

        try
        {
            var watcher = new FileSystemWatcher(worktreePath)
            {
                Filter = "*.trx",
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };

            watcher.Created += OnTrxEvent;
            watcher.Changed += OnTrxEvent;
            watcher.Renamed += OnTrxEvent;

            // A dropped event only delays a refresh, because every active run is
            // re-read on the tick anyway. Nothing to do but keep watching.
            watcher.Error += (_, _) => { };
            watcher.EnableRaisingEvents = true;

            _watchers[worktreePath] = watcher;
        }
        catch (Exception e) when (e is ArgumentException or FileNotFoundException or IOException)
        {
            // The worktree is gone or unreadable. It will be dropped from the list
            // on the next refresh.
        }
    }

    private void OnTrxEvent(object sender, FileSystemEventArgs e) => _dirty[e.FullPath] = 0;

    /// <summary>Reads new results out of one TRX. Returns whether anything moved.</summary>
    private bool ReadTrx(string trxPath)
    {
        var worktree = WorktreeFor(trxPath);
        if (worktree is null)
        {
            return false;
        }

        if (!_runs.TryGetValue(trxPath, out var state))
        {
            state = new RunState
            {
                TrxPath = trxPath,
                WorktreePath = worktree,
                ProjectName = TrxLocator.ProjectNameFrom(trxPath),
                StartedAt = CreationTime(trxPath) ?? _clock.Now,
                CreatedAt = _clock.Now,
                LastUpdated = _clock.Now,
            };

            _runs[trxPath] = state;
            Trim(worktree);
        }

        try
        {
            using var stream = new FileStream(
                trxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < state.ByteOffset)
            {
                // Rewritten in place by a new run. Start the run over rather than
                // merging two runs' results into one.
                state.Reset();
            }

            if (stream.Length == state.ByteOffset)
            {
                return false;
            }

            stream.Seek(state.ByteOffset, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - state.ByteOffset];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var chunk = Encoding.UTF8.GetString(buffer, 0, read);

            var scan = TrxParser.Scan(chunk);
            foreach (var result in scan.Results)
            {
                state.Upsert(result);
            }

            state.ByteOffset += Encoding.UTF8.GetByteCount(chunk[..Math.Min(scan.NextOffset, chunk.Length)]);
            state.DefinitionCount += scan.DefinitionCount;
            state.DeclaredTotal = scan.DeclaredTotal
                                  ?? (state.DefinitionCount > 0 ? state.DefinitionCount : state.DeclaredTotal);
            state.Completed |= scan.Completed;
            state.StartedAt = scan.StartedAt ?? state.StartedAt;

            // A read that found nothing is not movement. The offset stops just past
            // the last complete element, so a report always has a few bytes of
            // trailing whitespace left to re-read; treating that as activity would
            // keep a dead run looking alive forever.
            var moved = scan.Results.Count > 0 || scan.Completed || scan.DeclaredTotal is not null;
            if (!moved)
            {
                return false;
            }

            // Two different clocks, on purpose. The run's own start and the file's
            // last write describe the run, and are what the card shows, so a run
            // that finished before the dashboard opened reports its real duration
            // rather than the time since. Staleness is judged on our own clock,
            // because that is the one that says how long we have been waiting.
            state.FinishedAt = LastWrite(trxPath);
            state.LastUpdated = _clock.Now;

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Mid-write, or removed between the event and the read.
            return false;
        }
    }

    /// <summary>
    /// Reconciles the process scan: a worktree with a test process and no live run
    /// gets a placeholder run so the build phase is visible, and a run whose
    /// process has gone loses its pid.
    /// </summary>
    private bool ReconcileProcesses(IReadOnlyDictionary<string, int> running)
    {
        var changed = false;

        foreach (var (worktree, pid) in running)
        {
            var live = _runs.Values
                .Where(r => r.WorktreePath == worktree && !r.Completed)
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefault();

            if (live is not null)
            {
                if (live.Pid != pid)
                {
                    live.Pid = pid;
                    changed = true;
                }

                live.LastUpdated = _clock.Now;
                continue;
            }

            // No report yet: this is the restore-and-build stretch.
            var key = "process:" + worktree;
            if (_runs.ContainsKey(key))
            {
                continue;
            }

            _runs[key] = new RunState
            {
                TrxPath = null,
                WorktreePath = worktree,
                ProjectName = null,
                StartedAt = _clock.Now,
                CreatedAt = _clock.Now,
                LastUpdated = _clock.Now,
                Pid = pid,
            };

            Trim(worktree);
            changed = true;
        }

        foreach (var run in _runs.Values.Where(r => r.Pid is not null))
        {
            if (!running.ContainsKey(run.WorktreePath))
            {
                run.Pid = null;
                changed = true;
            }
        }

        // A placeholder whose real report has since appeared has done its job.
        // Judged on when this tracker first saw each run rather than on the times
        // inside the report, which come from whatever wrote it.
        foreach (var placeholder in _runs
                     .Where(kv => kv.Value.TrxPath is null)
                     .Where(kv => _runs.Values.Any(r =>
                         r.TrxPath is not null
                         && r.WorktreePath == kv.Value.WorktreePath
                         && r.CreatedAt >= kv.Value.CreatedAt))
                     .Select(kv => kv.Key)
                     .ToList())
        {
            _runs.Remove(placeholder);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// A run with no process and no writes for a while did not finish: the host
    /// crashed, or the agent killed it. Saying so beats a progress bar that never
    /// moves again.
    /// </summary>
    private bool RetireStalled()
    {
        var changed = false;
        var cutoff = _clock.Now - StallTimeout;

        foreach (var run in _runs.Values)
        {
            if (!run.Completed && !run.Aborted && run.Pid is null && run.LastUpdated < cutoff)
            {
                run.Aborted = true;
                changed = true;
            }
        }

        return changed;
    }

    private void Trim(string worktree)
    {
        var forWorktree = _runs
            .Where(kv => kv.Value.WorktreePath == worktree)
            .OrderByDescending(kv => kv.Value.StartedAt)
            .Skip(HistoryPerWorktree)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in forWorktree)
        {
            _runs.Remove(key);
        }
    }

    /// <summary>The watched worktree a path sits under, longest match first.</summary>
    private string? WorktreeFor(string path) =>
        _worktrees
            .Where(w => path.StartsWith(w + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        || path.StartsWith(w + "/", StringComparison.Ordinal))
            .MaxBy(w => w.Length);

    private static DateTimeOffset? LastWrite(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToLocalTime();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset? CreationTime(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetCreationTimeUtc(path)).ToLocalTime();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }

    private sealed class RunState
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly List<TestResultItem> _results = [];

        public required string? TrxPath { get; init; }
        public required string WorktreePath { get; init; }
        public string? ProjectName { get; init; }
        /// <summary>When the run says it began, for display.</summary>
        public required DateTimeOffset StartedAt { get; set; }

        /// <summary>When this tracker first saw the run, on our own clock.</summary>
        public required DateTimeOffset CreatedAt { get; init; }

        /// <summary>When the report was last written, for display.</summary>
        public DateTimeOffset? FinishedAt { get; set; }

        /// <summary>When this tracker last saw movement, on our own clock.</summary>
        public DateTimeOffset LastUpdated { get; set; }
        public long ByteOffset { get; set; }
        public int DefinitionCount { get; set; }
        public int? DeclaredTotal { get; set; }
        public bool Completed { get; set; }
        public bool Aborted { get; set; }
        public int? Pid { get; set; }

        /// <summary>
        /// Replaces a result with the same id rather than appending. A retried
        /// test writes a second element, and the run has one outcome for it, not
        /// two.
        /// </summary>
        public void Upsert(TestResultItem item)
        {
            if (_index.TryGetValue(item.TestId, out var at))
            {
                _results[at] = item;
                return;
            }

            _index[item.TestId] = _results.Count;
            _results.Add(item);
        }

        public void Reset()
        {
            _index.Clear();
            _results.Clear();
            ByteOffset = 0;
            DefinitionCount = 0;
            DeclaredTotal = null;
            Completed = false;
            Aborted = false;
        }

        public TestRun ToModel()
        {
            var failed = _results.Any(r => r.Outcome == TestOutcome.Failed);
            var state = Completed
                ? failed ? TestRunState.Failed : TestRunState.Passed
                : Aborted
                    ? TestRunState.Aborted
                    : TestRunState.Running;

            return new TestRun
            {
                Id = TrxPath ?? "process:" + WorktreePath,
                WorktreePath = WorktreePath,
                TrxPath = TrxPath,
                ProjectName = ProjectName,
                StartedAt = StartedAt,
                CompletedAt = Completed || Aborted ? FinishedAt ?? LastUpdated : null,
                LastUpdatedAt = FinishedAt ?? LastUpdated,
                State = state,
                Total = DeclaredTotal ?? 0,
                Results = _results.ToArray(),
                Pid = Pid,
            };
        }
    }
}
