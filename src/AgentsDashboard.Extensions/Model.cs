namespace AgentsDashboard.Extensions;

/// <summary>
/// An agent, as an extension sees it. Backend-neutral: the same shape for any
/// agent the dashboard can run.
/// </summary>
public sealed record AgentContext(
    string AgentId,
    string Label,
    string Backend,
    AgentState State,
    WorktreeContext? Worktree);

/// <summary>What an agent is doing.</summary>
public enum AgentState
{
    /// <summary>In a turn.</summary>
    Active,

    /// <summary>Between turns, ready for a message.</summary>
    Idle,

    /// <summary>Blocked on the user: a permission or a question.</summary>
    Waiting,

    /// <summary>Its session ended. A message resumes it.</summary>
    Parked,

    /// <summary>Its session failed to start or crashed.</summary>
    Failed,
}

/// <summary>A git worktree the dashboard follows.</summary>
public sealed record WorktreeContext(
    string Path,
    string Name,
    string? Branch,
    bool IsPrimary,
    string RepoRoot,
    string RepoName,
    GitSummary? Status);

/// <summary>The counts from <c>git status</c>.</summary>
public sealed record GitSummary(int Changed, int Staged, int Untracked, int Ahead, int Behind);

/// <summary>Everything the dashboard follows, as of one monitor tick.</summary>
public sealed record DashboardView(
    IReadOnlyList<WorktreeContext> Worktrees,
    DateTimeOffset TakenAt);
