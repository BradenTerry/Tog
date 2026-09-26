using AgentsDashboard.Extensions;

namespace AgentsDashboard.App.Extensions;

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
/// </remarks>
public sealed class CodeNavigation : IDisposable
{
    private readonly ExtensionHost _host;
    private readonly Lock _gate = new();
    private IReadOnlyList<ICodeIntelligence> _providers = [];

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
    public ICodeIntelligence? For(string relativePath)
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
