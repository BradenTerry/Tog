using System.Diagnostics;

namespace AgentsDashboard.Core.Testing;

/// <summary>Which worktrees have a test run in flight right now.</summary>
public interface ITestProcessScanner
{
    /// <summary>
    /// The subset of <paramref name="worktreePaths"/> that a test process is
    /// currently running in, with the pid that says so.
    /// </summary>
    IReadOnlyDictionary<string, int> Scan(IReadOnlyCollection<string> worktreePaths);
}

/// <summary>
/// Spots a test run from the process table.
/// </summary>
/// <remarks>
/// <para>
/// The report file cannot say a run has started, because for the first stretch
/// of a .NET test run there is no report: restore and build come first, and on a
/// real solution that is most of the wait. Watching for the process closes that
/// gap, so a run shows as "building" the moment the agent kicks it off instead
/// of appearing from nowhere half a minute later.
/// </para>
/// <para>
/// Matching is by command line rather than working directory: a process's cwd is
/// not portably readable, and the paths a .NET test run puts on its command line
/// (the project, the built test application, the results directory) all sit
/// inside the worktree anyway.
/// </para>
/// </remarks>
public sealed class TestProcessScanner : ITestProcessScanner
{
    private static readonly string[] Markers =
    [
        "dotnet test",
        "testhost",
        "vstest.console",
        "--report-trx",
        "--internal-msbuild-node",
        "--dotnet-test-pipe",
    ];

    public IReadOnlyDictionary<string, int> Scan(IReadOnlyCollection<string> worktreePaths)
    {
        if (worktreePaths.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        var lines = OperatingSystem.IsWindows() ? ScanWindows() : ScanUnix();
        var found = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (pid, command) in lines)
        {
            if (!Markers.Any(m => command.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var worktree in worktreePaths)
            {
                if (!found.ContainsKey(worktree)
                    && command.Contains(worktree, StringComparison.Ordinal))
                {
                    found[worktree] = pid;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// One <c>ps</c> for the whole table. Cheaper and more complete than opening
    /// every process by hand, and it is the only portable way to see a command
    /// line on macOS.
    /// </summary>
    private static List<(int Pid, string Command)> ScanUnix()
    {
        var result = new List<(int, string)>();

        string output;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ps")
            {
                ArgumentList = { "-axo", "pid=,args=" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return result;
            }

            output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                return result;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return result;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space > 0 && int.TryParse(line[..space], out var pid))
            {
                result.Add((pid, line[(space + 1)..]));
            }
        }

        return result;
    }

    /// <summary>
    /// Windows has no <c>ps</c>, and querying command lines needs WMI, which is
    /// slow enough to matter on a poll. The main module path is enough: an MTP
    /// test application is the worktree's own build output, so its path names the
    /// worktree.
    /// </summary>
    private static List<(int Pid, string Command)> ScanWindows()
    {
        var result = new List<(int, string)>();

        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (InvalidOperationException)
        {
            return result;
        }

        foreach (var process in all)
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null)
                {
                    result.Add((process.Id, path));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                          or System.ComponentModel.Win32Exception)
            {
                // Another user's process, or one that exited between the listing
                // and the read. Nothing to see either way.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }
}
