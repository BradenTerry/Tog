namespace AgentsDashboard.Core.Model;

/// <summary>How a worktree's branch stands against the branch work lands on.</summary>
public enum MergeState
{
    /// <summary>Git could not say.</summary>
    Unknown,

    /// <summary>No commits of its own: it is where it started, or behind.</summary>
    NoOwnCommits,

    /// <summary>Its tip is an ancestor of the base, so a merge commit or a fast-forward took it in.</summary>
    Merged,

    /// <summary>
    /// Its commits are not in the base, but merging it would change nothing,
    /// which is what a squash or rebase merge leaves behind.
    /// </summary>
    SquashMerged,

    /// <summary>Merging it would still bring something in.</summary>
    NotMerged,
}

/// <summary>A file or directory in a worktree, and what it takes on disk.</summary>
/// <param name="Path">Worktree-relative, with a trailing slash for a directory, as git lists it.</param>
/// <param name="Bytes">What it takes up on disk, everything under it for a directory.</param>
public sealed record DiskEntry(string Path, long Bytes);

/// <summary>A file the worktree has changed, as git status reports it, and its size on disk.</summary>
/// <param name="Path">Worktree-relative.</param>
/// <param name="Kind">
/// One letter, as VS Code shows it: M modified, A added, D deleted, R renamed,
/// U untracked, C conflicted.
/// </param>
/// <param name="Bytes">Its size now; nothing for a deleted file.</param>
public sealed record ChangedFile(string Path, char Kind, long Bytes);

/// <summary>What the dashboard found out about one worktree when it last measured it.</summary>
public sealed record WorktreeFacts
{
    public GitStatusInfo? Status { get; init; }

    /// <summary>
    /// Commits reachable from HEAD and from nothing else: no remote branch and not
    /// the base. Removing the worktree and its branch would lose these.
    /// </summary>
    public int OnlyHere { get; init; }

    public MergeState Merge { get; init; }

    /// <summary>The branch merges were checked against, such as <c>origin/main</c>.</summary>
    public string? Base { get; init; }

    public DateTimeOffset? LastCommitAt { get; init; }

    /// <summary>
    /// Everything on disk, in three parts that add up to it: the project's files
    /// (<see cref="ProjectBytes"/>), this worktree's changes
    /// (<see cref="ChangesBytes"/>) and ignored output (<see cref="OutputBytes"/>).
    /// </summary>
    public long TotalBytes { get; init; }

    /// <summary>Of <see cref="TotalBytes"/>, what is in ignored files.</summary>
    public long OutputBytes { get; init; }

    /// <summary>Of <see cref="TotalBytes"/>, the files git status lists: modified, added and untracked.</summary>
    public long ChangesBytes { get; init; }

    /// <summary>
    /// The checked-out project, unchanged files only: what any worktree of the
    /// repository takes before anyone works in it.
    /// </summary>
    public long ProjectBytes => TotalBytes - OutputBytes - ChangesBytes;

    /// <summary>The ignored entries, wherever they are in the tree, largest first.</summary>
    public IReadOnlyList<DiskEntry> Output { get; init; } = [];

    /// <summary>The worktree's changes, largest first.</summary>
    public IReadOnlyList<ChangedFile> Changes { get; init; } = [];

    /// <summary>
    /// The project's files by top-level folder and file, largest first. Ignored
    /// output and changes are counted in their own lists, not here.
    /// </summary>
    public IReadOnlyList<DiskEntry> Top { get; init; } = [];

    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>Set when git could not answer, with its message.</summary>
    public string? Error { get; init; }
}

/// <summary>Where a worktree stands for cleaning up, most protected first.</summary>
public enum CleanupState
{
    /// <summary>Not measured yet.</summary>
    Checking,

    /// <summary>The repository's main working tree, which is never removed.</summary>
    Primary,

    /// <summary>The dashboard itself is running from it.</summary>
    Dashboard,

    /// <summary>Its directory is gone and git still lists it.</summary>
    Orphaned,

    /// <summary>An agent is attached to a session in it.</summary>
    AgentRunning,

    /// <summary>Changes or commits that exist nowhere else.</summary>
    HasWork,

    /// <summary>Clean and pushed, but not merged: removable without loss, not offered in bulk.</summary>
    NotMerged,

    /// <summary>Clean, nothing unpushed, merged or never diverged, no agent.</summary>
    SafeToRemove,

    /// <summary>Git could not be asked.</summary>
    Unknown,
}
