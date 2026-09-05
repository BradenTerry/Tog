using System.Diagnostics;

namespace AgentsDashboard.Core.Platform;

/// <summary>
/// "Is this pid still running?" A session's registry file outlives a process
/// that was killed with its terminal, so every session is confirmed against the
/// pid it recorded for itself before it is shown.
/// </summary>
public interface IProcessProbe
{
    bool IsAlive(int pid);
}

/// <inheritdoc />
public sealed class ProcessProbe : IProcessProbe
{
    public bool IsAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with that id.
            return false;
        }
        catch (InvalidOperationException)
        {
            // It exited between the lookup and the check.
            return false;
        }
    }
}
