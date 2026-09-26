using System.Text.Json;

namespace AgentsDashboard.Core.Agents;

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

/// <summary>The MCP servers to hand an agent session in a folder. The app's own tools, today.</summary>
public interface IAgentMcpServers
{
    IReadOnlyList<McpServer> For(string cwd);

    /// <summary>
    /// Variables the agent process needs for those servers, such as a key a
    /// header names. Kept out of the server list, which the SDK puts on the
    /// CLI's command line where any process can read it.
    /// </summary>
    IReadOnlyDictionary<string, string> Environment { get; }
}

/// <summary>A conversation the agent knows about, from <c>session/list</c>.</summary>
public sealed record AcpSessionInfo(string SessionId, string Cwd, string? Title, DateTimeOffset? UpdatedAt);

/// <summary>
/// The client side of the Agent Client Protocol, typed over a JSON-RPC connection.
/// </summary>
/// <remarks>
/// Only what the dashboard uses. The client advertises no file system and no
/// terminal capability, so the agent uses its own tools for both, exactly as it
/// would in a terminal: the dashboard watches the work, it does not perform it.
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
                // would send MCP OAuth sign-ins through the dashboard to open.
                elicitation = new { form = new { } },
            },
            clientInfo = new { name = "agents-dashboard", title = "Agents Dashboard", version = "1" },
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

    public async Task<(string SessionId, IReadOnlyList<AcpConfigOption> Options)> NewSessionAsync(
        string cwd,
        CancellationToken ct = default,
        IReadOnlyList<McpServer>? servers = null)
    {
        var result = await rpc.RequestAsync("session/new", new { cwd, mcpServers = Servers(servers) }, ct)
            .ConfigureAwait(false);

        return (result.GetProperty("sessionId").GetString()!, ReadOptions(result));
    }

    /// <summary>
    /// Continues a saved conversation without replaying it: the dashboard reads the
    /// history from the transcript, as it does for every conversation.
    /// </summary>
    public async Task<IReadOnlyList<AcpConfigOption>> ResumeSessionAsync(
        string sessionId,
        string cwd,
        CancellationToken ct = default,
        IReadOnlyList<McpServer>? servers = null)
    {
        var result = await rpc.RequestAsync(
            "session/resume",
            new { sessionId, cwd, mcpServers = Servers(servers) },
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
