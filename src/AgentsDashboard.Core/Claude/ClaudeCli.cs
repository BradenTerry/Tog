using System.Diagnostics;
using System.Text;

namespace AgentsDashboard.Core.Claude;

/// <summary>What a CLI call did.</summary>
/// <param name="Ok">Whether the command succeeded.</param>
/// <param name="Message">The CLI's own words, for showing rather than paraphrasing.</param>
public sealed record CliResult(bool Ok, string Message)
{
    public static CliResult Failed(string message) => new(false, message);
}

/// <summary>
/// Runs the Claude CLI.
/// </summary>
/// <remarks>
/// <para>
/// This is how the dashboard starts, stops and talks to agents. Everything here
/// goes through documented commands: there is a messaging socket in a session's
/// registry entry, but it is authenticated with a token whose handshake is not
/// documented and does not answer without it, so nothing is built on it.
/// </para>
/// <para>
/// The consequence is the one limit worth knowing: a message can only be
/// delivered to a session that is not currently running, because
/// <c>--resume</c> starts a copy of a live one rather than continuing it. Sending
/// therefore stops the agent first, which is free when it is idle and an
/// interruption when it is not.
/// </para>
/// </remarks>
public sealed class ClaudeCli(TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(45);

    /// <summary>Whether the CLI is on PATH at all, so the UI can say so once.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        (await RunAsync(null, ["--version"], ct).ConfigureAwait(false)).Ok;

    /// <summary>Starts a background agent in a worktree.</summary>
    public Task<(CliResult Result, string? Id)> StartAsync(
        string worktreePath,
        string? prompt,
        CancellationToken ct = default) =>
        StartAsync(worktreePath, new StartOptions(prompt), ct);

    /// <summary>Starts a background agent in a worktree with the given options.</summary>
    public async Task<(CliResult Result, string? Id)> StartAsync(
        string worktreePath,
        StartOptions options,
        CancellationToken ct = default)
    {
        var result = await RunAsync(worktreePath, ClaudeCommands.Start(options), ct).ConfigureAwait(false);
        return (result, result.Ok ? ClaudeCommands.ParseStartedId(result.Message) : null);
    }

    public Task<CliResult> StopAsync(string id, CancellationToken ct = default) =>
        RunAsync(null, ClaudeCommands.Stop(id), ct);

    public Task<CliResult> RemoveAsync(string id, CancellationToken ct = default) =>
        RunAsync(null, ClaudeCommands.Remove(id), ct);

    /// <summary>
    /// A background session's recent terminal output.
    /// </summary>
    /// <remarks>
    /// A session with nothing to show is not a failure, so an empty log reads as
    /// success with an empty message rather than as an error.
    /// </remarks>
    public Task<CliResult> LogsAsync(string id, CancellationToken ct = default) =>
        RunAsync(null, ClaudeCommands.Logs(id), ct);

    /// <summary>Restarts a background session on the current CLI binary.</summary>
    public Task<CliResult> RespawnAsync(string id, CancellationToken ct = default) =>
        RunAsync(null, ClaudeCommands.Respawn(id), ct);

    /// <summary>Stops several agents, carrying on past the ones that refuse.</summary>
    /// <remarks>
    /// In sequence rather than at once: these are calls into the same CLI over the
    /// same registry, and one failure should not take the rest of the selection
    /// with it.
    /// </remarks>
    public Task<CliResult> StopManyAsync(IReadOnlyList<string> ids, CancellationToken ct = default) =>
        EachAsync(ids, StopAsync, "Stopped", "stop", ct);

    /// <summary>Removes several agents, carrying on past the ones that refuse.</summary>
    public Task<CliResult> RemoveManyAsync(IReadOnlyList<string> ids, CancellationToken ct = default) =>
        EachAsync(ids, RemoveAsync, "Removed", "remove", ct);

    private static async Task<CliResult> EachAsync(
        IReadOnlyList<string> ids,
        Func<string, CancellationToken, Task<CliResult>> run,
        string done,
        string verb,
        CancellationToken ct)
    {
        var results = new List<(string Id, CliResult Result)>(ids.Count);
        foreach (var id in ids)
        {
            results.Add((id, await run(id, ct).ConfigureAwait(false)));
        }

        return BulkOutcome.Summarise(done, verb, results);
    }

    /// <summary>
    /// Delivers a message to a background agent, stopping it first if need be.
    /// </summary>
    /// <remarks>
    /// The stop is not optional: <c>--resume</c> on a running session starts a
    /// copy under a new id, which looks like it worked and is not what anyone
    /// wants. Stopping keeps the conversation, so resuming continues the same one.
    /// </remarks>
    public async Task<CliResult> SendAsync(
        string id,
        string sessionId,
        string worktreePath,
        string message,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return CliResult.Failed("Nothing to send.");
        }

        var stop = await StopAsync(id, ct).ConfigureAwait(false);
        if (!stop.Ok && !stop.Message.Contains("not running", StringComparison.OrdinalIgnoreCase))
        {
            return CliResult.Failed($"Could not stop the agent to hand it the message: {stop.Message}");
        }

        // `stop` returns before the process has finished going away, and resuming
        // a session the CLI still considers live starts a copy of the conversation
        // under a new id instead of continuing it. So wait for it to actually be
        // gone rather than for the command to return.
        if (!await WaitUntilStoppedAsync(sessionId, ct).ConfigureAwait(false))
        {
            return CliResult.Failed(
                "The agent did not stop, so the message would have started a copy of its "
                + "conversation. Nothing was sent.");
        }

        var resume = await RunAsync(
            worktreePath,
            ClaudeCommands.Send(sessionId, message),
            ct).ConfigureAwait(false);

        if (!resume.Ok)
        {
            return resume;
        }

        // The CLI says so itself when it could not continue the original.
        return resume.Message.Contains("started a copy", StringComparison.OrdinalIgnoreCase)
            ? CliResult.Failed(
                "The agent was still running, so the message would have started a copy of its "
                + "conversation. Nothing was sent.")
            : new CliResult(true, "Sent.");
    }

    /// <summary>
    /// Waits for a session to leave the CLI's list of running agents.
    /// </summary>
    /// <remarks>
    /// Presence in the running-only listing is the test, and it has to be: a
    /// stopped session keeps its entry in the <c>--all</c> listing, and a running
    /// session that has not transitioned yet reports no status at all. Judging
    /// liveness by either of those reads a live agent as stopped, and the resume
    /// that follows clones the conversation instead of continuing it.
    /// </remarks>
    private async Task<bool> WaitUntilStoppedAsync(string sessionId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var running = await ListAsync(includeStopped: false, ct).ConfigureAwait(false);
            if (!running.Any(a => string.Equals(a.SessionId, sessionId, StringComparison.Ordinal)))
            {
                return true;
            }

            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Background agents, with or without the ones that have stopped.</summary>
    public async Task<IReadOnlyList<BackgroundAgent>> ListAsync(
        bool includeStopped = true,
        CancellationToken ct = default)
    {
        var result = await RunAsync(null, ClaudeCommands.List(includeStopped), ct).ConfigureAwait(false);
        return result.Ok ? ClaudeCommands.ParseList(result.Message) : [];
    }

    private async Task<CliResult> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("claude")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (workingDirectory is not null && Directory.Exists(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
            {
                return CliResult.Failed("The Claude CLI could not be started.");
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return CliResult.Failed(
                $"The Claude CLI could not be started. Is `claude` on your PATH? ({e.Message})");
        }

        // The CLI reads stdin when it has a terminal; closing it keeps a call that
        // expects input from hanging until the timeout.
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return CliResult.Failed($"The Claude CLI did not answer within {_timeout.TotalSeconds:0}s.");
        }

        var output = (await stdout.ConfigureAwait(false)).Trim();
        var error = (await stderr.ConfigureAwait(false)).Trim();

        // Both streams, always. The CLI reports what it did on stdout but puts its
        // notes on stderr, and one of those notes is "this started a copy of that
        // conversation" -- exactly the outcome the caller has to notice.
        var message = string.Join(
            '\n',
            new[] { output, error }.Where(part => part.Length > 0));

        return process.ExitCode == 0 ? new CliResult(true, message) : CliResult.Failed(message);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
