using System.Text.Json;

namespace AgentsDashboard.Core.Claude;

/// <summary>A background agent, as <c>claude agents --json</c> describes it.</summary>
/// <param name="Id">The short id that <c>stop</c>, <c>rm</c>, <c>logs</c> and <c>attach</c> take.</param>
/// <param name="SessionId">The full session id, which is what <c>--resume</c> needs.</param>
/// <param name="State">
/// The CLI's own lifecycle word for a background session, such as "failed" or
/// "blocked". Absent on older sessions and on ones the CLI has nothing to say
/// about, so it is only ever shown, never depended on.
/// </param>
/// <param name="StartedAt">When the session was started, or null when the CLI did not say.</param>
public sealed record BackgroundAgent(
    string Id,
    string SessionId,
    string Cwd,
    string? Name,
    string? Status,
    int Pid,
    string? State,
    DateTimeOffset? StartedAt)
{
    /// <summary>Whether the CLI has marked the session as failed.</summary>
    public bool IsFailed => string.Equals(State, "failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the session looks like it is running.</summary>
    /// <remarks>
    /// A display hint only. A running session that has not transitioned yet has no
    /// pid, so a false answer here does not mean the session is gone: the
    /// running-only listing remains the sound liveness test. See the remarks on
    /// <c>ClaudeCli.WaitUntilStoppedAsync</c>.
    /// </remarks>
    public bool IsRunning => Pid > 0;
}

/// <summary>
/// The Claude CLI calls the dashboard makes, and the parsing of what comes back.
/// </summary>
/// <remarks>
/// Kept apart from running the process so both can be tested: getting these
/// arguments wrong is the kind of mistake that quietly does the wrong thing to a
/// real session rather than failing.
/// </remarks>
public static class ClaudeCommands
{
    /// <summary>
    /// Start a background agent. Without a prompt it starts idle and costs
    /// nothing until you give it something to do.
    /// </summary>
    public static IReadOnlyList<string> Start(string? prompt) => Start(new StartOptions(prompt));

    /// <summary>
    /// Start a background agent with the options the start form offers.
    /// </summary>
    /// <remarks>
    /// The prompt goes last because the CLI takes it as a positional argument:
    /// put it before a flag and the flag's value is read as part of the prompt.
    /// Every option left blank is simply omitted, so the CLI's own default stands.
    /// </remarks>
    public static IReadOnlyList<string> Start(StartOptions options)
    {
        var args = new List<string> { "--bg" };

        AddOption(args, "--model", options.Model);
        AddOption(args, "--effort", options.Effort);
        AddOption(args, "--permission-mode", options.PermissionMode);

        if (options.NewWorktree)
        {
            args.Add("--worktree");

            // The name is optional: bare `--worktree` lets the CLI pick one.
            if (!string.IsNullOrWhiteSpace(options.WorktreeName))
            {
                args.Add(options.WorktreeName);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.Prompt))
        {
            args.Add(options.Prompt);
        }

        return args;
    }

    private static void AddOption(List<string> args, string flag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        args.Add(flag);
        args.Add(value);
    }

    /// <summary>Stop a background agent. Its conversation is kept.</summary>
    public static IReadOnlyList<string> Stop(string id) => ["stop", id];

    /// <summary>Delete a background agent and its conversation.</summary>
    public static IReadOnlyList<string> Remove(string id) => ["rm", id];

    /// <summary>
    /// The recent terminal output of a background session.
    /// </summary>
    /// <remarks>
    /// This fails for a session whose daemon is gone ("Couldn't read logs ...
    /// connect ENOENT"), which is normal rather than exceptional: the CLI's own
    /// words are worth showing instead of a paraphrase.
    /// </remarks>
    public static IReadOnlyList<string> Logs(string id) => ["logs", id];

    /// <summary>Restarts a background session so it runs the current CLI binary.</summary>
    public static IReadOnlyList<string> Respawn(string id) => ["respawn", id];

    /// <summary>
    /// The line that opens a session in a terminal.
    /// </summary>
    /// <remarks>
    /// Offered for copying rather than run: <c>attach</c> is interactive and wants
    /// a terminal, which the dashboard does not have.
    /// </remarks>
    public static string AttachCommand(string id) => $"claude attach {id}";

    /// <summary>
    /// The shell line these arguments amount to, so the UI can show exactly what
    /// it is about to run before it runs it.
    /// </summary>
    /// <remarks>
    /// For reading and pasting only. The CLI is started from an argument list, so
    /// nothing is ever handed to a shell, but the preview has to be quoted as
    /// though it were or a prompt with a space in it would read as several
    /// arguments.
    /// </remarks>
    public static string Render(IReadOnlyList<string> args) =>
        "claude " + string.Join(' ', args.Select(Quote));

    private static string Quote(string arg)
    {
        if (arg.Length == 0)
        {
            return "''";
        }

        return arg.Any(NeedsQuoting)
            ? "'" + arg.Replace("'", @"'\''") + "'"
            : arg;
    }

    private static bool NeedsQuoting(char c) =>
        char.IsWhiteSpace(c) || c is '\'' or '"' or '$' or '`' or '\\' or '!' or '*' or '?'
            or ';' or '&' or '|' or '<' or '>' or '(' or ')' or '#';

    /// <summary>
    /// Continue a stopped session with a message.
    /// </summary>
    /// <remarks>
    /// The <em>full</em> session id, not the short one: given the short id the CLI
    /// starts a copy of the conversation under a new id instead of continuing the
    /// original, which is silently the wrong thing.
    /// </remarks>
    public static IReadOnlyList<string> Send(string sessionId, string message) =>
        ["--bg", "--resume", sessionId, message];

    /// <summary>
    /// Lists background agents.
    /// </summary>
    /// <param name="includeStopped">
    /// With <c>--all</c> the list also holds sessions that have exited, which is
    /// what the parked list needs. Without it, the list is exactly the sessions
    /// that are running, which is the only sound way to ask whether one still is:
    /// a stopped session keeps its entry under <c>--all</c>, and a running one
    /// that has not transitioned yet has no status to read.
    /// </param>
    public static IReadOnlyList<string> List(bool includeStopped = true) =>
        includeStopped ? ["agents", "--json", "--all"] : ["agents", "--json"];

    /// <summary>Parses <c>claude agents --json</c>, keeping only background sessions.</summary>
    public static IReadOnlyList<BackgroundAgent> ParseList(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var agents = new List<BackgroundAgent>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || Text(entry, "kind") != "background"
                    || Text(entry, "id") is not { Length: > 0 } id
                    || Text(entry, "sessionId") is not { Length: > 0 } sessionId)
                {
                    continue;
                }

                agents.Add(new BackgroundAgent(
                    id,
                    sessionId,
                    Text(entry, "cwd") ?? "",
                    Text(entry, "name"),
                    Text(entry, "status"),
                    Number(entry, "pid"),
                    Text(entry, "state"),
                    Moment(entry, "startedAt")));
            }

            return agents;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The short id the CLI prints when it backgrounds a session, so a start can
    /// be reported before the registry has caught up.
    /// </summary>
    public static string? ParseStartedId(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            const string marker = "backgrounded";
            if (!line.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // "backgrounded · a2498250" or "backgrounded · a2498250 (idle — ...)"
            var rest = line[marker.Length..].TrimStart(' ', '·', '-', ':');
            var end = rest.IndexOf(' ');
            var id = (end < 0 ? rest : rest[..end]).Trim();
            if (id.Length > 0)
            {
                return id;
            }
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// An integer property, or zero when it is absent or null. The kind is checked
    /// first because TryGetInt32 throws on an element of the wrong type rather
    /// than returning false, and a stopped session reports a null pid.
    /// </summary>
    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    /// <summary>
    /// A Unix-milliseconds property, or null when it is absent, null or not a
    /// number. The kind is checked first for the same reason as in Number.
    /// </summary>
    private static DateTimeOffset? Moment(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;
}

/// <summary>The choices the start form offers, in the order the CLI documents them.</summary>
public static class StartChoices
{
    public static readonly IReadOnlyList<string> Models = ["fable", "opus", "sonnet", "haiku"];

    public static readonly IReadOnlyList<string> Efforts = ["low", "medium", "high", "xhigh", "max"];

    public static readonly IReadOnlyList<string> PermissionModes =
        ["acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan"];
}
