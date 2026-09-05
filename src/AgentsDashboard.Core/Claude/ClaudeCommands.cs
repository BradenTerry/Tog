using System.Text.Json;

namespace AgentsDashboard.Core.Claude;

/// <summary>A background agent, as <c>claude agents --json</c> describes it.</summary>
/// <param name="Id">The short id that <c>stop</c>, <c>rm</c>, <c>logs</c> and <c>attach</c> take.</param>
/// <param name="SessionId">The full session id, which is what <c>--resume</c> needs.</param>
public sealed record BackgroundAgent(
    string Id,
    string SessionId,
    string Cwd,
    string? Name,
    string? Status,
    int Pid);

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
    public static IReadOnlyList<string> Start(string? prompt)
    {
        var args = new List<string> { "--bg" };
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            args.Add(prompt);
        }

        return args;
    }

    /// <summary>Stop a background agent. Its conversation is kept.</summary>
    public static IReadOnlyList<string> Stop(string id) => ["stop", id];

    /// <summary>Delete a background agent and its conversation.</summary>
    public static IReadOnlyList<string> Remove(string id) => ["rm", id];

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
                    Number(entry, "pid")));
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
}
