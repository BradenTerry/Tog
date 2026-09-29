using Tog.Core.Claude;
using Tog.Core.Model;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

public class ConversationReaderTests
{
    private static string User(string text, string origin = "human", bool meta = false) =>
        $$$"""{"type":"user","isSidechain":false{{{(meta ? ",\"isMeta\":true" : "")}}},"origin":{"kind":"{{{origin}}}"},"timestamp":"2026-09-25T10:00:00Z","message":{"role":"user","content":{{{System.Text.Json.JsonSerializer.Serialize(text)}}}}}""";

    private static string Assistant(string blocks, bool sidechain = false) =>
        $$$"""{"type":"assistant","isSidechain":{{{(sidechain ? "true" : "false")}}},"timestamp":"2026-09-25T10:00:05Z","message":{"role":"assistant","content":[{{{blocks}}}]}}""";

    private static string Text(string text) => $$"""{"type":"text","text":{{System.Text.Json.JsonSerializer.Serialize(text)}}}""";

    private static string Tool(string name, string input) => $$"""{"type":"tool_use","id":"t","name":"{{name}}","input":{{input}}}""";

    private static List<ChatEntry> Parse(params string[] lines)
    {
        var entries = new List<ChatEntry>();
        foreach (var line in lines)
        {
            ConversationReader.Consume(line, entries);
        }

        return entries;
    }

    [Fact]
    public void Reads_what_was_said_on_each_side()
    {
        var entries = Parse(
            User("Can you run the tests"),
            Assistant(Text("Running them now.")));

        Assert.Equal([ChatKind.You, ChatKind.Agent], entries.Select(e => e.Kind));
        Assert.Equal("Can you run the tests", entries[0].Text);
        Assert.Equal("Running them now.", entries[1].Text);
    }

    [Fact]
    public void Folds_a_run_of_tool_calls_into_one_entry()
    {
        var entries = Parse(
            Assistant(Text("Looking.")),
            Assistant(Tool("Bash", """{"command":"dotnet test","description":"Run the tests"}""")),
            Assistant(Tool("Read", """{"file_path":"/repo/a.cs"}""")),
            Assistant(Tool("Bash", """{"command":"git status"}""")),
            Assistant(Text("Done.")));

        Assert.Equal([ChatKind.Agent, ChatKind.Activity, ChatKind.Agent], entries.Select(e => e.Kind));
        var steps = entries[1].Steps;
        Assert.Equal(["Run the tests", "/repo/a.cs", "git status"], steps.Select(s => s.Summary));
        Assert.Equal("Ran 2 commands, read a file", ConversationReader.Describe(steps));
    }

    [Fact]
    public void Leaves_out_the_working_nobody_said()
    {
        var toolResult = """{"type":"user","isSidechain":false,"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"ok"}]}}""";
        var thinking = Assistant("""{"type":"thinking","thinking":"hmm"}""");
        var subagent = Assistant(Text("inside a subagent"), sidechain: true);
        var meta = User("Base directory for this skill: /x", meta: true);
        var reminder = User("<system-reminder>be brief</system-reminder>");
        var attachment = """{"type":"attachment","attachment":{"type":"file"}}""";

        Assert.Empty(Parse(toolResult, thinking, subagent, meta, reminder, attachment));
    }

    [Fact]
    public void Shows_what_a_command_the_cli_runs_itself_printed_as_the_reply()
    {
        var command = User("<command-name>/list-agents</command-name>\n<command-message>list-agents</command-message>\n<command-args></command-args>");
        var output = """{"type":"system","subtype":"local_command","content":"<local-command-stdout>This session: \u001b[1mdash\u001b[22m\n\nNo other sessions.</local-command-stdout>","isSidechain":false,"isMeta":false,"timestamp":"2026-09-25T10:00:01Z"}""";
        var older = User("<local-command-stdout>Set model to opus</local-command-stdout>");
        var empty = """{"type":"system","subtype":"local_command","content":"<local-command-stdout></local-command-stdout>","timestamp":"2026-09-25T10:00:02Z"}""";

        var entries = Parse(command, output, older, empty);

        Assert.Equal(
            [(ChatKind.You, "/list-agents"), (ChatKind.Agent, "This session: dash\n\nNo other sessions."), (ChatKind.Agent, "Set model to opus")],
            entries.Select(e => (e.Kind, e.Text)));
    }

    [Fact]
    public void Reads_a_subagents_own_transcript_whose_lines_are_all_side_chain()
    {
        var task = """{"type":"user","isSidechain":true,"timestamp":"2026-09-25T10:00:00Z","message":{"role":"user","content":"Review the diff"}}""";
        var reply = """{"type":"assistant","isSidechain":true,"timestamp":"2026-09-25T10:00:05Z","message":{"role":"assistant","content":[{"type":"text","text":"Looks fine"}]}}""";

        var asSession = new List<ChatEntry>();
        var asSubagent = new List<ChatEntry>();
        foreach (var line in new[] { task, reply })
        {
            ConversationReader.Consume(line, asSession);
            ConversationReader.Consume(line, asSubagent, sidechain: true);
        }

        Assert.Empty(asSession);
        Assert.Equal([(ChatKind.You, "Review the diff"), (ChatKind.Agent, "Looks fine")], asSubagent.Select(e => (e.Kind, e.Text)));
    }

    [Fact]
    public void Shows_a_background_task_finishing_as_a_notice()
    {
        var entries = Parse(User(
            "<task-notification>\n<task-id>b1</task-id>\n<summary>Background command \"Start Podman\" completed (exit code 0)</summary>\n</task-notification>",
            origin: "task-notification"));

        var notice = Assert.Single(entries);
        Assert.Equal(ChatKind.Notice, notice.Kind);
        Assert.Equal("Background command \"Start Podman\" completed (exit code 0)", notice.Text);
    }

    [Fact]
    public void Collects_the_files_a_run_changed_apart_from_its_other_steps()
    {
        var entries = Parse(
            Assistant(Tool("Bash", """{"command":"ls"}""")),
            Assistant("""{"type":"tool_use","id":"e1","name":"Edit","input":{"file_path":"/repo/a.cs","old_string":"one","new_string":"two\nthree"}}"""),
            Assistant("""{"type":"tool_use","id":"w1","name":"Write","input":{"file_path":"/repo/b.md","content":"# B\n\ntext\n"}}"""),
            Assistant("""{"type":"tool_use","id":"e2","name":"Edit","input":{"file_path":"/repo/a.cs","old_string":"x","new_string":"y"}}"""),
            Assistant(Text("Done.")));

        Assert.Equal([ChatKind.Activity, ChatKind.Agent], entries.Select(e => e.Kind));
        var run = entries[0];
        Assert.Equal(["ls"], run.Steps.Select(s => s.Summary));
        Assert.Equal(["/repo/a.cs", "/repo/b.md"], run.ChangedFiles.Select(f => f.Key));
        Assert.Equal((3, 2), (run.ChangedFiles[0].Sum(e => e.Added), run.ChangedFiles[0].Sum(e => e.Removed)));
        Assert.True(run.Edits[1].Created);
        Assert.Equal(["+# B", "+", "+text"], run.Edits[1].Hunks[0].Lines);
    }

    [Fact]
    public void Takes_the_patch_with_line_numbers_from_the_edits_result()
    {
        var call = Assistant("""{"type":"tool_use","id":"e1","name":"Edit","input":{"file_path":"/repo/a.cs","old_string":"one","new_string":"two"}}""");
        var result = """{"type":"user","isSidechain":false,"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"e1","content":"ok"}]},"toolUseResult":{"filePath":"/repo/a.cs","structuredPatch":[{"oldStart":10,"oldLines":3,"newStart":10,"newLines":3,"lines":[" before","-one","+two"," after"]}]}}""";
        var failedCall = Assistant("""{"type":"tool_use","id":"e2","name":"Edit","input":{"file_path":"/repo/c.cs","old_string":"a","new_string":"b"}}""");
        var failed = """{"type":"user","isSidechain":false,"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"e2","is_error":true,"content":"String not found"}]}}""";

        var entries = Parse(call, result, failedCall, failed);

        var edits = Assert.Single(entries).Edits;
        var hunk = Assert.Single(edits[0].Hunks);
        Assert.Equal((10, 10), (hunk.OldStart, hunk.NewStart));
        Assert.Equal([" before", "-one", "+two", " after"], hunk.Lines);
        Assert.False(edits[0].Failed);
        Assert.True(edits[1].Failed);
    }

    [Fact]
    public void Shows_a_message_sent_mid_turn_where_the_agent_read_it()
    {
        var queued = """{"type":"attachment","isSidechain":false,"timestamp":"2026-09-25T10:00:03Z","attachment":{"type":"queued_command","prompt":[{"type":"text","text":"also fix the title"}],"commandMode":"prompt","origin":{"kind":"human"}}}""";
        var harness = """{"type":"attachment","isSidechain":false,"attachment":{"type":"queued_command","prompt":"ran by a hook","commandMode":"prompt","origin":{"kind":"hook"}}}""";

        var entries = Parse(
            Assistant(Tool("Bash", """{"command":"ls"}""")),
            queued,
            harness,
            Assistant(Tool("Read", """{"file_path":"/repo/a.cs"}""")),
            Assistant(Text("Done, and the title too.")));

        Assert.Equal([ChatKind.Activity, ChatKind.You, ChatKind.Activity, ChatKind.Agent], entries.Select(e => e.Kind));
        Assert.Equal("also fix the title", entries[1].Text);
    }

    [Fact]
    public void Shows_a_slash_command_as_it_was_typed()
    {
        var entries = Parse(User("<command-name>/review</command-name>\n<command-args>PR 12</command-args>"));

        Assert.Equal("/review PR 12", Assert.Single(entries).Text);
    }

    [Fact]
    public void Follows_a_transcript_as_it_grows()
    {
        using var temp = new TempDir();
        var file = temp.File("s.jsonl", User("first") + "\n");
        var reader = new ConversationReader();

        Assert.Single(reader.Read("s", file));

        File.AppendAllText(file, Assistant(Text("reply")) + "\n" + Assistant(Text("half")));

        // The second line has no newline yet, so it is still being written.
        var entries = reader.Read("s", file);
        Assert.Equal(["first", "reply"], entries.Select(e => e.Text));

        File.AppendAllText(file, "\n");
        Assert.Equal(3, reader.Read("s", file).Count);
    }

    [Fact]
    public void Shows_the_agents_questions_as_said_and_your_answers_as_yours()
    {
        const string Questions = "\"questions\":" + """[{"question":"Which database?","header":"Database","multiSelect":false,"options":[{"label":"Postgres","description":"d"},{"label":"SQLite","description":"d"}]},{"question":"Which platforms?","header":"Targets","multiSelect":true,"options":[{"label":"macOS","description":"d"},{"label":"Linux","description":"d"}]}]""";
        const string Head = """{"type":"user","isSidechain":false,"timestamp":"2026-09-25T10:00:09Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"done"}]},"toolUseResult":{""";
        var answered = Head + Questions + ""","answers":{"Which database?":"SQLite","Which platforms?":"macOS, Linux"},"annotations":{"Which database?":{"notes":"small data"}}}}""";
        var skipped = Head + Questions + ""","answers":{}}}""";

        var entries = Parse(Assistant(Tool("AskUserQuestion", "{" + Questions + "}")), answered, skipped);

        Assert.Equal([ChatKind.Agent, ChatKind.You, ChatKind.Notice], entries.Select(e => e.Kind));
        Assert.Equal("1. Which database?\n2. Which platforms?", entries[0].Text);
        Assert.Equal("Database: SQLite (small data)\nTargets: macOS, Linux", entries[1].Text);
    }
}
