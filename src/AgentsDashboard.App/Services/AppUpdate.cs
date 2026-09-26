using System.Diagnostics;
using Photino.NET;

namespace AgentsDashboard.App.Services;

/// <summary>A build waiting next to the running one, as <c>tools/publish-local.sh</c> described it.</summary>
public sealed record StagedUpdate(string Commit, string Subject, DateTimeOffset? StagedAt);

/// <summary>
/// Notices a newer build staged beside the running one, and restarts into it.
/// </summary>
/// <remarks>
/// The app cannot replace its own files while it runs, so publishing does not
/// try: with the app open, <c>tools/publish-local.sh</c> writes the new build to
/// <c>Contents/Resources/app.next</c> with a marker file, and the bundle's
/// launcher swaps it in on the next start, whoever starts it. All this service
/// does is see the marker and, when asked, quit and start the app again. The
/// relaunch is a small shell loop outside this process, because nothing inside
/// it survives the quit.
/// <para>
/// Only a copy running from an app bundle, in its own window, can do this. Run
/// from a build output or with <c>--browser</c> there is nothing to swap and no
/// window to close, and <see cref="Staged"/> stays null.
/// </para>
/// </remarks>
public sealed class AppUpdate : IDisposable
{
    /// <summary>Moved into place with the staged build, so its presence means the copy is complete.</summary>
    public const string MarkerFile = ".staged";

    private readonly ILogger<AppUpdate> _log;
    private readonly string? _bundle;
    private readonly string? _marker;
    private readonly Timer? _timer;
    private PhotinoWindow? _window;
    private volatile StagedUpdate? _staged;

    public AppUpdate(ILogger<AppUpdate> log)
    {
        _log = log;

        // .../Name.app/Contents/Resources/app/
        var app = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (app is { Name: "app", Parent: { Name: "Resources", Parent: { Name: "Contents", Parent: { } bundle } resources } }
            && bundle.Name.EndsWith(".app", StringComparison.Ordinal))
        {
            _bundle = bundle.FullName;
            _marker = Path.Combine(resources.FullName, "app.next", MarkerFile);

            // A file check every few seconds; a watcher would be finer than an
            // update needs, and would have to survive the folder being replaced.
            _timer = new Timer(_ => Check(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>The build waiting to be started, or null when there is none or this copy cannot restart.</summary>
    public StagedUpdate? Staged => _window is null ? null : _staged;

    internal void Attach(PhotinoWindow? window) => _window = window;

    /// <summary>
    /// Starts a helper that waits for this process to end and opens the bundle
    /// again, then closes the window, which shuts the app down as a quit would.
    /// The launcher does the swap on the way up.
    /// </summary>
    public bool Restart()
    {
        if (_bundle is null || _window is not { } window)
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo("/bin/zsh") { UseShellExecute = false };
            foreach (var arg in (string[])
                     [
                         "-c",
                         "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; open \"$2\"",
                         "relaunch",
                         Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         _bundle,
                     ])
            {
                psi.ArgumentList.Add(arg);
            }

            using var _ = Process.Start(psi);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(e, "Could not start the relaunch helper");
            return false;
        }

        _log.LogInformation("Restarting into the staged build");
        // Called from the circuit; the window belongs to the main thread.
        window.Invoke(window.Close);
        return true;
    }

    private void Check()
    {
        try
        {
            _staged = File.Exists(_marker) ? Parse(File.ReadAllLines(_marker!)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Mid-write or mid-swap; the next check sees how it ended.
        }
    }

    /// <summary>The marker is <c>key=value</c> lines: commit, subject, staged.</summary>
    internal static StagedUpdate Parse(IEnumerable<string> lines)
    {
        var values = lines
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.Ordinal);

        return new StagedUpdate(
            values.GetValueOrDefault("commit") ?? "unknown",
            values.GetValueOrDefault("subject") ?? "",
            DateTimeOffset.TryParse(values.GetValueOrDefault("staged"), out var at) ? at : null);
    }

    public void Dispose() => _timer?.Dispose();
}
