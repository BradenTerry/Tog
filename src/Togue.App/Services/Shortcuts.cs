using Togue.Core.Input;
using Togue.Core.Repos;

namespace Togue.App.Services;

/// <summary>
/// The keyboard shortcuts in force, for every window.
/// </summary>
/// <remarks>
/// A singleton over the settings file rather than something each view reads for
/// itself: the layout sends the map to its page's key listener, tooltips print
/// the keys, and a change on the Settings page has to reach all of them, in
/// every window, without a restart. The window runs on this machine, so the
/// server's platform is the keyboard's.
/// </remarks>
public sealed class Shortcuts(SettingsStore store)
{
    public static bool Mac => OperatingSystem.IsMacOS();

    private KeyMap? _map;

    public KeyMap Map => _map ??= KeyMap.Resolve(store.Load().KeyBindings, Mac);

    /// <summary>The keys changed, from the Settings page.</summary>
    public event Action? Changed;

    /// <summary>Takes up newly saved settings. The Settings page saves them; this only reads.</summary>
    public void Apply(Settings settings)
    {
        _map = KeyMap.Resolve(settings.KeyBindings, Mac);
        Changed?.Invoke();
    }

    /// <summary>A tooltip's title with the command's key after it: "Go to File (⌘P)".</summary>
    public string Title(string text, string commandId) =>
        Map.Hint(commandId) is { } hint ? $"{text} ({hint})" : text;
}
