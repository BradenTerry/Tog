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

    /// <summary>Raise an OS notification when an agent starts waiting.</summary>
    public bool NotifyOnWaiting { get; init; } = true;

    /// <summary>Model to preselect when starting an agent. Null leaves it to the CLI.</summary>
    public string? DefaultModel { get; init; }

    /// <summary>Effort to preselect when starting an agent. Null leaves it to the CLI.</summary>
    public string? DefaultEffort { get; init; }

    /// <summary>Permission mode to preselect when starting an agent. Null leaves it to the CLI.</summary>
    public string? DefaultPermissionMode { get; init; }

    /// <summary>
    /// Repositories you have confirmed agents may read and change. Asked once per
    /// repository, the first time an agent is started in it, and kept here rather
    /// than in Claude's own config, which the dashboard never writes.
    /// </summary>
    public IReadOnlyList<string> TrustedRoots { get; init; } = [];

    /// <summary>
    /// Folders the user linked as extensions: usually a project they are writing,
    /// reloaded on every build.
    /// </summary>
    public IReadOnlyList<string> LinkedExtensions { get; init; } = [];

    /// <summary>Per extension id: whether it is on, and what was trusted.</summary>
    public IReadOnlyDictionary<string, ExtensionState> Extensions { get; init; } =
        new Dictionary<string, ExtensionState>();
}

/// <summary>What the user decided about one extension.</summary>
public sealed record ExtensionState
{
    public bool Enabled { get; init; }

    /// <summary>
    /// The SHA-256 of the entry assembly when an installed extension was enabled.
    /// A different hash means different code, which is asked about again.
    /// </summary>
    public string? TrustedHash { get; init; }
}
