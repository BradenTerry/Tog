using System.Text.Json;

namespace AgentsDashboard.Extensions;

/// <summary>
/// A tool the agents the dashboard runs can call, such as starting a test run
/// and asking how it is going. Since API 1.2.
/// </summary>
/// <remarks>
/// <para>
/// The app serves every loaded extension's tools as one MCP server on its own
/// loopback address and hands it to each agent it starts or resumes, so the
/// agent sees them beside its own tools. Agents started in a terminal do not
/// get them: the dashboard never writes their configuration.
/// </para>
/// <para>
/// A call runs inside the app, so whatever it starts is the dashboard's to show
/// the moment it starts. Answer quickly: start long work and return, and give
/// the agent a second tool to ask how it is going, rather than holding the call
/// open for minutes.
/// </para>
/// </remarks>
public interface IAgentTool
{
    /// <summary>
    /// Lower case letters, digits and underscores, at most 48 characters, and
    /// unique across the loaded extensions. Prefix it with what the extension is
    /// about (<c>tests_run</c>), since the agent sees every extension's tools in
    /// one list.
    /// </summary>
    string Name { get; }

    /// <summary>What the tool does and when to reach for it, written for the agent.</summary>
    string Description { get; }

    /// <summary>A JSON Schema object for the arguments, as JSON text.</summary>
    string InputSchema { get; }

    /// <summary>Runs the tool. An exception is returned to the agent as an error result.</summary>
    Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct);
}

/// <summary>One call of an agent tool.</summary>
/// <param name="Arguments">The arguments the agent passed, an object, empty when it passed none.</param>
/// <param name="Cwd">The folder the calling agent works in, when the app knows it.</param>
public sealed record AgentToolCall(JsonElement Arguments, string? Cwd)
{
    /// <summary>A string argument, or null when it is missing or not a string.</summary>
    public string? Text(string name) =>
        Arguments.ValueKind == JsonValueKind.Object
        && Arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A whole-number argument, or null when it is missing or not a number.</summary>
    public long? Number(string name) =>
        Arguments.ValueKind == JsonValueKind.Object
        && Arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;
}

/// <summary>What a tool call returns to the agent.</summary>
/// <param name="Text">The answer, as text the agent reads. JSON is fine.</param>
/// <param name="IsError">True when the call failed; the agent is told so.</param>
public sealed record AgentToolResult(string Text, bool IsError = false)
{
    /// <summary>A failed call, with why.</summary>
    public static AgentToolResult Error(string message) => new(message, true);
}
