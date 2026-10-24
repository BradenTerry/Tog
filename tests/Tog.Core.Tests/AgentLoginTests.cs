using System.Text.Json;
using Tog.Core.Agents;
using Tog.Core.Claude;
using Tog.Core.Repos;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

public class AgentLoginTests
{
    private static readonly AgentBackend Backend = new AgentBackend("claude", "Claude", "fake", []).Hooked();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A sign-in method whose command prints a page and exits with the code given.</summary>
    private static object Method(string id, int exitCode) => new
    {
        id,
        name = "Claude Subscription",
        description = "Use Claude subscription ",
        type = "terminal",
        args = new[] { "--cli", "auth", "login" },
        _meta = new Dictionary<string, object>
        {
            ["terminal-auth"] = OperatingSystem.IsWindows()
                ? new { command = "cmd.exe", args = new[] { "/c", $"echo Visit https://claude.ai/oauth?x=1 to sign in & exit {exitCode}" } }
                : new { command = "/bin/sh", args = new[] { "-c", $"echo 'Visit https://claude.ai/oauth?x=1 to sign in'; exit {exitCode}" } },
        },
    };

    /// <summary>An agent that refuses every prompt for want of a login until <paramref name="signedIn"/> says otherwise.</summary>
    private static FakeAcpAgent SignedOut(Func<bool> signedIn, int exitCode = 0) => new()
    {
        AuthMethods = [Method("claude-ai-login", exitCode)],
        OnPrompt = _ => signedIn()
            ? Task.FromResult("end_turn")
            : throw new JsonRpcException(AcpClient.AuthRequired, "Authentication required: Please run /login"),
    };

    private static async Task<HostedAgent> Until(AgentHost host, string sessionId, Func<HostedAgent, bool> done)
    {
        for (var i = 0; i < 250; i++)
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
    public async Task Tells_the_agent_it_can_run_a_sign_in()
    {
        var agent = new FakeAcpAgent();
        using var dir = new TempDir();
        await using var host = new AgentHost(Backend, agent, new HostedAgentStore(new AppPaths(dir.Path)));

        await host.StartAsync(new AgentStart("/repo"), Ct);

        Assert.Contains("initialize:terminal-auth", agent.Calls);
    }

    [Fact]
    public async Task A_turn_refused_for_want_of_a_login_asks_for_one()
    {
        using var dir = new TempDir();
        await using var host = new AgentHost(Backend, SignedOut(() => false), new HostedAgentStore(new AppPaths(dir.Path)));

        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "fix it"), Ct)).SessionId!;

        var failed = await Until(host, id, a => a.State == HostedState.Failed);
        Assert.True(failed.NeedsLogin);
        Assert.Contains("signed out", failed.Error);
        Assert.Equal("Claude Subscription", Assert.Single(host.LoginMethods("claude")).Name);
    }

    [Fact]
    public async Task Another_refusal_is_not_a_login()
    {
        var agent = new FakeAcpAgent { OnPrompt = _ => throw new JsonRpcException(JsonRpcConnection.InternalError, "boom") };
        using var dir = new TempDir();
        await using var host = new AgentHost(Backend, agent, new HostedAgentStore(new AppPaths(dir.Path)));

        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "fix it"), Ct)).SessionId!;

        var failed = await Until(host, id, a => a.State == HostedState.Failed);
        Assert.False(failed.NeedsLogin);
    }

    [Fact]
    public async Task Signing_in_sends_the_refused_message_again()
    {
        var signedIn = false;
        var agent = SignedOut(() => signedIn);
        using var dir = new TempDir();
        await using var host = new AgentHost(Backend, agent, new HostedAgentStore(new AppPaths(dir.Path)));
        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "fix it"), Ct)).SessionId!;
        await Until(host, id, a => a.NeedsLogin);

        signedIn = true;
        var result = host.LogIn("claude", "claude-ai-login");
        Assert.True(result.Ok, result.Message);
        var login = host.Login("claude")!;
        await login.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(LoginState.Succeeded, login.State);
        Assert.Equal("https://claude.ai/oauth?x=1", login.Url);
        // Unblocking it and sending again are two steps, so idle alone could be
        // seen between them.
        var idle = await Until(host, id, a =>
            a.State == HostedState.Idle && agent.Calls.Count(c => c == "session/prompt:fix it") == 2);
        Assert.False(idle.NeedsLogin);
        Assert.Null(idle.Error);
    }

    [Fact]
    public async Task A_failed_sign_in_leaves_the_agent_asking()
    {
        using var dir = new TempDir();
        await using var host = new AgentHost(Backend, SignedOut(() => false, exitCode: 1), new HostedAgentStore(new AppPaths(dir.Path)));
        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "fix it"), Ct)).SessionId!;
        await Until(host, id, a => a.NeedsLogin);

        host.LogIn("claude", "claude-ai-login");
        var login = host.Login("claude")!;
        await login.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(LoginState.Failed, login.State);
        Assert.True(host.Find(id)!.NeedsLogin);
    }

    [Fact]
    public void Reads_only_the_sign_ins_that_name_their_command()
    {
        var result = JsonSerializer.SerializeToElement(new
        {
            authMethods = new object[]
            {
                new { id = "plain", name = "Plain", type = "terminal", args = new[] { "--cli" } },
                new
                {
                    id = "meta",
                    name = "Meta",
                    _meta = new Dictionary<string, object> { ["terminal-auth"] = new { command = "node", args = new[] { "index.js", "--cli" } } },
                },
            },
        });

        var method = Assert.Single(LoginMethod.Read(result));

        Assert.Equal("meta", method.Id);
        Assert.Equal("node", method.Command);
        Assert.Equal(["index.js", "--cli"], method.Arguments);
    }

    [Fact]
    public void Strips_terminal_colours_and_finds_the_page()
    {
        var (line, url) = AgentLogin.Clean("\u001b[1mOpen\u001b[0m https://claude.ai/oauth/authorize?code=true&x=1.");

        Assert.Equal("Open https://claude.ai/oauth/authorize?code=true&x=1.", line);
        Assert.Equal("https://claude.ai/oauth/authorize?code=true&x=1", url);
    }
}
