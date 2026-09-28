using System.Net;
using System.Text.Json.Nodes;
using AgentsDashboard.Core.Agents;
using AgentsDashboard.Core.Claude;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class McpBridgeTests
{
    private static readonly McpLink Running = new("http://127.0.0.1:1/_mcp", "terminal-key", Environment.ProcessId);

    /// <summary>Answers every POST with what <paramref name="answer"/> says, keeping each request.</summary>
    private sealed class FakeServer(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (Seen)
            {
                Seen.Add((request, body));
            }

            return answer(request, body);
        }
    }

    private static HttpResponseMessage Json(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json") };

    private static async Task<List<JsonNode>> Run(McpLink? link, HttpMessageHandler handler, string? sessionKey, params string[] lines)
    {
        var output = new StringWriter();
        var bridge = new McpStdioBridge(() => link, handler, "/work/repo", sessionKey, output);
        await bridge.RunAsync(new StringReader(string.Join('\n', lines)), CancellationToken.None);
        return [.. output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!)];
    }

    [Fact]
    public async Task With_no_dashboard_open_it_answers_with_no_tools_and_a_failed_call()
    {
        var answers = await Run(null, new FakeServer((_, _) => throw new InvalidOperationException()), null,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"x"}}""");

        var byId = answers.ToDictionary(a => (int)a["id"]!);
        Assert.Equal(3, byId.Count);
        Assert.Equal("2025-03-26", (string?)byId[1]["result"]!["protocolVersion"]);
        Assert.True((bool)byId[1]["result"]!["capabilities"]!["tools"]!["listChanged"]!);
        Assert.Empty(byId[2]["result"]!["tools"]!.AsArray());
        Assert.True((bool)byId[3]["result"]!["isError"]!);
    }

    [Fact]
    public async Task It_forwards_with_the_terminal_key_and_the_folder_and_says_the_list_can_change()
    {
        var server = new FakeServer((_, _) => Json(
            """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18","capabilities":{"tools":{"listChanged":false}}}}"""));

        var answers = await Run(Running, server, null,
            """{"jsonrpc":"2.0","id":1,"method":"initialize"}""");

        var (request, _) = Assert.Single(server.Seen);
        Assert.Equal("terminal-key", request.Headers.Authorization!.Parameter);
        Assert.Equal(Uri.EscapeDataString("/work/repo"), request.Headers.GetValues(McpStdioBridge.CwdHeader).Single());
        Assert.True((bool)Assert.Single(answers)["result"]!["capabilities"]!["tools"]!["listChanged"]!);
    }

    [Fact]
    public async Task A_session_key_is_tried_first_and_a_revoked_one_falls_back_to_the_terminal_key()
    {
        var server = new FakeServer((request, _) => request.Headers.Authorization!.Parameter == "session-key"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : Json("""{"jsonrpc":"2.0","id":7,"result":{"tools":[]}}"""));

        var answers = await Run(Running, server, "session-key", """{"jsonrpc":"2.0","id":7,"method":"tools/list"}""");

        Assert.Equal(["session-key", "terminal-key"], server.Seen.Select(s => s.Request.Headers.Authorization!.Parameter));
        Assert.Equal(7, (int)Assert.Single(answers)["id"]!);
    }

    [Fact]
    public async Task A_notification_the_dashboard_accepts_gets_no_answer()
    {
        var server = new FakeServer((_, _) => new HttpResponseMessage(HttpStatusCode.Accepted));

        var answers = await Run(Running, server, null, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Empty(answers);
    }

    [Fact]
    public void A_link_round_trips_and_is_withdrawn_only_by_its_own_process()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "mcp-link.json");
        Running.Write(file);

        Assert.Equal(Running, McpLink.Read(file));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }

        McpLink.Withdraw(file, Environment.ProcessId + 1);
        Assert.True(File.Exists(file));
        McpLink.Withdraw(file, Environment.ProcessId);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void A_link_left_by_an_app_that_has_exited_is_ignored()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "mcp-link.json");
        (Running with { Pid = int.MaxValue }).Write(file);

        Assert.Null(McpLink.Read(file));
    }

    private static ClaudeMcpConfig Config(TempDir dir) =>
        new(new ClaudePaths(new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = dir.Path }));

    private static readonly McpCommand Ours = new("/Apps/Agents Dashboard", ["mcp"]);

    [Fact]
    public void The_entry_is_missing_until_claude_has_one_by_that_name()
    {
        using var dir = new TempDir();
        Assert.Equal(McpEntryState.Missing, Config(dir).State(Ours));

        dir.File(".claude.json", """{"mcpServers":{"other":{"command":"x"}}}""");
        Assert.Equal(McpEntryState.Missing, Config(dir).State(Ours));
    }

    [Fact]
    public void The_entry_is_current_only_when_it_starts_this_copy()
    {
        using var dir = new TempDir();
        dir.File(".claude.json", $$$"""{"numStartups":3,"mcpServers":{"agents-dashboard":{{{ClaudeMcpConfig.EntryJson(Ours)}}}}}""");
        Assert.Equal(McpEntryState.Current, Config(dir).State(Ours));

        Assert.Equal(McpEntryState.Different, Config(dir).State(Ours with { Command = "/elsewhere/AgentsDashboard.App" }));
        Assert.Equal(McpEntryState.Different, Config(dir).State(Ours with { Args = ["mcp", "--data-dir", "/tmp/d"] }));
    }

    [Fact]
    public void The_shell_command_survives_a_quote_in_a_path()
    {
        var command = ClaudeMcpConfig.ShellCommand(new McpCommand("/Users/o'neil/app", ["mcp"]));

        Assert.StartsWith("claude mcp add-json --scope user agents-dashboard '", command);
        Assert.EndsWith("'", command);
        Assert.Equal(2, command.Count(c => c == '\''));
    }
}
