using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Togue.Core.Agents;

namespace Togue.Core.Tests.Support;

/// <summary>
/// An ACP agent in memory, speaking the protocol over pipes through the same
/// <see cref="JsonRpcConnection"/> the host uses, so a test drives the real wire.
/// </summary>
public sealed class FakeAcpAgent : IAgentLauncher
{
    private readonly List<Running> _running = [];

    /// <summary>Every request and notification the agent received, as "method" or "method:detail".</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>The mcpServers of the last session/new, as JSON.</summary>
    public string? McpServers { get; set; }

    /// <summary>The _meta of the last session/new or session/resume, as JSON, null when it had none.</summary>
    public string? SessionMeta { get; set; }

    public int Launches { get; private set; }

    /// <summary>
    /// What a prompt does, given a way to send updates and to ask for permission.
    /// Returns the stop reason. By default it says "hello" in two chunks.
    /// </summary>
    public Func<PromptScript, Task<string>> OnPrompt { get; set; } = async script =>
    {
        await script.Say("hel");
        await script.Say("lo");
        return "end_turn";
    };

    public IAgentProcess Launch(AgentBackend backend)
    {
        Launches++;
        var toAgent = new AnonymousPipeServerStream(PipeDirection.Out);
        var agentReads = new AnonymousPipeClientStream(PipeDirection.In, toAgent.ClientSafePipeHandle);
        var toHost = new AnonymousPipeServerStream(PipeDirection.Out);
        var hostReads = new AnonymousPipeClientStream(PipeDirection.In, toHost.ClientSafePipeHandle);

        var rpc = new JsonRpcConnection(agentReads, toHost);
        var running = new Running(this, rpc, hostReads, toAgent, agentReads, toHost);
        rpc.RequestHandler = running.HandleAsync;
        rpc.Notified += (method, p) => Calls.Enqueue(method + ":" + Text(p, "sessionId"));
        rpc.Start();
        _running.Add(running);
        return running;
    }

    /// <summary>Kills the agent process, as a crash would.</summary>
    public async Task CrashAsync()
    {
        foreach (var running in _running)
        {
            await running.DisposeAsync();
        }

        _running.Clear();
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static object Options(string model = "opus[1m]", string mode = "auto") => new object[]
    {
        new
        {
            id = "model", name = "Model", type = "select", currentValue = model,
            options = new object[]
            {
                new { value = "default", name = "Default" },
                new { value = "opus[1m]", name = "Opus 5.5" },
                new { value = "haiku", name = "Haiku 4.5" },
            },
        },
        new
        {
            id = "mode", name = "Mode", type = "select", currentValue = mode,
            options = new object[]
            {
                new { value = "default", name = "Manual" },
                new { value = "acceptEdits", name = "Accept edits" },
                new { value = "auto", name = "Auto" },
            },
        },
    };

    public sealed class PromptScript(JsonRpcConnection rpc, string sessionId, string text)
    {
        public string Text { get; } = text;

        public Task Say(string chunk) => rpc.NotifyAsync("session/update", new
        {
            sessionId,
            update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = chunk } },
        });

        public Task Title(string title) => rpc.NotifyAsync("session/update", new
        {
            sessionId,
            update = new { sessionUpdate = "session_info_update", title },
        });

        public Task Usage(long used, long size) => rpc.NotifyAsync("session/update", new
        {
            sessionId,
            update = new { sessionUpdate = "usage_update", used, size, cost = new { amount = 0.12, currency = "USD" } },
        });

        /// <summary>A usage update carrying the plan's rate limits, as the Claude bridge sends one.</summary>
        public Task RateLimit(object rateLimit) => rpc.NotifyAsync("session/update", new
        {
            sessionId,
            update = new Dictionary<string, object>
            {
                ["sessionUpdate"] = "usage_update",
                ["used"] = 1_000,
                ["size"] = 200_000,
                ["_meta"] = new Dictionary<string, object> { ["_claude/rateLimit"] = rateLimit },
            },
        });

        /// <summary>The slash commands the session takes, the way the Claude bridge lists them.</summary>
        public Task Commands(params object[] availableCommands) => rpc.NotifyAsync("session/update", new
        {
            sessionId,
            update = new { sessionUpdate = "available_commands_update", availableCommands },
        });

        public Task Tool(string title) => rpc.NotifyAsync("session/update", new
        {
            sessionId,
            update = new { sessionUpdate = "tool_call", toolCallId = "t1", title, status = "pending" },
        });

        /// <summary>
        /// Sends an elicitation and returns the client's whole response. The bridge
        /// ties AskUserQuestion to its tool call; an MCP server's form has no
        /// <paramref name="toolCallId"/>.
        /// </summary>
        public Task<JsonElement> Elicit(string message, object requestedSchema, string mode = "form", string? toolCallId = "t1") =>
            rpc.RequestAsync("elicitation/create", new
            {
                mode,
                sessionId,
                toolCallId,
                message,
                requestedSchema,
            });

        /// <summary>Asks for permission and returns the chosen option id, or null when declined.</summary>
        public async Task<string?> Ask(string title)
        {
            var answer = await rpc.RequestAsync("session/request_permission", new
            {
                sessionId,
                toolCall = new { toolCallId = "t1", title, rawInput = new { description = "because" } },
                options = new object[]
                {
                    new { optionId = "allow-once", name = "Yes", kind = "allow_once" },
                    new { optionId = "reject", name = "No", kind = "reject_once" },
                },
            });

            var outcome = answer.GetProperty("outcome");
            return outcome.GetProperty("outcome").GetString() == "selected"
                ? outcome.GetProperty("optionId").GetString()
                : null;
        }
    }

    private sealed class Running(
        FakeAcpAgent owner,
        JsonRpcConnection rpc,
        Stream input,
        Stream output,
        Stream agentReads,
        Stream agentWrites) : IAgentProcess
    {
        private int _sessions;

        public Stream Input { get; } = input;

        public Stream Output { get; } = output;

        public int ProcessId => 4242;

        public string RecentErrors => "fake agent log line";

        public async Task<object?> HandleAsync(string method, JsonElement p, CancellationToken ct)
        {
            var sessionId = Text(p, "sessionId");
            switch (method)
            {
                case "initialize":
                    owner.Calls.Enqueue(method);
                    if (p.TryGetProperty("clientCapabilities", out var caps)
                        && caps.TryGetProperty("elicitation", out var elicitation)
                        && elicitation.TryGetProperty("form", out _))
                    {
                        owner.Calls.Enqueue("initialize:elicitation.form");
                    }

                    return new { protocolVersion = 1, agentCapabilities = new { loadSession = true, mcpCapabilities = new { http = true }, sessionCapabilities = new { resume = new { }, close = new { } } } };

                case "session/new":
                    owner.Calls.Enqueue(method);
                    owner.McpServers = p.GetProperty("mcpServers").GetRawText();
                    owner.SessionMeta = p.TryGetProperty("_meta", out var newMeta) ? newMeta.GetRawText() : null;
                    return new { sessionId = "s" + Interlocked.Increment(ref _sessions), configOptions = Options() };

                case "session/resume":
                    owner.Calls.Enqueue(method + ":" + sessionId);
                    owner.SessionMeta = p.TryGetProperty("_meta", out var resumeMeta) ? resumeMeta.GetRawText() : null;
                    return new { configOptions = Options() };

                case "session/set_config_option":
                    owner.Calls.Enqueue($"{method}:{Text(p, "configId")}={Text(p, "value")}");
                    return new { configOptions = Options(model: Text(p, "configId") == "model" ? Text(p, "value")! : "opus[1m]") };

                case "session/close":
                    owner.Calls.Enqueue(method + ":" + sessionId);
                    return new { };

                case "session/prompt":
                    var text = p.GetProperty("prompt")[0].GetProperty("text").GetString()!;
                    owner.Calls.Enqueue(method + ":" + text);
                    var reason = await owner.OnPrompt(new PromptScript(rpc, sessionId!, text));
                    return new { stopReason = reason };

                default:
                    throw new JsonRpcException(JsonRpcConnection.MethodNotFound, method);
            }
        }

        /// <remarks>
        /// Ends the way a process does: the writing ends close first, which is
        /// what ends a read blocked on the other side. On Windows an anonymous
        /// pipe is synchronous, and closing the end a read is blocked on does not
        /// wake it, so a crash made by closing the host's read end is never seen
        /// and the host waits for ever. Unix wakes the read either way.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            await agentWrites.DisposeAsync();
            await Output.DisposeAsync();
            await rpc.DisposeAsync();
            await Input.DisposeAsync();
            await agentReads.DisposeAsync();
        }
    }
}
