using System.Diagnostics;
using System.Text;

namespace AgentsDashboard.Core.Agents;

/// <summary>
/// An agent the dashboard can run: a program that speaks the Agent Client
/// Protocol on its standard input and output.
/// </summary>
/// <remarks>
/// This is the extension point for other agents. Claude is one definition,
/// launching the official ACP bridge; another agent is another definition with
/// its own command, and everything above the protocol stays the same.
/// </remarks>
/// <param name="Id">A stable key, such as "claude".</param>
/// <param name="Name">What the UI calls it.</param>
/// <param name="Command">The program to run.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Problem">Set when the agent cannot run here, with the reason, so the UI can say so instead of failing on first use.</param>
public sealed record AgentBackend(
    string Id,
    string Name,
    string Command,
    IReadOnlyList<string> Arguments,
    string? Problem = null);

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
public sealed class ProcessAgentLauncher(IAgentMcpServers? mcpServers = null) : IAgentLauncher
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

        foreach (var (name, value) in mcpServers?.Environment ?? new Dictionary<string, string>())
        {
            psi.Environment[name] = value;
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
