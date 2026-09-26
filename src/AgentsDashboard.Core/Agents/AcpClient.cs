using System.Text.Json;

namespace AgentsDashboard.Core.Agents;

/// <summary>One choice in a session's config option, such as a model or a mode.</summary>
public sealed record AcpChoice(string Value, string Name, string? Description);

/// <summary>A session setting the agent offers, and which value it has now.</summary>
public sealed record AcpConfigOption(string Id, string Name, string? Current, IReadOnlyList<AcpChoice> Choices);

/// <summary>A conversation the agent knows about, from <c>session/list</c>.</summary>
public sealed record AcpSessionInfo(string SessionId, string Cwd, string? Title, DateTimeOffset? UpdatedAt);

/// <summary>
/// The client side of the Agent Client Protocol, typed over a JSON-RPC connection.
/// </summary>
/// <remarks>
/// Only what the dashboard uses. The client advertises no file system and no
/// terminal capability, so the agent uses its own tools for both, exactly as it
/// would in a terminal: the dashboard watches the work, it does not perform it.
/// Session updates and permission requests arrive on the connection and are the
/// host's to interpret; see <see cref="AgentHost"/>.
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

    public async Task<(string SessionId, IReadOnlyList<AcpConfigOption> Options)> NewSessionAsync(
        string cwd,
        CancellationToken ct = default)
    {
        var result = await rpc.RequestAsync("session/new", new { cwd, mcpServers = Array.Empty<object>() }, ct)
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
        CancellationToken ct = default)
    {
        var result = await rpc.RequestAsync(
            "session/resume",
            new { sessionId, cwd, mcpServers = Array.Empty<object>() },
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
