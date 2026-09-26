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
    public StartedTestRun Start(string worktreePath, bool mtp, string? filter = null)
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
            psi.ArgumentList.Add("--logger");
            psi.ArgumentList.Add("trx");
            if (!string.IsNullOrWhiteSpace(filter))
            {
                psi.ArgumentList.Add("--filter");
                psi.ArgumentList.Add(filter);
            }
        }

        var command = "dotnet " + string.Join(' ', psi.ArgumentList);

        try
        {
            var process = Process.Start(psi);
            if (process is null)
            {
                return new StartedTestRun(0, command);
            }

            // Drain the pipes so a chatty build cannot fill them and block the run,
            // and let the process outlive this handle.
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            var pid = process.Id;
            process.Dispose();
            return new StartedTestRun(pid, command);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new StartedTestRun(0, command);
        }
    }
}
