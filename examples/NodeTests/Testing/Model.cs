namespace NodeTests.Testing;

public enum TestStatus
{
    NotRun,
    Running,
    Passed,
    Failed,
    Skipped,
}

/// <param name="Suite">The <c>describe</c> it sits in, once a run has said so.</param>
/// <param name="Failure">What the assertion said, for a failed test.</param>
public sealed record TestCase(
    string Name,
    string? Suite = null,
    TestStatus Status = TestStatus.NotRun,
    TimeSpan? Duration = null,
    string? Failure = null);

/// <param name="Path">Relative to the worktree, with forward slashes.</param>
/// <param name="Error">Why the file produced no results: it did not load, or node is missing.</param>
public sealed record TestFile(
    string Path,
    IReadOnlyList<TestCase> Tests,
    TestStatus Status = TestStatus.NotRun,
    string? Error = null);

/// <summary>
/// Everything the view draws for one worktree. Immutable and replaced whole on
/// every change, so a render on the window's thread never sees a run halfway
/// through updating it.
/// </summary>
public sealed record WorktreeTests(
    IReadOnlyList<TestFile> Files,
    bool Listed = false,
    bool Running = false,
    DateTimeOffset? LastRun = null,
    string? Error = null)
{
    public static readonly WorktreeTests Empty = new([]);

    public int Count(TestStatus status) => Files.Sum(f => f.Tests.Count(t => t.Status == status));
}
