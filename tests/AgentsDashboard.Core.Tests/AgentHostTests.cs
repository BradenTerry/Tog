using AgentsDashboard.Core.Agents;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class AgentHostTests
{
    private static readonly AgentBackend Backend = new("claude", "Claude", "fake", []);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (AgentHost Host, FakeAcpAgent Agent, TempDir Dir) Build(FakeAcpAgent? agent = null)
    {
        var dir = new TempDir();
        agent ??= new FakeAcpAgent();
        var host = new AgentHost(Backend, agent, new HostedAgentStore(new AppPaths(dir.Path)));
        return (host, agent, dir);
    }

    /// <summary>Waits for the host to reach a state, since turns run in the background.</summary>
    private static async Task<HostedAgent> Until(AgentHost host, string sessionId, Func<HostedAgent, bool> done)
    {
        for (var i = 0; i < 200; i++)
        {
            if (host.Find(sessionId) is { } agent && done(agent))
            {
                return agent;
            }

            await Task.Delay(20, Ct);
        }

        throw new TimeoutException($"Agent {sessionId} never got there: {host.Find(sessionId)}");
    }

    [Fact]
    public async Task Starts_an_agent_and_runs_its_first_turn_to_idle()
    {
        var (host, agent, dir) = Build();
        await using var _ = host;
        using var __ = dir;

        var result = await host.StartAsync(new AgentStart("/repo", Prompt: "fix the parser"), Ct);

        Assert.True(result.Ok, result.Message);
        var idle = await Until(host, result.SessionId!, a => a.State == HostedState.Idle);
        Assert.Null(idle.Title);
        Assert.Equal("fix the parser", idle.Prompt);
        Assert.Equal("", idle.LiveText);
        Assert.Contains("session/prompt:fix the parser", agent.Calls);
    }

    [Fact]
    public async Task Streams_what_the_agent_says_while_the_turn_runs()
    {
        var release = new TaskCompletionSource();
        var agent = new FakeAcpAgent
        {
            OnPrompt = async script =>
            {
                await script.Say("hel");
                await script.Say("lo");
                await release.Task;
                return "end_turn";
            },
        };
        var (host, _, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;

        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "go"), Ct)).SessionId!;

        var working = await Until(host, id, a => a.LiveText == "hello");
        Assert.Equal(HostedState.Working, working.State);
        release.SetResult();
        await Until(host, id, a => a.State == HostedState.Idle);
    }

    [Fact]
    public async Task Sets_the_model_by_the_name_you_know_it_by()
    {
        var (host, agent, dir) = Build();
        await using var _ = host;
        using var __ = dir;

        await host.StartAsync(new AgentStart("/repo", Model: "opus", Mode: "manual"), Ct);

        Assert.Contains("session/set_config_option:model=opus[1m]", agent.Calls);
        Assert.Contains("session/set_config_option:mode=default", agent.Calls);
    }

    [Fact]
    public async Task A_setting_the_agent_does_not_have_is_left_out()
    {
        var (host, agent, dir) = Build();
        await using var _ = host;
        using var __ = dir;

        var result = await host.StartAsync(new AgentStart("/repo", Model: "gpt-9"), Ct);

        Assert.True(result.Ok);
        Assert.DoesNotContain(agent.Calls, c => c.StartsWith("session/set_config_option", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_permission_prompt_waits_on_you_and_your_answer_goes_back()
    {
        string? answered = "unset";
        var agent = new FakeAcpAgent
        {
            OnPrompt = async script =>
            {
                answered = await script.Ask("touch made.txt");
                return "end_turn";
            },
        };
        var (host, _, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;

        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "go"), Ct)).SessionId!;

        var waiting = await Until(host, id, a => a.State == HostedState.Waiting);
        var ask = waiting.Permission!;
        Assert.Equal("touch made.txt", ask.Title);
        Assert.Equal("because", ask.Detail);
        Assert.Equal(["allow-once", "reject"], ask.Choices.Select(c => c.OptionId));
        Assert.Equal(AgentStatus.Waiting, Assert.Single(host.Read()).Status);

        host.Answer(id, ask.Key, "allow-once");

        await Until(host, id, a => a.State == HostedState.Idle);
        Assert.Equal("allow-once", answered);
    }

    [Fact]
    public async Task Stopping_mid_turn_declines_the_open_permission()
    {
        string? answered = "unset";
        var agent = new FakeAcpAgent
        {
            OnPrompt = async script =>
            {
                answered = await script.Ask("rm -rf build");
                return "cancelled";
            },
        };
        var (host, _, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;

        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "go"), Ct)).SessionId!;
        await Until(host, id, a => a.State == HostedState.Waiting);

        await host.CancelAsync(id, Ct);

        await Until(host, id, a => a.State == HostedState.Idle);
        Assert.Null(answered);
    }

    [Fact]
    public async Task A_stopped_agent_is_resumed_before_it_is_sent_a_message()
    {
        var (host, agent, dir) = Build();
        await using var _ = host;
        using var __ = dir;
        var id = (await host.StartAsync(new AgentStart(dir.Path), Ct)).SessionId!;
        await host.StopAsync(id, Ct);
        Assert.Equal(HostedState.Stopped, host.Find(id)!.State);
        Assert.Contains("session/close:" + id, agent.Calls);

        await host.SendAsync(id, "carry on", Ct);

        await Until(host, id, a => a.State == HostedState.Idle);
        var calls = agent.Calls.ToList();
        Assert.True(calls.IndexOf("session/resume:" + id) < calls.IndexOf("session/prompt:carry on"));
    }

    [Fact]
    public async Task Agents_come_back_as_stopped_after_a_restart()
    {
        var dir = new TempDir();
        using var _ = dir;
        var store = new HostedAgentStore(new AppPaths(dir.Path));

        string id;
        await using (var first = new AgentHost(Backend, new FakeAcpAgent(), store))
        {
            id = (await first.StartAsync(new AgentStart("/repo", Title: "Parser work"), Ct)).SessionId!;
        }

        await using var second = new AgentHost(Backend, new FakeAcpAgent(), store);

        var back = Assert.Single(second.Agents);
        Assert.Equal(id, back.SessionId);
        Assert.Equal("Parser work", back.Title);
        Assert.Equal(HostedState.Stopped, back.State);
        Assert.Empty(second.Read());
    }

    [Fact]
    public async Task An_agent_whose_folder_is_gone_is_not_resumed()
    {
        var (host, agent, dir) = Build();
        await using var _ = host;
        using var __ = dir;
        var folder = Path.Combine(dir.Path, "worktree");
        Directory.CreateDirectory(folder);
        var id = (await host.StartAsync(new AgentStart(folder), Ct)).SessionId!;
        await host.StopAsync(id, Ct);
        Directory.Delete(folder);

        var sent = await host.SendAsync(id, "carry on", Ct);

        Assert.False(sent.Ok);
        Assert.Contains(folder, sent.Message);
        var stopped = host.Find(id)!;
        Assert.True(stopped.FolderGone);
        Assert.Equal(HostedState.Stopped, stopped.State);
        Assert.DoesNotContain("session/resume:" + id, agent.Calls);
    }

    [Fact]
    public async Task A_crash_mid_turn_fails_the_agent_and_the_next_message_starts_the_agent_again()
    {
        var release = new TaskCompletionSource();
        var agent = new FakeAcpAgent
        {
            OnPrompt = async script =>
            {
                await script.Say("working");
                await release.Task;
                return "end_turn";
            },
        };
        var (host, _, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;
        var id = (await host.StartAsync(new AgentStart(dir.Path, Prompt: "go"), Ct)).SessionId!;
        await Until(host, id, a => a.LiveText == "working");

        await agent.CrashAsync();

        var failed = await Until(host, id, a => a.State == HostedState.Failed);
        Assert.Contains("fake agent log line", failed.Error);

        agent.OnPrompt = _ => Task.FromResult("end_turn");
        var sent = await host.SendAsync(id, "again", Ct);

        Assert.True(sent.Ok, sent.Message);
        await Until(host, id, a => a.State == HostedState.Idle);
        Assert.Equal(2, agent.Launches);
    }

    [Fact]
    public async Task The_title_the_agent_gives_a_conversation_is_kept_for_the_next_start()
    {
        var dir = new TempDir();
        using var _ = dir;
        var store = new HostedAgentStore(new AppPaths(dir.Path));
        var agent = new FakeAcpAgent { OnPrompt = async script => { await script.Title("Fix the parser"); return "end_turn"; } };
        await using var host = new AgentHost(Backend, agent, store);

        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "the parser drops the last token"), Ct)).SessionId!;
        await Until(host, id, a => a.Title == "Fix the parser" && a.State == HostedState.Idle);
        await Task.Delay(250, Ct);

        Assert.Equal("Fix the parser", Assert.Single(store.Load()).Title);
    }

    [Fact]
    public async Task The_prompt_handed_back_as_a_title_is_not_taken_for_one()
    {
        var agent = new FakeAcpAgent { OnPrompt = async script => { await script.Title("the parser drops the last token"); return "end_turn"; } };
        var (host, _, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;

        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "the parser\ndrops the last token"), Ct)).SessionId!;
        var idle = await Until(host, id, a => a.State == HostedState.Idle);

        Assert.Null(idle.Title);
    }

    [Theory]
    [InlineData("fix it", "fix it", true)]
    [InlineData("fix   it", "fix\nit", true)]
    [InlineData("the parser dr\u2026", "the parser drops the last token", true)]
    [InlineData("Fix the parser", "Fix the parser bug in the lexer", false)]
    [InlineData("Parser work", "fix it", false)]
    public void Knows_the_prompt_cut_down_from_a_generated_title(string title, string prompt, bool echo) =>
        Assert.Equal(echo, AgentHost.IsPromptEcho(title, prompt));

    private sealed class Servers : IAgentMcpServers
    {
        public IReadOnlyList<McpServer> For(string cwd) =>
            [new McpServer("agents-dashboard", "http://127.0.0.1:1/_mcp?cwd=" + cwd, [new("Authorization", "Bearer ${KEY}")])];

        public IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>();
    }

    [Fact]
    public async Task Hands_each_session_the_dashboards_mcp_server_for_its_folder()
    {
        var dir = new TempDir();
        using var _ = dir;
        var agent = new FakeAcpAgent();
        await using var host = new AgentHost(Backend, agent, new HostedAgentStore(new AppPaths(dir.Path)), mcpServers: new Servers());

        await host.StartAsync(new AgentStart("/repo"), Ct);

        Assert.Equal(
            """[{"type":"http","name":"agents-dashboard","url":"http://127.0.0.1:1/_mcp?cwd=/repo","headers":[{"name":"Authorization","value":"Bearer ${KEY}"}]}]""",
            agent.McpServers);
    }

    [Fact]
    public async Task Lists_the_slash_commands_the_agent_takes_and_offers_them_to_agents_that_have_not_said()
    {
        var agent = new FakeAcpAgent
        {
            OnPrompt = async script =>
            {
                await script.Commands(
                    new { name = "review", description = "Review the diff", input = new { hint = "[level]" } },
                    new { name = "init", description = "Write a CLAUDE.md", input = (object?)null });
                return "end_turn";
            },
        };
        var (host, _, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;

        var first = (await host.StartAsync(new AgentStart(dir.Path, Prompt: "go"), Ct)).SessionId!;
        var idle = await Until(host, first, a => a.Commands is { Count: 2 } && a.State == HostedState.Idle);

        Assert.Equal(
            [new AcpCommand("init", "Write a CLAUDE.md", null), new AcpCommand("review", "Review the diff", "[level]")],
            idle.Commands);

        agent.OnPrompt = _ => Task.FromResult("end_turn");
        var second = (await host.StartAsync(new AgentStart(dir.Path), Ct)).SessionId!;
        Assert.Equal(2, host.Agents.Single(a => a.SessionId == second).Commands!.Count);
    }

    [Fact]
    public async Task Keeps_how_full_the_context_window_is_across_a_restart()
    {
        var dir = new TempDir();
        using var _ = dir;
        var store = new HostedAgentStore(new AppPaths(dir.Path));
        var agent = new FakeAcpAgent { OnPrompt = async script => { await script.Usage(142_000, 1_000_000); return "end_turn"; } };

        string id;
        await using (var host = new AgentHost(Backend, agent, store))
        {
            id = (await host.StartAsync(new AgentStart("/repo", Prompt: "go"), Ct)).SessionId!;
            var idle = await Until(host, id, a => a.Context is not null && a.State == HostedState.Idle);
            Assert.Equal(14, idle.Context!.Percent);
            await Task.Delay(250, Ct);
        }

        await using var second = new AgentHost(Backend, new FakeAcpAgent(), store);
        Assert.Equal(new ContextUsage(142_000, 1_000_000), Assert.Single(second.Agents).Context);
    }

    [Fact]
    public async Task Removing_takes_it_off_the_list_for_good()
    {
        var dir = new TempDir();
        using var _ = dir;
        var store = new HostedAgentStore(new AppPaths(dir.Path));
        await using var host = new AgentHost(Backend, new FakeAcpAgent(), store);
        var id = (await host.StartAsync(new AgentStart("/repo"), Ct)).SessionId!;

        await host.RemoveAsync(id, Ct);

        Assert.Empty(host.Agents);
        Assert.Empty(store.Load());
    }

    [Fact]
    public async Task An_agent_that_cannot_start_says_why()
    {
        var dir = new TempDir();
        using var _ = dir;
        var broken = Backend with { Problem = "Node is not installed." };
        await using var host = new AgentHost(broken, new ProcessAgentLauncher(), new HostedAgentStore(new AppPaths(dir.Path)));

        var result = await host.StartAsync(new AgentStart("/repo"), Ct);

        Assert.False(result.Ok);
        Assert.Equal("Node is not installed.", result.Message);
    }
}

public class JsonRpcConnectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_error_answer_is_thrown_with_its_message()
    {
        var agent = new FakeAcpAgent();
        await using var process = agent.Launch(new AgentBackend("x", "X", "x", []));
        await using var rpc = new JsonRpcConnection(process.Input, process.Output);
        rpc.Start();

        var error = await Assert.ThrowsAsync<JsonRpcException>(() => rpc.RequestAsync("no/such", null, Ct));

        Assert.Equal(JsonRpcConnection.MethodNotFound, error.Code);
    }

    [Fact]
    public async Task A_request_still_waiting_fails_when_the_other_side_goes_away()
    {
        var agent = new FakeAcpAgent { OnPrompt = _ => new TaskCompletionSource<string>().Task };
        var process = agent.Launch(new AgentBackend("x", "X", "x", []));
        await using var rpc = new JsonRpcConnection(process.Input, process.Output);
        rpc.Start();
        var waiting = rpc.RequestAsync("session/prompt", new { sessionId = "s", prompt = new[] { new { type = "text", text = "hi" } } }, Ct);

        await agent.CrashAsync();

        await Assert.ThrowsAsync<IOException>(() => waiting);
    }
}
