namespace AgentsDashboard.Core.Model;

/// <summary>A git worktree, as <c>git worktree list --porcelain</c> describes it.</summary>
public sealed record WorktreeInfo
{
    public required string Path { get; init; }

    /// <summary>Last path segment, which is what the UI shows.</summary>
    public required string Name { get; init; }

    public string? Branch { get; init; }

    /// <summary>True for the repository's main working tree.</summary>
    public bool IsPrimary { get; init; }

    public bool Detached { get; init; }

    public bool Locked { get; init; }
}

/// <summary>The parts of <c>git status</c> a card shows.</summary>
public sealed record GitStatusInfo
{
    public int Changed { get; init; }
    public int Untracked { get; init; }
    public int Staged { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public string? Upstream { get; init; }

    public bool IsClean => Changed == 0 && Untracked == 0 && Staged == 0;
}

/// <summary>A worktree with everything the dashboard knows about it attached.</summary>
public sealed record WorktreeView
{
    public required WorktreeInfo Worktree { get; init; }
    public required string RepoRoot { get; init; }
    public GitStatusInfo? Status { get; init; }
    public IReadOnlyList<AgentSession> Agents { get; init; } = [];
    public IReadOnlyList<TestRun> TestRuns { get; init; } = [];

    public TestRun? LatestRun => TestRuns.Count > 0 ? TestRuns[0] : null;

    public int WaitingCount => Agents.Count(a => a.Status == AgentStatus.Waiting);
    public int ActiveCount => Agents.Count(a => a.Status == AgentStatus.Active);
}

/// <summary>A repository and every worktree of it.</summary>
public sealed record RepoView
{
    public required string Root { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<WorktreeView> Worktrees { get; init; } = [];

    /// <summary>Set when the last refresh of this repo failed, with the reason.</summary>
    public string? Error { get; init; }

    public int WaitingCount => Worktrees.Sum(w => w.WaitingCount);
    public int ActiveCount => Worktrees.Sum(w => w.ActiveCount);
}

/// <summary>Everything on screen, rebuilt on each refresh.</summary>
public sealed record DashboardSnapshot
{
    public IReadOnlyList<RepoView> Repos { get; init; } = [];
    public DateTimeOffset TakenAt { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// Every waiting agent across every repo, so the Needs You rail does not
    /// have to walk the tree. Longest-blocked first: the one that has been stuck
    /// the longest is the one costing you the most.
    /// </summary>
    public IReadOnlyList<WaitingAgent> Waiting { get; init; } = [];
}

/// <summary>A blocked agent, with enough context to act on it without navigating.</summary>
public sealed record WaitingAgent(
    AgentSession Session,
    string RepoRoot,
    string RepoName,
    string WorktreePath,
    string WorktreeName,
    string? Branch);
