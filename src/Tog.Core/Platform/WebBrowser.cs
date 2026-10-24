using System.Diagnostics;

namespace Tog.Core.Platform;

/// <summary>Opens a web page in the system's default browser.</summary>
/// <remarks>
/// Only https addresses: what gets opened here comes from a program's output,
/// and <c>open</c> or <c>xdg-open</c> handed anything else would run whatever
/// is registered for it.
/// </remarks>
public static class WebBrowser
{
    public static bool TryOpen(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false, CreateNoWindow = true };
            if (!OperatingSystem.IsWindows())
            {
                psi.ArgumentList.Add(uri.AbsoluteUri);
            }

            using var process = Process.Start(psi);
            return process is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
