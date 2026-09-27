using System.Text.Json;
using AgentsDashboard.Core.Repos;

namespace AgentsDashboard.Core.Platform;

/// <summary>A file something outside the app asked to see, and the line to land on.</summary>
public sealed record OpenRequest(string Path, int? Line = null);

/// <summary>
/// Picks up requests to open a file in the running dashboard, dropped as small
/// JSON files into <see cref="AppPaths.OpenRequestsDir"/>.
/// </summary>
/// <remarks>
/// <para>
/// A folder rather than an HTTP route, so an agent in any terminal can use it
/// with nothing but a file write: the dashboard's port is picked fresh on every
/// start, and a route on it would also be reachable by any web page the user
/// has open. A file in the user's own folder can only come from the user's own
/// processes.
/// </para>
/// <para>
/// A request is <c>{"path": "/abs/file.png", "line": 12}</c>. Writers should
/// write under another name and rename it to <c>.json</c>, so a half-written
/// file is never read; a read that fails is tried again shortly in case the
/// writer did not. Each request is deleted once read. Requests older than
/// <see cref="Stale"/> are dropped, so one left while the app was closed
/// does not pop up the next morning.
/// </para>
/// <para>
/// A request that arrives before any window has subscribed is held and handed
/// to the first one, which is what happens when an agent opens the app and
/// asks for a file in the same breath.
/// </para>
/// </remarks>
public sealed class OpenRequests : IDisposable
{
    /// <summary>How old a request can be and still be opened.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan[] Retries = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400)];

    private readonly string _dir;
    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private readonly List<Action<OpenRequest>> _listeners = [];
    private readonly List<OpenRequest> _held = [];
    private readonly HashSet<string> _taking = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;

    public OpenRequests(AppPaths paths, IClock clock)
    {
        _dir = paths.OpenRequestsDir;
        _clock = clock;
    }

    /// <summary>Creates the folder, takes whatever is already in it, and watches for more.</summary>
    public void Start()
    {
        Directory.CreateDirectory(_dir);
        _watcher = new FileSystemWatcher(_dir, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        _watcher.Created += (_, e) => _ = TakeAsync(e.FullPath);
        _watcher.Renamed += (_, e) => _ = TakeAsync(e.FullPath);
        _watcher.EnableRaisingEvents = true;

        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            _ = TakeAsync(file);
        }
    }

    /// <summary>
    /// Calls <paramref name="listener"/> with every request from now on, on a
    /// pool thread, and at once with any held for want of a window.
    /// </summary>
    public IDisposable Subscribe(Action<OpenRequest> listener)
    {
        List<OpenRequest> held;
        lock (_gate)
        {
            _listeners.Add(listener);
            held = [.. _held];
            _held.Clear();
        }

        foreach (var request in held)
        {
            listener(request);
        }

        return new Unsubscriber(this, listener);
    }

    /// <summary>
    /// Opens a file for a caller inside the app, such as an agent's tool call,
    /// without going through the folder. True when a window took it, false when
    /// none is open yet and it is held for the first.
    /// </summary>
    public bool Open(OpenRequest request) => Deliver(request);

    /// <summary>
    /// A request's contents, or null when they are not one. The path has to be
    /// absolute: the writer's working folder means nothing to this process.
    /// </summary>
    public static OpenRequest? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("path", out var pathProp)
                || pathProp.ValueKind != JsonValueKind.String
                || pathProp.GetString() is not { Length: > 0 } path
                || !Path.IsPathFullyQualified(path))
            {
                return null;
            }

            int? line = root.TryGetProperty("line", out var lineProp) && lineProp.TryGetInt32(out var l) && l > 0 ? l : null;
            return new OpenRequest(Path.GetFullPath(path), line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task TakeAsync(string file)
    {
        // A rename can raise Created and Renamed both, and the first scan can
        // race the watcher, so one file is only ever taken once at a time.
        lock (_gate)
        {
            if (!_taking.Add(file))
            {
                return;
            }
        }

        try
        {
            OpenRequest? request = null;
            var written = DateTime.MinValue;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(file))
                    {
                        return;
                    }

                    written = File.GetLastWriteTimeUtc(file);
                    request = Parse(await File.ReadAllTextAsync(file));
                    if (request is not null)
                    {
                        break;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }

                if (attempt == Retries.Length)
                {
                    break;
                }

                await Task.Delay(Retries[attempt]);
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left behind, it goes stale and is dropped on the next start.
            }

            if (request is not null && _clock.Now.UtcDateTime - written <= Stale)
            {
                Deliver(request);
            }
        }
        finally
        {
            lock (_gate)
            {
                _taking.Remove(file);
            }
        }
    }

    private bool Deliver(OpenRequest request)
    {
        Action<OpenRequest>[] listeners;
        lock (_gate)
        {
            if (_listeners.Count == 0)
            {
                _held.Add(request);
                return false;
            }

            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            listener(request);
        }

        return true;
    }

    public void Dispose() => _watcher?.Dispose();

    private sealed class Unsubscriber(OpenRequests owner, Action<OpenRequest> listener) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._listeners.Remove(listener);
            }
        }
    }
}
