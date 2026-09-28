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

    /// <summary>
    /// The colour theme, by id from <c>Themes.All</c>. Null follows the OS
    /// between Dark and Light.
    /// </summary>
    public string? Theme { get; init; }

    /// <summary>Show times of day as 14:05 rather than 2:05 PM.</summary>
    public bool TwentyFourHourClock { get; init; }

    /// <summary>
    /// Open a file picked in Go to File as the preview tab, which the next one
    /// replaces. Off by default, as VS Code's enablePreviewFromQuickOpen is:
    /// a file searched for by name is usually one you meant to keep.
    /// </summary>
    public bool QuickOpenPreview { get; init; }

    /// <summary>List recently opened files in Go to File, first when nothing is typed and ahead of other matches.</summary>
    public bool QuickOpenHistory { get; init; } = true;

    /// <summary>Close Go to File when focus moves elsewhere, a click outside it say.</summary>
    public bool QuickOpenCloseOnBlur { get; init; } = true;

    /// <summary>
    /// Keys the user bound, by command id. Each list replaces that command's
    /// defaults, and an empty one unbinds it. Commands not here keep their
    /// defaults. See <c>KeyMap</c>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> KeyBindings { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

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

    /// <summary>
    /// Folders of extensions: every folder directly inside one that has an
    /// <c>extension.json</c> is found as if it were linked, including ones added
    /// later.
    /// </summary>
    public IReadOnlyList<string> ExtensionFolders { get; init; } = [];

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

    /// <summary>
    /// The values of the settings the extension declares, by setting id, as
    /// strings. Kept while the extension is off, so turning it back on keeps them.
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>();
}
