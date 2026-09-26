using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Claude;

/// <summary>What a session's transcript adds to its registry entry.</summary>
/// <param name="Summary">
/// What the session is called: the title someone set for it, else the one Claude
/// generated. A set title wins, because it was chosen rather than inferred.
/// </param>
/// <param name="Skills">Skills the session has invoked, deduped, in first-use order.</param>
/// <param name="LastPrompt">The most recent prompt sent to the session.</param>
/// <param name="LastReply">The agent's most recent words, so you can read what it said.</param>
/// <param name="BackgroundCommands">Shell commands started in the background with no completion notice yet.</param>
public sealed record TranscriptFacts(
    string? Summary,
    IReadOnlyList<string> Skills,
    string? LastPrompt,
    string? LastReply = null,
    IReadOnlyList<BackgroundCommand>? BackgroundCommands = null);

/// <summary>
/// Reads the two things the registry cannot answer: what the agent says it is
/// working on, and which skills it has used.
/// </summary>
/// <remarks>
/// A transcript is append-only and can reach tens of megabytes, so it is read
/// forward from a per-session cursor: the first read scans what is there, and
/// every read after it only sees what was appended. Whole lines only, so a
/// record still being written is picked up on the next pass rather than parsed
/// half-formed.
/// </remarks>
public sealed class TranscriptReader
{
    private const int MaxFirstReadBytes = 4 * 1024 * 1024;

    private readonly Dictionary<string, Cursor> _cursors = new(StringComparer.Ordinal);

    private sealed class Cursor
    {
        public long Offset;

        /// <summary>A title someone set for the session.</summary>
        public string? CustomTitle;

        /// <summary>A title Claude generated for the session.</summary>
        public string? AiTitle;

        public string? LastPrompt;
        public string? LastReply;
        public readonly List<string> Skills = [];
        public readonly HashSet<string> SkillSet = new(StringComparer.Ordinal);

        /// <summary>Background commands still running, in the order they started.</summary>
        public readonly List<BackgroundCommand> Background = [];

        /// <summary>Background task id to the Bash call that started it, for a notice that only names the task.</summary>
        public readonly Dictionary<string, string> TaskCalls = new(StringComparer.Ordinal);
    }

    /// <summary>Everything known about this session from its transcript so far.</summary>
    public TranscriptFacts Read(string sessionId, string transcriptPath)
    {
        if (!_cursors.TryGetValue(sessionId, out var cursor))
        {
            cursor = new Cursor();
            _cursors[sessionId] = cursor;
        }

        try
        {
            using var stream = new FileStream(
                transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < cursor.Offset)
            {
                // The file shrank, which means it was replaced. Start over.
                cursor.Offset = 0;
            }

            if (cursor.Offset == 0 && stream.Length > MaxFirstReadBytes)
            {
                // A long-running session resumed into the dashboard. Read only the
                // tail: the work summary is rewritten constantly, so the recent
                // part has it, and an older skill is worth less than a fast start.
                cursor.Offset = SeekToLineStart(stream, stream.Length - MaxFirstReadBytes);
            }

            stream.Seek(cursor.Offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);

            var consumed = cursor.Offset;
            while (reader.ReadLine() is { } line)
            {
                // ReadLine cannot tell a final line without a newline from a
                // complete one, so only count a line as consumed once the buffer
                // has moved past it.
                var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (consumed + lineBytes > stream.Length)
                {
                    break;
                }

                consumed += lineBytes;
                Consume(line, cursor);
            }

            cursor.Offset = consumed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Mid-write or gone. Whatever was already read still stands.
        }

        return new TranscriptFacts(
            cursor.CustomTitle ?? cursor.AiTitle,
            cursor.Skills.ToArray(),
            cursor.LastPrompt,
            cursor.LastReply,
            cursor.Background.ToArray());
    }

    /// <summary>Drop cursors for sessions that have ended.</summary>
    public void Forget(IReadOnlySet<string> liveSessionIds)
    {
        foreach (var gone in _cursors.Keys.Where(k => !liveSessionIds.Contains(k)).ToList())
        {
            _cursors.Remove(gone);
        }
    }

    private static void Consume(string line, Cursor cursor)
    {
        // Cheap reject before parsing. Assistant records are most of a transcript
        // and have to be parsed anyway for the agent's last words, so the saving
        // is smaller than it was; it still skips the attachments and tool results
        // that make up the bulk of the rest.
        var interesting = line.Contains("\"assistant\"", StringComparison.Ordinal)
            || line.Contains("\"ai-title\"", StringComparison.Ordinal)
            || line.Contains("\"custom-title\"", StringComparison.Ordinal)
            || line.Contains("\"last-prompt\"", StringComparison.Ordinal)
            || line.Contains("backgroundTaskId", StringComparison.Ordinal)
            || (cursor.Background.Count > 0 && line.Contains("\"is_error\":true", StringComparison.Ordinal));

        // A finished background command is announced as a task notification.
        // Read from the raw line rather than a parsed record, because it arrives
        // as a user message when the agent is idle and as a queued attachment
        // when it is mid-turn, and the tags are the same either way.
        if (line.Contains("<task-notification>", StringComparison.Ordinal))
        {
            var call = Tag(line, "tool-use-id")
                       ?? (Tag(line, "task-id") is { } task && cursor.TaskCalls.TryGetValue(task, out var mapped) ? mapped : null);
            if (call is not null)
            {
                cursor.Background.RemoveAll(b => b.ToolUseId == call);
            }

            return;
        }

        if (!interesting)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            switch (type)
            {
                // Two kinds of title, and a session has one or the other rather
                // than both: Claude stops generating one once a title is set. They
                // are read separately so a set title is never overwritten by a
                // generated one arriving later.
                case "ai-title":
                    if (Text(root, "aiTitle") is { } aiTitle)
                    {
                        cursor.AiTitle = aiTitle;
                    }

                    break;

                case "custom-title":
                    if (Text(root, "customTitle") is { } customTitle)
                    {
                        cursor.CustomTitle = customTitle;
                    }

                    break;

                case "last-prompt":
                    cursor.LastPrompt = ExtractPrompt(root);
                    break;

                case "user":
                    BackgroundResults(root, cursor);
                    break;

                case "assistant":
                    CollectSkills(root, cursor);
                    CollectBackground(root, cursor);
                    if (AssistantText(root) is { } reply)
                    {
                        cursor.LastReply = reply;
                    }

                    break;
            }
        }
        catch (JsonException)
        {
            // A line we cannot parse tells us nothing and is not worth reporting.
        }
    }

    private static string? ExtractPrompt(JsonElement root) =>
        Text(root, "lastPrompt") ?? Text(root, "prompt") ?? Text(root, "text");

    /// <summary>A string property, or null when it is absent or not a string.</summary>
    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>
    /// What the agent last said, in words. Tool calls and thinking are skipped:
    /// the point is to see the reply, not the working.
    /// </summary>
    private static string? AssistantText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return Trimmed(content.GetString());
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var type)
                && type.GetString() == "text"
                && block.TryGetProperty("text", out var text)
                && Trimmed(text.GetString()) is { } value)
            {
                parts.Add(value);
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static string? Trimmed(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// Skill invocations show up as Skill tool calls in the assistant's content.
    /// Recorded in first-use order because that is the order they mattered in.
    /// </summary>
    private static void CollectSkills(JsonElement root, Cursor cursor)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !block.TryGetProperty("type", out var bt)
                || bt.GetString() != "tool_use"
                || !block.TryGetProperty("name", out var name)
                || name.GetString() != "Skill"
                || !block.TryGetProperty("input", out var input)
                || !input.TryGetProperty("skill", out var skill)
                || skill.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = skill.GetString();
            if (!string.IsNullOrWhiteSpace(value) && cursor.SkillSet.Add(value))
            {
                cursor.Skills.Add(value);
            }
        }
    }

    /// <summary>
    /// Bash calls made with <c>run_in_background</c>. They return at once, and
    /// the command goes on running until a task notification says it finished.
    /// A subagent's calls are its own business and are left out.
    /// </summary>
    private static void CollectBackground(JsonElement root, Cursor cursor)
    {
        if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True
            || !root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var at = root.TryGetProperty("timestamp", out var ts)
                 && ts.ValueKind == JsonValueKind.String
                 && DateTimeOffset.TryParse(ts.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || Text(block, "type") != "tool_use"
                || Text(block, "name") != "Bash"
                || Text(block, "id") is not { } id
                || !block.TryGetProperty("input", out var input)
                || !input.TryGetProperty("run_in_background", out var bg)
                || bg.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            var what = Text(input, "description") ?? Text(input, "command") ?? "Background command";
            cursor.Background.Add(new BackgroundCommand(id, AgentSession.Shorten(what, 80), at));
        }
    }

    /// <summary>
    /// The result of a background Bash call: it names the task id a later notice
    /// may use, or says the command never started.
    /// </summary>
    private static void BackgroundResults(JsonElement root, Cursor cursor)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || Text(block, "type") != "tool_result"
                || Text(block, "tool_use_id") is not { } id
                || !cursor.Background.Exists(b => b.ToolUseId == id))
            {
                continue;
            }

            if (block.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True)
            {
                cursor.Background.RemoveAll(b => b.ToolUseId == id);
            }
            else if (root.TryGetProperty("toolUseResult", out var result)
                     && result.ValueKind == JsonValueKind.Object
                     && Text(result, "backgroundTaskId") is { } task)
            {
                cursor.TaskCalls[task] = id;
            }
        }
    }

    private static string? Tag(string text, string name)
    {
        var match = Regex.Match(text, $"<{name}>(.*?)</{name}>");
        return match.Success && match.Groups[1].Value.Trim() is { Length: > 0 } value ? value : null;
    }

    /// <summary>Move back to the start of the line containing <paramref name="from"/>.</summary>
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
