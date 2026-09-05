using AgentsDashboard.Core.Claude;

namespace AgentsDashboard.Core.Tests;

public class ClaudeCommandsTests
{
    [Fact]
    public void Starting_without_a_prompt_leaves_the_agent_idle() =>
        Assert.Equal(["--bg"], ClaudeCommands.Start(null));

    [Fact]
    public void Starting_with_a_prompt_passes_it_as_one_argument() =>
        Assert.Equal(["--bg", "fix the build"], ClaudeCommands.Start("fix the build"));

    [Fact]
    public void A_blank_prompt_is_the_same_as_none() =>
        Assert.Equal(["--bg"], ClaudeCommands.Start("   "));

    [Fact]
    public void Sending_uses_the_full_session_id()
    {
        // The short id makes the CLI start a copy of the conversation under a new
        // id rather than continuing the original, which looks like it worked.
        const string sessionId = "a2498250-c333-4bae-939a-9cb8f4044d87";

        Assert.Equal(["--bg", "--resume", sessionId, "carry on"], ClaudeCommands.Send(sessionId, "carry on"));
    }

    [Fact]
    public void Stopping_and_removing_use_the_short_id()
    {
        Assert.Equal(["stop", "a2498250"], ClaudeCommands.Stop("a2498250"));
        Assert.Equal(["rm", "a2498250"], ClaudeCommands.Remove("a2498250"));
    }

    [Fact]
    public void Reads_background_agents_and_ignores_the_rest()
    {
        const string json = """
            [
              { "pid": 1, "kind": "interactive", "sessionId": "s-1", "cwd": "/a", "name": "n" },
              { "pid": 2, "kind": "background", "id": "a2498250",
                "sessionId": "a2498250-c333-4bae-939a-9cb8f4044d87",
                "cwd": "/repo/wt", "name": "fix the build", "status": "idle" }
            ]
            """;

        var agent = Assert.Single(ClaudeCommands.ParseList(json));

        Assert.Equal("a2498250", agent.Id);
        Assert.Equal("a2498250-c333-4bae-939a-9cb8f4044d87", agent.SessionId);
        Assert.Equal("/repo/wt", agent.Cwd);
        Assert.Equal("fix the build", agent.Name);
        Assert.Equal("idle", agent.Status);
        Assert.Equal(2, agent.Pid);
    }

    [Fact]
    public void Skips_a_background_entry_with_no_ids() =>
        Assert.Empty(ClaudeCommands.ParseList("""[{ "kind": "background", "cwd": "/a" }]"""));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("")]
    public void Survives_output_it_cannot_read(string json) =>
        Assert.Empty(ClaudeCommands.ParseList(json));

    [Theory]
    [InlineData("backgrounded · a2498250", "a2498250")]
    [InlineData("backgrounded · a2498250 (idle — send a prompt to start)", "a2498250")]
    [InlineData("Starting background service…\nbackgrounded · fe6d8cab\n  claude agents  list", "fe6d8cab")]
    public void Reads_the_id_the_cli_reports(string output, string expected) =>
        Assert.Equal(expected, ClaudeCommands.ParseStartedId(output));

    [Fact]
    public void Reports_no_id_when_the_cli_said_nothing_about_one() =>
        Assert.Null(ClaudeCommands.ParseStartedId("something else entirely"));
}
