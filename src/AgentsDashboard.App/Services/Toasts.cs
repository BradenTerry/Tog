namespace AgentsDashboard.App.Services;

/// <summary>A short note in the window's bottom right corner.</summary>
/// <param name="Id">Tells toasts with the same text apart, so closing one closes only it.</param>
/// <param name="Message">What happened, in a sentence or two.</param>
/// <param name="Danger">Drawn with the danger edge rather than the accent one.</param>
public sealed record Toast(long Id, string Message, bool Danger);

/// <summary>
/// Notes that something finished, for results that need no answer: a dialog
/// that only says "done" and waits for Close is one more click for nothing.
/// Anything the user still has to act on stays in its dialog.
/// </summary>
/// <remarks>
/// Scoped, one per window, like <see cref="Workbench"/>. Each toast goes after
/// <see cref="Lifetime"/> unless closed first.
/// </remarks>
public sealed class Toasts : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly List<Toast> _shown = [];
    private readonly CancellationTokenSource _closed = new();
    private long _next;

    public event Action? Changed;

    public IReadOnlyList<Toast> Shown
    {
        get
        {
            lock (_shown)
            {
                return [.. _shown];
            }
        }
    }

    public void Show(string message, bool danger = false)
    {
        Toast toast;
        lock (_shown)
        {
            toast = new Toast(++_next, message, danger);
            _shown.Add(toast);
        }

        Changed?.Invoke();
        _ = ExpireAsync(toast);
    }

    public void Close(long id)
    {
        bool removed;
        lock (_shown)
        {
            removed = _shown.RemoveAll(t => t.Id == id) > 0;
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    private async Task ExpireAsync(Toast toast)
    {
        try
        {
            await Task.Delay(Lifetime, _closed.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Close(toast.Id);
    }

    public void Dispose()
    {
        _closed.Cancel();
        _closed.Dispose();
    }
}
