using System.Diagnostics;

namespace Tog.Core.Platform;

/// <summary>Opens a folder in the system's file manager.</summary>
public interface IFileManager
{
    /// <summary>What the file manager is called here, for a button's label.</summary>
    string Name { get; }

    bool TryOpen(string directory);
}

/// <summary>
/// Finder, Explorer, or whatever the desktop has registered for folders.
/// </summary>
/// <remarks>
/// Like the clipboard, this is the machine Tog runs on, which is the one the
/// desktop window is on. The folder is passed as an argument, never through a
/// shell, so a path with spaces or quotes is still one path.
/// </remarks>
public sealed class FileManager : IFileManager
{
    public string Name =>
        OperatingSystem.IsMacOS() ? "Finder"
        : OperatingSystem.IsWindows() ? "Explorer"
        : "file manager";

    public bool TryOpen(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var file = OperatingSystem.IsMacOS() ? "open"
            : OperatingSystem.IsWindows() ? "explorer.exe"
            : "xdg-open";

        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(directory);

            // Not waited on: explorer.exe exits with 1 even when it opened the
            // window, and xdg-open can stay around as long as the window does.
            using var process = Process.Start(psi);
            return process is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
