using System.Text.Json;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Platform;

namespace AgentsDashboard.Core.Claude;

/// <summary>
/// Claude Code's own session registry: where every agent on the dashboard comes from.
/// </summary>
/// <remarks>
/// <para>
/// Claude writes one file per session at <c>&lt;config&gt;/sessions/&lt;pid&gt;.json</c>
/// and rewrites it on every status transition:
/// </para>
/// <code>
/// { "pid": 567, "sessionId": "...", "cwd": "/repo", "status": "busy",
///   "startedAt": 1785193745550, "kind": "interactive",
///   "messagingSocketPath": "/tmp/cc-socks/567.sock" }
/// </code>
/// <para>
/// This is the process describing itself, which beats inferring the same thing
/// from an event stream: a resumed session, a long shell command and a finished
/// turn are indistinguishable from the outside, and are not from the inside.
/// </para>
/// <para>
/// Two things it does not tell us. First, whether the process is still there:
/// a session killed with its terminal never gets to delete its own file, so
/// every pid is probed. Second, when the status last <em>changed</em>: the file's
/// mtime moves for other reasons too, so transitions are tracked here, which is
/// what makes a waiting agent's "blocked for" timer honest.
/// </para>
/// </remarks>
public sealed class SessionRegistryReader(
    ClaudePaths paths,
    IProcessProbe? probe = null,
    IClock? clock = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IProcessProbe _probe = probe ?? new ProcessProbe();
    private readonly IClock _clock = clock ?? new SystemClock();

    /// <summary>
    /// Parsed files, valid while their mtime is unchanged. A null entry records a
    /// file that parsed to nothing usable (garbage, a partial write, a
    /// non-interactive kind) so it is not re-parsed every poll; a completed write
    /// moves the mtime and retries it. Liveness is deliberately not cached: a
    /// process can die without its file changing.
    /// </summary>
    private readonly Dictionary<string, CacheEntry> _cache = [];

    /// <summary>
    /// When each session entered the status it is in now, keyed by session id.
    /// Swept as sessions end so it cannot grow without bound.
    /// </summary>
    private readonly Dictionary<string, (AgentStatus Status, DateTimeOffset Since)> _transitions = [];

    private sealed record CacheEntry(long MtimeTicks, RawSession? Session);

    /// <summary>Every session in the registry whose process is still alive.</summary>
    public IReadOnlyList<AgentSession> Read()
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(paths.SessionsDir, "*.json");
        }
        catch (DirectoryNotFoundException)
        {
            // Claude has never run, or the config dir points somewhere else.
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        var now = _clock.Now;
        var live = new List<AgentSession>(files.Length);
        var seenFiles = new HashSet<string>(files.Length, StringComparer.Ordinal);
        var seenSessions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            seenFiles.Add(file);

            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists)
                {
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }

            var mtime = info.LastWriteTimeUtc.Ticks;
            if (!_cache.TryGetValue(file, out var cached) || cached.MtimeTicks != mtime)
            {
                cached = new CacheEntry(mtime, ParseFile(file));
                _cache[file] = cached;
            }

            var raw = cached.Session;
            if (raw is null || !_probe.IsAlive(raw.Pid))
            {
                continue;
            }

            var status = MapStatus(raw.Status);
            var since = TrackTransition(raw.SessionId, status, now);
            seenSessions.Add(raw.SessionId);

            live.Add(new AgentSession
            {
                SessionId = raw.SessionId,
                Pid = raw.Pid,
                Cwd = raw.Cwd,
                Status = status,
                WaitingFor = raw.WaitingFor,
                Name = raw.Name,
                StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(raw.StartedAt).ToLocalTime(),
                LastActivity = new DateTimeOffset(info.LastWriteTimeUtc).ToLocalTime(),
                StatusSince = since,
                IsBackground = raw.Kind == "bg",
                JobId = raw.JobId,
            });
        }

        Sweep(seenFiles, seenSessions);
        return live;
    }

    /// <summary>
    /// Claude's raw statuses, mapped onto the three the dashboard shows.
    /// <c>shell</c> is a local command rather than an LLM turn, but it is work in
    /// progress as far as you are concerned, so it reads as active. Anything
    /// unrecognized, including a file with no status at all, reads as idle rather
    /// than being asserted to be something it is not.
    /// </summary>
    public static AgentStatus MapStatus(string? raw) => raw switch
    {
        "busy" or "shell" => AgentStatus.Active,
        "waiting" => AgentStatus.Waiting,
        _ => AgentStatus.Idle,
    };

    private DateTimeOffset TrackTransition(string sessionId, AgentStatus status, DateTimeOffset now)
    {
        if (_transitions.TryGetValue(sessionId, out var prev) && prev.Status == status)
        {
            return prev.Since;
        }

        _transitions[sessionId] = (status, now);
        return now;
    }

    private void Sweep(HashSet<string> seenFiles, HashSet<string> seenSessions)
    {
        foreach (var stale in _cache.Keys.Where(k => !seenFiles.Contains(k)).ToList())
        {
            _cache.Remove(stale);
        }

        foreach (var gone in _transitions.Keys.Where(k => !seenSessions.Contains(k)).ToList())
        {
            _transitions.Remove(gone);
        }
    }

    private static RawSession? ParseFile(string file)
    {
        try
        {
            using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var raw = JsonSerializer.Deserialize<RawSession>(stream, JsonOptions);
            if (raw is null
                || raw.Pid <= 0
                || string.IsNullOrWhiteSpace(raw.SessionId)
                || string.IsNullOrWhiteSpace(raw.Cwd))
            {
                return null;
            }

            // Two kinds are agents: a session someone is sitting in front of, and
            // one started in the background. The rest are print-mode and SDK runs,
            // which are real processes with nobody to answer and nothing to reveal.
            if (raw.Kind is not null && raw.Kind is not ("interactive" or "bg"))
            {
                return null;
            }

            return raw;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A half-written file. Its mtime moves when the write completes, and
            // the next read parses it then.
            return null;
        }
    }

    /// <summary>The registry file's shape, before anything in it is trusted.</summary>
    private sealed record RawSession
    {
        public int Pid { get; init; }
        public string SessionId { get; init; } = "";
        public string Cwd { get; init; } = "";
        public string? Status { get; init; }
        public string? WaitingFor { get; init; }
        public string? Name { get; init; }
        public long StartedAt { get; init; }
        public string? Kind { get; init; }
        public string? JobId { get; init; }
    }
}
