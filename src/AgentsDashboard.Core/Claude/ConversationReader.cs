using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Claude;

/// <summary>
/// Reads a session's transcript as a conversation: what you said, what the agent
/// said, and what it did in between.
/// </summary>
/// <remarks>
/// <para>
/// Read on demand, for the chat that is open, rather than every tick for every
/// agent: nothing else needs the whole thread, and a transcript is mostly tool
/// output nobody reads. Like <see cref="TranscriptReader"/> it keeps a cursor per
/// session and only parses what was appended since the last read, so following a
/// working agent once a second costs a stat and a few lines.
/// </para>
/// <para>
/// Tool calls are folded into one entry per run between two things said, with a
/// one-line summary of each step. Thinking, tool results, attachments, subagent
/// sidechains and harness bookkeeping are left out: the chat is for reading the
/// conversation, and <c>claude attach</c> is there for the rest. The exception is
/// AskUserQuestion: its questions read as the agent speaking and its result as
/// your answers, since that exchange is the conversation.
/// </para>
/// <para>
/// One attachment is kept: a message you send while the agent is working is not
/// recorded as a user message. The CLI queues it and hands it to the agent
/// between two steps of the running turn, and the transcript records that as a
/// <c>queued_command</c> attachment at the point it was read. Leaving it out
/// would lose what you said, and leave the chat's "Sent." bubble with nothing to
/// match, so it would sit below the agent's reply as if it had been ignored.
/// </para>
/// </remarks>
public sealed class ConversationReader
{
    private const int MaxFirstReadBytes = 4 * 1024 * 1024;

    /// <summary>How many entries a thread keeps. Older ones fall off the top.</summary>
    public const int MaxEntries = 400;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Cursor> _cursors = new(StringComparer.Ordinal);

    private sealed class Cursor
    {
        public string Path = "";
        public long Offset;
        public readonly List<ChatEntry> Entries = [];
    }

    /// <summary>The conversation so far, oldest first.</summary>
    public IReadOnlyList<ChatEntry> Read(string sessionId, string transcriptPath)
    {
        lock (_gate)
        {
            if (!_cursors.TryGetValue(sessionId, out var cursor) || cursor.Path != transcriptPath)
            {
                cursor = new Cursor { Path = transcriptPath };
                _cursors[sessionId] = cursor;
            }

            ReadAppended(cursor);
            return cursor.Entries.ToArray();
        }
    }

    /// <summary>Forgets a session, so the next read starts from the top.</summary>
    public void Forget(string sessionId)
    {
        lock (_gate)
        {
            _cursors.Remove(sessionId);
        }
    }

    private static void ReadAppended(Cursor cursor)
    {
        try
        {
            using var stream = new FileStream(
                cursor.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < cursor.Offset)
            {
                cursor.Offset = 0;
                cursor.Entries.Clear();
            }

            if (cursor.Offset == 0 && stream.Length > MaxFirstReadBytes)
            {
                cursor.Offset = SeekToLineStart(stream, stream.Length - MaxFirstReadBytes);
            }

            stream.Seek(cursor.Offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);

            var consumed = cursor.Offset;
            while (reader.ReadLine() is { } line)
            {
                // Only whole lines: a record still being written is left for the
                // next read rather than parsed half-formed.
                var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (consumed + lineBytes > stream.Length)
                {
                    break;
                }

                consumed += lineBytes;
                Consume(line, cursor.Entries);
            }

            cursor.Offset = consumed;

            if (cursor.Entries.Count > MaxEntries)
            {
                cursor.Entries.RemoveRange(0, cursor.Entries.Count - MaxEntries);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Mid-write or gone. What was read still stands.
        }
    }

    /// <summary>Parses one transcript line and appends what it says to the thread.</summary>
    public static void Consume(string line, List<ChatEntry> entries)
    {
        if (!line.Contains("\"user\"", StringComparison.Ordinal)
            && !line.Contains("\"assistant\"", StringComparison.Ordinal)
            && !line.Contains("queued_command", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Bool(root, "isSidechain")
                || Bool(root, "isMeta"))
            {
                return;
            }

            var at = root.TryGetProperty("timestamp", out var ts)
                     && ts.ValueKind == JsonValueKind.String
                     && DateTimeOffset.TryParse(ts.GetString(), out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            if (Str(root, "type") == "attachment")
            {
                Queued(root, at, entries);
                return;
            }

            if (!root.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content))
            {
                return;
            }

            switch (Str(root, "type"))
            {
                case "user":
                    EditResults(root, content, entries);
                    Answers(root, at, entries);
                    User(root, content, at, entries);
                    break;
                case "assistant":
                    Assistant(content, at, entries);
                    break;
            }
        }
        catch (JsonException)
        {
            // A line that does not parse says nothing worth showing.
        }
    }

    private static void User(JsonElement root, JsonElement content, DateTimeOffset at, List<ChatEntry> entries)
    {
        var text = Text(content);

        if (string.IsNullOrWhiteSpace(text))
        {
            // Tool results are user records too. They are the agent's working, not
            // anything anyone said.
            return;
        }

        var origin = root.TryGetProperty("origin", out var o) ? Str(o, "kind") : null;
        if (origin == "task-notification" || text.TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal))
        {
            var summary = Tag(text, "summary") ?? "A background task finished.";
            entries.Add(ChatEntry.Said(ChatKind.Notice, at, summary));
            return;
        }

        if (origin is not null and not "human")
        {
            return;
        }

        var trimmed = text.Trim();

        // A slash command is recorded as markup around the command. Show what was
        // typed, and leave out the command's own output.
        if (trimmed.StartsWith("<local-command", StringComparison.Ordinal)
            || trimmed.StartsWith("<system-reminder>", StringComparison.Ordinal))
        {
            return;
        }

        if (Tag(trimmed, "command-name") is { } command)
        {
            var args = Tag(trimmed, "command-args");
            trimmed = string.IsNullOrWhiteSpace(args) ? command : command + " " + args;
        }

        entries.Add(ChatEntry.Said(ChatKind.You, at, trimmed));
    }

    /// <summary>A message you sent mid-turn, where the agent read it.</summary>
    private static void Queued(JsonElement root, DateTimeOffset at, List<ChatEntry> entries)
    {
        if (!root.TryGetProperty("attachment", out var attachment)
            || Str(attachment, "type") != "queued_command"
            || Str(attachment, "commandMode") is { } mode && mode != "prompt"
            || (attachment.TryGetProperty("origin", out var o) ? Str(o, "kind") : null) is { } origin && origin != "human"
            || !attachment.TryGetProperty("prompt", out var prompt))
        {
            return;
        }

        var text = Text(prompt);
        if (!string.IsNullOrWhiteSpace(text))
        {
            entries.Add(ChatEntry.Said(ChatKind.You, at, text.Trim()));
        }
    }

    /// <summary>A content value as text: a plain string, or the text blocks of an array joined.</summary>
    private static string? Text(JsonElement content) =>
        content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : content.ValueKind == JsonValueKind.Array
                ? string.Join("\n\n", content.EnumerateArray()
                    .Where(b => Str(b, "type") == "text")
                    .Select(b => Str(b, "text"))
                    .OfType<string>())
                : null;

    private static void Assistant(JsonElement content, DateTimeOffset at, List<ChatEntry> entries)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            if (content.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
            {
                entries.Add(ChatEntry.Said(ChatKind.Agent, at, s.Trim()));
            }

            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            switch (Str(block, "type"))
            {
                case "text" when Str(block, "text") is { } said && !string.IsNullOrWhiteSpace(said):
                    entries.Add(ChatEntry.Said(ChatKind.Agent, at, said.Trim()));
                    break;

                case "tool_use" when Str(block, "name") == "AskUserQuestion" && Asked(block) is { } asked:
                    // Questions for you are the agent talking to you, not working,
                    // so they read as something it said rather than a folded step.
                    entries.Add(ChatEntry.Said(ChatKind.Agent, at, asked));
                    break;

                case "tool_use" when EditOf(block) is { } edit:
                    // A file change belongs to the run like any call, but is kept
                    // out of its steps: the view folds the files changed on their
                    // own, since they are what you most often want to look at.
                    if (entries.Count > 0 && entries[^1].Kind == ChatKind.Activity)
                    {
                        var run = entries[^1];
                        entries[^1] = run with { Edits = [.. run.Edits, edit] };
                    }
                    else
                    {
                        entries.Add(new ChatEntry(ChatKind.Activity, at, "", []) { Edits = [edit] });
                    }

                    break;

                case "tool_use":
                    var step = Step(block);

                    // Consecutive calls fold into one entry, so a run of twenty
                    // reads is one line in the chat rather than twenty.
                    if (entries.Count > 0 && entries[^1].Kind == ChatKind.Activity)
                    {
                        var last = entries[^1];
                        entries[^1] = last with { Steps = [.. last.Steps, step] };
                    }
                    else
                    {
                        entries.Add(new ChatEntry(ChatKind.Activity, at, "", [step]));
                    }

                    break;
            }
        }
    }

    /// <summary>An AskUserQuestion call as the questions it asks, numbered when there are several.</summary>
    private static string? Asked(JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input)
            || !input.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var asked = questions.EnumerateArray().Select(q => Str(q, "question")).OfType<string>().ToList();
        return asked.Count switch
        {
            0 => null,
            1 => asked[0],
            _ => string.Join('\n', asked.Select((q, i) => $"{i + 1}. {q}")),
        };
    }

    /// <summary>
    /// The answers to an AskUserQuestion, from the tool's result: one line per
    /// question, by its short header, with a note typed beside a pick after it.
    /// Answered in the dashboard or in a terminal, they are yours, so they read as
    /// something you said. Skipped, they are a notice.
    /// </summary>
    private static void Answers(JsonElement root, DateTimeOffset at, List<ChatEntry> entries)
    {
        if (!root.TryGetProperty("toolUseResult", out var result)
            || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array
            || !result.TryGetProperty("answers", out var answers)
            || answers.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var notes = result.TryGetProperty("annotations", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        var lines = new List<string>();
        foreach (var question in questions.EnumerateArray())
        {
            if (Str(question, "question") is not { } asked || Str(answers, asked) is not { Length: > 0 } answer)
            {
                continue;
            }

            var label = Str(question, "header") is { Length: > 0 } header ? header : asked;
            var note = notes.ValueKind == JsonValueKind.Object && notes.TryGetProperty(asked, out var n) ? Str(n, "notes") : null;
            lines.Add(string.IsNullOrWhiteSpace(note) ? $"{label}: {answer}" : $"{label}: {answer} ({note.Trim()})");
        }

        entries.Add(lines.Count == 0
            ? ChatEntry.Said(ChatKind.Notice, at, "You skipped the agent's questions.")
            : ChatEntry.Said(ChatKind.You, at, string.Join('\n', lines)));
    }

    /// <summary>The most lines one edit shows. A Write of a generated file can be thousands.</summary>
    public const int MaxEditLines = 400;

    /// <summary>
    /// An Edit, MultiEdit or Write call as a change, with a first diff made from
    /// its input. The input has no line numbers; the tool's result, a record
    /// or two later, carries the real patch and replaces it (<see cref="EditResults"/>).
    /// </summary>
    private static ChatEdit? EditOf(JsonElement block)
    {
        var name = Str(block, "name");
        if (name is not ("Edit" or "MultiEdit" or "Write")
            || Str(block, "id") is not { } id
            || !block.TryGetProperty("input", out var input)
            || Str(input, "file_path") is not { } path)
        {
            return null;
        }

        var hunks = new List<ChatHunk>();
        switch (name)
        {
            case "Write":
                hunks.Add(new ChatHunk(null, 1, Prefixed('+', Str(input, "content"))));
                break;
            case "Edit":
                hunks.Add(Replacement(Str(input, "old_string"), Str(input, "new_string")));
                break;
            case "MultiEdit" when input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array:
                hunks.AddRange(edits.EnumerateArray().Select(e => Replacement(Str(e, "old_string"), Str(e, "new_string"))));
                break;
        }

        var (capped, truncated) = Cap(hunks);
        return new ChatEdit(id, path, name == "Write", capped, Truncated: truncated);
    }

    /// <summary>
    /// Finishes the edits whose results this user record carries: the patch with
    /// line numbers in place of the one made from the input, whether a Write made
    /// the file or replaced it, and whether the tool failed.
    /// </summary>
    private static void EditResults(JsonElement root, JsonElement content, List<ChatEntry> entries)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (Str(block, "type") != "tool_result" || Str(block, "tool_use_id") is not { } id)
            {
                continue;
            }

            // The result follows its call closely, so only the tail is searched.
            int index = -1, at = -1;
            for (var i = entries.Count - 1; i >= Math.Max(0, entries.Count - 16) && index < 0; i--)
            {
                var found = entries[i].Edits.ToList().FindIndex(e => e.ToolUseId == id);
                if (found >= 0)
                {
                    index = i;
                    at = found;
                }
            }

            if (index < 0)
            {
                continue;
            }

            var edit = entries[index].Edits[at];
            if (block.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True)
            {
                edit = edit with { Failed = true };
            }
            else if (root.TryGetProperty("toolUseResult", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (Str(result, "type") is { } kind)
                {
                    edit = edit with { Created = kind == "create" };
                }

                if (result.TryGetProperty("structuredPatch", out var patch)
                    && patch.ValueKind == JsonValueKind.Array
                    && patch.GetArrayLength() > 0)
                {
                    var hunks = patch.EnumerateArray().Select(h => new ChatHunk(
                        Int(h, "oldStart"),
                        Int(h, "newStart"),
                        h.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array
                            ? lines.EnumerateArray().Select(l => l.GetString() ?? "").ToList()
                            : [])).ToList();
                    var (capped, truncated) = Cap(hunks);
                    edit = edit with { Hunks = capped, Truncated = truncated };
                }
            }

            var edits = entries[index].Edits.ToArray();
            edits[at] = edit;
            entries[index] = entries[index] with { Edits = edits };
        }
    }

    private static ChatHunk Replacement(string? before, string? after) =>
        new(null, null, [.. Prefixed('-', before), .. Prefixed('+', after)]);

    private static List<string> Prefixed(char mark, string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.TrimEnd('\n').Split('\n').Select(l => mark + l.TrimEnd('\r')).ToList();

    private static (IReadOnlyList<ChatHunk> Hunks, bool Truncated) Cap(List<ChatHunk> hunks)
    {
        var left = MaxEditLines;
        var kept = new List<ChatHunk>();
        foreach (var hunk in hunks)
        {
            if (left <= 0)
            {
                return (kept, true);
            }

            if (hunk.Lines.Count > left)
            {
                kept.Add(hunk with { Lines = hunk.Lines.Take(left).ToList() });
                return (kept, true);
            }

            kept.Add(hunk);
            left -= hunk.Lines.Count;
        }

        return (kept, false);
    }

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
            ? n
            : null;

    /// <summary>One tool call as the few words that say what it did.</summary>
    public static ChatStep Step(JsonElement block)
    {
        var name = Str(block, "name") ?? "tool";
        var input = block.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        string? Arg(string key) => input.ValueKind == JsonValueKind.Object ? Str(input, key) : null;

        var summary = name switch
        {
            "Bash" => Arg("description") ?? Arg("command"),
            "Read" or "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => Arg("file_path") ?? Arg("notebook_path"),
            "Grep" or "Glob" => Arg("pattern"),
            "Skill" => Arg("skill"),
            "Agent" or "Task" => Arg("description"),
            "WebFetch" => Arg("url"),
            "WebSearch" => Arg("query"),
            "TodoWrite" => "updated the todo list",
            _ => Arg("description"),
        };

        return new ChatStep(name, OneLine(summary ?? ""));
    }

    /// <summary>How a run of steps reads folded: "ran 3 commands, edited 2 files".</summary>
    public static string Describe(IReadOnlyList<ChatStep> steps)
    {
        var parts = steps
            .GroupBy(s => Verb(s.Tool))
            .Select(g => g.Key.Count(g.Count()))
            .ToList();
        return parts.Count == 0 ? "" : char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..];
    }

    private sealed record VerbPhrase(string Verb, string One, string Many)
    {
        public string Count(int n) => n == 1 ? $"{Verb} {One}" : $"{Verb} {n} {Many}";
    }

    private static VerbPhrase Verb(string tool) => tool switch
    {
        "Bash" => new("ran", "a command", "commands"),
        "Read" => new("read", "a file", "files"),
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => new("edited", "a file", "files"),
        "Grep" or "Glob" => new("searched", "once", "times"),
        "Skill" => new("used", "a skill", "skills"),
        "Agent" or "Task" => new("started", "a subagent", "subagents"),
        "WebFetch" or "WebSearch" => new("looked up", "a page", "pages"),
        "TodoWrite" => new("updated", "its todo list", "its todo list"),
        _ => new("used", tool, tool),
    };

    private static string OneLine(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 140 ? flat : flat[..137] + "...";
    }

    private static string? Tag(string text, string name)
    {
        var match = Regex.Match(text, $"<{name}>(.*?)</{name}>", RegexOptions.Singleline);
        return match.Success && match.Groups[1].Value.Trim() is { Length: > 0 } value ? value : null;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long SeekToLineStart(FileStream stream, long from)
    {
        stream.Seek(from, SeekOrigin.Begin);
        var buffer = new byte[1];
        while (stream.Position < stream.Length)
        {
            if (stream.Read(buffer, 0, 1) != 1)
            {
                break;
            }

            if (buffer[0] == (byte)'\n')
            {
                return stream.Position;
            }
        }

        return stream.Length;
    }
}
