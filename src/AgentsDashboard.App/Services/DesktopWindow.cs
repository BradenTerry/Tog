using System.Drawing;
using Photino.NET;

namespace AgentsDashboard.App.Services;

/// <summary>The native window the local app is shown in.</summary>
/// <remarks>
/// Photino wraps the platform's own webview (WebView2 on Windows, WKWebView on
/// macOS, WebKitGTK on Linux) and ships native binaries for all six
/// win/linux/osx x64 and arm64 targets, which is what makes one desktop build
/// cover every machine you might run agents on.
/// <para>
/// If the window cannot be created the app does not die with it: the web host is
/// already serving, so the URL is printed and the dashboard is still usable in a
/// browser. A missing native dependency on a Linux box should cost you a window,
/// not the tool.
/// </para>
/// </remarks>
public static class DesktopWindow
{
    public static void Open(string url, ILoggerFactory loggers, FolderPicker folders, AppUpdate update, Action fallbackUrlPrinted)
    {
        var log = loggers.CreateLogger(nameof(DesktopWindow));

        try
        {
            var window = new PhotinoWindow()
                .SetTitle("Agents Dashboard")
                .SetUseOsDefaultSize(false)
                .SetSize(new Size(1440, 940))
                .SetMinSize(900, 600)
                .Center()
                .SetResizable(true)
                .SetContextMenuEnabled(false)
                .Load(new Uri(url));

            folders.Attach(window);
            update.Attach(window);
            window.WaitForClose();
            update.Attach(null);
            folders.Attach(null);
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException
                                      or PlatformNotSupportedException or EntryPointNotFoundException)
        {
            log.LogWarning(e, "Could not open the native window; serving in the browser instead.");
            fallbackUrlPrinted();
            Console.WriteLine("Open that URL in a browser. Press Ctrl+C to stop.");

            var done = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, args) =>
            {
                args.Cancel = true;
                done.Set();
            };

            done.Wait();
        }
    }
}
