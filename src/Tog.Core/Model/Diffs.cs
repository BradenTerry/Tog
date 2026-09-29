namespace Tog.Core.Model;

/// <summary>What a line is in a unified diff.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

/// <summary>Which side of the diff a comment is anchored to.</summary>
public enum DiffSide
{
    /// <summary>The new file: added and context lines.</summary>
    Right,

    /// <summary>The old file: removed lines.</summary>
    Left,
}

/// <param name="Kind">Context, added or removed.</param>
/// <param name="OldLine">Line number in the old file, absent for additions.</param>
/// <param name="NewLine">Line number in the new file, absent for removals.</param>
/// <param name="Text">The line without its leading marker.</param>
public sealed record DiffLine(DiffLineKind Kind, int? OldLine, int? NewLine, string Text);

/// <summary>One <c>@@</c> block.</summary>
public sealed record DiffHunk
{
    public int OldStart { get; init; }
    public int OldCount { get; init; }
    public int NewStart { get; init; }
    public int NewCount { get; init; }

    /// <summary>The trailing section text git puts after the ranges, when there is one.</summary>
    public string? Section { get; init; }

    public IReadOnlyList<DiffLine> Lines { get; init; } = [];
}

public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Untracked,
}

/// <summary>One file's diff.</summary>
public sealed record DiffFile
{
    /// <summary>Repo-relative path of the new file, or of the old one when deleted.</summary>
    public required string Path { get; init; }

    /// <summary>Previous path, set only on a rename.</summary>
    public string? OldPath { get; init; }

    public FileChangeKind Kind { get; init; } = FileChangeKind.Modified;

    /// <summary>True when git reported the change as binary, so there are no hunks.</summary>
    public bool IsBinary { get; init; }

    public IReadOnlyList<DiffHunk> Hunks { get; init; } = [];

    public int Additions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));
    public int Deletions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Removed));
}

/// <summary>What the diff is taken against.</summary>
public enum DiffBase
{
    /// <summary>Everything not committed: staged and unstaged, plus untracked files.</summary>
    WorkingTree,

    /// <summary>Merge base with the repo's default branch. The "what this branch changed" view.</summary>
    DefaultBranch,

    /// <summary>A ref the user named.</summary>
    CustomRef,
}

/// <summary>A whole diff, with the base it was taken against.</summary>
public sealed record DiffSet
{
    public required string WorktreePath { get; init; }
    public DiffBase Base { get; init; }

    /// <summary>The resolved ref this was diffed against, for display. Absent for the working tree.</summary>
    public string? BaseRef { get; init; }

    public IReadOnlyList<DiffFile> Files { get; init; } = [];

    /// <summary>Set when the diff could not be produced, with the reason.</summary>
    public string? Error { get; init; }

    public int Additions => Files.Sum(f => f.Additions);
    public int Deletions => Files.Sum(f => f.Deletions);
}
