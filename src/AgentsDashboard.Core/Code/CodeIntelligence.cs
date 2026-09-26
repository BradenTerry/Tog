using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AgentsDashboard.Core.Code;

/// <summary>
/// One warm Roslyn solution per worktree, and the queries the editor runs
/// against it.
/// </summary>
/// <remarks>
/// <para>
/// Loading is on demand: the first C# file opened in a worktree starts it, and
/// nothing loads for the worktrees on screen that are never browsed. A load
/// costs seconds and hundreds of megabytes, so doing it eagerly for every
/// watched worktree would be the most expensive thing the dashboard does.
/// </para>
/// <para>
/// The lock covers the per-worktree entry only. A <see cref="Solution"/> is
/// immutable, so a query takes its snapshot under the lock and then runs
/// outside it, and an edit arriving mid-query answers from the text the query
/// started with rather than blocking.
/// </para>
/// </remarks>
public sealed class CodeIntelligence(SolutionLoader loader)
{
    private sealed class Entry
    {
        public Solution? Solution { get; set; }

        public LoadStatus Status { get; set; } = new(LoadState.NotLoaded, null, 0, 0);

        public Task? Loading { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Raised with the worktree path whenever its load state changes.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Where a worktree's solution is, for the chip in the editor.</summary>
    public LoadStatus Status(string worktreePath)
    {
        var key = Key(worktreePath);

        lock (_gate)
        {
            return EntryFor(key).Status;
        }
    }

    /// <summary>
    /// Start the load if it has not run, or join the one in flight. Returns at
    /// once when the solution is ready, and a failed load is retried rather than
    /// remembered forever: the usual cause is a project that did not build yet.
    /// </summary>
    public Task EnsureLoadedAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var key = Key(worktreePath);
        Task task;

        lock (_gate)
        {
            var entry = EntryFor(key);

            switch (entry.Status.State)
            {
                case LoadState.Ready:
                    return Task.CompletedTask;
                case LoadState.Loading when entry.Loading is not null:
                    return entry.Loading;
            }

            entry.Status = new LoadStatus(LoadState.Loading, "Loading...", 0, 0);
            entry.Loading = task = LoadAsync(key, entry, cancellationToken);
        }

        StatusChanged?.Invoke(key);

        return task;
    }

    /// <summary>Drop the solution and load it again, for when a project file changed.</summary>
    public Task ReloadAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var key = Key(worktreePath);

        lock (_gate)
        {
            var entry = EntryFor(key);
            entry.Solution = null;
            entry.Loading = null;
            entry.Status = new LoadStatus(LoadState.NotLoaded, null, 0, 0);
        }

        StatusChanged?.Invoke(key);

        return EnsureLoadedAsync(key, cancellationToken);
    }

    /// <summary>
    /// Push unsaved editor text into the solution, so references and hovers
    /// answer for the buffer rather than for what is on disk. A no-op when the
    /// worktree is not loaded or the file is not part of the solution.
    /// </summary>
    public void UpdateDocument(string worktreePath, string relativeFile, string text)
    {
        var key = Key(worktreePath);

        lock (_gate)
        {
            var entry = EntryFor(key);
            if (entry.Solution is null)
            {
                return;
            }

            var id = DocumentIdFor(entry.Solution, Absolute(key, relativeFile));
            if (id is not null)
            {
                entry.Solution = entry.Solution.WithDocumentText(id, SourceText.From(text));
            }
        }
    }

    /// <summary>
    /// Re-read one file from disk into the solution, after a save or after an
    /// agent edited it. The rest of the solution stays as it was; rebuilding
    /// everything is what <see cref="ReloadAsync"/> is for.
    /// </summary>
    public void RefreshDocumentFromDisk(string worktreePath, string relativeFile)
    {
        var key = Key(worktreePath);
        var path = Absolute(key, relativeFile);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        UpdateDocument(key, relativeFile, text);
    }

    /// <summary>What the symbol at a position is.</summary>
    public async Task<HoverInfo?> HoverAsync(
        string worktreePath,
        string relativeFile,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var (solution, key) = await SolutionForAsync(worktreePath, cancellationToken).ConfigureAwait(false);

        return solution is null
            ? null
            : await CodeQueries
                .HoverAsync(solution, key, Absolute(key, relativeFile), line, column, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Where the symbol at a position is declared.</summary>
    public async Task<IReadOnlyList<CodeLocation>> DefinitionAsync(
        string worktreePath,
        string relativeFile,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var (solution, key) = await SolutionForAsync(worktreePath, cancellationToken).ConfigureAwait(false);

        return solution is null
            ? []
            : await CodeQueries
                .DefinitionAsync(solution, key, Absolute(key, relativeFile), line, column, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Every use of the symbol at a position.</summary>
    public async Task<IReadOnlyList<CodeLocation>> ReferencesAsync(
        string worktreePath,
        string relativeFile,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var (solution, key) = await SolutionForAsync(worktreePath, cancellationToken).ConfigureAwait(false);

        return solution is null
            ? []
            : await CodeQueries
                .ReferencesAsync(solution, key, Absolute(key, relativeFile), line, column, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Callers and callees of the method at a position.</summary>
    public async Task<CallHierarchy?> CallHierarchyAsync(
        string worktreePath,
        string relativeFile,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var (solution, key) = await SolutionForAsync(worktreePath, cancellationToken).ConfigureAwait(false);

        return solution is null
            ? null
            : await CodeQueries
                .CallHierarchyAsync(solution, key, Absolute(key, relativeFile), line, column, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// The loaded solution for a worktree, loading it first. Null when the load
    /// failed, which is how every query answers with nothing instead of throwing
    /// the load error again at each keystroke.
    /// </summary>
    private async Task<(Solution? Solution, string Key)> SolutionForAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var key = Key(worktreePath);

        try
        {
            await EnsureLoadedAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (Status(key).State == LoadState.Failed)
        {
            return (null, key);
        }

        lock (_gate)
        {
            return (EntryFor(key).Solution, key);
        }
    }

    private async Task LoadAsync(string key, Entry entry, CancellationToken cancellationToken)
    {
        // Off the caller's thread: the first query for a worktree should not wait
        // on MSBuild before it can even report that loading started.
        await Task.Yield();

        var progress = new Progress<string>(message => SetStatus(key, entry, s =>
            s.State == LoadState.Loading ? s with { Message = message } : s));

        try
        {
            var solution = await loader.LoadAsync(key, progress, cancellationToken).ConfigureAwait(false);
            var projects = solution.Projects.Count();
            var documents = solution.Projects.Sum(p => p.DocumentIds.Count);

            SetStatus(key, entry, _ =>
            {
                entry.Solution = solution;

                return new LoadStatus(LoadState.Ready, null, projects, documents);
            });
        }
        catch (Exception ex)
        {
            SetStatus(key, entry, _ =>
            {
                entry.Solution = null;

                return new LoadStatus(LoadState.Failed, ex.Message, 0, 0);
            });

            throw;
        }
    }

    private void SetStatus(string key, Entry entry, Func<LoadStatus, LoadStatus> next)
    {
        lock (_gate)
        {
            var updated = next(entry.Status);
            if (updated == entry.Status)
            {
                return;
            }

            entry.Status = updated;
        }

        StatusChanged?.Invoke(key);
    }

    private Entry EntryFor(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = entry = new Entry();
        }

        return entry;
    }

    private static DocumentId? DocumentIdFor(Solution solution, string path) =>
        solution.GetDocumentIdsWithFilePath(path).FirstOrDefault();

    private static string Key(string worktreePath) =>
        Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar);

    private static string Absolute(string worktreePath, string relativeFile) =>
        Path.GetFullPath(Path.Combine(worktreePath, relativeFile.Replace('/', Path.DirectorySeparatorChar)));
}
