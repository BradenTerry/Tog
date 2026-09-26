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
/// conversation, and <c>claude attach</c> is there for the rest.
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
            && !line.Contains("\"assistant\"", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Bool(root, "isSidechain")
                || Bool(root, "isMeta")
                || !root.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content))
            {
                return;
            }

            var at = root.TryGetProperty("timestamp", out var ts)
                     && ts.ValueKind == JsonValueKind.String
                     && DateTimeOffset.TryParse(ts.GetString(), out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            switch (Str(root, "type"))
            {
                case "user":
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
        var text = content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : content.ValueKind == JsonValueKind.Array
                ? string.Join("\n\n", content.EnumerateArray()
                    .Where(b => Str(b, "type") == "text")
                    .Select(b => Str(b, "text"))
                    .OfType<string>())
                : null;

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
