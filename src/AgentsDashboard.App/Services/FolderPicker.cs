using Photino.NET;

namespace AgentsDashboard.App.Services;

/// <summary>The operating system's own folder chooser, opened over the app window.</summary>
/// <remarks>
/// A page cannot open a native dialog itself: a webview's file input hands back
/// a file's contents, never a directory's path. So the dialog is opened here on
/// the server, through the Photino window, and the circuit only gets the path.
/// <para>
/// There is no window in <c>--browser</c> mode, or when the native window could
/// not be created, and then <see cref="Available"/> is false and the page falls
/// back to the typed path it always had.
/// </para>
/// </remarks>
public sealed class FolderPicker
{
    private PhotinoWindow? _window;

    public bool Available => _window is not null;

    internal void Attach(PhotinoWindow? window) => _window = window;

    /// <summary>The chosen directory, or null when the dialog was cancelled.</summary>
    public async Task<string?> PickAsync(string title, string? startIn = null)
    {
        if (_window is not { } window)
        {
            return null;
        }

        // Photino marshals the dialog onto the window's own thread and blocks
        // until it closes; ShowOpenFolderAsync keeps that wait off the circuit.
        var chosen = await window.ShowOpenFolderAsync(title, startIn, multiSelect: false);
        return chosen is { Length: > 0 } && chosen[0] is { Length: > 0 } path ? path : null;
    }
}
