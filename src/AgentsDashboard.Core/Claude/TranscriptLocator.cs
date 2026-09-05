namespace AgentsDashboard.Core.Claude;

/// <summary>
/// Finds a session's transcript. Claude files transcripts under a directory per
/// project, named by a slug it derives from the working directory.
/// </summary>
/// <remarks>
/// The slug is Claude's private convention, so it is used as a fast path only.
/// When it misses, the projects directory is scanned. A transcript we cannot
/// find costs a work summary, never an agent: the caller falls back to the
/// session's own name.
/// </remarks>
public sealed class TranscriptLocator(ClaudePaths paths)
{
    private readonly Dictionary<string, string> _found = new(StringComparer.Ordinal);

    /// <summary>Path of <c>&lt;sessionId&gt;.jsonl</c>, or null when there is none.</summary>
    public string? Locate(string sessionId, string cwd)
    {
        if (_found.TryGetValue(sessionId, out var cached) && File.Exists(cached))
        {
            return cached;
        }

        var direct = Path.Combine(paths.ProjectsDir, ClaudePaths.ProjectSlug(cwd), sessionId + ".jsonl");
        if (File.Exists(direct))
        {
            _found[sessionId] = direct;
            return direct;
        }

        var scanned = Scan(sessionId);
        if (scanned is not null)
        {
            _found[sessionId] = scanned;
        }

        return scanned;
    }

    /// <summary>
    /// The directory Claude keeps a session's subagent files in, which sits
    /// beside the transcript and shares its name minus the extension.
    /// </summary>
    public string? SubagentsDir(string sessionId, string cwd)
    {
        var transcript = Locate(sessionId, cwd);
        if (transcript is null)
        {
            return null;
        }

        var dir = Path.Combine(
            Path.GetDirectoryName(transcript)!,
            Path.GetFileNameWithoutExtension(transcript),
            "subagents");

        return Directory.Exists(dir) ? dir : null;
    }

    private string? Scan(string sessionId)
    {
        try
        {
            foreach (var project in Directory.EnumerateDirectories(paths.ProjectsDir))
            {
                var candidate = Path.Combine(project, sessionId + ".jsonl");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception e) when (e is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            // No projects directory yet, or we cannot read it. Either way there is
            // no transcript to report.
        }

        return null;
    }

    /// <summary>Forget a session that has ended, so the map cannot grow without bound.</summary>
    public void Forget(IReadOnlySet<string> liveSessionIds)
    {
        foreach (var gone in _found.Keys.Where(k => !liveSessionIds.Contains(k)).ToList())
        {
            _found.Remove(gone);
        }
    }
}
