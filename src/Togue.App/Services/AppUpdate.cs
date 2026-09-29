using System.Diagnostics;
using Togue.Core.Repos;
using Photino.NET;

namespace Togue.App.Services;

/// <summary>
/// Notices a newer build published beside the running one, and restarts into it.
/// </summary>
/// <remarks>
/// The app cannot replace its own files while it runs, so publishing does not
/// try: with the app open, <c>tools/publish-local.sh</c> adds the new build and
/// names it in <c>next</c>, and the bundle's launcher makes it current on the
/// next start, whoever starts it. See <see cref="AppInstall"/> for why the
/// builds live outside the bundle. All this service does is see <c>next</c>
/// and, when asked, quit and start the app again. The relaunch is a small shell
/// loop outside this process, because nothing inside it survives the quit.
/// <para>
/// Only a copy started by a bundle's launcher, in its own window, can do this.
/// Run from a build output or with <c>--browser</c> there is nothing to swap and
/// no window to close, and <see cref="Staged"/> stays null.
/// </para>
/// </remarks>
public sealed class AppUpdate : IDisposable
{
    private readonly ILogger<AppUpdate> _log;
    private readonly AppInstall? _install;
    private readonly Timer? _timer;
    private PhotinoWindow? _window;
    private volatile StagedUpdate? _staged;

    public AppUpdate(ILogger<AppUpdate> log)
    {
        _log = log;
        _install = AppInstall.FromEnvironment(Environment.GetEnvironmentVariable);
        if (_install is not null)
        {
            // A file check every few seconds; a watcher would be finer than an
            // update needs.
            _timer = new Timer(_ => _staged = _install.ReadStaged(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>The build waiting to be started, or null when there is none or this copy cannot restart.</summary>
    public StagedUpdate? Staged => _window is null ? null : _staged;

    internal void Attach(PhotinoWindow? window) => _window = window;

    /// <summary>
    /// Starts a helper that waits for this process to end and opens the bundle
    /// again, then closes the window, which shuts the app down as a quit would.
    /// The launcher makes the staged build current on the way up.
    /// </summary>
    public bool Restart()
    {
        if (_install is null || _window is not { } window)
        {
            return false;
        }

        try
        {
            // Started from the home folder: the helper outlives this process, and
            // this process's own working directory is no business of its.
            var psi = new ProcessStartInfo("/bin/zsh")
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            foreach (var arg in (string[])
                     [
                         "-c",
                         "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; open \"$2\"",
                         "relaunch",
                         Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         _install.Bundle,
                     ])
            {
                psi.ArgumentList.Add(arg);
            }

            using var _ = Process.Start(psi);
        }
        catch (Exception e)
        {
            // Anything at all: a restart that cannot start its helper must leave
            // the app running, not take the circuit down with it.
            _log.LogWarning(e, "Could not start the relaunch helper");
            return false;
        }

        _log.LogInformation("Restarting into the staged build");

        // Called from the circuit; the window belongs to the main thread.
        window.Invoke(window.Close);
        return true;
    }

    public void Dispose() => _timer?.Dispose();
}
