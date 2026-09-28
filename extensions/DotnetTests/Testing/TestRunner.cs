using System.Diagnostics;

namespace AgentsDashboard.Extensions.DotnetTests;

/// <summary>A test run the dashboard started itself.</summary>
public sealed record StartedTestRun(int Pid, string Command)
{
    public bool Started => Pid > 0;
}

/// <summary>
/// Starts <c>dotnet test</c> in a worktree.
/// </summary>
/// <remarks>
/// The dashboard does not read this process's output. It writes a TRX like any
/// other run, and the tracker is already watching for that, so a run started
/// here and one started by an agent are followed the same way and shown the
/// same way. The process is started detached, so closing the dashboard does not
/// kill a run in progress.
/// </remarks>
public sealed class TestRunner
{
    /// <summary>
    /// Starts a run and returns immediately.
    /// </summary>
    /// <param name="worktreePath">Directory to run in.</param>
    /// <param name="mtp">
    /// Whether the projects run on Microsoft.Testing.Platform, which decides how
    /// the TRX is asked for.
    /// </param>
    /// <param name="filter">An optional test name filter.</param>
    /// <param name="projectPath">One project to run, rather than whatever the worktree root builds.</param>
    public StartedTestRun Start(string worktreePath, bool mtp, string? filter = null, string? projectPath = null)
    {
        var (started, process) = Launch(worktreePath, mtp, filter, projectPath);
        process?.Dispose();
        return started;
    }

    /// <summary>
    /// Starts several runs one after another, and returns once the first has
    /// started.
    /// </summary>
    /// <remarks>
    /// One at a time rather than all at once, because test projects in a repo
    /// usually share the projects they test, and parallel builds of the same
    /// project fight over its <c>obj</c> directory. A run that fails to start
    /// does not stop the ones after it.
    /// </remarks>
    public StartedTestRun StartSequence(string worktreePath, bool mtp, IReadOnlyList<(string? ProjectPath, string? Filter)> runs)
    {
        if (runs.Count == 0)
        {
            return new StartedTestRun(0, "");
        }

        var (first, process) = Launch(worktreePath, mtp, runs[0].Filter, runs[0].ProjectPath);
        if (runs.Count == 1)
        {
            process?.Dispose();
            return first;
        }

        _ = Task.Run(async () =>
        {
            var current = process;
            foreach (var (projectPath, filter) in runs.Skip(1))
            {
                if (current is not null)
                {
                    await current.WaitForExitAsync().ConfigureAwait(false);
                    current.Dispose();
                }

                current = Launch(worktreePath, mtp, filter, projectPath).Process;
            }

            current?.Dispose();
        });

        return first with { Command = $"{first.Command} (then {runs.Count - 1} more)" };
    }

    private static (StartedTestRun Started, Process? Process) Launch(
        string worktreePath, bool mtp, string? filter, string? projectPath)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = worktreePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        psi.ArgumentList.Add("test");

        if (mtp)
        {
            if (projectPath is { Length: > 0 })
            {
                psi.ArgumentList.Add("--project");
                psi.ArgumentList.Add(projectPath);
            }

            // Note: --nologo is deliberately not passed. In Microsoft.Testing.Platform
            // mode `dotnet test` forwards it to the test application, which rejects
            // it and reports zero tests.
            psi.ArgumentList.Add("--report-trx");
            if (!string.IsNullOrWhiteSpace(filter))
            {
                psi.ArgumentList.Add("--filter");
                psi.ArgumentList.Add(filter);
            }
        }
        else
        {
            if (projectPath is { Length: > 0 })
            {
                psi.ArgumentList.Add(projectPath);
            }

            psi.ArgumentList.Add("--logger");
            psi.ArgumentList.Add("trx");
            if (!string.IsNullOrWhiteSpace(filter))
            {
                psi.ArgumentList.Add("--filter");
                psi.ArgumentList.Add(filter);
            }
        }

        var command = "dotnet " + string.Join(' ', psi.ArgumentList.Select(Quote));

        try
        {
            var process = Process.Start(psi);
            if (process is null)
            {
                return (new StartedTestRun(0, command), null);
            }

            // Drain the pipes so a chatty build cannot fill them and block the run,
            // and let the process outlive this handle.
            // The caller disposes the handle; disposing it does not end the run.
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            return (new StartedTestRun(process.Id, command), process);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (new StartedTestRun(0, command), null);
        }
    }

    /// <summary>Only for showing the command: a filter with spaces or operators reads wrong unquoted.</summary>
    private static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '/' or '_')
            ? arg
            : "\"" + arg.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
