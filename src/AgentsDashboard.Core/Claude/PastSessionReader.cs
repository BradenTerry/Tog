using System.Text;
using System.Text.Json;

namespace AgentsDashboard.Core.Claude;

/// <summary>A conversation that once ran in a folder, which can be resumed.</summary>
/// <param name="Title">The name you gave it, or the one Claude generated, as the CLI's /resume shows.</param>
/// <param name="LastPrompt">The last thing you asked it, as Claude recorded it.</param>
/// <param name="UpdatedAt">When its transcript was last written.</param>
public sealed record PastSession(string SessionId, string? Title, string? LastPrompt, DateTimeOffset UpdatedAt);

/// <summary>
/// Lists the conversations Claude has kept for a folder, newest first.
/// </summary>
/// <remarks>
/// Claude keeps one transcript per session under the projects directory, in a
/// folder named after the working directory. The title and the last prompt are
/// records Claude appends as a conversation goes on, so the latest of each is
/// near the end: only the tail of each transcript is read, and the whole file
/// only when the tail has neither, which keeps a folder with long transcripts
/// cheap to list. A transcript with neither is a session that never got going,
/// such as one opened and cleared, and is left out.
/// </remarks>
public sealed class PastSessionReader(ClaudePaths paths)
{
    private const int TailBytes = 256 * 1024;

    public IReadOnlyList<PastSession> For(string cwd, int max = 50)
    {
        var dir = Path.Combine(paths.ProjectsDir, ClaudePaths.ProjectSlug(cwd));
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var sessions = new List<PastSession>();
        var files = new DirectoryInfo(dir)
            .EnumerateFiles("*.jsonl")
            .OrderByDescending(f => f.LastWriteTimeUtc);

        foreach (var file in files)
        {
            if (sessions.Count >= max)
            {
                break;
            }

            try
            {
                if (Read(file) is { } session)
                {
                    sessions.Add(session);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Being written as we read, or gone. The list is a convenience.
            }
        }

        return sessions;
    }

    private static PastSession? Read(FileInfo file)
    {
        var (title, prompt) = Scan(Tail(file));
        if (title is null && prompt is null && file.Length > TailBytes)
        {
            (title, prompt) = Scan(File.ReadAllText(file.FullName));
        }

        return title is null && prompt is null
            ? null
            : new PastSession(
                Path.GetFileNameWithoutExtension(file.Name),
                title,
                prompt,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static string Tail(FileInfo file)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The latest title and prompt in some transcript text. A name you gave the
    /// session wins over the generated one wherever each appears. The first line
    /// of a tail is usually cut in half, and fails to parse like any other broken
    /// line: skipped.
    /// </summary>
    private static (string? Title, string? Prompt) Scan(string text)
    {
        string? custom = null, generated = null, prompt = null;

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || !line.Contains("\"type\"", StringComparison.Ordinal))
            {
                continue;
            }

            // Most lines are messages, and parsing each of them to find the few
            // records that matter here would be most of the cost.
            if (!line.Contains("custom-title", StringComparison.Ordinal)
                && !line.Contains("ai-title", StringComparison.Ordinal)
                && !line.Contains("last-prompt", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                switch (Text(root, "type"))
                {
                    case "custom-title":
                        custom = Text(root, "customTitle") ?? custom;
                        break;
                    case "ai-title":
                        generated = Text(root, "aiTitle") ?? generated;
                        break;
                    case "last-prompt":
                        prompt = Text(root, "lastPrompt") ?? prompt;
                        break;
                }
            }
            catch (JsonException)
            {
            }
        }

        return (custom ?? generated, prompt);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
