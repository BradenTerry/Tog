using AgentsDashboard.Core.Claude;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class TranscriptReaderTests
{
    private static string Title(string text) =>
        "{\"type\":\"ai-title\",\"aiTitle\":"
        + System.Text.Json.JsonSerializer.Serialize(text) + ",\"sessionId\":\"s\"}";

    private static string Skill(string name) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\","
        + "\"name\":\"Skill\",\"input\":{\"skill\":\"" + name + "\"}}]}}";

    private const string Noise =
        """{"type":"user","message":{"content":[{"type":"text","text":"hello"}]}}""";

    [Fact]
    public void Reads_the_latest_work_summary()
    {
        using var dir = new TempDir();
        var file = dir.File("t.jsonl", string.Join('\n',
        [
            Title("First guess at the task"),
            Noise,
            Title("Estimate Azure hosting costs"),
            "",
        ]));

        var facts = new TranscriptReader().Read("s", file);

        Assert.Equal("Estimate Azure hosting costs", facts.Summary);
    }

    [Fact]
    public void Collects_skills_in_first_use_order_without_repeats()
    {
        using var dir = new TempDir();
        var file = dir.File("t.jsonl", string.Join('\n',
            [Skill("task-spec"), Noise, Skill("tdd-workflow"), Skill("task-spec"), ""]));

        var facts = new TranscriptReader().Read("s", file);

        Assert.Equal(["task-spec", "tdd-workflow"], facts.Skills);
    }

    [Fact]
    public void Only_reads_what_was_appended_since_last_time()
    {
        using var dir = new TempDir();
        var file = dir.File("t.jsonl", Title("First") + "\n");
        var reader = new TranscriptReader();

        Assert.Equal("First", reader.Read("s", file).Summary);

        File.AppendAllText(file, Title("Second") + "\n");

        // A transcript is append-only and can reach tens of megabytes, so it is
        // read forward from a cursor rather than re-read whole.
        Assert.Equal("Second", reader.Read("s", file).Summary);
    }

    [Fact]
    public void Ignores_a_record_that_is_only_half_written()
    {
        using var dir = new TempDir();
        var file = dir.File("t.jsonl", Title("Complete") + "\n" + "{\"type\":\"ai-tit");
        var reader = new TranscriptReader();

        Assert.Equal("Complete", reader.Read("s", file).Summary);

        // Once the writer finishes the line it is picked up.
        File.AppendAllText(file, "le\",\"aiTitle\":\"Finished\"}\n");
        Assert.Equal("Finished", reader.Read("s", file).Summary);
    }

    [Fact]
    public void Starts_over_when_the_transcript_is_replaced()
    {
        using var dir = new TempDir();
        var file = dir.File("t.jsonl", Title("Long session") + "\n" + Noise + "\n" + Noise + "\n");
        var reader = new TranscriptReader();
        Assert.Equal("Long session", reader.Read("s", file).Summary);

        File.WriteAllText(file, Title("Fresh") + "\n");

        Assert.Equal("Fresh", reader.Read("s", file).Summary);
    }

    [Fact]
    public void A_transcript_with_nothing_useful_in_it_yields_nothing()
    {
        using var dir = new TempDir();
        var file = dir.File("t.jsonl", Noise + "\n" + Noise + "\n");

        var facts = new TranscriptReader().Read("s", file);

        Assert.Null(facts.Summary);
        Assert.Empty(facts.Skills);
    }

    [Fact]
    public void A_missing_transcript_is_not_an_error()
    {
        var facts = new TranscriptReader().Read("s", "/nowhere/at/all.jsonl");

        Assert.Null(facts.Summary);
        Assert.Empty(facts.Skills);
    }
}

public class TranscriptLocatorTests
{
    [Fact]
    public void Finds_a_transcript_by_the_slug_claude_derives_from_the_cwd()
    {
        using var dir = new TempDir();
        var paths = new ClaudePaths(home: dir.Path);
        dir.File(".claude/projects/-repo-wt/abc.jsonl", "{}");

        Assert.NotNull(new TranscriptLocator(paths).Locate("abc", "/repo/wt"));
    }

    [Fact]
    public void Falls_back_to_a_scan_when_the_slug_does_not_match()
    {
        using var dir = new TempDir();
        var paths = new ClaudePaths(home: dir.Path);

        // The slug is Claude's private convention, so a mismatch must cost a work
        // summary at most, never the agent.
        dir.File(".claude/projects/some-other-shape/abc.jsonl", "{}");

        Assert.NotNull(new TranscriptLocator(paths).Locate("abc", "/repo/wt"));
    }

    [Fact]
    public void Reports_nothing_when_there_is_no_transcript()
    {
        using var dir = new TempDir();
        var paths = new ClaudePaths(home: dir.Path);
        dir.Dir(".claude/projects");

        Assert.Null(new TranscriptLocator(paths).Locate("abc", "/repo/wt"));
    }

    [Fact]
    public void Finds_the_subagent_directory_beside_the_transcript()
    {
        using var dir = new TempDir();
        var paths = new ClaudePaths(home: dir.Path);
        dir.File(".claude/projects/-repo/abc.jsonl", "{}");
        dir.Dir(".claude/projects/-repo/abc/subagents");

        Assert.NotNull(new TranscriptLocator(paths).SubagentsDir("abc", "/repo"));
    }
}

public class SubagentReaderTests
{
    [Fact]
    public void Reads_the_subagents_running_under_a_session()
    {
        using var dir = new TempDir();
        var paths = new ClaudePaths(home: dir.Path);
        dir.File(".claude/projects/-repo/abc.jsonl", "{}");
        dir.File(
            ".claude/projects/-repo/abc/subagents/agent-a1e86d7e5.meta.json",
            """{"agentType":"general-purpose","description":"Review webview UI","toolUseId":"t1","spawnDepth":1}""");

        var subagent = Assert.Single(new SubagentReader(new TranscriptLocator(paths)).Read("abc", "/repo"));

        Assert.Equal("a1e86d7e5", subagent.Id);
        Assert.Equal("general-purpose", subagent.AgentType);
        Assert.Equal("Review webview UI", subagent.Description);
        Assert.Equal(1, subagent.SpawnDepth);
    }

    [Fact]
    public void A_session_with_no_subagent_directory_has_no_subagents()
    {
        using var dir = new TempDir();
        var paths = new ClaudePaths(home: dir.Path);
        dir.File(".claude/projects/-repo/abc.jsonl", "{}");

        Assert.Empty(new SubagentReader(new TranscriptLocator(paths)).Read("abc", "/repo"));
    }
}

public class AgentLabelTests
{
    private static AgentSession Session(string? summary = null, string? prompt = null, string? name = null) =>
        new()
        {
            SessionId = "2086ee18-3519-4927-ad0a-2d7fcecdefaf",
            Pid = 1,
            Cwd = "/repo",
            Summary = summary,
            LastPrompt = prompt,
            Name = name,
        };

    [Fact]
    public void Prefers_the_title()
    {
        var session = Session("Claude agent version display", "open pr and release", "agent-worktrees-07");

        Assert.Equal("Claude agent version display", session.Label);
        Assert.False(session.LabelIsPrompt);
    }

    [Fact]
    public void Falls_back_to_the_last_prompt_rather_than_the_derived_name()
    {
        // Claude's derived name is the directory plus a counter, so it repeats
        // what the worktree heading already said.
        var session = Session(prompt: "open pr and release patch version", name: "agent-worktrees-07");

        Assert.Equal("open pr and release patch version", session.Label);
        Assert.True(session.LabelIsPrompt);
    }

    [Fact]
    public void Uses_the_name_when_there_is_nothing_better() =>
        Assert.Equal("agent-worktrees-07", Session(name: "agent-worktrees-07").Label);

    [Fact]
    public void Falls_all_the_way_back_to_the_session_id() =>
        Assert.Equal("2086ee18", Session().Label);

    [Fact]
    public void Flattens_and_cuts_a_long_prompt()
    {
        var session = Session(prompt: string.Join('\n', Enumerable.Repeat("some fairly long words here", 10)));

        Assert.True(session.Label.Length <= 94);
        Assert.EndsWith("...", session.Label, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', session.Label);
    }

    [Fact]
    public void Leaves_a_short_prompt_alone() =>
        Assert.Equal("fix the build", AgentSession.Shorten("fix the build"));

    [Fact]
    public void Cuts_at_a_word_boundary() =>
        Assert.Equal("one two...", AgentSession.Shorten("one two three four", limit: 9));
}

public class TranscriptTitleTests
{
    private const string Noise = """{"type":"user","message":{"content":[]}}""";

    private static string AiTitle(string text) =>
        "{\"type\":\"ai-title\",\"aiTitle\":\"" + text + "\"}";

    private static string CustomTitle(string text) =>
        "{\"type\":\"custom-title\",\"customTitle\":\"" + text + "\"}";

    [Fact]
    public void Reads_a_title_someone_set()
    {
        using var dir = new Support.TempDir();
        var file = dir.File("t.jsonl", CustomTitle("Claude agent version display") + "\n");

        Assert.Equal("Claude agent version display", new TranscriptReader().Read("s", file).Summary);
    }

    [Fact]
    public void A_set_title_outranks_a_generated_one_whichever_came_first()
    {
        using var dir = new Support.TempDir();
        var file = dir.File("t.jsonl", string.Join('\n',
            [CustomTitle("Chosen"), Noise, AiTitle("Guessed"), ""]));

        // A title someone chose must not be replaced by one Claude inferred later.
        Assert.Equal("Chosen", new TranscriptReader().Read("s", file).Summary);
    }

    [Fact]
    public void Reads_the_last_prompt_from_its_own_field()
    {
        using var dir = new Support.TempDir();
        var file = dir.File(
            "t.jsonl",
            """{"type":"last-prompt","lastPrompt":"open pr and release","sessionId":"s"}""" + "\n");

        Assert.Equal("open pr and release", new TranscriptReader().Read("s", file).LastPrompt);
    }

    [Fact]
    public void A_transcript_with_neither_kind_of_title_has_no_summary()
    {
        using var dir = new Support.TempDir();
        var file = dir.File("t.jsonl", Noise + "\n");

        Assert.Null(new TranscriptReader().Read("s", file).Summary);
    }
}
