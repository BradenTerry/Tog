using Tog.Core.Git;
using Tog.Core.Model;
using Tog.Core.Monitoring;

namespace Tog.App.Services;

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
    Staging staging,
    Commits commits,
    MonitorService monitor) : IDisposable
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

    /// <summary>
    /// The commit message being written. Kept here rather than in the panel so
    /// a draft survives switching agents and back, as VS Code keeps it per
    /// repository.
    /// </summary>
    public string CommitMessage { get; set; } = "";

    /// <summary>A commit, undo, push or pull is running.</summary>
    public bool IsCommitting { get; private set; }

    public string? CommitError { get; private set; }

    public bool HasStaged => Stages.Values.Any(s => s.Staged);

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
    /// Commits what is staged with <see cref="CommitMessage"/>, which is
    /// cleared once it is in. With <paramref name="stageAll"/> everything is
    /// staged first, which is what VS Code offers when nothing is.
    /// </summary>
    public Task Commit(bool amend = false, bool stageAll = false, bool push = false) => RunGit(async () =>
    {
        if (stageAll)
        {
            var staged = await staging.StageAllAsync(WorktreePath);
            if (!staged.Ok)
            {
                return staged;
            }
        }

        var result = await commits.CommitAsync(WorktreePath, CommitMessage, amend);
        if (!result.Ok)
        {
            return result;
        }

        CommitMessage = "";
        return push ? await commits.PushAsync(WorktreePath) : result;
    });

    /// <summary>
    /// Takes the last commit back into Staged Changes, and its message back into
    /// the box when the box is empty, so undo and commit again is a round trip.
    /// </summary>
    public Task UndoLastCommit() => RunGit(async () =>
    {
        var message = await commits.LastMessageAsync(WorktreePath);
        var result = await commits.UndoLastAsync(WorktreePath);
        if (result.Ok && string.IsNullOrWhiteSpace(CommitMessage) && message is not null)
        {
            CommitMessage = message;
        }

        return result;
    });

    public Task Push() => RunGit(() => commits.PushAsync(WorktreePath));

    public Task Pull() => RunGit(() => commits.PullAsync(WorktreePath));

    public void DismissCommitError()
    {
        CommitError = null;
        Raise();
    }

    /// <summary>
    /// Runs a commit, undo, push or pull, then reads everything again: each one
    /// can move files out of the list or into it, and the push and pull counts.
    /// </summary>
    private async Task RunGit(Func<Task<GitResult>> action)
    {
        if (IsCommitting)
        {
            return;
        }

        IsCommitting = true;
        CommitError = null;
        Raise();

        try
        {
            var result = await action();
            CommitError = result.Ok ? null : result.Message.Trim();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            CommitError = e.Message;
        }
        finally
        {
            IsCommitting = false;
            monitor.InvalidateStatus(WorktreePath);
            await Load();
        }
    }

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
