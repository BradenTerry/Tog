using Togue.Core.Git;
using Togue.Core.Model;

namespace Togue.App.Services;

/// <summary>
/// One worktree's uncommitted changes and what of them is staged.
/// </summary>
/// <remarks>
/// Source control lists these, and every open diff tab reads its sides again
/// when <see cref="Changed"/> says they moved, so a file staged in the panel is
/// staged everywhere and nothing reads the diff twice. Everything is called on
/// the circuit's thread, from event handlers, so there is no locking;
/// <see cref="Changed"/> is raised on it too.
/// </remarks>
public sealed class ChangesModel(
    string worktreePath,
    DiffReader diffs,
    Staging staging) : IDisposable
{
    private CancellationTokenSource? _load;

    public string WorktreePath { get; } = worktreePath;

    /// <summary>Raised whenever anything here moves.</summary>
    public event Action? Changed;

    /// <summary>Null until the first read.</summary>
    public DiffSet? Diff { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>Where each file's changes are, refreshed alongside the diff.</summary>
    public IReadOnlyDictionary<string, FileStage> Stages { get; private set; } = new Dictionary<string, FileStage>();

    public bool IsStaging { get; private set; }

    public string? StageError { get; private set; }

    public FileStage StageOf(string path) => Stages.TryGetValue(path, out var stage) ? stage : default;

    public async Task Load()
    {
        // A refresh while a read is in flight would otherwise race, and the
        // slower one would win and show a stale diff.
        if (_load is not null)
        {
            await _load.CancelAsync();
            _load.Dispose();
        }

        _load = new CancellationTokenSource();
        var token = _load.Token;

        Loading = true;
        Raise();

        try
        {
            var result = await diffs.ReadAsync(WorktreePath, DiffBase.WorkingTree, ct: token);
            if (!token.IsCancellationRequested)
            {
                Diff = result;
                await ReadStages();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                Loading = false;
                Raise();
            }
        }
    }

    public Task Stage(IReadOnlyList<string> paths) => Restage(() => staging.StageAsync(WorktreePath, paths));

    public Task Unstage(IReadOnlyList<string> paths) => Restage(() => staging.UnstageAsync(WorktreePath, paths));

    public Task StageAll() => Restage(() => staging.StageAllAsync(WorktreePath));

    /// <summary>
    /// Throws away the working tree's changes to these files, deleting new ones.
    /// Unlike a stage this changes lines, so the diff is read again after.
    /// </summary>
    public async Task DiscardChanges(IReadOnlyList<string> paths)
    {
        await Restage(() => staging.DiscardAsync(WorktreePath, paths));
        await Load();
    }

    public Task UnstageAll() => Restage(() => staging.UnstageAllAsync(WorktreePath));

    /// <summary>
    /// Runs a staging change and re-reads where things are.
    /// </summary>
    /// <remarks>
    /// Only the staging state is re-read, not the diff: the list is taken
    /// against HEAD and so holds staged and unstaged work together, which means
    /// moving a file between them does not change a single line of it.
    /// </remarks>
    private async Task Restage(Func<Task<GitResult>> action)
    {
        IsStaging = true;
        Raise();

        try
        {
            var result = await action();
            StageError = result.Ok ? null : result.Message;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            StageError = e.Message;
        }
        finally
        {
            IsStaging = false;
            await ReadStages();
            Raise();
        }
    }

    private async Task ReadStages()
    {
        try
        {
            Stages = await staging.ReadAsync(WorktreePath);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            StageError = e.Message;
        }
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        _load?.Cancel();
        _load?.Dispose();
    }
}
