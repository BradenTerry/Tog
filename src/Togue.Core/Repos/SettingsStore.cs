using System.Text.Json;

namespace Togue.Core.Repos;

/// <summary>Reads and writes <see cref="Settings"/>.</summary>
/// <remarks>
/// Written whole and atomically: a torn settings file would be worse than a lost
/// change, because it would fail to load on the next start and silently reset
/// the user's repo list.
/// </remarks>
public sealed class SettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Lock _gate = new();

    public Settings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(paths.SettingsFile))
                {
                    return new Settings();
                }

                var json = File.ReadAllText(paths.SettingsFile);
                return JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings();
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return new Settings();
            }
        }
    }

    public void Save(Settings settings)
    {
        lock (_gate)
        {
            paths.EnsureCreated();
            var json = JsonSerializer.Serialize(settings, Options);
            var temp = paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, paths.SettingsFile, overwrite: true);
        }
    }
}
