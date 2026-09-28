using AgentsDashboard.Extensions;

namespace AgentsDashboard.App.Extensions;

/// <summary>
/// One loaded extension's <see cref="IExtensionSettings"/>: the settings it
/// declared and their values, read from the settings file when it loads and
/// changed only through <see cref="ExtensionHost.SetSetting"/>.
/// </summary>
/// <remarks>
/// Kept in memory because the extension reads it from places that must not do
/// IO, such as a code intelligence provider asked on the window's thread. A
/// stored value the extension no longer accepts reads as its default.
/// </remarks>
internal sealed class ExtensionSettingValues : IExtensionSettings
{
    private readonly Dictionary<string, ExtensionSetting> _declared;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public ExtensionSettingValues(IReadOnlyList<ExtensionSetting> declared, IReadOnlyDictionary<string, string> stored)
    {
        Declared = declared;
        _declared = declared.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var setting in declared)
        {
            _values[setting.Id] = stored.TryGetValue(setting.Id, out var value) && setting.Accepts(value)
                ? value
                : setting.Default;
        }
    }

    /// <summary>What the extension declared, in the order it did.</summary>
    public IReadOnlyList<ExtensionSetting> Declared { get; }

    public event Action<string>? Changed;

    public string Get(string id)
    {
        lock (_gate)
        {
            return _values.TryGetValue(id, out var value)
                ? value
                : throw new ArgumentException($"No setting \"{id}\" was added with AddSetting.", nameof(id));
        }
    }

    public bool IsOn(string id) => Get(id) == ExtensionSetting.On;

    /// <summary>Sets a value the setting accepts; false for anything else.</summary>
    public bool Set(string id, string value)
    {
        if (!_declared.TryGetValue(id, out var setting) || !setting.Accepts(value))
        {
            return false;
        }

        lock (_gate)
        {
            if (_values[id] == value)
            {
                return true;
            }

            _values[id] = value;
        }

        Changed?.Invoke(id);
        return true;
    }
}
