namespace AgentsDashboard.Core.Input;

/// <summary>Something a key can be bound to.</summary>
/// <param name="Id">VS Code's command id where there is one, so the names read familiar.</param>
/// <param name="Mac">The default keys on macOS.</param>
/// <param name="Other">The default keys on Windows and Linux.</param>
public sealed record Command(string Id, string Title, string Category, IReadOnlyList<string> Mac, IReadOnlyList<string> Other)
{
    public IReadOnlyList<string> Defaults(bool mac) => mac ? Mac : Other;
}

/// <summary>
/// Every command a key can run, with its default keys.
/// </summary>
/// <remarks>
/// The defaults are VS Code's, since that is where the hands learned them, with
/// one addition: Ctrl+T also opens Go to File. In VS Code it goes to a symbol,
/// which this app has no workspace-wide index for, and it was asked for as the
/// file search. Anything added here shows up on the Keyboard shortcuts page and
/// needs a case in the layout's command switch.
/// </remarks>
public static class Commands
{
    public const string QuickOpen = "workbench.action.quickOpen";
    public const string GoToLine = "workbench.action.gotoLine";
    public const string NavigateBack = "workbench.action.navigateBack";
    public const string NavigateForward = "workbench.action.navigateForward";
    public const string ToggleLeft = "workbench.action.toggleSidebarVisibility";
    public const string ToggleRight = "workbench.action.toggleAuxiliaryBar";
    public const string ToggleBottom = "workbench.action.togglePanel";
    public const string OpenSettings = "workbench.action.openSettings";
    public const string OpenKeybindings = "workbench.action.openGlobalKeybindings";
    public const string NewAgent = "agentsDashboard.newAgent";

    public static IReadOnlyList<Command> All { get; } =
    [
        new(QuickOpen, "Go to File...", "Go", ["cmd+p", "ctrl+t"], ["ctrl+p", "ctrl+t"]),
        new(GoToLine, "Go to Line...", "Go", ["ctrl+g"], ["ctrl+g"]),
        new(NavigateBack, "Go Back", "Go", ["ctrl+-"], ["ctrl+-", "alt+left"]),
        new(NavigateForward, "Go Forward", "Go", ["ctrl+shift+-"], ["ctrl+shift+-", "alt+right"]),
        new(ToggleLeft, "Toggle Left Panel", "View", ["cmd+b"], ["ctrl+b"]),
        new(ToggleRight, "Toggle Right Panel", "View", ["alt+cmd+b"], ["ctrl+alt+b"]),
        new(ToggleBottom, "Toggle Bottom Panel", "View", ["cmd+j"], ["ctrl+j"]),
        new(OpenSettings, "Open Settings", "Preferences", ["cmd+,"], ["ctrl+,"]),
        new(OpenKeybindings, "Open Keyboard Shortcuts", "Preferences", [], []),
        new(NewAgent, "New Agent", "Agents", [], []),
    ];

    public static Command? Find(string id) => All.FirstOrDefault(c => c.Id == id);
}

/// <summary>
/// A key with its modifiers, written the way VS Code writes one:
/// <c>ctrl+shift+p</c>, modifiers in a fixed order, lower case.
/// </summary>
/// <remarks>
/// The key part is the physical key (from the browser's <c>event.code</c>), not
/// the character it types, so <c>ctrl+shift+-</c> stays that rather than turning
/// into <c>ctrl+_</c>, and a binding does not change with the keyboard layout's
/// shifted characters. app.js builds the same strings from key events.
/// </remarks>
public static class KeyChord
{
    private static readonly string[] Order = ["ctrl", "shift", "alt", "cmd", "meta"];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["control"] = "ctrl",
        ["option"] = "alt",
        ["opt"] = "alt",
        ["command"] = "cmd",
        ["win"] = "meta",
        ["super"] = "meta",
        ["esc"] = "escape",
        ["return"] = "enter",
        ["arrowup"] = "up",
        ["arrowdown"] = "down",
        ["arrowleft"] = "left",
        ["arrowright"] = "right",
    };

    /// <summary>
    /// A chord in its one written form, or null when it is not one: no key, a
    /// modifier alone, or a modifier twice.
    /// </summary>
    public static string? Normalize(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord))
        {
            return null;
        }

        var text = chord.Trim().ToLowerInvariant().Replace(" ", "");

        // "+" is the separator, so a plus key can only be the last part, written "ctrl++".
        var parts = text.EndsWith("++", StringComparison.Ordinal)
            ? [.. text[..^2].Split('+', StringSplitOptions.RemoveEmptyEntries), "+"]
            : text.Split('+');

        if (parts.Length == 0 || parts.Any(p => p.Length == 0))
        {
            return null;
        }

        var mods = new HashSet<string>();
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var mod = Aliases.GetValueOrDefault(parts[i], parts[i]);
            if (!Order.Contains(mod) || !mods.Add(mod))
            {
                return null;
            }
        }

        var key = Aliases.GetValueOrDefault(parts[^1], parts[^1]);
        if (Order.Contains(key))
        {
            return null;
        }

        return string.Join('+', Order.Where(mods.Contains).Append(key));
    }

    /// <summary>
    /// A chord as the platform prints it: <c>⇧⌘P</c> on a Mac, in the order
    /// macOS menus use, and <c>Ctrl+Shift+P</c> elsewhere.
    /// </summary>
    public static string Display(string chord, bool mac)
    {
        var parts = (Normalize(chord) ?? chord).Split('+');
        var key = KeyLabel(parts[^1] == "" ? "+" : parts[^1], mac);
        var mods = parts[..^1];

        if (mac)
        {
            var symbols = new (string Mod, string Symbol)[] { ("ctrl", "⌃"), ("alt", "⌥"), ("shift", "⇧"), ("cmd", "⌘"), ("meta", "⌘") };
            return string.Concat(symbols.Where(s => mods.Contains(s.Mod)).Select(s => s.Symbol)) + key;
        }

        var names = new (string Mod, string Name)[] { ("ctrl", "Ctrl"), ("shift", "Shift"), ("alt", "Alt"), ("meta", "Win"), ("cmd", "Win") };
        return string.Join('+', names.Where(n => mods.Contains(n.Mod)).Select(n => n.Name).Append(key));
    }

    private static string KeyLabel(string key, bool mac) => key switch
    {
        "up" => mac ? "↑" : "Up",
        "down" => mac ? "↓" : "Down",
        "left" => mac ? "←" : "Left",
        "right" => mac ? "→" : "Right",
        "enter" => mac ? "↩" : "Enter",
        "escape" => "Esc",
        "backspace" => mac ? "⌫" : "Backspace",
        "delete" => mac ? "⌦" : "Delete",
        "tab" => mac ? "⇥" : "Tab",
        "space" => "Space",
        "pageup" => "PageUp",
        "pagedown" => "PageDown",
        _ when key.Length == 1 => key.ToUpperInvariant(),
        _ => char.ToUpperInvariant(key[0]) + key[1..],
    };
}

/// <summary>
/// The keys in force: each command's defaults, with the user's own choices laid
/// over them.
/// </summary>
/// <remarks>
/// The user's choice for a command replaces its defaults whole, the way VS
/// Code's "Change Keybinding" does, and an empty list is a command they unbound.
/// Only the commands they changed are stored, so a default that changes in a
/// later version reaches everyone who left it alone.
/// </remarks>
public sealed class KeyMap
{
    private readonly Dictionary<string, IReadOnlyList<string>> _byCommand = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);

    public bool Mac { get; }

    private KeyMap(bool mac) => Mac = mac;

    public static KeyMap Resolve(IReadOnlyDictionary<string, IReadOnlyList<string>>? overrides, bool mac)
    {
        var map = new KeyMap(mac);
        foreach (var command in Commands.All)
        {
            if (overrides is not null && overrides.TryGetValue(command.Id, out var chosen))
            {
                map._byCommand[command.Id] = Clean(chosen);
                map._changed.Add(command.Id);
            }
            else
            {
                map._byCommand[command.Id] = Clean(command.Defaults(mac));
            }
        }

        return map;
    }

    private static IReadOnlyList<string> Clean(IEnumerable<string>? chords) =>
        (chords ?? []).Select(KeyChord.Normalize).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The keys that run a command, first the one menus and tooltips show.</summary>
    public IReadOnlyList<string> For(string commandId) =>
        _byCommand.TryGetValue(commandId, out var chords) ? chords : [];

    /// <summary>Whether the user changed this command's keys from the defaults.</summary>
    public bool IsChanged(string commandId) => _changed.Contains(commandId);

    /// <summary>
    /// Which command each key runs, for the page to listen for. Where two
    /// commands share a key the one listed first in <see cref="Commands.All"/>
    /// wins; the shortcuts page says which ones clash.
    /// </summary>
    public IReadOnlyDictionary<string, string> ByChord()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var command in Commands.All)
        {
            foreach (var chord in For(command.Id))
            {
                map.TryAdd(chord, command.Id);
            }
        }

        return map;
    }

    /// <summary>Every command a key is bound to.</summary>
    public IReadOnlyList<string> CommandsFor(string chord)
    {
        var normal = KeyChord.Normalize(chord);
        return normal is null
            ? []
            : Commands.All.Where(c => For(c.Id).Contains(normal)).Select(c => c.Id).ToList();
    }

    /// <summary>The first key of a command as the platform prints it, or null when it has none.</summary>
    public string? Hint(string commandId) =>
        For(commandId) is [var first, ..] ? KeyChord.Display(first, Mac) : null;
}
