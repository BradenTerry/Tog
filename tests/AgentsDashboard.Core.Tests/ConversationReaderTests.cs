using AgentsDashboard.Core.Claude;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

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
        var reminder = User("<local-command-stdout>ok</local-command-stdout>");
        var attachment = """{"type":"attachment","attachment":{"type":"file"}}""";

        Assert.Empty(Parse(toolResult, thinking, subagent, meta, reminder, attachment));
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
}
