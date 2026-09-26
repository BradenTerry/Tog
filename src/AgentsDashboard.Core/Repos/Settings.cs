namespace AgentsDashboard.Core.Repos;

/// <summary>Everything the dashboard remembers between runs.</summary>
public sealed record Settings
{
    /// <summary>
    /// Repository roots the user added by hand. Roots discovered from live
    /// sessions are not stored: they come and go with the agents, and persisting
    /// them would leave a list that only ever grows.
    /// </summary>
    public IReadOnlyList<string> RepoRoots { get; init; } = [];

    /// <summary>Roots the user explicitly hid, so discovery cannot bring them back.</summary>
    public IReadOnlyList<string> HiddenRoots { get; init; } = [];

    /// <summary>Seconds between git status polls for a worktree nothing else watches.</summary>
    public int GitPollSeconds { get; init; } = 10;

    /// <summary>Raise an OS notification when an agent starts waiting.</summary>
    public bool NotifyOnWaiting { get; init; } = true;

    /// <summary>Model to preselect when starting an agent. Null leaves it to the CLI.</summary>
    public string? DefaultModel { get; init; }

    /// <summary>Effort to preselect when starting an agent. Null leaves it to the CLI.</summary>
    public string? DefaultEffort { get; init; }

    /// <summary>Permission mode to preselect when starting an agent. Null leaves it to the CLI.</summary>
    public string? DefaultPermissionMode { get; init; }
}
