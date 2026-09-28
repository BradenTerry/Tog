using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentsDashboard.Core.Agents;
using AgentsDashboard.Extensions;

namespace AgentsDashboard.App.Extensions;

/// <summary>
/// Serves the app's own agent tools and the loaded extensions' as one MCP
/// server, and tells the agent host to hand it to every session.
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
/// request needs a bearer key, and every session has its own, minted when it
/// starts or resumes and revoked when it stops. The key is never in the server's
/// address or headers as handed to the agent: the SDK puts those on the Claude
/// CLI's command line, which any process on the machine can list. The header
/// names an environment variable instead, <c>${AGENTS_DASHBOARD_MCP_KEY}</c>,
/// which the CLI expands, and the value is set only in that session's CLI
/// process (<see cref="McpGrant.Environment"/>), which only the same user can
/// read. On top of that, only loopback callers are
/// answered, and a request with an Origin header or a body that is not JSON is
/// refused before it is read: that is what a web page's request looks like, and
/// the CLI never sends one. The app binds to loopback whatever its options, so a
/// caller from elsewhere only gets in through a tunnel the user set up, and then
/// the key is the only guard.
/// </para>
/// <para>
/// The key is also who is calling. ACP gives an MCP server nothing to tell one
/// session's calls from another's, and anything in the address could be edited
/// by the caller, so the folder and the session a tool sees are the ones the key
/// was minted for. A process the agent runs inherits the key and can call as
/// that agent, which is no more than the agent could do itself, but not as any
/// other.
/// </para>
/// <para>
/// An agent started in a terminal reaches the server through
/// <see cref="McpStdioBridge"/>, with one more key minted per start and left
/// in <see cref="McpLink"/>, readable by the user alone. That key says nothing
/// about who is calling, so it is the one caller whose folder comes from the
/// request: the folder the bridge was started in. Its calls have no agent id.
/// </para>
/// </remarks>
public sealed class AgentToolServer(
    AgentToolServer.Endpoint endpoint,
    IEnumerable<IAgentTool> builtIn,
    ExtensionHost extensions,
    ILogger<AgentToolServer> log)
    : IAgentMcpServers
{
    /// <summary>Where the app listens.</summary>
    public sealed record Endpoint(int Port);

    /// <summary>What agents see the server as: its tools are <c>mcp__agents-dashboard__{name}</c>.</summary>
    public const string Name = McpStdioBridge.Name;

    /// <summary>The variable the agent process holds the key in, which the header names.</summary>
    public const string KeyVariable = McpStdioBridge.KeyVariable;

    /// <summary>Messages served from one request. A batch is answered in order, and a call can wait minutes.</summary>
    private const int MaxBatch = 16;

    private static readonly string[] Versions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>
    /// The live keys, by their SHA-256 rather than themselves, so finding one
    /// takes the same time however much of a guess is right.
    /// </summary>
    private readonly ConcurrentDictionary<string, Caller> _callers = new(StringComparer.Ordinal);

    /// <summary>
    /// Who a key was minted for. The session is null between <c>session/new</c>
    /// being sent and answered, and always for the terminal key, whose folder
    /// is each request's own.
    /// </summary>
    private sealed record Caller(string Cwd, string? SessionId, bool Terminal = false);

    private readonly string _terminalKey = NewKey();

    private readonly IReadOnlyList<IAgentTool> _builtIn = [.. builtIn];

    /// <summary>Clashes already logged: the list is read on every request.</summary>
    private readonly ConcurrentDictionary<(string, string), bool> _clashes = new();

    private string Url => $"http://127.0.0.1:{endpoint.Port}/_mcp";

    /// <summary>What <see cref="McpStdioBridge"/> needs to reach this server, for as long as the app runs.</summary>
    public McpLink Link()
    {
        _callers[Hash(_terminalKey)] = new Caller("", null, Terminal: true);
        return new McpLink(Url, _terminalKey, Environment.ProcessId);
    }

    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public McpGrant Grant(string cwd, string? sessionId)
    {
        var key = NewKey();
        _callers[Hash(key)] = new Caller(cwd, sessionId);

        // Only with --verbose, which is for debugging: enough to call a tool by hand.
        log.LogInformation("Agent tools for {Cwd} are served at {Url} with the header Authorization: Bearer {Key}", cwd, Url, key);

        return new McpGrant(
            key,
            [new McpServer(Name, Url, [new("Authorization", $"Bearer ${{{KeyVariable}}}")])],
            new Dictionary<string, string> { [KeyVariable] = key });
    }

    public void Bind(string key, string sessionId)
    {
        var hash = Hash(key);
        if (_callers.TryGetValue(hash, out var caller))
        {
            _callers.TryUpdate(hash, caller with { SessionId = sessionId }, caller);
        }
    }

    public void Revoke(string key) => _callers.TryRemove(Hash(key), out _);

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static void Map(WebApplication app)
    {
        app.MapPost("/_mcp", (HttpContext http, AgentToolServer server) => server.HandleAsync(http));

        // No event stream is offered; the spec's answer to a GET for one is 405.
        app.MapMethods("/_mcp", ["GET", "DELETE"], () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
    }

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
        if (given.Length == 0 || !_callers.TryGetValue(Hash(given), out var caller))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        if (caller.Terminal)
        {
            var cwd = Uri.UnescapeDataString(http.Request.Headers[McpStdioBridge.CwdHeader].ToString());
            if (!Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            caller = caller with { Cwd = cwd };
        }

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
                if (message is JsonObject one && await DispatchAsync(one, caller, http.RequestAborted) is { } answer)
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

        return await DispatchAsync(request, caller, http.RequestAborted) is { } response
            ? Results.Json(response)
            : Results.Accepted();
    }

    /// <summary>The answer to one message, or null for a notification, which gets none.</summary>
    private async Task<JsonObject?> DispatchAsync(JsonObject request, Caller caller, CancellationToken ct)
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
            "tools/call" => await CallAsync(id, parameters, caller, ct),
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
        foreach (var (name, (_, tool)) in Tools().OrderBy(t => t.Key, StringComparer.Ordinal))
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

    private async Task<JsonObject> CallAsync(JsonNode id, JsonObject? parameters, Caller caller, CancellationToken ct)
    {
        var name = Str(parameters?["name"]);
        if (name is null || !Tools().TryGetValue(name, out var found))
        {
            return Error(id, -32602, $"There is no tool called {name}.");
        }

        using var arguments = JsonDocument.Parse(parameters?["arguments"]?.ToJsonString() ?? "{}");
        AgentToolResult result;
        try
        {
            var call = new AgentToolCall(arguments.RootElement.Clone(), caller.Cwd) { AgentId = caller.SessionId };
            result = await found.Tool.CallAsync(call, ct);
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

    /// <summary>
    /// The app's tools, then every loaded extension's. An extension that picks
    /// a name the app uses loses it, so an extension cannot stand in for the
    /// app's own tool.
    /// </summary>
    private Dictionary<string, (string ExtensionId, IAgentTool Tool)> Tools()
    {
        var tools = _builtIn.ToDictionary(t => t.Name, t => (ExtensionId: "app", Tool: t), StringComparer.Ordinal);
        foreach (var (name, found) in extensions.AgentTools())
        {
            if (!tools.TryAdd(name, found) && _clashes.TryAdd((found.ExtensionId, name), true))
            {
                log.LogWarning("{Extension} adds the agent tool {Tool}, which the app already has", found.ExtensionId, name);
            }
        }

        return tools;
    }

    /// <summary>A string, or null for anything else: a caller's JSON can hold a number where a name belongs.</summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Result(JsonNode id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
