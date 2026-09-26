using AgentsDashboard.Core.Agents;
using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Review;

namespace AgentsDashboard.App.Services;

/// <summary>
/// One worktree's diff, what is staged, and the review being drafted on it.
/// </summary>
/// <remarks>
/// Two components draw this: Source control in the right panel lists the files
/// and stages them, and the Changes document in the editor draws the diff that
/// is commented on. They used to be one tab with the state inside it. Held here
/// instead, a file staged on one side is staged on the other, and neither reads
/// the diff twice. Everything is called on the circuit's thread, from event
/// handlers, so there is no locking; <see cref="Changed"/> is raised on it too.
/// </remarks>
public sealed class ChangesModel(
    string worktreePath,
    DiffReader diffs,
    Staging staging,
    WorktreeViews views,
    ReviewDraftStore drafts,
    FeedbackDispatcher dispatcher,
    AgentHost host) : IDisposable
{
    /// <summary>The recipient that is not an agent yet: the review starts one in this worktree.</summary>
    public const string NewAgent = "new";

    private CancellationTokenSource? _load;
    private bool _pickedRecipient;

    public string WorktreePath { get; } = worktreePath;

    /// <summary>Raised whenever anything here moves.</summary>
    public event Action? Changed;

    public DiffBase Base { get; private set; } = views.For(worktreePath).Base;

    public string CustomRef { get; set; } = views.For(worktreePath).CustomRef;

    /// <summary>Null until the first read, and while a new base has no ref typed yet.</summary>
    public DiffSet? Diff { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>Where each file's changes are, refreshed alongside the diff.</summary>
    public IReadOnlyDictionary<string, FileStage> Stages { get; private set; } = new Dictionary<string, FileStage>();

    public bool IsStaging { get; private set; }

    public string? StageError { get; private set; }

    public ReviewDraft Draft { get; private set; } = drafts.Load(worktreePath);

    public bool Submitting { get; private set; }

    public ReviewSubmission? Submission { get; private set; }

    /// <summary>The session to hand the review to, <see cref="NewAgent"/>, or null for nobody.</summary>
    public string? Recipient { get; private set; }

    /// <summary>
    /// A file Source control asked the diff to show. Held rather than only raised,
    /// because the Changes document may not be built yet when the click lands.
    /// </summary>
    public string? PendingScroll { get; private set; }

    public FileStage StageOf(string path) => Stages.TryGetValue(path, out var stage) ? stage : default;

    public int CommentCount(string path) => Draft.Comments.Count(c => c.FilePath == path);

    public void RequestScroll(string path)
    {
        PendingScroll = path;
        Raise();
    }

    /// <summary>Taken by the diff once it has scrolled there.</summary>
    public string? TakeScroll()
    {
        var path = PendingScroll;
        PendingScroll = null;
        return path;
    }

    public async Task SetBase(DiffBase next)
    {
        Base = next;
        if (next != DiffBase.CustomRef || !string.IsNullOrWhiteSpace(CustomRef))
        {
            await Load();
        }
        else
        {
            Diff = null;
            Raise();
        }
    }

    public async Task Load()
    {
        // A base change while a read is in flight would otherwise race, and the
        // slower one would win and show the wrong diff.
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
            var result = await diffs.ReadAsync(WorktreePath, Base, CustomRef, token);
            if (!token.IsCancellationRequested)
            {
                // Recorded once the base has actually been compared against, not
                // as a ref is typed, so the editor never marks against half a
                // ref name.
                if (result.Error is null)
                {
                    views.SetBase(WorktreePath, Base, CustomRef);
                }

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
    /// Only the staging state is re-read, not the diff: the Uncommitted view is
    /// taken against HEAD and so shows staged and unstaged work together, which
    /// means moving a file between them does not change a single line of it.
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

    public string Summary
    {
        get => Draft.Summary ?? "";
        set
        {
            Draft = Draft with { Summary = value };
            drafts.Save(Draft);
            Raise();
        }
    }

    public void AddComment(ReviewComment comment)
    {
        Draft = Draft with { Comments = [.. Draft.Comments, comment] };
        drafts.Save(Draft);
        Raise();
    }

    public void RemoveComment(string id)
    {
        Draft = Draft with { Comments = Draft.Comments.Where(c => c.Id != id).ToArray() };
        drafts.Save(Draft);
        Raise();
    }

    public void Discard()
    {
        drafts.Clear(WorktreePath);
        Draft = new ReviewDraft { WorktreePath = WorktreePath };
        Raise();
    }

    public void SetRecipient(string? recipient)
    {
        _pickedRecipient = true;
        Recipient = recipient;
        Raise();
    }

    public void DismissSubmission()
    {
        Submission = null;
        Raise();
    }

    /// <summary>
    /// Who the review can be handed to: the agents working in this worktree,
    /// running or stopped. A stopped one is resumed with the review as its next
    /// instruction, which is often exactly what you want after reading its work.
    /// </summary>
    public IReadOnlyList<ReviewRecipient> Recipients =>
        host.Agents
            .Where(a => IsUnder(a.Cwd, WorktreePath))
            .Select(a => new ReviewRecipient(
                a.SessionId,
                a.Title ?? "Agent " + a.SessionId[..Math.Min(8, a.SessionId.Length)],
                a.State != HostedState.Stopped))
            .OrderByDescending(a => a.Running)
            .ToList();

    /// <summary>
    /// Picks who the review goes to, once. After that a refresh must not quietly
    /// move the review to a different agent.
    /// </summary>
    public void PickRecipient(string? preferred)
    {
        if (_pickedRecipient)
        {
            return;
        }

        _pickedRecipient = true;

        // An agent already working here is the obvious default. With none,
        // starting one on the review is more use than writing a file nobody
        // has been told about.
        var recipients = Recipients;
        Recipient = recipients.FirstOrDefault(r => r.SessionId == preferred)?.SessionId
            ?? recipients.FirstOrDefault()?.SessionId
            ?? NewAgent;
    }

    public async Task Submit()
    {
        Submitting = true;
        Raise();

        try
        {
            var submission = await dispatcher.SubmitAsync(Draft);
            drafts.Clear(WorktreePath);
            Draft = new ReviewDraft { WorktreePath = WorktreePath };
            Submission = await HandOver(submission);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Submission = new ReviewSubmission("", "", false, false, e.Message);
        }
        finally
        {
            Submitting = false;
            Raise();
        }
    }

    private async Task<ReviewSubmission> HandOver(ReviewSubmission submission)
    {
        if (Recipient == NewAgent)
        {
            var started = await host.StartAsync(new AgentStart(WorktreePath, Prompt: submission.Prompt, Title: "Review feedback"));
            return submission with { Sent = started.Ok, SendError = started.Ok ? null : started.Message };
        }

        if (Recipients.FirstOrDefault(a => a.SessionId == Recipient) is not { } target)
        {
            return submission;
        }

        var sent = await host.SendAsync(target.SessionId, submission.Prompt);
        return submission with { Sent = sent.Ok, SendError = sent.Ok ? null : sent.Message };
    }

    private static bool IsUnder(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal)
        || path.StartsWith(root + "/", StringComparison.Ordinal)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        _load?.Cancel();
        _load?.Dispose();
    }
}

public sealed record ReviewRecipient(string SessionId, string Label, bool Running);
