using System.Reflection;
using AgentsDashboard.Core.Extensions;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentsDashboard.App.Extensions;

/// <summary>How the app was asked to treat extensions on this run.</summary>
/// <param name="CommandLine">Folders passed with <c>--extension</c>, loaded for this run only.</param>
/// <param name="Disabled">Started with <c>--no-extensions</c>, for when one breaks startup.</param>
public sealed record ExtensionOptions(IReadOnlyList<string> CommandLine, bool Disabled);

public enum ExtensionStatus { Disabled, NeedsConsent, Loaded, Failed, Invalid }

/// <summary>One extension as Settings lists it.</summary>
public sealed record ExtensionEntry(
    FoundExtension Found,
    ExtensionStatus Status,
    string? Message,
    int Generation,
    IReadOnlyList<ExtensionView> Views);

/// <summary>A view, with the context its components are given.</summary>
public sealed record LiveView(ExtensionView View, ExtensionContext Context, int Generation);

/// <summary>
/// Finds, loads, reloads and supervises extensions, and holds what they add.
/// </summary>
/// <remarks>
/// A reload is "load the new copy alongside, stop using the old one". The old
/// copy's workers are stopped and its load context asked to unload, but the page
/// may still hold one of its component types in a cache, so the memory is only
/// sure to come back at the next start. Every load runs from a copy of the build
/// output, so the next build can overwrite the original while this one runs.
/// </remarks>
public sealed class ExtensionHost : IDisposable
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly IServiceProvider _app;
    private readonly ILogger<ExtensionHost> _log;
    private readonly ExtensionOptions _options;
    private readonly Lock _gate = new();

    private readonly Dictionary<string, Loaded> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ExtensionStatus Status, string? Message)> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileSystemWatcher> _folderWatchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Timer> _debounce = new(StringComparer.Ordinal);
    private IReadOnlyList<FoundExtension> _found = [];
    private IReadOnlyList<string> _folders = [];
    private int _generation;

    public ExtensionHost(
        AppPaths paths,
        SettingsStore settings,
        IServiceProvider app,
        ILogger<ExtensionHost> log,
        ExtensionOptions options)
    {
        _paths = paths;
        _settings = settings;
        _app = app;
        _log = log;
        _options = options;
    }

    /// <summary>Raised when an extension loads, unloads or fails.</summary>
    public event Action? Changed;

    private sealed class Loaded
    {
        public required FoundExtension Found { get; init; }
        public required int Generation { get; init; }
        public required ExtensionLoadContext LoadContext { get; init; }
        public required ServiceProvider Services { get; init; }
        public required ExtensionContext Context { get; init; }
        public required IReadOnlyList<ExtensionView> Views { get; init; }
        public required IReadOnlyDictionary<string, IAgentIndicator> Indicators { get; init; }
        public required IReadOnlyList<ICodeIntelligence> CodeIntelligence { get; init; }
        public required IReadOnlyList<IAgentTool> AgentTools { get; init; }
        public required CancellationTokenSource Stopping { get; init; }
        public required string CopyDirectory { get; init; }

        /// <summary>The entry assembly's write time and size when this copy was taken.</summary>
        public required (DateTime, long) EntryStamp { get; init; }
        public List<Task> Workers { get; } = [];
    }

    /// <summary>Publishes the SDK, then finds and loads every extension that is on.</summary>
    public void Start()
    {
        PublishSdk();
        ClearCache();
        Rescan();
    }

    /// <summary>
    /// Reads the folders again: picks up an extension dropped into the extensions
    /// folder, loads what is on and not loaded, and unloads what has gone.
    /// </summary>
    public void Rescan()
    {
        var settings = _settings.Load();
        var found = ExtensionCatalog.Discover(_paths, settings, _options.CommandLine);
        WatchFolders(settings.ExtensionFolders);

        lock (_gate)
        {
            _found = found;
            _folders = settings.ExtensionFolders;
            foreach (var gone in _watchers.Keys.Where(id => found.All(f => f.Id != id)).ToList())
            {
                _watchers.Remove(gone, out var watcher);
                watcher!.Dispose();
            }
        }

        foreach (var gone in LoadedIds().Where(id => found.All(f => f.Id != id)))
        {
            Unload(gone);
        }

        foreach (var extension in found)
        {
            Watch(extension);
            if (!IsLoaded(extension.Id))
            {
                Evaluate(extension);
            }
        }

        Raise();
    }

    /// <summary>The extension folders, as of the last scan.</summary>
    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_gate)
            {
                return _folders;
            }
        }
    }

    public IReadOnlyList<ExtensionEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _found.Select(f =>
                {
                    var (status, message) = _states.GetValueOrDefault(f.Id, (ExtensionStatus.Disabled, null));
                    var loaded = _loaded.GetValueOrDefault(f.Id);
                    return new ExtensionEntry(f, status, message, loaded?.Generation ?? 0, loaded?.Views ?? []);
                }).ToList();
            }
        }
    }

    /// <summary>Every view of every loaded extension, in order.</summary>
    public IReadOnlyList<LiveView> Views
    {
        get
        {
            lock (_gate)
            {
                return _loaded.Values
                    .SelectMany(l => l.Views.Select(v => new LiveView(v, l.Context, l.Generation)))
                    .OrderBy(v => v.View.Order)
                    .ThenBy(v => v.View.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    /// <summary>The folder an extension's <c>assets</c> are served from, while it is loaded.</summary>
    public string? AssetsDirectory(string id)
    {
        lock (_gate)
        {
            return _loaded.GetValueOrDefault(id) is { } loaded
                ? Path.Combine(loaded.CopyDirectory, AssetsFolder)
                : null;
        }
    }

    /// <summary>
    /// Stylesheets to put in the page: an extension that ships
    /// <c>assets/extension.css</c> gets it linked while it is loaded. The
    /// generation is in the address so a rebuilt one is fetched again.
    /// </summary>
    public IReadOnlyList<string> Stylesheets
    {
        get
        {
            lock (_gate)
            {
                return _loaded.Values
                    .Where(l => File.Exists(Path.Combine(l.CopyDirectory, AssetsFolder, "extension.css")))
                    .Select(l => $"_ext/{l.Found.Id}/extension.css?g={l.Generation}")
                    .ToList();
            }
        }
    }

    /// <summary>
    /// A folder in the build output served at <c>/_ext/{id}/</c>. Not the Razor
    /// SDK's <c>wwwroot</c>: that is served from a manifest the app's own build
    /// writes, and an extension loaded at runtime is not in it.
    /// </summary>
    public const string AssetsFolder = "assets";

    /// <summary>Every code intelligence provider of the extensions loaded now, in load order.</summary>
    public IReadOnlyList<ICodeIntelligence> CodeIntelligence()
    {
        lock (_gate)
        {
            return [.. _loaded.Values.OrderBy(l => l.Generation).SelectMany(l => l.CodeIntelligence)];
        }
    }

    /// <summary>
    /// Every agent tool of the extensions loaded now, by name. When two extensions
    /// use one name the one loaded first keeps it, so a reload of the other does
    /// not take it over mid-session.
    /// </summary>
    /// <summary>Clashes already logged, so the list being read on every request says so once.</summary>
    private readonly HashSet<(string, int, string)> _toolClashes = [];

    public IReadOnlyDictionary<string, (string ExtensionId, IAgentTool Tool)> AgentTools()
    {
        lock (_gate)
        {
            var tools = new Dictionary<string, (string, IAgentTool)>(StringComparer.Ordinal);
            foreach (var loaded in _loaded.Values.OrderBy(l => l.Generation))
            {
                foreach (var tool in loaded.AgentTools)
                {
                    if (!tools.TryAdd(tool.Name, (loaded.Found.Id, tool))
                        && _toolClashes.Add((loaded.Found.Id, loaded.Generation, tool.Name)))
                    {
                        _log.LogWarning("{Extension} adds the agent tool {Tool}, which another extension already has", loaded.Found.Id, tool.Name);
                    }
                }
            }

            return tools;
        }
    }

    /// <summary>
    /// The indicator on a view for an agent. An indicator that throws shows
    /// nothing rather than breaking the tab strip.
    /// </summary>
    public Indicator? IndicatorFor(ExtensionView view, AgentContext agent)
    {
        IAgentIndicator? provider;
        lock (_gate)
        {
            provider = _loaded.GetValueOrDefault(view.ExtensionId)?.Indicators.GetValueOrDefault(view.ViewId);
        }

        try
        {
            return provider?.For(agent);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Indicator for {View} threw", view.Key);
            return null;
        }
    }

    /// <summary>
    /// Turns an extension on. For an installed one this is the consent: the hash
    /// of what is there now is what is trusted.
    /// </summary>
    public void Enable(string id)
    {
        var found = Find(id);
        if (found?.Manifest is null)
        {
            return;
        }

        var hash = found.IsDev ? null : ExtensionCatalog.Hash(found);
        SaveState(id, new ExtensionState { Enabled = true, TrustedHash = hash });
        Unload(id);
        Evaluate(found);
        Raise();
    }

    public void Disable(string id)
    {
        SaveState(id, new ExtensionState { Enabled = false });
        Unload(id);
        SetState(id, ExtensionStatus.Disabled, null);
        Raise();
    }

    /// <summary>Loads a fresh copy of an extension that is on.</summary>
    public void Reload(string id)
    {
        if (Find(id) is not { } found)
        {
            return;
        }

        Unload(id);
        Evaluate(found);
        Raise();
    }

    /// <summary>Links a folder with an <c>extension.json</c>, and turns it on.</summary>
    public string? Link(string folder)
    {
        var full = Path.GetFullPath(folder.Trim());
        var found = ExtensionCatalog.Read(full, ExtensionSource.Linked);
        if (found.Manifest is null)
        {
            return found.Error;
        }

        var settings = _settings.Load();
        if (!settings.LinkedExtensions.Contains(full, StringComparer.Ordinal))
        {
            _settings.Save(settings with { LinkedExtensions = [.. settings.LinkedExtensions, full] });
        }

        // You linked it, so you meant to run it.
        SaveState(found.Manifest.Id, new ExtensionState { Enabled = true });
        Unload(found.Manifest.Id);
        Rescan();
        return null;
    }

    public void Unlink(string folder)
    {
        var settings = _settings.Load();
        _settings.Save(settings with
        {
            LinkedExtensions = settings.LinkedExtensions.Where(f => f != folder).ToList(),
        });

        Rescan();
    }

    /// <summary>
    /// Adds a folder of extensions. What is in it now is found and turned on,
    /// and so is anything put in it later.
    /// </summary>
    public string? AddFolder(string folder)
    {
        var full = Path.GetFullPath(folder.Trim());
        if (!Directory.Exists(full))
        {
            return $"{full} does not exist.";
        }

        if (File.Exists(Path.Combine(full, ExtensionManifests.FileName)))
        {
            return $"{full} is an extension itself. Link it instead, or add the folder it is in.";
        }

        var settings = _settings.Load();
        if (!settings.ExtensionFolders.Contains(full, StringComparer.Ordinal))
        {
            _settings.Save(settings with { ExtensionFolders = [.. settings.ExtensionFolders, full] });
        }

        Rescan();
        return null;
    }

    public void RemoveFolder(string folder)
    {
        var settings = _settings.Load();
        _settings.Save(settings with
        {
            ExtensionFolders = settings.ExtensionFolders.Where(f => f != folder).ToList(),
        });

        Rescan();
    }

    /// <summary>
    /// Whether the user wants an extension running. One in an extension folder
    /// is on until it is turned off: adding the folder was the choice to run
    /// what is in it, and an extension that appears there later should not need
    /// a second click.
    /// </summary>
    private bool IsOn(FoundExtension found)
    {
        if (found.Source == ExtensionSource.CommandLine)
        {
            return true;
        }

        var state = _settings.Load().Extensions.GetValueOrDefault(found.Id);
        return state is { Enabled: true } || (state is null && found.Source == ExtensionSource.InFolder);
    }

    /// <summary>Decides whether a found extension should run, and loads it if so.</summary>
    private void Evaluate(FoundExtension found)
    {
        if (found.Manifest is null)
        {
            _log.LogWarning("Extension at {Directory} is not usable: {Error}", found.Directory, found.Error);
            SetState(found.Id, ExtensionStatus.Invalid, found.Error);
            return;
        }

        if (_options.Disabled)
        {
            SetState(found.Id, ExtensionStatus.Disabled, "Started with --no-extensions.");
            return;
        }

        var state = _settings.Load().Extensions.GetValueOrDefault(found.Id);
        if (!IsOn(found))
        {
            SetState(found.Id, ExtensionStatus.Disabled, null);
            return;
        }

        if (found.Source == ExtensionSource.Installed && ExtensionCatalog.Hash(found) != state?.TrustedHash)
        {
            SetState(found.Id, ExtensionStatus.NeedsConsent,
                "Its code has changed since you enabled it. Enable it again to run the new version.");
            return;
        }

        Load(found);
    }

    private void Load(FoundExtension found)
    {
        var manifest = found.Manifest!;
        if (found.EntryPath is not { } entry || !File.Exists(entry))
        {
            SetState(found.Id, ExtensionStatus.Failed, $"Not built yet: {found.EntryPath} does not exist.");
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        var copy = Path.Combine(_paths.ExtensionCacheDir, found.Id, generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ExtensionLoadContext? context = null;
        ServiceProvider? services = null;

        try
        {
            CopyDirectory(found.OutputDirectory!, copy);
            var copiedEntry = Path.Combine(copy, manifest.Entry);

            context = new ExtensionLoadContext(copiedEntry, $"extension:{found.Id}:{generation}");
            var assembly = context.LoadFromAssemblyPath(copiedEntry);

            var entryTypes = assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDashboardExtension).IsAssignableFrom(t))
                .ToList();

            if (entryTypes.Count != 1)
            {
                throw new InvalidOperationException(entryTypes.Count == 0
                    ? $"{manifest.Entry} has no public class implementing IDashboardExtension."
                    : $"{manifest.Entry} has more than one class implementing IDashboardExtension.");
            }

            var info = new ExtensionInfo(manifest.Id, manifest.Name, manifest.Version, found.Directory);
            var builder = new ExtensionBuilder(info);
            ((IDashboardExtension)Activator.CreateInstance(entryTypes[0])!).Configure(builder);

            services = BuildServices(builder, info);
            var indicators = builder.Indicators.ToDictionary(
                i => i.ViewId,
                i => (IAgentIndicator)services.GetRequiredService(i.Provider),
                StringComparer.Ordinal);

            var loaded = new Loaded
            {
                Found = found,
                Generation = generation,
                LoadContext = context,
                Services = services,
                Context = new ExtensionContext(info, services),
                Views = builder.Views,
                Indicators = indicators,
                CodeIntelligence = [.. builder.CodeIntelligence.Select(t => (ICodeIntelligence)services.GetRequiredService(t))],
                AgentTools = [.. builder.AgentTools.Select(t => (IAgentTool)services.GetRequiredService(t))],
                Stopping = new CancellationTokenSource(),
                CopyDirectory = copy,
                EntryStamp = Stamp(entry),
            };

            foreach (var (id, type) in builder.Workers)
            {
                var worker = (IExtensionWorker)services.GetRequiredService(type);
                loaded.Workers.Add(Task.Run(() => Supervise(found.Id, id, worker, loaded.Stopping.Token)));
            }

            lock (_gate)
            {
                _loaded[found.Id] = loaded;
            }

            SetState(found.Id, ExtensionStatus.Loaded, null);
            _log.LogInformation("Loaded extension {Id} generation {Generation}", found.Id, generation);
        }
        catch (Exception e)
        {
            // A reflection load wraps the useful exception.
            var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            _log.LogWarning(cause, "Extension {Id} failed to load", found.Id);
            SetState(found.Id, ExtensionStatus.Failed, cause.Message);
            services?.Dispose();
            context?.Unload();
        }
    }

    /// <summary>
    /// The extension's own container: what it registered, and the API services
    /// from the app. Built per load, because the app's container is fixed once
    /// the app has started.
    /// </summary>
    private ServiceProvider BuildServices(ExtensionBuilder builder, ExtensionInfo info)
    {
        var services = builder.Services;
        services.AddSingleton(info);
        services.AddSingleton(_app.GetRequiredService<IDashboardView>());
        services.AddSingleton(_app.GetRequiredService<INavigation>());
        services.AddSingleton(_app.GetRequiredService<ITextLinker>());
        services.AddSingleton<IExtensionStorage>(new ExtensionStorage(_paths, info.Id));
        services.AddSingleton(_app.GetRequiredService<ILoggerFactory>());
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Runs a worker, and runs it again after a pause if it throws. An exception
    /// on a thread nobody watches would end the whole process.
    /// </summary>
    private async Task Supervise(string extensionId, string workerId, IExtensionWorker worker, CancellationToken stopping)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await worker.RunAsync(stopping).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Worker {Worker} of {Id} threw; restarting in {Delay}", workerId, extensionId, delay);
            }

            try
            {
                await Task.Delay(delay, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    private void Unload(string id)
    {
        Loaded? loaded;
        lock (_gate)
        {
            if (!_loaded.Remove(id, out loaded))
            {
                return;
            }
        }

        loaded.Stopping.Cancel();
        try
        {
            Task.WaitAll([.. loaded.Workers], TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Already logged by the supervisor.
        }

        loaded.Services.Dispose();
        loaded.Stopping.Dispose();
        loaded.LoadContext.Unload();
        _log.LogInformation("Unloaded extension {Id} generation {Generation}", id, loaded.Generation);
    }

    /// <summary>
    /// Follows a folder you are writing an extension in, and reloads it when its
    /// entry assembly is rebuilt. Installed copies are not followed: they only
    /// change when you replace them, and that asks for consent.
    /// </summary>
    private void Watch(FoundExtension found)
    {
        if (!found.IsDev || found.Manifest is null || !Directory.Exists(found.Directory))
        {
            return;
        }

        lock (_gate)
        {
            if (_watchers.ContainsKey(found.Id))
            {
                return;
            }

            var watcher = new FileSystemWatcher(found.Directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };

            watcher.Changed += (_, e) => OnDevChange(found.Id, e.FullPath);
            watcher.Created += (_, e) => OnDevChange(found.Id, e.FullPath);
            watcher.Renamed += (_, e) => OnDevChange(found.Id, e.FullPath);
            watcher.EnableRaisingEvents = true;
            _watchers[found.Id] = watcher;
        }
    }

    /// <summary>
    /// Follows the extension folders for extensions being added or taken away:
    /// a folder appearing or going directly inside, or a manifest being written
    /// into one. Everything deeper, such as a build's output, is ignored.
    /// </summary>
    private void WatchFolders(IReadOnlyList<string> folders)
    {
        lock (_gate)
        {
            foreach (var gone in _folderWatchers.Keys.Where(f => !folders.Contains(f)).ToList())
            {
                _folderWatchers.Remove(gone, out var watcher);
                watcher!.Dispose();
            }

            foreach (var folder in folders.Where(f => !_folderWatchers.ContainsKey(f) && Directory.Exists(f)))
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };

                watcher.Created += (_, e) => OnFolderChange(folder, e.FullPath);
                watcher.Deleted += (_, e) => OnFolderChange(folder, e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    OnFolderChange(folder, e.OldFullPath);
                    OnFolderChange(folder, e.FullPath);
                };
                watcher.EnableRaisingEvents = true;
                _folderWatchers[folder] = watcher;
            }
        }
    }

    private void OnFolderChange(string folder, string path)
    {
        var parts = Path.GetRelativePath(folder, path).Split(Path.DirectorySeparatorChar);
        if (!(parts.Length == 1
              || (parts.Length == 2 && string.Equals(parts[1], ExtensionManifests.FileName, StringComparison.Ordinal))))
        {
            return;
        }

        // Keyed by the folder's path, which cannot clash with an extension id:
        // ids have no slashes. Creating a project writes several files at once,
        // so this waits for them all like a build does.
        lock (_gate)
        {
            if (_debounce.TryGetValue(folder, out var timer))
            {
                timer.Change(500, Timeout.Infinite);
                return;
            }

            _debounce[folder] = new Timer(_ => FolderRescan(folder), null, 500, Timeout.Infinite);
        }
    }

    private void FolderRescan(string folder)
    {
        lock (_gate)
        {
            if (_debounce.Remove(folder, out var timer))
            {
                timer.Dispose();
            }
        }

        try
        {
            Rescan();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Rescanning after a change in {Folder} failed", folder);
        }
    }

    /// <summary>
    /// A build writes the assembly more than once, so a reload waits until the
    /// output has been quiet for half a second.
    /// </summary>
    private void OnDevChange(string id, string path)
    {
        var found = Find(id);
        if (found?.EntryPath is not { } entry
            || !(string.Equals(path, entry, StringComparison.Ordinal)
                 || string.Equals(Path.GetFileName(path), ExtensionManifests.FileName, StringComparison.Ordinal)))
        {
            return;
        }

        lock (_gate)
        {
            if (_debounce.TryGetValue(id, out var timer))
            {
                timer.Change(500, Timeout.Infinite);
                return;
            }

            _debounce[id] = new Timer(_ => DevReload(id), null, 500, Timeout.Infinite);
        }
    }

    private void DevReload(string id)
    {
        lock (_gate)
        {
            if (_debounce.Remove(id, out var timer))
            {
                timer.Dispose();
            }
        }

        try
        {
            // File events arrive more than once per build on some platforms, and
            // for files that did not change at all. Only a new assembly reloads.
            lock (_gate)
            {
                if (_loaded.GetValueOrDefault(id) is { } current
                    && current.Found.EntryPath is { } entry
                    && current.EntryStamp == Stamp(entry))
                {
                    return;
                }
            }

            // The manifest may have changed as well as the code.
            Rescan();
            if (Find(id) is { } found && IsOn(found))
            {
                Reload(id);
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Reloading {Id} after a build failed", id);
        }
    }

    /// <summary>
    /// Copies the API assembly and its documentation where an extension project
    /// can reference them, so writing one needs no NuGet feed and always compiles
    /// against the version of the app that will run it.
    /// </summary>
    private void PublishSdk()
    {
        try
        {
            var api = typeof(IDashboardExtension).Assembly.Location;
            if (api.Length == 0)
            {
                return;
            }

            var target = Path.Combine(_paths.SdkDir, $"{ExtensionManifests.Api.Major}.{ExtensionManifests.Api.Minor}");
            Directory.CreateDirectory(target);
            foreach (var file in new[] { api, Path.ChangeExtension(api, ".xml") }.Where(File.Exists))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Could not publish the extension SDK");
        }
    }

    /// <summary>Nothing is loaded yet at startup, so every old copy can go.</summary>
    private void ClearCache()
    {
        try
        {
            if (Directory.Exists(_paths.ExtensionCacheDir))
            {
                Directory.Delete(_paths.ExtensionCacheDir, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogInformation(e, "Could not clear the extension cache");
        }
    }

    private static (DateTime, long) Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    private FoundExtension? Find(string id)
    {
        lock (_gate)
        {
            return _found.FirstOrDefault(f => f.Id == id);
        }
    }

    private bool IsLoaded(string id)
    {
        lock (_gate)
        {
            return _loaded.ContainsKey(id);
        }
    }

    private List<string> LoadedIds()
    {
        lock (_gate)
        {
            return [.. _loaded.Keys];
        }
    }

    private void SetState(string id, ExtensionStatus status, string? message)
    {
        lock (_gate)
        {
            _states[id] = (status, message);
        }
    }

    private void SaveState(string id, ExtensionState state)
    {
        var settings = _settings.Load();
        var states = new Dictionary<string, ExtensionState>(settings.Extensions, StringComparer.Ordinal) { [id] = state };
        _settings.Save(settings with { Extensions = states });
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        foreach (var id in LoadedIds())
        {
            Unload(id);
        }

        lock (_gate)
        {
            foreach (var watcher in _watchers.Values.Concat(_folderWatchers.Values))
            {
                watcher.Dispose();
            }

            foreach (var timer in _debounce.Values)
            {
                timer.Dispose();
            }

            _watchers.Clear();
            _folderWatchers.Clear();
            _debounce.Clear();
        }
    }
}
