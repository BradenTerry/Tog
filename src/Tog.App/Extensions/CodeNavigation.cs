using System.Collections.Concurrent;
using Tog.Extensions;

namespace Tog.App.Extensions;

/// <summary>
/// The code intelligence the loaded extensions provide, as the editor asks for
/// it: which provider answers for a file, and one event for all their load states.
/// </summary>
/// <remarks>
/// The app has no language support of its own beyond Monaco's colouring. With no
/// extension providing it, a file has no provider, the bar shows no chip, and
/// every query answers with nothing. A provider comes and goes with its extension,
/// so nothing here holds one across a reload: <see cref="For"/> is asked again
/// each time, and <see cref="ProvidersChanged"/> says when the answer may differ.
/// <para>
/// Nothing a provider does runs on the caller's thread. The caller is a Blazor
/// component, whose thread is the circuit's synchronization context, the one
/// dispatcher the whole window has: a provider that did seconds of synchronous
/// work there, or awaited Task.Yield believing it had left, would freeze every
/// panel. So <see cref="Run{T}"/>, <see cref="Start"/> and <see cref="Post"/>
/// hand the call to the thread pool, and <see cref="For"/> caches its answer
/// because it is asked on every render.
/// </para>
/// </remarks>
public sealed class CodeNavigation : IDisposable
{
    private readonly ExtensionHost _host;
    private readonly Lock _gate = new();
    private IReadOnlyList<ICodeIntelligence> _providers = [];
    private readonly ConcurrentDictionary<string, ICodeIntelligence?> _handles = new(StringComparer.Ordinal);

    // Document updates run one after another, in the order they were posted: a
    // buffer pushed while typing and the re-read after its save must not land
    // the other way round.
    private Task _posted = Task.CompletedTask;

    /// <summary>
    /// How long a query may take before the editor gives up on it. Long enough
    /// for references across a large solution, short enough that a hung provider
    /// costs a hover rather than the editor.
    /// </summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    public CodeNavigation(ExtensionHost host)
    {
        _host = host;
        _host.Changed += Refresh;
        Refresh();
    }

    /// <summary>Raised with a worktree path when any provider's load state for it changes, on any thread.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised when an extension with a provider loads or unloads, on any thread.</summary>
    public event Action? ProvidersChanged;

    /// <summary>The editor language ids some provider answers for.</summary>
    public IReadOnlyList<string> Languages
    {
        get
        {
            lock (_gate)
            {
                return [.. _providers.Select(p => p.Language).Distinct(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>
    /// The provider that answers for a file, or null. The first loaded wins when
    /// two claim the same file. A provider that throws is passed over: this runs
    /// on the render path, where an exception ends the circuit.
    /// </summary>
    public ICodeIntelligence? For(string relativePath) => _handles.GetOrAdd(relativePath, Find);

    private ICodeIntelligence? Find(string relativePath)
    {
        IReadOnlyList<ICodeIntelligence> providers;
        lock (_gate)
        {
            providers = _providers;
        }

        foreach (var provider in providers)
        {
            try
            {
                if (provider.Handles(relativePath))
                {
                    return provider;
                }
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    private void Refresh()
    {
        var next = _host.CodeIntelligence();
        IReadOnlyList<ICodeIntelligence> previous;

        lock (_gate)
        {
            previous = _providers;
            if (previous.SequenceEqual(next, ReferenceEqualityComparer.Instance))
            {
                return;
            }

            _providers = next;
            _handles.Clear();
        }

        foreach (var provider in previous.Except(next, ReferenceEqualityComparer.Instance).Cast<ICodeIntelligence>())
        {
            provider.StatusChanged -= OnStatusChanged;
        }

        foreach (var provider in next.Except(previous, ReferenceEqualityComparer.Instance).Cast<ICodeIntelligence>())
        {
            provider.StatusChanged += OnStatusChanged;
        }

        ProvidersChanged?.Invoke();
    }

    /// <summary>
    /// A query on the thread pool, bounded by <see cref="QueryTimeout"/> as well
    /// as the caller's token. Any failure, a timeout included, answers with the
    /// fallback: the caller is a JSInvokable, and an exception out of one ends
    /// the circuit.
    /// </summary>
    public static async Task<T> Run<T>(
        ICodeIntelligence provider,
        Func<ICodeIntelligence, CancellationToken, Task<T>> query,
        T fallback,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QueryTimeout);
        var token = timeout.Token;

        try
        {
            // WaitAsync as well as the token: a provider that ignores
            // cancellation still lets the editor go at the deadline.
            return await Task.Run(() => query(provider, token), token).WaitAsync(token);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Starts a load or a reload on the thread pool and does not wait for it.
    /// The load belongs to the worktree, not the caller, and its progress
    /// arrives through <see cref="StatusChanged"/>. It is queued with
    /// <see cref="Post"/> so that an unload clicked before it cannot land after.
    /// </summary>
    public void Start(Func<Task> load) => Post(() =>
    {
        // The provider reports a failed load on its chip; this only observes it.
        _ = load().ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    });

    /// <summary>A notification to a provider, such as new buffer text, run on the pool in order.</summary>
    public void Post(Action work)
    {
        lock (_gate)
        {
            _posted = _posted.ContinueWith(
                _ =>
                {
                    try
                    {
                        work();
                    }
                    catch (Exception)
                    {
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private void OnStatusChanged(string worktreePath) => StatusChanged?.Invoke(worktreePath);

    public void Dispose()
    {
        _host.Changed -= Refresh;

        lock (_gate)
        {
            foreach (var provider in _providers)
            {
                provider.StatusChanged -= OnStatusChanged;
            }

            _providers = [];
        }
    }
}
