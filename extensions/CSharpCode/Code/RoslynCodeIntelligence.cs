using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AgentsDashboard.Extensions.CSharpCode;

/// <summary>
/// One warm Roslyn solution per worktree, and the queries the editor runs
/// against it.
/// </summary>
/// <remarks>
/// <para>
/// Loading is opt-in: nothing loads until the user asks for it in a worktree,
/// not even when a C# file is opened there. A load costs seconds and hundreds of
/// megabytes, most worktrees are only ever read as diffs, and navigation is
/// wanted for the few where a change needs tracing. Queries never start a load;
/// they answer with nothing until one has been asked for.
/// </para>
/// <para>
/// A load belongs to the worktree, not to whoever asked for it. It runs on a
/// token of its own that only <see cref="ReloadAsync"/> and <see cref="Unload"/>
/// cancel, and a caller's token only stops that caller waiting. Tying the load
/// to the first caller meant closing that editor, as switching agents does,
/// cancelled a load everyone else was sharing, left it failed, and the next
/// visit started it again from nothing.
/// </para>
/// <para>
/// The lock covers the per-worktree entry only. A <see cref="Solution"/> is
/// immutable, so a query takes its snapshot under the lock and then runs
/// outside it, and an edit arriving mid-query answers from the text the query
/// started with rather than blocking.
/// </para>
/// </remarks>
public sealed class RoslynCodeIntelligence(SolutionLoader loader) : ICodeIntelligence, IDisposable
{
    private sealed class Entry
    {
        public Solution? Solution { get; set; }

        public CodeLoadStatus Status { get; set; } = new(CodeLoadState.NotLoaded, null, 0, 0);

        public Task? Loading { get; set; }

        /// <summary>Cancelled when this load is superseded by a reload or an unload.</summary>
        public CancellationTokenSource? Lifetime { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Name => "Roslyn";

    /// <inheritdoc />
    public string Language => "csharp";

    /// <inheritdoc />
    public bool Handles(string relativePath) => relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Raised with the worktree path whenever its load state changes.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Where a worktree's solution is, for the chip in the editor.</summary>
    public CodeLoadStatus Status(string worktreePath)
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
    /// Cancelling <paramref name="cancellationToken"/> stops the wait, not the load.
    /// </summary>
    public Task LoadAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var key = Key(worktreePath);
        Task task;

        lock (_gate)
        {
            var entry = EntryFor(key);

            switch (entry.Status.State)
            {
                case CodeLoadState.Ready:
                    return Task.CompletedTask;
                case CodeLoadState.Loading when entry.Loading is not null:
                    return entry.Loading.WaitAsync(cancellationToken);
            }

            var lifetime = new CancellationTokenSource();
            entry.Lifetime = lifetime;
            entry.Status = new CodeLoadStatus(CodeLoadState.Loading, "Loading...", 0, 0);
            // Task.Run, not an await of Task.Yield inside: under the Blazor
            // circuit's synchronization context a yield posts straight back to
            // the window's one dispatcher, and MSBuild registration, workspace
            // creation and the solution search would all run there. Starting on
            // the pool also means the Progress below captures no context.
            entry.Loading = task = Task.Run(() => RunLoadAsync(key, entry, lifetime));
        }

        StatusChanged?.Invoke(key);

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>Drop the solution and load it again, for when a project file changed.</summary>
    public Task ReloadAsync(string worktreePath, CancellationToken cancellationToken)
    {
        Unload(worktreePath);

        return LoadAsync(worktreePath, cancellationToken);
    }

    /// <summary>
    /// Drop the solution and stop a load in flight, handing back the memory. The
    /// worktree goes back to not loaded, where queries answer with nothing.
    /// </summary>
    public void Unload(string worktreePath)
    {
        var key = Key(worktreePath);
        CancellationTokenSource? lifetime;

        lock (_gate)
        {
            var entry = EntryFor(key);
            lifetime = entry.Lifetime;
            entry.Lifetime = null;
            entry.Solution = null;
            entry.Loading = null;
            entry.Status = new CodeLoadStatus(CodeLoadState.NotLoaded, null, 0, 0);
        }

        // Cancelled, not disposed: the load it belonged to may still be reading
        // the token, and a source with no timer holds nothing worth freeing.
        lifetime?.Cancel();

        StatusChanged?.Invoke(key);
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
        await OffCaller();
        var (solution, key) = await SolutionForAsync(worktreePath, cancellationToken).ConfigureAwait(false);

        return solution is null
            ? null
            : await CodeQueries
                .HoverAsync(solution, key, Absolute(key, relativeFile), line, column, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// What kind of symbol every name in a file is, so the editor can colour
    /// types, methods and locals apart. Empty until the solution is loaded.
    /// </summary>
    public async Task<IReadOnlyList<ClassifiedRun>> ClassifyAsync(
        string worktreePath,
        string relativeFile,
        CancellationToken cancellationToken)
    {
        await OffCaller();
        var (solution, key) = await SolutionForAsync(worktreePath, cancellationToken).ConfigureAwait(false);

        return solution is null
            ? []
            : await CodeQueries
                .ClassifyAsync(solution, Absolute(key, relativeFile), cancellationToken)
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
        await OffCaller();
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
        await OffCaller();
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
        await OffCaller();
        var (solution, key) = await SolutionForAsync(worktreePath, cancellationToken).ConfigureAwait(false);

        return solution is null
            ? null
            : await CodeQueries
                .CallHierarchyAsync(solution, key, Absolute(key, relativeFile), line, column, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// The loaded solution for a worktree, waiting for a load in flight but never
    /// starting one. Null when nothing is loaded or the load failed, which is how
    /// every query answers with nothing instead of throwing the load error again
    /// at each keystroke.
    /// </summary>
    private async Task<(Solution? Solution, string Key)> SolutionForAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var key = Key(worktreePath);
        Task? loading;

        lock (_gate)
        {
            var entry = EntryFor(key);
            if (entry.Status.State != CodeLoadState.Loading)
            {
                return (entry.Solution, key);
            }

            loading = entry.Loading;
        }

        if (loading is not null)
        {
            try
            {
                await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Failed or unloaded; the entry says which, and either way there
                // is no solution to answer from.
            }
        }

        lock (_gate)
        {
            return (EntryFor(key).Solution, key);
        }
    }

    /// <summary>
    /// Leaves the caller's thread before a query does any work. Every await
    /// below is ConfigureAwait(false), but that only helps once something
    /// actually yields: with the solution ready, SolutionForAsync and a cached
    /// document text complete synchronously, and classifying a whole file would
    /// then run on whatever thread asked, which in the app is the window's.
    /// Task.Yield would not do it either, since it posts back to the caller's
    /// synchronization context. ForceYielding without the captured context always
    /// queues the rest to the thread pool.
    /// </summary>
    private static ConfiguredTaskAwaitable OffCaller() =>
        Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

    private async Task RunLoadAsync(string key, Entry entry, CancellationTokenSource lifetime)
    {
        var token = lifetime.Token;

        var progress = new Progress<string>(message => SetStatus(key, entry, lifetime, s =>
            s.State == CodeLoadState.Loading ? s with { Message = message } : s));

        try
        {
            var solution = await loader.LoadAsync(key, progress, token).ConfigureAwait(false);
            var projects = solution.Projects.Count();
            var documents = solution.Projects.Sum(p => p.DocumentIds.Count);

            SetStatus(key, entry, lifetime, _ =>
            {
                entry.Solution = solution;

                return new CodeLoadStatus(CodeLoadState.Ready, null, projects, documents);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a reload or an unload, which has already set the entry.
            throw;
        }
        catch (Exception ex)
        {
            SetStatus(key, entry, lifetime, _ =>
            {
                entry.Solution = null;

                return new CodeLoadStatus(CodeLoadState.Failed, ex.Message, 0, 0);
            });

            throw;
        }
    }

    /// <summary>
    /// Applies a load's outcome, unless the load has been superseded: a load
    /// that finishes after an unload or a reload must not put its solution back.
    /// </summary>
    private void SetStatus(string key, Entry entry, CancellationTokenSource lifetime, Func<CodeLoadStatus, CodeLoadStatus> next)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(entry.Lifetime, lifetime))
            {
                return;
            }

            var updated = next(entry.Status);
            if (updated == entry.Status)
            {
                return;
            }

            entry.Status = updated;
        }

        StatusChanged?.Invoke(key);
    }

    /// <summary>
    /// The extension is unloading: stop every load in flight and let the
    /// solutions go with it.
    /// </summary>
    public void Dispose()
    {
        List<CancellationTokenSource> lifetimes;

        lock (_gate)
        {
            lifetimes = [.. _entries.Values.Select(e => e.Lifetime).OfType<CancellationTokenSource>()];
            _entries.Clear();
        }

        foreach (var lifetime in lifetimes)
        {
            lifetime.Cancel();
        }
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
