using System.Text.Json;

namespace Tog.Core.Agents;

/// <summary>One choice in a session's config option, such as a model or a mode.</summary>
public sealed record AcpChoice(string Value, string Name, string? Description);

/// <summary>A session setting the agent offers, and which value it has now.</summary>
public sealed record AcpConfigOption(string Id, string Name, string? Current, IReadOnlyList<AcpChoice> Choices);

/// <summary>A slash command the agent takes in a prompt: a skill, a custom command, one of its own.</summary>
/// <param name="Name">Without the slash.</param>
/// <param name="Hint">What its argument is, when it takes one.</param>
public sealed record AcpCommand(string Name, string Description, string? Hint);

/// <summary>An MCP server handed to an agent's sessions, reached over HTTP.</summary>
/// <param name="Headers">Sent with every request. A value may name an environment variable as <c>${NAME}</c>, which the Claude CLI expands.</param>
public sealed record McpServer(string Name, string Url, IReadOnlyList<KeyValuePair<string, string>>? Headers = null);

/// <summary>What one agent session is handed to reach the app's MCP servers.</summary>
/// <param name="Key">The session's own key, which <see cref="IAgentMcpServers"/> knows it by.</param>
/// <param name="Environment">
/// Variables the session's agent process needs for those servers, such as the
/// key a header names. Kept out of the server list, which the SDK puts on the
/// CLI's command line where any process can read it.
/// </param>
public sealed record McpGrant(string Key, IReadOnlyList<McpServer> Servers, IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// The MCP servers to hand an agent session, each with a key of its own. The
/// app's own tools, today.
/// </summary>
/// <remarks>
/// The server tells callers apart by key alone, so the key is what says which
/// session and folder a call comes from. A key is minted when a session starts
/// or resumes and revoked when it stops, so a process an agent left running
/// cannot go on calling as that agent.
/// </remarks>
public interface IAgentMcpServers
{
    /// <summary>A fresh key for a session in a folder. The session is null until <c>session/new</c> names it.</summary>
    McpGrant Grant(string cwd, string? sessionId);

    /// <summary>Names the session a key was granted for, once the agent has said it.</summary>
    void Bind(string key, string sessionId);

    /// <summary>Ends a key. Calls with it are refused from then on.</summary>
    void Revoke(string key);
}

/// <summary>A conversation the agent knows about, from <c>session/list</c>.</summary>
public sealed record AcpSessionInfo(string SessionId, string Cwd, string? Title, DateTimeOffset? UpdatedAt);

/// <summary>
/// The client side of the Agent Client Protocol, typed over a JSON-RPC connection.
/// </summary>
/// <remarks>
/// Only what Tog uses. The client advertises no file system and no
/// terminal capability, so the agent uses its own tools for both, exactly as it
/// would in a terminal: Tog watches the work, it does not perform it.
/// The one thing it does advertise is form elicitation, so the agent can ask you
/// questions. Session updates, permission requests and forms arrive on the
/// connection and are the host's to interpret; see <see cref="AgentHost"/>.
/// </remarks>
public sealed class AcpClient(JsonRpcConnection rpc)
{
    public const int ProtocolVersion = 1;

    public JsonRpcConnection Connection => rpc;

    /// <summary>What the agent said it supports, from <c>initialize</c>.</summary>
    public JsonElement AgentCapabilities { get; private set; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var result = await rpc.RequestAsync("initialize", new
        {
            protocolVersion = ProtocolVersion,
            clientCapabilities = new
            {
                fs = new { readTextFile = false, writeTextFile = false },
                terminal = false,

                // Form elicitation only. It is what lets Claude use AskUserQuestion
                // at all (the bridge disallows the tool without it), and it also
                // routes MCP servers' form elicitations and the refusal-fallback
                // "retry on another model?" prompt here. URL mode is left out: it
                // would send MCP OAuth sign-ins through Tog to open.
                elicitation = new { form = new { } },
            },
            clientInfo = new { name = "tog", title = "Tog", version = "1" },
        }, ct).ConfigureAwait(false);

        AgentCapabilities = result.TryGetProperty("agentCapabilities", out var caps) ? caps.Clone() : default;
    }

    /// <summary>Whether the agent offers a session capability, such as <c>resume</c> or <c>list</c>.</summary>
    public bool Supports(string sessionCapability) =>
        AgentCapabilities.ValueKind == JsonValueKind.Object
        && AgentCapabilities.TryGetProperty("sessionCapabilities", out var session)
        && session.ValueKind == JsonValueKind.Object
        && session.TryGetProperty(sessionCapability, out _);

    /// <summary>
    /// The servers in the shape ACP takes, or none when the agent did not say it
    /// can reach an MCP server over HTTP: it would refuse the session.
    /// </summary>
    private object[] Servers(IReadOnlyList<McpServer>? servers) =>
        servers is { Count: > 0 }
        && AgentCapabilities.ValueKind == JsonValueKind.Object
        && AgentCapabilities.TryGetProperty("mcpCapabilities", out var mcp)
        && mcp.ValueKind == JsonValueKind.Object
        && mcp.TryGetProperty("http", out var http)
        && http.ValueKind == JsonValueKind.True
            ? [.. servers.Select(s => new
            {
                type = "http",
                name = s.Name,
                url = s.Url,
                headers = (s.Headers ?? []).Select(h => new { name = h.Key, value = h.Value }).ToArray(),
            })]
            : [];

    /// <summary>
    /// The variables a session's CLI needs, where the Claude bridge takes them
    /// for one session rather than the whole process: every session shares the
    /// bridge, so its own environment cannot hold anything one session's alone.
    /// An agent that is not Claude ignores it.
    /// </summary>
    private static object? Meta(McpGrant? grant) =>
        grant is { Environment.Count: > 0 } ? new { claudeCode = new { options = new { env = grant.Environment } } } : null;

    public async Task<(string SessionId, IReadOnlyList<AcpConfigOption> Options)> NewSessionAsync(
        string cwd,
        CancellationToken ct = default,
        McpGrant? mcp = null)
    {
        var result = await rpc.RequestAsync("session/new", new { cwd, mcpServers = Servers(mcp?.Servers), _meta = Meta(mcp) }, ct)
            .ConfigureAwait(false);

        return (result.GetProperty("sessionId").GetString()!, ReadOptions(result));
    }

    /// <summary>
    /// Continues a saved conversation without replaying it: Tog reads the
    /// history from the transcript, as it does for every conversation.
    /// </summary>
    public async Task<IReadOnlyList<AcpConfigOption>> ResumeSessionAsync(
        string sessionId,
        string cwd,
        CancellationToken ct = default,
        McpGrant? mcp = null)
    {
        var result = await rpc.RequestAsync(
            "session/resume",
            new { sessionId, cwd, mcpServers = Servers(mcp?.Servers), _meta = Meta(mcp) },
            ct).ConfigureAwait(false);

        return ReadOptions(result);
    }

    /// <summary>Sends a message and waits for the turn it starts to end. Returns why it ended.</summary>
    public async Task<string> PromptAsync(string sessionId, string text, CancellationToken ct = default)
    {
        var result = await rpc.RequestAsync(
            "session/prompt",
            new { sessionId, prompt = new object[] { new { type = "text", text } } },
            ct).ConfigureAwait(false);

        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("stopReason", out var reason)
            ? reason.GetString() ?? "end_turn"
            : "end_turn";
    }

    /// <summary>Asks the agent to stop the turn in progress. The prompt then ends as cancelled.</summary>
    public Task CancelAsync(string sessionId, CancellationToken ct = default) =>
        rpc.NotifyAsync("session/cancel", new { sessionId }, ct);

    public Task CloseSessionAsync(string sessionId, CancellationToken ct = default) =>
        rpc.RequestAsync("session/close", new { sessionId }, ct);

    /// <summary>Sets a config option. Returns the options as they now stand.</summary>
    public async Task<IReadOnlyList<AcpConfigOption>> SetConfigOptionAsync(
        string sessionId,
        string configId,
        string value,
        CancellationToken ct = default)
    {
        var result = await rpc.RequestAsync(
            "session/set_config_option",
            new { sessionId, configId, value },
            ct).ConfigureAwait(false);

        return ReadOptions(result);
    }

    public async Task<IReadOnlyList<AcpSessionInfo>> ListSessionsAsync(string cwd, CancellationToken ct = default)
    {
        var result = await rpc.RequestAsync("session/list", new { cwd }, ct).ConfigureAwait(false);
        if (!result.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return sessions.EnumerateArray()
            .Select(s => new AcpSessionInfo(
                Text(s, "sessionId") ?? "",
                Text(s, "cwd") ?? cwd,
                Text(s, "title"),
                DateTimeOffset.TryParse(Text(s, "updatedAt"), out var at) ? at : null))
            .Where(s => s.SessionId.Length > 0)
            .ToList();
    }

    public static IReadOnlyList<AcpConfigOption> ReadOptions(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("configOptions", out var options)
            || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return options.EnumerateArray()
            .Where(o => Text(o, "id") is not null)
            .Select(o => new AcpConfigOption(
                Text(o, "id")!,
                Text(o, "name") ?? Text(o, "id")!,
                Text(o, "currentValue"),
                o.TryGetProperty("options", out var choices) && choices.ValueKind == JsonValueKind.Array
                    ? choices.EnumerateArray()
                        .Where(c => Text(c, "value") is not null)
                        .Select(c => new AcpChoice(Text(c, "value")!, Text(c, "name") ?? Text(c, "value")!, Text(c, "description")))
                        .ToList()
                    : []))
            .ToList();
    }

    /// <summary>The commands in an <c>available_commands_update</c>.</summary>
    public static IReadOnlyList<AcpCommand> ReadCommands(JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object
            || !update.TryGetProperty("availableCommands", out var commands)
            || commands.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return commands.EnumerateArray()
            .Where(c => Text(c, "name") is { Length: > 0 })
            .Select(c => new AcpCommand(
                Text(c, "name")!.TrimStart('/'),
                Text(c, "description") ?? "",
                c.TryGetProperty("input", out var input) ? Text(input, "hint") : null))
            .DistinctBy(c => c.Name, StringComparer.Ordinal)
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
    }

    internal static long? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
