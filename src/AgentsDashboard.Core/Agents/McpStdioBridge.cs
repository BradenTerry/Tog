using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentsDashboard.Core.Agents;

/// <summary>
/// The dashboard's MCP server as a stdio server, for an agent the dashboard did
/// not start: Claude runs the app with <c>mcp</c> and this forwards every
/// message to whichever dashboard is running.
/// </summary>
/// <remarks>
/// <para>
/// Agents the dashboard runs are handed the HTTP server directly, with a key
/// of their own. A terminal agent's config is written once, while the port
/// changes every start, so it names a command instead, and the command finds
/// the app through <see cref="McpLink"/> on every message.
/// </para>
/// <para>
/// With no dashboard open it still answers, with no tools and a failed call
/// that says to open it, so the agent's MCP list shows the server as connected
/// rather than broken. It tells the agent the list changed when the dashboard
/// opens or closes, which the HTTP server cannot do since it keeps no stream.
/// </para>
/// <para>
/// When the dashboard started the agent after all, its session key is in the
/// environment and is used first, so a tool still sees which agent is calling.
/// </para>
/// </remarks>
public sealed class McpStdioBridge
{
    /// <summary>What the server is called, in the agent and in Claude's config.</summary>
    public const string Name = "agents-dashboard";

    /// <summary>The folder a caller with the terminal key is working in, URL-escaped.</summary>
    public const string CwdHeader = "X-Agents-Dashboard-Cwd";

    /// <summary>The variable a session the dashboard started holds its own key in.</summary>
    public const string KeyVariable = "AGENTS_DASHBOARD_MCP_KEY";

    public static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(2);

    private readonly Func<McpLink?> _link;
    private readonly HttpClient _http;
    private readonly string _cwd;
    private readonly string? _sessionKey;
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private volatile bool _initialized;

    public McpStdioBridge(Func<McpLink?> link, HttpMessageHandler handler, string cwd, string? sessionKey, TextWriter output)
    {
        _link = link;

        // A tool call can hold the request for as long as the tool takes.
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _cwd = cwd;
        _sessionKey = string.IsNullOrEmpty(sessionKey) ? null : sessionKey;
        _output = output;
    }

    /// <summary>Runs over the process's own stdin and stdout until stdin closes.</summary>
    public static async Task RunConsoleAsync(string linkFile)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8);
        using var handler = new SocketsHttpHandler { UseProxy = false };
        var bridge = new McpStdioBridge(() => McpLink.Read(linkFile), handler, Environment.CurrentDirectory,
            Environment.GetEnvironmentVariable(KeyVariable), output);
        await bridge.RunAsync(input, CancellationToken.None);
    }

    public async Task RunAsync(TextReader input, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watching = WatchAsync(stop.Token);
        var pending = new List<Task>();

        // Messages are handled side by side: a tool call can take minutes, and
        // a ping or a second call must not wait behind it.
        while (await input.ReadLineAsync(ct) is { } line)
        {
            if (line.Length > 0)
            {
                pending.RemoveAll(t => t.IsCompleted);
                pending.Add(Task.Run(() => HandleAsync(line, ct), ct));
            }
        }

        await Task.WhenAll(pending);
        await stop.CancelAsync();
        try
        {
            await watching;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Tells the agent to list the tools again whenever the dashboard opens, closes or restarts.</summary>
    private async Task WatchAsync(CancellationToken ct)
    {
        var last = _link()?.Url;
        while (true)
        {
            await Task.Delay(WatchInterval, ct);
            var now = _link()?.Url;
            if (now != last)
            {
                last = now;
                if (_initialized)
                {
                    await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" });
                }
            }
        }
    }

    private async Task HandleAsync(string line, CancellationToken ct)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(line);
        }
        catch (System.Text.Json.JsonException)
        {
            await WriteAsync(Error(null, -32700, "The message is not JSON."));
            return;
        }

        if (message is not (JsonObject or JsonArray))
        {
            await WriteAsync(Error(null, -32600, "Expected a JSON-RPC message."));
            return;
        }

        if (message is JsonObject { } one && Str(one["method"]) is "initialize" or "notifications/initialized")
        {
            _initialized = true;
        }

        if (_link() is { } link && await ForwardAsync(link, line, ct) is { } answered)
        {
            if (answered.Length > 0)
            {
                await WriteAsync(Patch(JsonNode.Parse(answered)));
            }

            return;
        }

        var ids = message is JsonArray batch ? batch.OfType<JsonObject>() : [(JsonObject)message];
        var answers = ids.Select(Offline).OfType<JsonObject>().ToArray();
        if (message is JsonArray)
        {
            if (answers.Length > 0)
            {
                await WriteAsync(new JsonArray(answers));
            }
        }
        else if (answers.Length == 1)
        {
            await WriteAsync(answers[0]);
        }
    }

    /// <summary>
    /// The dashboard's answer, empty for a notification it accepted, or null
    /// when it could not be reached and the bridge answers for it.
    /// </summary>
    private async Task<string?> ForwardAsync(McpLink link, string body, CancellationToken ct)
    {
        foreach (var key in _sessionKey is null ? [link.Key] : new[] { _sessionKey, link.Key })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, link.Url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation(CwdHeader, Uri.EscapeDataString(_cwd));

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException)
            {
                return null;
            }

            using (response)
            {
                // The session's key ends with its session. Its agent keeps
                // running tools as a terminal agent would.
                if (response.StatusCode == HttpStatusCode.Unauthorized && key == _sessionKey)
                {
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    return "";
                }

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(ct);
                }

                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The dashboard says its list never changes, which is true of one server
    /// process, but through the bridge it does when the dashboard restarts.
    /// </summary>
    private static JsonNode? Patch(JsonNode? answer)
    {
        if (answer?["result"]?["capabilities"] is JsonObject capabilities)
        {
            capabilities["tools"] = new JsonObject { ["listChanged"] = true };
        }

        return answer;
    }

    /// <summary>The bridge's own answer with no dashboard to ask, or null for a notification.</summary>
    private static JsonObject? Offline(JsonObject request)
    {
        var id = request["id"]?.DeepClone();
        if (id is null)
        {
            return null;
        }

        return Str(request["method"]) switch
        {
            "initialize" => Result(id, new JsonObject
            {
                ["protocolVersion"] = Str(request["params"]?["protocolVersion"]) ?? "2025-06-18",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
                ["serverInfo"] = new JsonObject { ["name"] = Name, ["version"] = "1.0.0" },
                ["instructions"] = "Tools from the Agents Dashboard. It is not open right now, so there are none until it is.",
            }),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = new JsonArray() }),
            "tools/call" => Result(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "The Agents Dashboard is not open. Ask the user to open it, then try again.",
                }),
                ["isError"] = true,
            }),
            var method => Error(id, -32601, $"{method} is not offered."),
        };
    }

    private async Task WriteAsync(JsonNode? message)
    {
        // One message per line: ToJsonString writes none of its own.
        var text = message?.ToJsonString() ?? "null";
        await _writing.WaitAsync();
        try
        {
            await _output.WriteLineAsync(text);
            await _output.FlushAsync();
        }
        finally
        {
            _writing.Release();
        }
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Result(JsonNode id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
