using Tog.Core.Agents;
using Tog.Core.Claude;
using Tog.Core.Model;
using Tog.Core.Repos;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

public class AgentHostTests
{
    private static readonly AgentBackend Backend = new AgentBackend("claude", "Claude", "fake", []).Hooked();

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
    public async Task Clear_starts_a_new_session_in_the_same_folder_and_never_reaches_the_agent()
    {
        var (host, agent, dir) = Build();
        await using var _ = host;
        using var __ = dir;
        var id = (await host.StartAsync(new AgentStart(dir.Path, Model: "haiku"), Ct)).SessionId!;

        var result = await host.SendAsync(id, " /clear ", Ct);

        Assert.True(result.Ok, result.Message);
        Assert.NotEqual(id, result.SessionId);
        Assert.Null(host.Find(id));
        var fresh = host.Find(result.SessionId!)!;
        Assert.Equal(dir.Path, fresh.Cwd);
        Assert.Equal(HostedState.Idle, fresh.State);
        Assert.Contains("session/close:" + id, agent.Calls);
        Assert.Equal(2, agent.Calls.Count(c => c == "session/set_config_option:model=haiku"));
        Assert.DoesNotContain(agent.Calls, c => c.StartsWith("session/prompt:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stopped_agent_can_be_resumed_without_sending_it_anything()
    {
        var (host, agent, dir) = Build();
        await using var _ = host;
        using var __ = dir;
        var id = (await host.StartAsync(new AgentStart(dir.Path), Ct)).SessionId!;
        await host.StopAsync(id, Ct);

        var result = await host.WakeAsync(id, Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(HostedState.Idle, host.Find(id)!.State);
        Assert.Contains("session/resume:" + id, agent.Calls);
        Assert.DoesNotContain(agent.Calls, c => c.StartsWith("session/prompt:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resuming_an_agent_whose_folder_is_gone_says_so_and_does_not_fail_it()
    {
        var (host, agent, dir) = Build();
        await using var _ = host;
        using var __ = dir;
        var gone = Path.Combine(dir.Path, "removed");
        Directory.CreateDirectory(gone);
        var id = (await host.StartAsync(new AgentStart(gone), Ct)).SessionId!;
        await host.StopAsync(id, Ct);
        Directory.Delete(gone);

        var result = await host.WakeAsync(id, Ct);

        Assert.False(result.Ok);
        Assert.Equal(HostedState.Stopped, host.Find(id)!.State);
        Assert.DoesNotContain("session/resume:" + id, agent.Calls);
    }

    [Fact]
    public async Task The_slash_commands_an_agent_listed_are_kept_across_a_restart()
    {
        var dir = new TempDir();
        using var _ = dir;
        var store = new HostedAgentStore(new AppPaths(dir.Path));
        var agent = new FakeAcpAgent
        {
            OnPrompt = async script =>
            {
                await script.Commands(new { name = "review", description = "Review the diff", input = new { hint = "[level]" } });
                return "end_turn";
            },
        };

        await using (var host = new AgentHost(Backend, agent, store))
        {
            var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "go"), Ct)).SessionId!;
            await Until(host, id, a => a.Commands is { Count: 1 } && a.State == HostedState.Idle);
            await Task.Delay(250, Ct);
        }

        await using var second = new AgentHost(Backend, new FakeAcpAgent(), store);
        Assert.Equal([new AcpCommand("review", "Review the diff", "[level]")], Assert.Single(second.Agents).Commands);

        // A new agent that has not listed any yet is offered the saved ones too.
        var fresh = (await second.StartAsync(new AgentStart(dir.Path), Ct)).SessionId!;
        Assert.Single(second.Agents.Single(a => a.SessionId == fresh).Commands!);
    }

    [Fact]
    public async Task When_the_last_turn_ended_is_kept_across_a_restart()
    {
        var dir = new TempDir();
        using var _ = dir;
        var store = new HostedAgentStore(new AppPaths(dir.Path));

        DateTimeOffset ended;
        await using (var first = new AgentHost(Backend, new FakeAcpAgent(), store))
        {
            var id = (await first.StartAsync(new AgentStart("/repo"), Ct)).SessionId!;
            Assert.Null(first.Find(id)!.TurnEndedAt);

            await first.SendAsync(id, "go", Ct);
            ended = (await Until(first, id, a => a.TurnEndedAt is not null)).TurnEndedAt!.Value;
        }

        await using var second = new AgentHost(Backend, new FakeAcpAgent(), store);
        Assert.Equal(ended, Assert.Single(second.Agents).TurnEndedAt);
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
        private int _minted;

        public List<string> Log { get; } = [];

        public McpGrant Grant(string cwd, string? sessionId)
        {
            var key = "k" + ++_minted;
            Log.Add($"grant {key} {cwd} {sessionId}");
            return new McpGrant(
                key,
                [new McpServer("tog", "http://127.0.0.1:1/_mcp", [new("Authorization", "Bearer ${KEY}")])],
                new Dictionary<string, string> { ["KEY"] = key });
        }

        public void Bind(string key, string sessionId) => Log.Add($"bind {key} {sessionId}");

        public void Revoke(string key) => Log.Add($"revoke {key}");
    }

    [Fact]
    public async Task Hands_each_session_Togs_mcp_server_with_a_key_of_its_own()
    {
        var dir = new TempDir();
        using var _ = dir;
        var agent = new FakeAcpAgent();
        var servers = new Servers();
        await using var host = new AgentHost(Backend, agent, new HostedAgentStore(new AppPaths(dir.Path)), mcpServers: servers);

        await host.StartAsync(new AgentStart("/repo"), Ct);
        Assert.Equal("""{"claudeCode":{"options":{"env":{"KEY":"k1"}}}}""", agent.SessionMeta);

        await host.StartAsync(new AgentStart("/other"), Ct);
        Assert.Equal("""{"claudeCode":{"options":{"env":{"KEY":"k2"}}}}""", agent.SessionMeta);

        Assert.Equal(
            """[{"type":"http","name":"tog","url":"http://127.0.0.1:1/_mcp","headers":[{"name":"Authorization","value":"Bearer ${KEY}"}]}]""",
            agent.McpServers);
        Assert.Equal(["grant k1 /repo ", "bind k1 s1", "grant k2 /other ", "bind k2 s2"], servers.Log);
    }

    [Fact]
    public async Task Revokes_a_sessions_mcp_key_when_it_stops_and_grants_a_new_one_when_it_resumes()
    {
        var agent = new FakeAcpAgent();
        var servers = new Servers();
        var dir = new TempDir();
        using var _ = dir;
        await using var host = new AgentHost(Backend, agent, new HostedAgentStore(new AppPaths(dir.Path)), mcpServers: servers);

        var id = (await host.StartAsync(new AgentStart(dir.Path), Ct)).SessionId!;
        await host.StopAsync(id, Ct);
        await host.WakeAsync(id, Ct);
        Assert.Equal("""{"claudeCode":{"options":{"env":{"KEY":"k2"}}}}""", agent.SessionMeta);
        await host.RemoveAsync(id, Ct);

        Assert.Equal(
            [$"grant k1 {dir.Path} ", $"bind k1 {id}", "revoke k1", $"grant k2 {dir.Path} {id}", "revoke k2"],
            servers.Log);
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

    /// <summary>Hands each kind of agent its own fake, as each would be its own program.</summary>
    private sealed class ByBackend(Dictionary<string, FakeAcpAgent> agents) : IAgentLauncher
    {
        public IAgentProcess Launch(AgentBackend backend) => agents[backend.Id].Launch(backend);
    }

    [Fact]
    public async Task Runs_each_kind_of_agent_on_its_own_process_and_a_crash_only_stops_its_own()
    {
        var dir = new TempDir();
        using var _ = dir;
        var claude = new FakeAcpAgent();
        var other = new FakeAcpAgent { SessionPrefix = "o" };
        var backends = new FixedAgentBackends(Backend, new AgentBackend("other", "Other", "fake", []));
        await using var host = new AgentHost(
            backends,
            new ByBackend(new() { ["claude"] = claude, ["other"] = other }),
            new HostedAgentStore(new AppPaths(dir.Path)));

        var first = (await host.StartAsync(new AgentStart(dir.Path), Ct)).SessionId!;
        var second = (await host.StartAsync(new AgentStart(dir.Path, Backend: "other"), Ct)).SessionId!;

        Assert.Equal((1, 1), (claude.Launches, other.Launches));
        Assert.Equal("claude", host.Find(first)!.Backend);
        Assert.Equal("other", host.Find(second)!.Backend);

        await other.CrashAsync();
        await Until(host, second, a => a.State == HostedState.Stopped);
        Assert.Equal(HostedState.Idle, host.Find(first)!.State);

        // A message picks the crashed one up again on a process of its own kind.
        await host.SendAsync(second, "carry on", Ct);
        await Until(host, second, a => a.State == HostedState.Idle);
        Assert.Equal((1, 2), (claude.Launches, other.Launches));
        Assert.Contains($"session/resume:{second}", other.Calls);
    }

    [Fact]
    public async Task Says_so_when_no_agent_or_not_that_one_is_set_up()
    {
        var dir = new TempDir();
        using var _ = dir;
        await using var none = new AgentHost(new FixedAgentBackends(), new FakeAcpAgent(), new HostedAgentStore(new AppPaths(dir.Path)));

        Assert.Contains("No agent is set up", (await none.StartAsync(new AgentStart(dir.Path), Ct)).Message);
        Assert.Contains("\"codex\"", (await none.StartAsync(new AgentStart(dir.Path, Backend: "codex"), Ct)).Message);
    }

    [Fact]
    public async Task Agents_saved_before_there_was_a_choice_come_back_as_claude_and_keep_their_kind()
    {
        var dir = new TempDir();
        using var _ = dir;
        var store = new HostedAgentStore(new AppPaths(dir.Path));
        store.Save([new HostedAgentRecord("old", dir.Path, "Old work", DateTimeOffset.UnixEpoch)]);

        await using (var host = new AgentHost(Backend, new FakeAcpAgent(), store))
        {
            Assert.Equal("claude", host.Find("old")!.Backend);
            await host.StartAsync(new AgentStart(dir.Path), Ct);
        }

        Assert.All(store.Load(), r => Assert.Equal("claude", r.Backend));
    }

    [Fact]
    public async Task Gives_no_mcp_server_to_an_agent_that_has_no_way_to_take_its_key()
    {
        var dir = new TempDir();
        using var _ = dir;
        var agent = new FakeAcpAgent();
        var servers = new Servers();
        await using var host = new AgentHost(
            new AgentBackend("plain", "Plain", "fake", []), agent, new HostedAgentStore(new AppPaths(dir.Path)), mcpServers: servers);

        await host.StartAsync(new AgentStart(dir.Path), Ct);

        Assert.Equal("[]", agent.McpServers);
        Assert.Null(agent.SessionMeta);
        Assert.Empty(servers.Log);
    }

    [Fact]
    public async Task Records_the_conversation_of_an_agent_with_no_history_of_its_own()
    {
        var dir = new TempDir();
        using var _ = dir;
        var agent = new FakeAcpAgent
        {
            OnPrompt = async script =>
            {
                await script.Say("Looking");
                await script.Say(" first.");
                await script.Tool("ls -la");
                await script.Say("Done.");
                await script.Title("Tidy the parser");
                return "end_turn";
            },
        };
        var paths = new AppPaths(dir.Path);
        var recorder = new RecordedTranscripts(paths);
        await using var host = new AgentHost(
            new AgentBackend("plain", "Plain", "fake", []), agent, new HostedAgentStore(paths), recorder: recorder);

        var id = (await host.StartAsync(new AgentStart(dir.Path, Prompt: "tidy the parser"), Ct)).SessionId!;
        await Until(host, id, a => a.State == HostedState.Idle);

        var history = host.Transcripts("plain")!;
        var thread = history.Conversation(id, dir.Path)!;
        Assert.Equal(
            [(ChatKind.You, "tidy the parser"), (ChatKind.Agent, "Looking first."), (ChatKind.Activity, ""), (ChatKind.Agent, "Done.")],
            thread.Select(e => (e.Kind, e.Text)));
        Assert.Equal(new ChatStep("other", "ls -la"), Assert.Single(thread[2].Steps));

        var activity = history.Activity(id, dir.Path)!;
        Assert.Equal(("Tidy the parser", "tidy the parser", "Done."), (activity.Title, activity.LastPrompt, activity.LastReply));

        var past = Assert.Single(history.PastSessions(dir.Path));
        Assert.Equal((id, "Tidy the parser"), (past.SessionId, past.Title));
        Assert.Empty(recorder.For("claude").PastSessions(dir.Path));
        Assert.Empty(history.PastSessions("/elsewhere"));
    }

    [Fact]
    public async Task Does_not_record_an_agent_that_has_its_own_history()
    {
        var dir = new TempDir();
        using var _ = dir;
        var paths = new AppPaths(dir.Path);
        var own = Backend with { Transcripts = new RecordedTranscripts(paths).For("elsewhere") };
        await using var host = new AgentHost(own, new FakeAcpAgent(), new HostedAgentStore(paths), recorder: new RecordedTranscripts(paths));

        var id = (await host.StartAsync(new AgentStart(dir.Path, Prompt: "go"), Ct)).SessionId!;
        await Until(host, id, a => a.State == HostedState.Idle);

        Assert.False(Directory.Exists(Path.Combine(dir.Path, "history")));
    }

    [Fact]
    public void A_recording_refuses_a_session_id_that_would_name_a_path()
    {
        var dir = new TempDir();
        using var _ = dir;
        var recorder = new RecordedTranscripts(new AppPaths(dir.Path));

        recorder.Begin("plain", "../escape", dir.Path, DateTimeOffset.UnixEpoch);
        recorder.You("../escape", "hi", DateTimeOffset.UnixEpoch);

        Assert.False(File.Exists(Path.Combine(dir.Path, "escape.jsonl")));
        Assert.Null(recorder.For("plain").Conversation("../escape", dir.Path));
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
