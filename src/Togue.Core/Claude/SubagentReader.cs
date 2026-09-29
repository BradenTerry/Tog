using System.Text.Json;
using Togue.Core.Model;

namespace Togue.Core.Claude;

/// <summary>
/// Subagents in flight under a session.
/// </summary>
/// <remarks>
/// Subagents run inside the parent Claude process, so they have no registry
/// entry of their own. Claude writes one small file per subagent beside the
/// transcript. It used to remove it when the subagent finished; it now keeps it,
/// since a finished subagent can be resumed, so the files are every subagent that
/// ever ran. Which are still running is the parent transcript's to say (see
/// <see cref="TranscriptReader"/>), set against each subagent's own transcript
/// being written since, which is what a resumed one does.
/// </remarks>
public sealed class SubagentReader(TranscriptLocator locator)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Subagents running under this session, oldest first.</summary>
    public IReadOnlyList<Subagent> Read(string sessionId, string cwd)
    {
        var dir = locator.SubagentsDir(sessionId, cwd);
        if (dir is null)
        {
            return [];
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(dir, "agent-*.meta.json");
        }
        catch (Exception e) when (e is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return [];
        }

        var result = new List<Subagent>(files.Length);
        foreach (var file in files)
        {
            var parsed = Parse(file);
            if (parsed is not null)
            {
                result.Add(parsed);
            }
        }

        result.Sort((a, b) => a.StartedAt.CompareTo(b.StartedAt));
        return result;
    }

    private static Subagent? Parse(string file)
    {
        try
        {
            using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var raw = JsonSerializer.Deserialize<RawSubagent>(stream, JsonOptions);
            if (raw is null)
            {
                return null;
            }

            // The id is in the file name: agent-<id>.meta.json.
            var name = Path.GetFileName(file);
            var id = name["agent-".Length..^".meta.json".Length];

            return new Subagent(
                id,
                raw.AgentType,
                raw.Description,
                raw.SpawnDepth,
                new DateTimeOffset(File.GetCreationTimeUtc(file)).ToLocalTime(),
                raw.ToolUseId,
                LastWrite(Path.Combine(Path.GetDirectoryName(file)!, $"agent-{id}.jsonl")));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
                                      or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTimeOffset? LastWrite(string path) =>
        File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)) : null;

    private sealed record RawSubagent
    {
        public string? ToolUseId { get; init; }
        public string? AgentType { get; init; }
        public string? Description { get; init; }
        public int SpawnDepth { get; init; }
    }
}
