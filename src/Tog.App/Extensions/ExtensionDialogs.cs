using Tog.Extensions;
using Microsoft.AspNetCore.Components;

namespace Tog.App.Extensions;

/// <summary>
/// One window's extension dialogs: a queue, drawn one at a time by
/// <c>ExtensionDialogHost</c>.
/// </summary>
/// <remarks>
/// Which extension is asking comes from the component's type, not from
/// anything the extension passes, so the header's name cannot be put on by
/// another. A dialog from a copy that has since reloaded or unloaded is
/// cancelled, since its component type belongs to code no longer running.
/// </remarks>
public sealed class ExtensionDialogs : IDialogs, IDisposable
{
    private readonly ExtensionHost _host;
    private readonly List<Open> _queue = [];

    public ExtensionDialogs(ExtensionHost host)
    {
        _host = host;
        _host.Changed += OnExtensionsChanged;
    }

    /// <summary>Raised when a dialog opens or closes, on whichever thread did it.</summary>
    public event Action? Changed;

    /// <summary>A dialog asked for and not yet closed.</summary>
    public sealed class Open(ExtensionDialogs owner) : DialogReference
    {
        public required string Title { get; init; }
        public required Type Component { get; init; }
        public required IReadOnlyDictionary<string, object?> Parameters { get; init; }
        public required ExtensionContext Context { get; init; }
        public required int Generation { get; init; }

        // Continuations run elsewhere, so an extension awaiting the result
        // never runs inside the host's render or under the queue's lock.
        internal TaskCompletionSource<DialogResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Close(object? value = null) => owner.Finish(this, new DialogResult(false, value));

        public override void Cancel() => owner.Finish(this, new DialogResult(true));
    }

    /// <summary>The dialog on screen and how many wait behind it.</summary>
    public (Open? Current, int Waiting) Front
    {
        get
        {
            lock (_queue)
            {
                return (_queue.FirstOrDefault(), Math.Max(0, _queue.Count - 1));
            }
        }
    }

    public Task<DialogResult> ShowAsync<TComponent>(string title, IReadOnlyDictionary<string, object?>? parameters = null)
        where TComponent : IComponent
    {
        var (context, generation) = _host.ContextFor(typeof(TComponent))
            ?? throw new InvalidOperationException($"{typeof(TComponent).Name} is not a component of a loaded extension.");

        var open = new Open(this)
        {
            Title = title,
            Component = typeof(TComponent),
            Parameters = parameters ?? new Dictionary<string, object?>(),
            Context = context,
            Generation = generation,
        };

        lock (_queue)
        {
            _queue.Add(open);
        }

        Changed?.Invoke();
        return open.Done.Task;
    }

    private void Finish(Open open, DialogResult result)
    {
        lock (_queue)
        {
            if (!_queue.Remove(open))
            {
                return;
            }
        }

        open.Done.TrySetResult(result);
        Changed?.Invoke();
    }

    private void OnExtensionsChanged()
    {
        List<Open> stale;
        lock (_queue)
        {
            stale = _queue.Where(o => !_host.IsLoaded(o.Context.Info.Id, o.Generation)).ToList();
        }

        foreach (var open in stale)
        {
            open.Cancel();
        }
    }

    public void Dispose()
    {
        _host.Changed -= OnExtensionsChanged;
        List<Open> all;
        lock (_queue)
        {
            all = [.. _queue];
            _queue.Clear();
        }

        foreach (var open in all)
        {
            open.Done.TrySetResult(new DialogResult(true));
        }
    }
}
