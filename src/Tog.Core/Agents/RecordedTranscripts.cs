using System.Text;
using System.Text.Json;
using Tog.Core.Model;
using Tog.Core.Repos;

namespace Tog.Core.Agents;

/// <summary>
/// The history of agents whose adapter has none of its own: what they streamed
/// over ACP, kept by the app.
/// </summary>
/// <remarks>
/// <para>
/// ACP streams a turn and forgets it, so an agent with no adapter for its
/// transcripts would show an empty chat after every restart. The host tells
/// this what you sent, what the agent said and which tools it called, and it
/// appends that to <c>history/&lt;session&gt;.jsonl</c> in the data folder, one
/// event per line. It is read back the way a transcript is: a cursor per
/// session, only what was appended since.
/// </para>
/// <para>
/// The agent's words are written when it moves on to a tool call or ends the
/// turn, as a transcript records them, so the chat's live text hands over to
/// the recorded message at the same point for every agent.
/// </para>
/// <para>
/// It only has what ran inside Tog, and a tool call only as its kind and
/// title: no diffs, no subagents. An adapter that can read the agent's own
/// transcripts does better, which is what <see cref="AgentBackend.Transcripts"/> is for.
/// </para>
/// </remarks>
public sealed class RecordedTranscripts(AppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, StringBuilder> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Cursor> _cursors = new(StringComparer.Ordinal);

    private string Dir => Path.Combine(paths.Root, "history");

    /// <summary>One line of a recording.</summary>
    /// <param name="T">start, you, agent, step or title.</param>
    private sealed record Event(string T, DateTimeOffset At, string? Text = null, string? Tool = null, string? Backend = null, string? Cwd = null);

    private sealed class Cursor
    {
        public long Offset;
        public readonly List<ChatEntry> Entries = [];
        public string? Title;
        public string? LastPrompt;
        public string? LastReply;
    }

    /// <summary>The recording's view for one agent.</summary>
    public IAgentTranscripts For(string backendId) => new View(this, backendId);

    /// <summary>Starts a session's recording, when it has none yet.</summary>
    public void Begin(string backendId, string sessionId, string cwd, DateTimeOffset at)
    {
        if (FileFor(sessionId) is { } file && !File.Exists(file))
        {
            Append(sessionId, new Event("start", at, Backend: backendId, Cwd: cwd));
        }
    }

    /// <summary>A message sent to the agent.</summary>
    public void You(string sessionId, string text, DateTimeOffset at) => Append(sessionId, new Event("you", at, text));

    /// <summary>A piece of the agent's reply, held until it moves on.</summary>
    public void Chunk(string sessionId, string text)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(sessionId, out var said))
            {
                _pending[sessionId] = said = new StringBuilder();
            }

            said.Append(text);
        }
    }

    /// <summary>A tool call: whatever the agent said before it is a finished message.</summary>
    public void Step(string sessionId, string? kind, string title, DateTimeOffset at)
    {
        Said(sessionId, at);
        Append(sessionId, new Event("step", at, title, kind ?? "other"));
    }

    /// <summary>The turn ended, so what the agent said last is finished.</summary>
    public void EndTurn(string sessionId, DateTimeOffset at) => Said(sessionId, at);

    /// <summary>The name the agent gave the conversation.</summary>
    public void Title(string sessionId, string title, DateTimeOffset at) => Append(sessionId, new Event("title", at, title));

    private void Said(string sessionId, DateTimeOffset at)
    {
        string? text;
        lock (_gate)
        {
            text = _pending.Remove(sessionId, out var said) ? said.ToString().Trim() : null;
        }

        if (!string.IsNullOrEmpty(text))
        {
            Append(sessionId, new Event("agent", at, text));
        }
    }

    private void Append(string sessionId, Event e)
    {
        if (FileFor(sessionId) is not { } file)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(file, JsonSerializer.Serialize(e, Json) + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The recording is the chat's history. Losing a line costs that line,
            // and failing the agent's turn over it would cost more.
        }
    }

    /// <summary>
    /// The session's file, or null for an id that is not safe as a file name.
    /// The id comes from the agent, and must not name a path of its choosing.
    /// </summary>
    private string? FileFor(string sessionId) =>
        sessionId.Length is > 0 and <= 200 && sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && sessionId.Trim('.').Length > 0
            ? Path.Combine(Dir, sessionId + ".jsonl")
            : null;

    private Cursor? Read(string sessionId)
    {
        if (FileFor(sessionId) is not { } file || !File.Exists(file))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_cursors.TryGetValue(sessionId, out var cursor))
            {
                _cursors[sessionId] = cursor = new Cursor();
            }

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < cursor.Offset)
                {
                    _cursors[sessionId] = cursor = new Cursor();
                }

                stream.Seek(cursor.Offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var consumed = cursor.Offset;
                while (reader.ReadLine() is { } line)
                {
                    var bytes = Encoding.UTF8.GetByteCount(line) + 1;
                    if (consumed + bytes > stream.Length)
                    {
                        break;
                    }

                    consumed += bytes;
                    Consume(line, cursor);
                }

                cursor.Offset = consumed;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // What was read still stands.
            }

            return cursor;
        }
    }

    private static void Consume(string line, Cursor cursor)
    {
        Event? e;
        try
        {
            e = JsonSerializer.Deserialize<Event>(line, Json);
        }
        catch (JsonException)
        {
            return;
        }

        var entries = cursor.Entries;
        switch (e)
        {
            case { T: "you", Text: { } text }:
                entries.Add(ChatEntry.Said(ChatKind.You, e.At, text));
                cursor.LastPrompt = text;
                break;

            case { T: "agent", Text: { } text }:
                entries.Add(ChatEntry.Said(ChatKind.Agent, e.At, text));
                cursor.LastReply = text;
                break;

            case { T: "step" }:
                var step = new ChatStep(e.Tool ?? "other", e.Text ?? "");
                if (entries.Count > 0 && entries[^1] is { Kind: ChatKind.Activity } run)
                {
                    entries[^1] = run with { Steps = [.. run.Steps, step] };
                }
                else
                {
                    entries.Add(new ChatEntry(ChatKind.Activity, e.At, "", [step]));
                }

                break;

            case { T: "title", Text: { } title }:
                cursor.Title = title;
                break;
        }

        if (entries.Count > 400)
        {
            entries.RemoveRange(0, entries.Count - 400);
        }
    }

    private IReadOnlyList<PastSession> Past(string backendId, string cwd)
    {
        if (!Directory.Exists(Dir))
        {
            return [];
        }

        var found = new List<PastSession>();
        foreach (var file in new DirectoryInfo(Dir).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            if (found.Count >= 50)
            {
                break;
            }

            try
            {
                using var reader = new StreamReader(new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                if (reader.ReadLine() is not { } first
                    || JsonSerializer.Deserialize<Event>(first, Json) is not { T: "start" } start
                    || start.Backend != backendId
                    || !string.Equals(start.Cwd, cwd, StringComparison.Ordinal))
                {
                    continue;
                }

                var session = Path.GetFileNameWithoutExtension(file.Name);
                var cursor = Read(session);
                if (cursor?.LastPrompt is null)
                {
                    continue;
                }

                found.Add(new PastSession(session, cursor.Title, cursor.LastPrompt, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Being written, or not one of ours. The list is a convenience.
            }
        }

        return found;
    }

    private sealed class View(RecordedTranscripts owner, string backendId) : IAgentTranscripts
    {
        public IReadOnlyList<ChatEntry>? Conversation(string sessionId, string cwd)
        {
            var cursor = owner.Read(sessionId);
            if (cursor is null)
            {
                return null;
            }

            lock (owner._gate)
            {
                return cursor.Entries.ToArray();
            }
        }

        public IReadOnlyList<ChatEntry>? SubagentConversation(string sessionId, string cwd, string subagentId) => null;

        public SessionActivity? Activity(string sessionId, string cwd)
        {
            var cursor = owner.Read(sessionId);
            if (cursor is null)
            {
                return null;
            }

            lock (owner._gate)
            {
                return new SessionActivity(cursor.Title, cursor.LastPrompt, cursor.LastReply, [], [], []);
            }
        }

        public IReadOnlyList<PastSession> PastSessions(string cwd) => owner.Past(backendId, cwd);

        public void Forget(IReadOnlySet<string> liveSessionIds)
        {
            lock (owner._gate)
            {
                foreach (var gone in owner._cursors.Keys.Where(k => !liveSessionIds.Contains(k)).ToList())
                {
                    owner._cursors.Remove(gone);
                }
            }
        }
    }
}
