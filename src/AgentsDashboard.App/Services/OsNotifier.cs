using System.Diagnostics;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Monitoring;

namespace AgentsDashboard.App.Services;

/// <summary>
/// Tells you an agent is blocked, through the operating system.
/// </summary>
/// <remarks>
/// Every other signal the dashboard has terminates inside its own window: the
/// waiting rail, the counts, the pulsing dot. None of them reach you once the
/// window is behind your editor, which is exactly when an agent sitting on a
/// question costs the most. This is the only channel that does, which is also
/// why the rules upstream about when to raise one are strict.
/// </remarks>
public sealed class OsNotifier(ILogger<OsNotifier> log) : INotifier
{
    public void AgentWaiting(WaitingAgent agent)
    {
        var title = $"{agent.WorktreeName} needs you";
        var body = agent.Session.WaitingFor is { Length: > 0 } asked
            ? asked
            : agent.Session.Label;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Run("osascript", ["-e", $"display notification {Quote(body)} with title {Quote(title)}"]);
            }
            else if (OperatingSystem.IsWindows())
            {
                // BurntToast and friends are not installed by default, and a toast
                // through the shell APIs needs a packaged identity. A balloon from
                // PowerShell needs neither and is enough to draw the eye.
                var script =
                    "[reflection.assembly]::LoadWithPartialName('System.Windows.Forms') > $null; "
                    + "$n = New-Object System.Windows.Forms.NotifyIcon; "
                    + "$n.Icon = [System.Drawing.SystemIcons]::Information; "
                    + "$n.Visible = $true; "
                    + $"$n.ShowBalloonTip(6000, {PsQuote(title)}, {PsQuote(body)}, 'Info'); "
                    + "Start-Sleep -Seconds 7; $n.Dispose()";

                Run("powershell", ["-NoProfile", "-WindowStyle", "Hidden", "-Command", script]);
            }
            else
            {
                Run("notify-send", ["-a", "Agents Dashboard", title, body]);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No notification tool on this machine. The in-app rail still shows it.
            log.LogDebug(e, "Could not raise a notification.");
        }
    }

    private static void Run(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi);
        process?.WaitForExit(8000);
    }

    /// <summary>AppleScript string literal: only backslash and quote need escaping.</summary>
    private static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>PowerShell single-quoted string: doubling the quote is the whole escape.</summary>
    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";
}
