using AgentsDashboard.Core.Claude;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class SessionRegistryReaderTests
{
    private static string Entry(
        int pid,
        string sessionId,
        string cwd,
        string? status = null,
        string kind = "interactive",
        long startedAt = 1788582486151) =>
        $$"""
          {"pid":{{pid}},"sessionId":"{{sessionId}}","cwd":"{{cwd}}",
           {{(status is null ? "" : $"\"status\":\"{status}\",")}}
           "startedAt":{{startedAt}},"kind":"{{kind}}",
           "jobId":"job-{{pid}}"}
          """;

    private static (SessionRegistryReader Reader, TempDir Dir) Build(FakeProbe probe, FakeClock? clock = null)
    {
        var dir = new TempDir();
        dir.Dir("sessions");
        var paths = new ClaudePaths(home: dir.Path);
        return (new SessionRegistryReader(paths, probe, clock ?? new FakeClock()), dir);
    }

    [Fact]
    public void Reads_a_live_session()
    {
        var (reader, dir) = Build(new FakeProbe(101));
        using var _ = dir;
        dir.File(".claude/sessions/101.json", Entry(101, "s-1", "/repo", "busy"));

        var session = Assert.Single(reader.Read());

        Assert.Equal("s-1", session.SessionId);
        Assert.Equal(101, session.Pid);
        Assert.Equal("/repo", session.Cwd);
        Assert.Equal(AgentStatus.Active, session.Status);
        Assert.False(session.IsBackground);
    }

    [Fact]
    public void Drops_a_session_whose_process_is_gone()
    {
        var (reader, dir) = Build(new FakeProbe(101));
        using var _ = dir;
        dir.File(".claude/sessions/101.json", Entry(101, "s-1", "/repo", "busy"));
        dir.File(".claude/sessions/202.json", Entry(202, "s-2", "/repo", "busy"));

        // 202 was killed with its terminal and never removed its own file.
        Assert.Equal("s-1", Assert.Single(reader.Read()).SessionId);
    }

    [Fact]
    public void Ignores_a_non_interactive_session()
    {
        var (reader, dir) = Build(new FakeProbe(101));
        using var _ = dir;
        dir.File(".claude/sessions/101.json", Entry(101, "s-1", "/repo", "busy", kind: "print"));

        Assert.Empty(reader.Read());
    }

    [Fact]
    public void Ignores_a_file_that_is_not_valid_json_yet()
    {
        var (reader, dir) = Build(new FakeProbe(101));
        using var _ = dir;
        dir.File(".claude/sessions/101.json", "{\"pid\":101,\"sessi");

        Assert.Empty(reader.Read());
    }

    [Fact]
    public void Reads_a_file_once_it_finishes_being_written()
    {
        var (reader, dir) = Build(new FakeProbe(101));
        using var _ = dir;
        var file = dir.File(".claude/sessions/101.json", "{\"pid\":101,\"sessi");
        Assert.Empty(reader.Read());

        File.WriteAllText(file, Entry(101, "s-1", "/repo", "idle"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));

        Assert.Single(reader.Read());
    }

    [Fact]
    public void Holds_status_since_across_reads_that_do_not_change_the_status()
    {
        var clock = new FakeClock();
        var (reader, dir) = Build(new FakeProbe(101), clock);
        using var _ = dir;
        var file = dir.File(".claude/sessions/101.json", Entry(101, "s-1", "/repo", "waiting"));

        var first = Assert.Single(reader.Read());
        var blockedAt = first.StatusSince;

        clock.Advance(TimeSpan.FromMinutes(4));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));

        // Claude rewrites the file for reasons other than a status change. The
        // "blocked for" timer must not restart when it does.
        Assert.Equal(blockedAt, Assert.Single(reader.Read()).StatusSince);
    }

    [Fact]
    public void Moves_status_since_when_the_status_changes()
    {
        var clock = new FakeClock();
        var (reader, dir) = Build(new FakeProbe(101), clock);
        using var _ = dir;
        var file = dir.File(".claude/sessions/101.json", Entry(101, "s-1", "/repo", "busy"));

        var first = Assert.Single(reader.Read());

        clock.Advance(TimeSpan.FromMinutes(4));
        File.WriteAllText(file, Entry(101, "s-1", "/repo", "waiting"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));

        var second = Assert.Single(reader.Read());
        Assert.Equal(AgentStatus.Waiting, second.Status);
        Assert.True(second.StatusSince > first.StatusSince);
    }

    [Fact]
    public void Returns_nothing_when_claude_has_never_run()
    {
        using var dir = new TempDir();
        var reader = new SessionRegistryReader(new ClaudePaths(home: dir.Path), new FakeProbe());

        Assert.Empty(reader.Read());
    }

    [Theory]
    [InlineData("busy", AgentStatus.Active)]
    [InlineData("shell", AgentStatus.Active)]
    [InlineData("waiting", AgentStatus.Waiting)]
    [InlineData("idle", AgentStatus.Idle)]
    [InlineData("something-new", AgentStatus.Idle)]
    [InlineData(null, AgentStatus.Idle)]
    public void Maps_claudes_statuses(string? raw, AgentStatus expected) =>
        Assert.Equal(expected, SessionRegistryReader.MapStatus(raw));
}

public class ClaudePathsTests
{
    [Theory]
    [InlineData("/Users/b/Projects/Soar", "-Users-b-Projects-Soar")]
    [InlineData("/Users/b/Projects/Soar/.claude/worktrees/gem", "-Users-b-Projects-Soar--claude-worktrees-gem")]
    [InlineData("/Users/b/Application Support/x", "-Users-b-Application-Support-x")]
    public void Derives_claudes_project_directory_name(string cwd, string expected) =>
        Assert.Equal(expected, ClaudePaths.ProjectSlug(cwd));

    [Fact]
    public void Honours_the_config_dir_override()
    {
        var env = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = "/custom/claude" };
        var paths = new ClaudePaths(env, "/home/b");

        Assert.Equal("/custom/claude", paths.Root);
        Assert.Equal(Path.Combine("/custom/claude", "sessions"), paths.SessionsDir);
    }
}
