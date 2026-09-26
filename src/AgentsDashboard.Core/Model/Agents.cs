namespace AgentsDashboard.Core.Model;

/// <summary>
/// The three states a Claude session is shown in. Claude records a richer set,
/// which <c>SessionRegistryReader</c> maps down: an unrecognized status reads as
/// <see cref="Idle"/> rather than being guessed at.
/// </summary>
public enum AgentStatus
{
    /// <summary>Started, or finished responding and awaiting you.</summary>
    Idle,

    /// <summary>Processing a prompt, or running a tool or shell command.</summary>
    Active,

    /// <summary>Blocked on you: a permission prompt or a question.</summary>
    Waiting,
}

/// <summary>A subagent running under a parent session right now.</summary>
/// <param name="Id">Claude's id for the subagent, unique within its parent.</param>
/// <param name="AgentType">Agent type, for example "Explore" or "general-purpose".</param>
/// <param name="Description">What the Agent tool was asked to do.</param>
/// <param name="SpawnDepth">Nesting depth; 1 is a direct child of the session.</param>
/// <param name="StartedAt">When the subagent's file first appeared.</param>
public sealed record Subagent(
    string Id,
    string? AgentType,
    string? Description,
    int SpawnDepth,
    DateTimeOffset StartedAt,
    string? ToolUseId = null,
    DateTimeOffset? LastActivity = null);

/// <summary>A shell command an agent started in the background and has not heard back from.</summary>
/// <param name="ToolUseId">The Bash call that started it, which its completion notice names.</param>
/// <param name="Description">What the agent said the command does, else the command itself.</param>
/// <param name="StartedAt">When the agent started it.</param>
public sealed record BackgroundCommand(string ToolUseId, string Description, DateTimeOffset StartedAt);

/// <summary>
/// One live Claude session, as the registry and its transcript describe it.
/// </summary>
public sealed record AgentSession
{
    public required string SessionId { get; init; }

    /// <summary>Pid of the Claude process, which is also its registry file name.</summary>
    public required int Pid { get; init; }

    /// <summary>Directory the session was started in.</summary>
    public required string Cwd { get; init; }

    public AgentStatus Status { get; init; } = AgentStatus.Idle;

    /// <summary>Claude's own explanation of a waiting status, when it gave one.</summary>
    public string? WaitingFor { get; init; }

    /// <summary>Claude's session name, when it has derived one.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// What the session is called: a title someone set for it, else the one
    /// Claude generated. Read from the transcript.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>The most recent prompt this session was given.</summary>
    public string? LastPrompt { get; init; }

    /// <summary>The agent's most recent words.</summary>
    public string? LastReply { get; init; }

    /// <summary>Skills this session has invoked, deduped, in first-use order.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the registry file was last written, that is, the last transition.</summary>
    public DateTimeOffset LastActivity { get; init; }

    /// <summary>
    /// When this session entered its current status. Only moves when the status
    /// itself changes, so a waiting agent's "blocked for" timer is honest even
    /// though the registry file is rewritten for other reasons.
    /// </summary>
    public DateTimeOffset StatusSince { get; init; }

    /// <summary>
    /// True for a session started with <c>claude --bg</c>. Those are the ones the
    /// dashboard can act on: it can stop them, remove them and hand them a
    /// message. A session someone is sitting in front of belongs to its terminal.
    /// </summary>
    public bool IsBackground { get; init; }

    /// <summary>
    /// The short id the Claude CLI's own commands take (<c>stop</c>, <c>rm</c>,
    /// <c>logs</c>, <c>attach</c>). Only background sessions have one.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>Subagents in flight under this session, oldest first.</summary>
    public IReadOnlyList<Subagent> Subagents { get; init; } = [];

    /// <summary>Shell commands the session started in the background that have not finished, oldest first.</summary>
    public IReadOnlyList<BackgroundCommand> BackgroundCommands { get; init; } = [];

    /// <summary>
    /// When the process running this session started, when the dashboard knows.
    /// Background work begun before then died with the process before it, even
    /// though nothing recorded it finishing.
    /// </summary>
    public DateTimeOffset? ProcessStartedAt { get; init; }

    /// <summary>How many subagents and background commands are running.</summary>
    public int BackgroundCount => Subagents.Count + BackgroundCommands.Count;

    /// <summary>
    /// What the row is called.
    /// </summary>
    /// <remarks>
    /// The title first, since that is what the session is about. Failing that the
    /// last prompt, because "open pr and release patch version" tells you what an
    /// agent is doing and "agent-worktrees-07" does not: Claude's derived name is
    /// made from the directory and a counter, so it says nothing a glance at the
    /// worktree has not already said. The name is kept as a last resort before the
    /// session id, which says less still.
    /// </remarks>
    public string Label =>
        !string.IsNullOrWhiteSpace(Summary) ? Summary!
        : !string.IsNullOrWhiteSpace(LastPrompt) ? Shorten(LastPrompt!)
        : !string.IsNullOrWhiteSpace(Name) ? Name!
        : SessionId[..Math.Min(8, SessionId.Length)];

    /// <summary>True when the label is a prompt rather than a title, so the view
    /// can show it as the quotation it is.</summary>
    public bool LabelIsPrompt =>
        string.IsNullOrWhiteSpace(Summary) && !string.IsNullOrWhiteSpace(LastPrompt);

    /// <summary>
    /// A prompt cut to something that fits a row, at a word boundary where there
    /// is one near enough to the limit to be worth using.
    /// </summary>
    public static string Shorten(string text, int limit = 90)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= limit)
        {
            return flat;
        }

        var cut = flat[..limit];
        var space = cut.LastIndexOf(' ');
        return (space > limit / 2 ? cut[..space] : cut).TrimEnd() + "...";
    }
}
