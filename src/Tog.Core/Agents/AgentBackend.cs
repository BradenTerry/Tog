using System.Diagnostics;
using System.Text.Json;
using Tog.Core.Model;
using System.Text;

namespace Tog.Core.Agents;

/// <summary>
/// An agent Tog can run: a program that speaks the Agent Client
/// Protocol on its standard input and output, and what the app needs to know
/// about it beyond the protocol.
/// </summary>
/// <remarks>
/// Every agent comes from an adapter: an extension that adds one, or a command
/// typed in Settings. The app has none of its own, Claude included, so what
/// one agent needs never has to wait for an app release.
/// </remarks>
/// <param name="Id">A stable key, such as "claude", saved with every agent started on it.</param>
/// <param name="Name">What the UI calls it.</param>
/// <param name="Command">The program to run.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Problem">Set when the agent cannot run here, with the reason, so the UI can say so instead of failing on first use.</param>
public sealed record AgentBackend(
    string Id,
    string Name,
    string Command,
    IReadOnlyList<string> Arguments,
    string? Problem = null)
{
    /// <summary>
    /// The <c>_meta</c> that carries a session's environment to its process, or
    /// null when the agent cannot take one. Such an agent is given no MCP
    /// servers: their key only reaches a session this way.
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>, object?>? SessionMeta { get; init; }

    /// <summary>The agent's own transcripts, or null for the app's recording of what it streamed.</summary>
    public IAgentTranscripts? Transcripts { get; init; }

    /// <summary>Usage limits from a <c>usage_update</c>'s <c>_meta</c>, when the agent reports them.</summary>
    public Func<JsonElement, DateTimeOffset, IReadOnlyList<PlanLimit>>? ReadUsage { get; init; }

    /// <summary>What the start form offers before the agent has reported its own lists.</summary>
    public StartDefaults Defaults { get; init; } = StartDefaults.None;
}

/// <summary>What the start form offers for one agent before it has reported its own lists.</summary>
public sealed record StartDefaults(
    IReadOnlyList<StartChoice> Models,
    IReadOnlyList<StartChoice> Efforts,
    IReadOnlyList<StartChoice> Modes)
{
    public static StartDefaults None { get; } = new([], [], []);
}

/// <summary>The agents this app can run right now.</summary>
/// <remarks>
/// Adapters come and go as extensions load, reload and are turned off, so the
/// list is asked for each time rather than kept. An agent whose adapter has gone
/// stays on the agent list and says so when it is sent a message.
/// </remarks>
public interface IAgentBackends
{
    /// <summary>Every agent that can be started, in the order New agent lists them.</summary>
    IReadOnlyList<AgentBackend> All { get; }

    /// <summary>Raised when an adapter is added, changed or removed.</summary>
    event Action? Changed;
}

/// <summary>A fixed list of agents, for tests and for a host with one.</summary>
public sealed class FixedAgentBackends(params AgentBackend[] backends) : IAgentBackends
{
    public IReadOnlyList<AgentBackend> All { get; } = backends;

    public event Action? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>
/// An agent's own record of its conversations, read for the chat, the agent
/// list and the start form's list of conversations to resume.
/// </summary>
public interface IAgentTranscripts
{
    /// <summary>The conversation so far, oldest first, or null when there is no record of it.</summary>
    IReadOnlyList<ChatEntry>? Conversation(string sessionId, string cwd);

    /// <summary>A subagent's own conversation, or null when there is none.</summary>
    IReadOnlyList<ChatEntry>? SubagentConversation(string sessionId, string cwd, string subagentId);

    /// <summary>What the session is doing, or null when there is no record of it.</summary>
    SessionActivity? Activity(string sessionId, string cwd);

    /// <summary>Conversations that once ran in a folder, newest first.</summary>
    IReadOnlyList<PastSession> PastSessions(string cwd);

    /// <summary>Drops what is kept for sessions no longer running.</summary>
    void Forget(IReadOnlySet<string> liveSessionIds);
}

/// <summary>A running agent process: its protocol streams, and a way to end it.</summary>
public interface IAgentProcess : IAsyncDisposable
{
    Stream Input { get; }

    Stream Output { get; }

    int ProcessId { get; }

    /// <summary>The last lines the agent wrote to its error stream, for when it dies.</summary>
    string RecentErrors { get; }
}

/// <summary>Starts agent processes. A seam so the host can be tested against an in-memory agent.</summary>
public interface IAgentLauncher
{
    IAgentProcess Launch(AgentBackend backend);
}

/// <inheritdoc />
public sealed class ProcessAgentLauncher : IAgentLauncher
{
    public IAgentProcess Launch(AgentBackend backend)
    {
        if (backend.Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var psi = new ProcessStartInfo(backend.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (var argument in backend.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException($"{backend.Name} could not be started ({backend.Command}): {e.Message}", e);
        }

        return new RunningAgent(process);
    }

    private sealed class RunningAgent : IAgentProcess
    {
        private const int KeptLines = 60;

        private readonly Process _process;
        private readonly Queue<string> _errors = new();
        private readonly Lock _gate = new();

        public RunningAgent(Process process)
        {
            _process = process;

            // The bridge logs every phase to its error stream. Left unread, the
            // pipe fills and the agent blocks on its next log line.
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (_gate)
                {
                    _errors.Enqueue(e.Data);
                    while (_errors.Count > KeptLines)
                    {
                        _errors.Dequeue();
                    }
                }
            };
            process.BeginErrorReadLine();
        }

        public Stream Input => _process.StandardOutput.BaseStream;

        public Stream Output => _process.StandardInput.BaseStream;

        public int ProcessId => _process.Id;

        public string RecentErrors
        {
            get
            {
                lock (_gate)
                {
                    return string.Join('\n', _errors);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.HasExited)
                {
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
            }

            _process.Dispose();
        }
    }
}
