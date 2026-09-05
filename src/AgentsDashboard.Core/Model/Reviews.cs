namespace AgentsDashboard.Core.Model;

/// <summary>One line-anchored comment in a pending review.</summary>
public sealed record ReviewComment
{
    public required string Id { get; init; }

    /// <summary>Repo-relative file path, exactly as it will appear in the review markdown.</summary>
    public required string FilePath { get; init; }

    /// <summary>First line of the range. Equal to <see cref="EndLine"/> for a single line.</summary>
    public required int StartLine { get; init; }

    public required int EndLine { get; init; }

    public DiffSide Side { get; init; } = DiffSide.Right;

    public required string Body { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>The diff text the comment was made against, quoted back so the agent sees it.</summary>
    public string? Snippet { get; init; }

    public string Range => StartLine == EndLine ? $"L{StartLine}" : $"L{StartLine}-{EndLine}";
}

/// <summary>
/// Comments collected but not yet submitted, for one worktree. Persisted so
/// navigating away or restarting does not lose a half-written review.
/// </summary>
public sealed record ReviewDraft
{
    public required string WorktreePath { get; init; }

    /// <summary>The overall note that leads the submitted review.</summary>
    public string? Summary { get; init; }

    public IReadOnlyList<ReviewComment> Comments { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public bool IsEmpty => Comments.Count == 0 && string.IsNullOrWhiteSpace(Summary);
}

/// <summary>What happened when a review was submitted.</summary>
/// <param name="MarkdownPath">Where the review was written. Always set on success.</param>
/// <param name="Prompt">The paste-ready prompt pointing the agent at the file.</param>
/// <param name="ClipboardCopied">Whether the prompt reached the system clipboard.</param>
/// <param name="Sent">Whether the prompt was handed to an agent.</param>
/// <param name="SendError">Why it was not, for the details line.</param>
public sealed record ReviewSubmission(
    string MarkdownPath,
    string Prompt,
    bool ClipboardCopied,
    bool Sent,
    string? SendError);
