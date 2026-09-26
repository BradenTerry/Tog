using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentsDashboard.Core.Agents;
using AgentsDashboard.Extensions;

namespace AgentsDashboard.App.Extensions;

/// <summary>
/// Serves the loaded extensions' agent tools as one MCP server, and tells the
/// agent host to hand it to every session.
/// </summary>
/// <remarks>
/// <para>
/// MCP's Streamable HTTP transport, the part of it a tools-only server needs:
/// the client POSTs one JSON-RPC message and the server may answer with plain
/// JSON rather than open an event stream. So there is no SDK here, only
/// <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c> and <c>ping</c>. The
/// tool list is read on every request, so an extension that reloads serves its
/// new tools at once; an agent that already listed them only sees a new one on
/// its next session or resume, since without a stream there is no way to tell
/// it the list changed.
/// </para>
/// <para>
/// A tool can start processes in a worktree, so who may call matters. Every
/// request needs a key made fresh each time the app starts, sent as a bearer
/// token. The key is never in the server's address or headers as handed to the
/// agent: the SDK puts those on the Claude CLI's command line, which any process
/// on the machine can list. The header names an environment variable instead,
/// <c>${AGENTS_DASHBOARD_MCP_KEY}</c>, which the CLI expands, and the value is
/// set only in the agent process's environment (<see cref="Environment"/>), which
/// only the same user can read. On top of that, only loopback callers are
/// answered, and a request with an Origin header or a body that is not JSON is
/// refused before it is read: that is what a web page's request looks like, and
/// the CLI never sends one. The app binds to loopback whatever its options, so a
/// caller from elsewhere only gets in through a tunnel the user set up, and then
/// the key is the only guard.
/// </para>
/// <para>
/// The folder the agent works in rides in the address: ACP gives an MCP server
/// nothing else to tell one session's calls from another's.
/// </para>
/// </remarks>
public sealed class AgentToolServer(AgentToolServer.Endpoint endpoint, ExtensionHost extensions, ILogger<AgentToolServer> log)
    : IAgentMcpServers
{
    /// <summary>Where the app listens.</summary>
    public sealed record Endpoint(int Port);

    /// <summary>What agents see the server as: its tools are <c>mcp__agents-dashboard__{name}</c>.</summary>
    public const string Name = "agents-dashboard";

    /// <summary>The variable the agent process holds the key in, which the header names.</summary>
    public const string KeyVariable = "AGENTS_DASHBOARD_MCP_KEY";

    /// <summary>Messages served from one request. A batch is answered in order, and a call can wait minutes.</summary>
    private const int MaxBatch = 16;

    private static readonly string[] Versions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly string _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public IReadOnlyList<McpServer> For(string cwd) =>
        [new McpServer(
            Name,
            $"http://127.0.0.1:{endpoint.Port}/_mcp?cwd={Uri.EscapeDataString(cwd)}",
            [new("Authorization", $"Bearer ${{{KeyVariable}}}")])];

    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string> { [KeyVariable] = _key };

    public static void Map(WebApplication app)
    {
        // Only with --verbose, which is for debugging: enough to call a tool by hand.
        app.Services.GetRequiredService<AgentToolServer>().LogAddress();

        app.MapPost("/_mcp", (HttpContext http, AgentToolServer server) => server.HandleAsync(http));

        // No event stream is offered; the spec's answer to a GET for one is 405.
        app.MapMethods("/_mcp", ["GET", "DELETE"], () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
    }

    private void LogAddress() =>
        log.LogInformation("Agent tools are served at {Url} with the header Authorization: Bearer {Key}", For("<cwd>")[0].Url, _key);

    private async Task<IResult> HandleAsync(HttpContext http)
    {
        if (http.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
            || http.Request.Headers.Origin.Count > 0
            || http.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var auth = http.Request.Headers.Authorization.ToString();
        var given = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth["Bearer ".Length..].Trim() : "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(_key)))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var cwd = http.Request.Query["cwd"].FirstOrDefault();
        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.Json(Error(null, -32700, "The request is not JSON."));
        }

        if (body is JsonArray { Count: > MaxBatch })
        {
            return Results.Json(Error(null, -32600, $"At most {MaxBatch} messages in one batch."));
        }

        if (body is JsonArray batch)
        {
            var answers = new JsonArray();
            foreach (var message in batch)
            {
                if (message is JsonObject one && await DispatchAsync(one, cwd, http.RequestAborted) is { } answer)
                {
                    answers.Add(answer);
                }
            }

            return answers.Count == 0 ? Results.Accepted() : Results.Json(answers);
        }

        if (body is not JsonObject request)
        {
            return Results.Json(Error(null, -32600, "Expected a JSON-RPC message."));
        }

        return await DispatchAsync(request, cwd, http.RequestAborted) is { } response
            ? Results.Json(response)
            : Results.Accepted();
    }

    /// <summary>The answer to one message, or null for a notification, which gets none.</summary>
    private async Task<JsonObject?> DispatchAsync(JsonObject request, string? cwd, CancellationToken ct)
    {
        var id = request["id"]?.DeepClone();
        var method = Str(request["method"]);
        if (id is null)
        {
            return null;
        }

        var parameters = request["params"] as JsonObject;
        return method switch
        {
            "initialize" => Result(id, Initialize(parameters)),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = ListTools() }),
            "tools/call" => await CallAsync(id, parameters, cwd, ct),
            _ => Error(id, -32601, $"{method} is not offered."),
        };
    }

    private static JsonObject Initialize(JsonObject? parameters)
    {
        var asked = Str(parameters?["protocolVersion"]);
        return new JsonObject
        {
            ["protocolVersion"] = Versions.Contains(asked) ? asked : Versions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = Name, ["version"] = typeof(AgentToolServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" },
            ["instructions"] = "Tools from the Agents Dashboard, the app you are running in. What they start shows to the user live, so prefer them to the shell equivalent.",
        };
    }

    private JsonArray ListTools()
    {
        var list = new JsonArray();
        foreach (var (name, (_, tool)) in extensions.AgentTools().OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            JsonNode schema;
            try
            {
                schema = JsonNode.Parse(tool.InputSchema) ?? new JsonObject { ["type"] = "object" };
            }
            catch (JsonException e)
            {
                log.LogWarning(e, "The agent tool {Tool} has an input schema that is not JSON", name);
                continue;
            }

            list.Add(new JsonObject { ["name"] = name, ["description"] = tool.Description, ["inputSchema"] = schema });
        }

        return list;
    }

    private async Task<JsonObject> CallAsync(JsonNode id, JsonObject? parameters, string? cwd, CancellationToken ct)
    {
        var name = Str(parameters?["name"]);
        if (name is null || !extensions.AgentTools().TryGetValue(name, out var found))
        {
            return Error(id, -32602, $"There is no tool called {name}.");
        }

        using var arguments = JsonDocument.Parse(parameters?["arguments"]?.ToJsonString() ?? "{}");
        AgentToolResult result;
        try
        {
            result = await found.Tool.CallAsync(new AgentToolCall(arguments.RootElement.Clone(), cwd), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The extension's mistake goes back to the agent as a failed call,
            // which it can read and work around, not as a broken server.
            log.LogWarning(e, "The agent tool {Tool} of {Extension} threw", name, found.ExtensionId);
            result = AgentToolResult.Error($"{name} failed: {e.Message}");
        }

        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
            ["isError"] = result.IsError,
        });
    }

    /// <summary>A string, or null for anything else: a caller's JSON can hold a number where a name belongs.</summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Result(JsonNode id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
