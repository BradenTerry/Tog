using AgentsDashboard.Core.Claude;

namespace AgentsDashboard.Core.Tests;

public class ClaudeCommandsTests
{
    [Fact]
    public void Starting_without_a_prompt_leaves_the_agent_idle() =>
        Assert.Equal(["--bg"], ClaudeCommands.Start((string?)null));

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

    [Fact]
    public void Reading_logs_and_respawning_use_the_short_id()
    {
        Assert.Equal(["logs", "a2498250"], ClaudeCommands.Logs("a2498250"));
        Assert.Equal(["respawn", "a2498250"], ClaudeCommands.Respawn("a2498250"));
    }

    [Fact]
    public void Attaching_is_offered_as_a_line_to_paste() =>
        Assert.Equal("claude attach a2498250", ClaudeCommands.AttachCommand("a2498250"));
}

public class ClaudeStartOptionsTests
{
    [Fact]
    public void Every_option_is_passed_in_the_order_the_cli_wants()
    {
        var args = ClaudeCommands.Start(new StartOptions(
            Prompt: "fix the build",
            Model: "opus",
            Effort: "high",
            PermissionMode: "acceptEdits"));

        Assert.Equal(
            [
                "--bg",
                "--model", "opus",
                "--effort", "high",
                "--permission-mode", "acceptEdits",
                "fix the build",
            ],
            args);
    }

    [Fact]
    public void Options_left_blank_are_left_to_the_cli() =>
        Assert.Equal(
            ["--bg", "--effort", "max"],
            ClaudeCommands.Start(new StartOptions(Model: "  ", Effort: "max", PermissionMode: null)));

    [Fact]
    public void A_named_worktree_passes_the_name() =>
        Assert.Equal(
            ["--bg", "--worktree", "retry-parser"],
            ClaudeCommands.Start(new StartOptions(NewWorktree: true, WorktreeName: "retry-parser")));

    [Fact]
    public void An_unnamed_worktree_lets_the_cli_pick() =>
        Assert.Equal(
            ["--bg", "--worktree"],
            ClaudeCommands.Start(new StartOptions(NewWorktree: true, WorktreeName: "   ")));

    [Fact]
    public void A_worktree_name_without_the_flag_is_ignored() =>
        Assert.Equal(["--bg"], ClaudeCommands.Start(new StartOptions(WorktreeName: "retry-parser")));

    [Fact]
    public void The_prompt_always_comes_last()
    {
        // It is a positional argument: put it before a flag and the flag's value
        // is read as part of the prompt.
        var args = ClaudeCommands.Start(new StartOptions(
            Prompt: "fix the build",
            Model: "sonnet",
            NewWorktree: true,
            WorktreeName: "fixes"));

        Assert.Equal("fix the build", args[^1]);
    }

    [Fact]
    public void The_prompt_only_overload_starts_the_same_way() =>
        Assert.Equal(ClaudeCommands.Start(new StartOptions("fix the build")), ClaudeCommands.Start("fix the build"));

    [Fact]
    public void The_choices_are_the_ones_the_cli_documents()
    {
        Assert.Equal(["fable", "opus", "sonnet", "haiku"], StartChoices.Models);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], StartChoices.Efforts);
        Assert.Equal(
            ["acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan"],
            StartChoices.PermissionModes);
    }
}

public class ClaudeRenderTests
{
    [Fact]
    public void Plain_arguments_are_left_alone() =>
        Assert.Equal("claude agents --json --all", ClaudeCommands.Render(ClaudeCommands.List()));

    [Fact]
    public void An_argument_with_spaces_is_quoted() =>
        Assert.Equal("claude --bg 'fix the build'", ClaudeCommands.Render(ClaudeCommands.Start("fix the build")));

    [Fact]
    public void A_single_quote_is_escaped_so_the_line_can_be_pasted() =>
        Assert.Equal(
            @"claude --bg 'don'\''t stop'",
            ClaudeCommands.Render(ClaudeCommands.Start("don't stop")));

    [Theory]
    [InlineData("a$b")]
    [InlineData("a;b")]
    [InlineData("a|b")]
    [InlineData("a>b")]
    [InlineData("a*b")]
    [InlineData("a#b")]
    public void Shell_characters_are_quoted(string arg) =>
        Assert.Equal($"claude '{arg}'", ClaudeCommands.Render([arg]));
}

public class ClaudeBulkOutcomeTests
{
    [Fact]
    public void All_succeeding_is_one_line()
    {
        var result = BulkOutcome.Summarise("Stopped", "stop", [
            ("a1", new CliResult(true, "")),
            ("a2", new CliResult(true, "")),
            ("a3", new CliResult(true, "")),
        ]);

        Assert.True(result.Ok);
        Assert.Equal("Stopped 3.", result.Message);
    }

    [Fact]
    public void A_failure_is_named_with_what_the_cli_said()
    {
        var result = BulkOutcome.Summarise("Stopped", "stop", [
            ("a1", new CliResult(true, "")),
            ("a2498250", CliResult.Failed("not running")),
            ("a3", new CliResult(true, "")),
        ]);

        Assert.False(result.Ok);
        Assert.Equal("Stopped 2. Could not stop a2498250: not running", result.Message);
    }

    [Fact]
    public void A_silent_failure_is_still_named()
    {
        var result = BulkOutcome.Summarise("Removed", "remove", [("a1", CliResult.Failed("  "))]);

        Assert.False(result.Ok);
        Assert.Equal("Removed 0. Could not remove a1.", result.Message);
    }

    [Fact]
    public void Nothing_selected_is_not_a_failure()
    {
        var result = BulkOutcome.Summarise("Stopped", "stop", []);

        Assert.True(result.Ok);
        Assert.Equal("Stopped 0.", result.Message);
    }
}

public class ClaudeListingTests
{
    [Fact]
    public void The_running_listing_leaves_out_stopped_sessions()
    {
        // Presence in this listing is the only sound liveness test. A stopped
        // session keeps its entry under --all, and a running one that has not
        // transitioned yet reports no status at all, so neither can be used.
        Assert.Equal(["agents", "--json"], ClaudeCommands.List(includeStopped: false));
    }

    [Fact]
    public void The_full_listing_includes_them() =>
        Assert.Equal(["agents", "--json", "--all"], ClaudeCommands.List(includeStopped: true));

    [Fact]
    public void A_running_session_with_no_status_yet_is_still_read()
    {
        const string json = """
            [{ "pid": 70312, "kind": "background", "id": "a2498250",
               "sessionId": "a2498250-c333-4bae-939a-9cb8f4044d87", "cwd": "/repo" }]
            """;

        var agent = Assert.Single(ClaudeCommands.ParseList(json));

        Assert.Null(agent.Status);
        Assert.Equal(70312, agent.Pid);
    }

    [Fact]
    public void A_stopped_session_reports_no_pid_and_no_status()
    {
        const string json = """
            [{ "pid": null, "kind": "background", "id": "87e0322a",
               "sessionId": "87e0322a-1111-2222-3333-444444444444",
               "cwd": "/repo", "name": "file change request", "status": null }]
            """;

        var agent = Assert.Single(ClaudeCommands.ParseList(json));

        Assert.Equal(0, agent.Pid);
        Assert.Null(agent.Status);
        Assert.Equal("file change request", agent.Name);
        Assert.False(agent.IsRunning);
    }

    [Fact]
    public void Reads_the_state_and_the_start_time()
    {
        const string json = """
            [{ "id": "12d77282", "cwd": "/x", "kind": "background", "startedAt": 1788591923628,
               "sessionId": "12d77282-7e2f-4c4a-9a0e-6d1b2c3d4e5f", "state": "failed" }]
            """;

        var agent = Assert.Single(ClaudeCommands.ParseList(json));

        Assert.Equal("failed", agent.State);
        Assert.True(agent.IsFailed);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1788591923628), agent.StartedAt);
    }

    [Fact]
    public void A_state_that_is_not_failure_is_only_carried()
    {
        const string json = """
            [{ "id": "87e0322a", "cwd": "/y", "kind": "background", "startedAt": 1788593861343,
               "sessionId": "87e0322a-1111-2222-3333-444444444444",
               "name": "file change request", "state": "blocked" }]
            """;

        var agent = Assert.Single(ClaudeCommands.ParseList(json));

        Assert.Equal("blocked", agent.State);
        Assert.False(agent.IsFailed);
    }

    [Theory]
    [InlineData("""[{ "kind": "background", "id": "a1", "sessionId": "s-1", "cwd": "/a" }]""")]
    [InlineData("""[{ "kind": "background", "id": "a1", "sessionId": "s-1", "state": null, "startedAt": null }]""")]
    [InlineData("""[{ "kind": "background", "id": "a1", "sessionId": "s-1", "startedAt": "yesterday" }]""")]
    public void Tolerates_a_listing_with_neither(string json)
    {
        var agent = Assert.Single(ClaudeCommands.ParseList(json));

        Assert.Null(agent.State);
        Assert.Null(agent.StartedAt);
        Assert.False(agent.IsFailed);
    }

    [Fact]
    public void A_running_session_is_the_one_with_a_pid()
    {
        const string json = """
            [{ "pid": 70312, "kind": "background", "id": "a1", "sessionId": "s-1", "cwd": "/a" },
             { "pid": null, "kind": "background", "id": "a2", "sessionId": "s-2", "cwd": "/b" }]
            """;

        var agents = ClaudeCommands.ParseList(json);

        Assert.True(agents[0].IsRunning);
        Assert.False(agents[1].IsRunning);
    }
}
