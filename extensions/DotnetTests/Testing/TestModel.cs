namespace AgentsDashboard.Extensions.DotnetTests;

/// <summary>How one test came out. TRX spells these as strings; unknown maps to <see cref="Other"/>.</summary>
public enum TestOutcome
{
    Passed,
    Failed,
    Skipped,
    NotExecuted,
    Other,
}

/// <summary>Where a run is in its life.</summary>
public enum TestRunState
{
    /// <summary>A test process is alive, or the TRX is still growing.</summary>
    Running,

    /// <summary>Finished with no failures.</summary>
    Passed,

    /// <summary>Finished with at least one failure.</summary>
    Failed,

    /// <summary>The process died before the report was closed.</summary>
    Aborted,
}

/// <summary>One test result read out of a TRX.</summary>
public sealed record TestResultItem
{
    public required string TestId { get; init; }
    public required string Name { get; init; }
    public TestOutcome Outcome { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public string? Message { get; init; }
    public string? StackTrace { get; init; }

    /// <summary>Namespace-qualified class, split off <see cref="Name"/> for grouping.</summary>
    public string? ClassName { get; init; }
}

/// <summary>
/// A test run the dashboard is following, live or finished.
/// </summary>
/// <remarks>
/// Identity is the TRX path, so a growing file keeps updating one run rather than
/// producing a new one per write. A run with no TRX yet (spotted by the process
/// scanner) uses the test process id as its id instead.
/// </remarks>
public sealed record TestRun
{
    public required string Id { get; init; }
    public required string WorktreePath { get; init; }

    /// <summary>The TRX being read. Absent while a run is only known from its process.</summary>
    public string? TrxPath { get; init; }

    /// <summary>Assembly or project name, taken from the TRX file name when it follows the default shape.</summary>
    public string? ProjectName { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset LastUpdatedAt { get; init; }
    public TestRunState State { get; init; } = TestRunState.Running;

    /// <summary>
    /// Tests the run said it would execute, from the TRX's TestDefinitions block.
    /// Zero means unknown, in which case progress is shown as a count rather than
    /// a bar: claiming a percentage of an unknown total is worse than not claiming one.
    /// </summary>
    public int Total { get; init; }

    public IReadOnlyList<TestResultItem> Results { get; init; } = [];

    /// <summary>Pid of the test process, while one is known to be alive.</summary>
    public int? Pid { get; init; }

    public int Passed => Results.Count(r => r.Outcome == TestOutcome.Passed);
    public int Failed => Results.Count(r => r.Outcome == TestOutcome.Failed);
    public int Skipped => Results.Count(r => r.Outcome is TestOutcome.Skipped or TestOutcome.NotExecuted);
    public int Completed => Results.Count;

    public bool HasTotal => Total > 0;

    /// <summary>0 to 1, or null when the total is not known.</summary>
    public double? Progress => HasTotal ? Math.Clamp((double)Completed / Total, 0, 1) : null;

    /// <summary>
    /// How long the run took, or has been going. Clamped at zero: the start time
    /// comes from the report and the end from the file, and a machine whose clock
    /// disagrees with itself should not produce a negative duration on screen.
    /// </summary>
    public TimeSpan Elapsed
    {
        get
        {
            var span = (CompletedAt ?? LastUpdatedAt) - StartedAt;
            return span < TimeSpan.Zero ? TimeSpan.Zero : span;
        }
    }

    public IEnumerable<TestResultItem> Failures =>
        Results.Where(r => r.Outcome == TestOutcome.Failed);
}

/// <summary>Which runner a test project uses, which decides whether its TRX streams.</summary>
public enum TestRunnerKind
{
    Unknown,

    /// <summary>Microsoft.Testing.Platform. From 2.3.0 its TRX streams as the run progresses.</summary>
    MicrosoftTestingPlatform,

    /// <summary>Classic VSTest. Its TRX is written once, at the end of the run.</summary>
    VsTest,
}

/// <summary>What a repo's test projects look like, and whether runs will be visible live.</summary>
public sealed record TestTelemetryReport
{
    public required string RepoRoot { get; init; }
    public IReadOnlyList<TestProjectInfo> Projects { get; init; } = [];

    /// <summary>True when something in the repo already forces a TRX on every run.</summary>
    public bool TrxAlwaysOn { get; init; }

    /// <summary>Path of the Directory.Build.targets the installer would write or amend.</summary>
    public required string TargetsPath { get; init; }

    public bool StreamsLive =>
        TrxAlwaysOn && Projects.Any(p => p.Runner == TestRunnerKind.MicrosoftTestingPlatform);
}

/// <summary>One test project found in a repo.</summary>
public sealed record TestProjectInfo(string ProjectPath, string Name, TestRunnerKind Runner);
