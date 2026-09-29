using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Togue.Core.Platform;

/// <summary>
/// Reports the paths that change under a folder. On macOS it talks to FSEvents
/// itself; everywhere else it is a <see cref="FileSystemWatcher"/>.
/// </summary>
/// <remarks>
/// <para>
/// .NET's watcher on macOS calls <c>sync()</c> every time it starts, so that no
/// event from before the start arrives after it. <c>sync()</c> flushes every
/// dirty buffer on the machine and waits for the disks, and with agents building
/// in a few worktrees that is seconds, not milliseconds. The app starts a
/// handful of watchers before the window can load and two more on every switch
/// of worktree, so it spent most of its startup, and a freeze on each switch,
/// inside that one call. Nothing here minds a stray event from just before the
/// start: every caller only uses a path to decide what to read again.
/// </para>
/// <para>
/// Callers get a path, not a kind of change. FSEvents coalesces several changes
/// to one file into one event, so created, changed and renamed cannot be told
/// apart reliably anyway, and a rename is two paths reported one at a time.
/// </para>
/// </remarks>
public abstract class PathWatcher : IDisposable
{
    /// <summary>
    /// Starts watching <paramref name="path"/>. <paramref name="changed"/> is
    /// called with the full path of anything created, changed, removed or renamed
    /// in it, spelled under <paramref name="path"/> as given; <paramref name="lost"/>
    /// when events were dropped and anything may have changed. Both are called on
    /// a background thread, never two at a time for one watcher. Throws
    /// <see cref="IOException"/> when the folder cannot be watched.
    /// </summary>
    public static PathWatcher Watch(string path, bool recursive, Action<string> changed, Action? lost = null)
    {
        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return OperatingSystem.IsMacOS()
            ? new FsEventsWatcher(root, recursive, changed, lost ?? (() => { }))
            : new PortableWatcher(root, recursive, changed, lost ?? (() => { }));
    }

    public abstract void Dispose();

    private sealed class PortableWatcher : PathWatcher
    {
        private readonly FileSystemWatcher _watcher;

        public PortableWatcher(string root, bool recursive, Action<string> changed, Action lost)
        {
            try
            {
                _watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
            }
            catch (ArgumentException e)
            {
                throw new IOException(e.Message, e);
            }

            _watcher.Changed += (_, e) => changed(e.FullPath);
            _watcher.Created += (_, e) => changed(e.FullPath);
            _watcher.Deleted += (_, e) => changed(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                changed(e.OldFullPath);
                changed(e.FullPath);
            };
            _watcher.Error += (_, _) => lost();
            _watcher.EnableRaisingEvents = true;
        }

        public override void Dispose()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }

    private sealed class FsEventsWatcher : PathWatcher
    {
        private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string LibSystem = "/usr/lib/libSystem.dylib";

        private const uint Utf8 = 0x08000100;
        private const ulong SinceNow = ulong.MaxValue;
        private const uint NoDefer = 0x02;
        private const uint FileEvents = 0x10;
        private const uint MustScanSubDirs = 0x01;
        private const uint UserDropped = 0x02;
        private const uint KernelDropped = 0x04;

        // The native side is handed a number, not a pointer to this object: a
        // callback already queued when the stream is torn down finds nothing
        // under its number and does nothing, rather than reading freed memory.
        private static readonly ConcurrentDictionary<nint, FsEventsWatcher> Live = new();
        private static readonly EventCallback Callback = OnEvents;
        private static readonly nint CallbackPointer = Marshal.GetFunctionPointerForDelegate(Callback);
        private static long _nextId;

        private readonly string _root;
        private readonly string _realRoot;
        private readonly bool _recursive;
        private readonly Action<string> _changed;
        private readonly Action _lost;
        private readonly nint _id;
        private readonly Lock _gate = new();
        private nint _stream;
        private nint _queue;

        public FsEventsWatcher(string root, bool recursive, Action<string> changed, Action lost)
        {
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"{root} does not exist.");
            }

            _root = root;
            // FSEvents reports the real path (/private/var for /var, the target
            // of a symlinked folder), and callers compare against what they
            // passed in, so events are spelled back under the given root.
            _realRoot = RealPath(root) ?? root;
            _recursive = recursive;
            _changed = changed;
            _lost = lost;
            _id = (nint)Interlocked.Increment(ref _nextId);
            Live[_id] = this;

            try
            {
                Start();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void Start()
        {
            var cfPath = CFStringCreateWithCString(0, _realRoot, Utf8);
            var values = new[] { cfPath };
            var callbacks = NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeArrayCallBacks");
            var paths = CFArrayCreate(0, values, 1, callbacks);
            CFRelease(cfPath);

            var context = new StreamContext { Info = _id };
            // Always recursive: FSEvents has no other kind. A shallow watch drops
            // anything deeper in OnEvents.
            _stream = FSEventStreamCreate(0, CallbackPointer, ref context, paths, SinceNow, 0.05, NoDefer | FileEvents);
            CFRelease(paths);
            if (_stream == 0)
            {
                throw new IOException($"FSEvents would not watch {_root}.");
            }

            _queue = dispatch_queue_create("togue.watcher", 0);
            FSEventStreamSetDispatchQueue(_stream, _queue);
            if (FSEventStreamStart(_stream) == 0)
            {
                throw new IOException($"FSEvents would not start watching {_root}.");
            }
        }

        private static void OnEvents(nint stream, nint info, nuint count, nint paths, nint flags, nint ids)
        {
            if (!Live.TryGetValue(info, out var watcher))
            {
                return;
            }

            try
            {
                for (var i = 0; i < (int)count; i++)
                {
                    var flag = (uint)Marshal.ReadInt32(flags, i * sizeof(uint));
                    if ((flag & (MustScanSubDirs | UserDropped | KernelDropped)) != 0)
                    {
                        watcher._lost();
                        continue;
                    }

                    if (Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(paths, i * IntPtr.Size)) is { } path
                        && watcher.Spell(path) is { } spelled)
                    {
                        watcher._changed(spelled);
                    }
                }
            }
            catch (Exception)
            {
                // An exception thrown back into a dispatch queue ends the
                // process. A caller's handler failing loses its own event only.
            }
        }

        /// <summary>The path under the given root, or null when it is not one this watch reports.</summary>
        private string? Spell(string path)
        {
            path = path.TrimEnd('/');
            string relative;
            if (path.StartsWith(_realRoot + "/", StringComparison.Ordinal))
            {
                relative = path[(_realRoot.Length + 1)..];
            }
            else if (path.StartsWith(_root + "/", StringComparison.Ordinal))
            {
                relative = path[(_root.Length + 1)..];
            }
            else
            {
                // The root itself, or somewhere FSEvents followed it to.
                return null;
            }

            return _recursive || !relative.Contains('/') ? Path.Combine(_root, relative) : null;
        }

        public override void Dispose()
        {
            Live.TryRemove(_id, out _);
            lock (_gate)
            {
                if (_stream != 0)
                {
                    FSEventStreamStop(_stream);
                    FSEventStreamInvalidate(_stream);
                    FSEventStreamRelease(_stream);
                    _stream = 0;
                }

                if (_queue != 0)
                {
                    dispatch_release(_queue);
                    _queue = 0;
                }
            }
        }

        private static string? RealPath(string path)
        {
            var resolved = realpath(path, 0);
            if (resolved == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUTF8(resolved);
            }
            finally
            {
                free(resolved);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EventCallback(nint stream, nint info, nuint count, nint paths, nint flags, nint ids);

        [StructLayout(LayoutKind.Sequential)]
        private struct StreamContext
        {
            public nint Version;
            public nint Info;
            public nint Retain;
            public nint Release;
            public nint CopyDescription;
        }

        [DllImport(CoreFoundation)]
        private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

        [DllImport(CoreFoundation)]
        private static extern nint CFArrayCreate(nint allocator, nint[] values, nint count, nint callbacks);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(nint value);

        [DllImport(CoreServices)]
        private static extern nint FSEventStreamCreate(nint allocator, nint callback, ref StreamContext context, nint paths, ulong sinceWhen, double latency, uint flags);

        [DllImport(CoreServices)]
        private static extern void FSEventStreamSetDispatchQueue(nint stream, nint queue);

        [DllImport(CoreServices)]
        private static extern byte FSEventStreamStart(nint stream);

        [DllImport(CoreServices)]
        private static extern void FSEventStreamStop(nint stream);

        [DllImport(CoreServices)]
        private static extern void FSEventStreamInvalidate(nint stream);

        [DllImport(CoreServices)]
        private static extern void FSEventStreamRelease(nint stream);

        [DllImport(LibSystem)]
        private static extern nint dispatch_queue_create([MarshalAs(UnmanagedType.LPUTF8Str)] string label, nint attributes);

        [DllImport(LibSystem)]
        private static extern void dispatch_release(nint queue);

        [DllImport(LibSystem)]
        private static extern nint realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint resolved);

        [DllImport(LibSystem)]
        private static extern void free(nint pointer);
    }
}
