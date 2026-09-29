using System.Drawing;
using Tog.Core.Repos;
using Photino.NET;

namespace Tog.App.Services;

/// <summary>The native window the local app is shown in.</summary>
/// <remarks>
/// Photino wraps the platform's own webview (WebView2 on Windows, WKWebView on
/// macOS, WebKitGTK on Linux) and ships native binaries for all six
/// win/linux/osx x64 and arm64 targets, which is what makes one desktop build
/// cover every machine you might run agents on.
/// <para>
/// If the window cannot be created the app does not die with it: the web host is
/// already serving, so the URL is printed and Tog is still usable in a
/// browser. A missing native dependency on a Linux box should cost you a window,
/// not the tool.
/// </para>
/// <para>
/// The window reopens where it was and the size it was, from
/// <see cref="WindowBoundsStore"/>. The bounds are followed as the window moves
/// and resizes, not read as it closes, because a maximized window reports the
/// screen's size and the one worth keeping is the size it comes back down to.
/// </para>
/// </remarks>
public static class DesktopWindow
{
    private static readonly Size DefaultSize = new(1440, 940);
    private static readonly Size MinSize = new(900, 600);

    public static void Open(string url, ILoggerFactory loggers, FolderPicker folders, AppUpdate update, WindowBoundsStore bounds, bool verbose, Action fallbackUrlPrinted)
    {
        var log = loggers.CreateLogger(nameof(DesktopWindow));

        try
        {
            var saved = bounds.Load();
            // Photino writes every call, Load(url) included, to stdout at its
            // default verbosity, and the address carries this start's UI key.
            var window = new PhotinoWindow()
                .SetLogVerbosity(verbose ? 2 : 0)
                .SetTitle("Tog")
                .SetUseOsDefaultSize(false)
                .SetSize(saved is null ? DefaultSize : new Size(Math.Max(saved.Width, MinSize.Width), Math.Max(saved.Height, MinSize.Height)))
                .SetMinSize(MinSize.Width, MinSize.Height)
                .SetResizable(true)
                .SetContextMenuEnabled(false);

            if (saved is null)
            {
                window.Center();
            }
            else
            {
                window.SetUseOsDefaultLocation(false).SetLocation(new Point(saved.X, saved.Y));
            }

            var tracker = new BoundsTracker(window, bounds, saved);

            window
                .RegisterWindowCreatedHandler((_, _) =>
                {
                    // A monitor unplugged since would leave the window off every
                    // screen, where it cannot even be dragged back.
                    if (saved is not null && !OnScreen(window, new Rectangle(saved.X, saved.Y, saved.Width, saved.Height)))
                    {
                        window.Center();
                    }

                    if (saved?.Maximized == true)
                    {
                        window.SetMaximized(true);
                    }
                })
                .RegisterSizeChangedHandler((_, size) => tracker.Moved(size: size))
                .RegisterLocationChangedHandler((_, point) => tracker.Moved(point: point))
                .RegisterWindowClosingHandler((_, _) =>
                {
                    tracker.Closing();
                    return false;
                })
                .Load(new Uri(url));

            folders.Attach(window);
            update.Attach(window);
            window.WaitForClose();
            tracker.Save();
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

    /// <summary>
    /// Follows the window's bounds while it is neither maximized nor minimized,
    /// which is what it reopens at, and writes them once it has been still a
    /// moment. Written as it goes rather than only on close, because quitting
    /// the app from the menu can end the process without the window closing.
    /// </summary>
    private sealed class BoundsTracker(PhotinoWindow window, WindowBoundsStore store, WindowBounds? saved)
    {
        private readonly Lock _gate = new();
        private Rectangle? _normal = saved is null ? null : new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
        private bool _maximized = saved?.Maximized ?? false;
        private Timer? _pending;

        public void Moved(Size? size = null, Point? point = null)
        {
            var maximized = Safe(() => window.Maximized);
            var minimized = Safe(() => window.Minimized);
            var at = point ?? Safe(() => window.Location);
            var extent = size ?? Safe(() => window.Size);

            lock (_gate)
            {
                _maximized = maximized;
                if (!maximized && !minimized && extent is { Width: > 0, Height: > 0 })
                {
                    _normal = new Rectangle(at, extent);
                }

                _pending?.Dispose();
                _pending = new Timer(_ => Save(), null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
            }
        }

        public void Closing()
        {
            var maximized = Safe(() => window.Maximized);
            lock (_gate)
            {
                _maximized = maximized;
            }

            Save();
        }

        public void Save()
        {
            WindowBounds? bounds;
            lock (_gate)
            {
                _pending?.Dispose();
                _pending = null;
                bounds = _normal is { } n ? new WindowBounds(n.X, n.Y, n.Width, n.Height, _maximized) : null;
            }

            if (bounds is not null)
            {
                store.Save(bounds);
            }
        }
    }

    /// <summary>Whether enough of the window is on some screen to grab its title bar.</summary>
    private static bool OnScreen(PhotinoWindow window, Rectangle wanted)
    {
        var monitors = Safe(() => window.Monitors);
        if (monitors is not { Count: > 0 })
        {
            return true;
        }

        var titleBar = new Rectangle(wanted.X, wanted.Y, wanted.Width, 40);
        return monitors.Any(m =>
        {
            var overlap = Rectangle.Intersect(m.WorkArea, titleBar);
            return overlap.Width >= 100 && overlap.Height > 0;
        });
    }

    /// <summary>A native read that can fail on a platform or while the window is going away; the default then stands.</summary>
    private static T Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is InvalidOperationException or ApplicationException)
        {
            return default!;
        }
    }
}
