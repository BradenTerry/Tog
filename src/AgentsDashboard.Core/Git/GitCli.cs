using System.Diagnostics;
using System.Text;

namespace AgentsDashboard.Core.Git;

/// <summary>The outcome of one git invocation.</summary>
/// <param name="ExitCode">Git's exit code, or -1 when it could not be started.</param>
/// <param name="StdOut">Standard output, trailing newline trimmed.</param>
/// <param name="StdErr">Standard error, trailing newline trimmed.</param>
public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Git's own message, for surfacing a failure without inventing one.</summary>
    public string Message => StdErr.Length > 0 ? StdErr : StdOut;
}

/// <summary>Runs git. The only place in the app that starts a process for git.</summary>
public interface IGitCli
{
    Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct = default);
}

/// <inheritdoc />
/// <remarks>
/// Arguments are passed as a list rather than a string so nothing has to be
/// quoted or escaped: a branch or path with a space in it is one element and
/// stays one element.
/// </remarks>
public sealed class GitCli(TimeSpan? timeout = null) : IGitCli
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // Never let git stop for credentials or an editor: this runs unattended
        // behind a UI, and a prompt would hang the poll rather than fail it.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
            {
                return new GitResult(-1, "", "git could not be started");
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // IOException covers a working folder that has been deleted, the
            // app's or this call's: .NET reads it while resolving "git" and
            // throws FileNotFoundException, which would otherwise reach
            // whichever component asked and take its window down.
            return new GitResult(-1, "", $"git could not be started: {e.Message}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new GitResult(-1, "", $"git timed out after {_timeout.TotalSeconds:0}s");
        }

        return new GitResult(
            process.ExitCode,
            (await stdout.ConfigureAwait(false)).TrimEnd('\n', '\r'),
            (await stderr.ConfigureAwait(false)).TrimEnd('\n', '\r'));
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
