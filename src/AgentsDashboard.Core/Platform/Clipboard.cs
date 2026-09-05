using System.Diagnostics;

namespace AgentsDashboard.Core.Platform;

/// <summary>Puts text on the system clipboard.</summary>
public interface IClipboard
{
    bool TryCopy(string text);
}

/// <summary>
/// Clipboard through the platform's own command line tool.
/// </summary>
/// <remarks>
/// This is the clipboard of the machine the dashboard runs on, which is the
/// right one for the desktop window and the wrong one when the UI is open in a
/// browser on another device. The review page therefore offers a copy button of
/// its own as well; this is the path that works without one being pressed.
/// </remarks>
public sealed class Clipboard : IClipboard
{
    public bool TryCopy(string text)
    {
        foreach (var (file, args) in Candidates())
        {
            if (Run(file, args, text))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(string File, string[] Args)> Candidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return ("pbcopy", []);
            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            yield return ("clip", []);
            yield break;
        }

        // Wayland first, then X11, then the terminal-multiplexer-friendly one.
        yield return ("wl-copy", []);
        yield return ("xclip", ["-selection", "clipboard"]);
        yield return ("xsel", ["--clipboard", "--input"]);
    }

    private static bool Run(string file, IReadOnlyList<string> args, string text)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.StandardInput.Write(text);
            process.StandardInput.Close();
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Not installed, which on Linux is normal. Try the next one.
            return false;
        }
    }
}
