using System.Text.Json;
using Togue.Core.Presentation;

namespace Togue.Core.Repos;

/// <summary>Where the native window was and how big, when it last closed.</summary>
/// <param name="Maximized">Whether it was maximized (zoomed, on macOS); the rest is then the size it comes back down to.</param>
public sealed record WindowBounds(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>Reads and writes <see cref="PanelLayout"/>, so the panels come back arranged as they were left.</summary>
/// <remarks>
/// A file for the same reason as <see cref="LastViewStore"/>: the window's origin
/// changes port on every start, and the browser's storage goes with it. A file
/// that fails to read is the default layout.
/// </remarks>
public sealed class PanelLayoutStore(AppPaths paths)
{
    private readonly Lock _gate = new();

    public PanelLayout Load() => (JsonFile.Read<PanelLayout>(paths.LayoutFile, _gate) ?? new PanelLayout()).Normalize();

    public void Save(PanelLayout layout) => JsonFile.Write(paths, paths.LayoutFile, layout, _gate);
}

/// <summary>Reads and writes <see cref="WindowBounds"/>, so the window reopens the size it was.</summary>
public sealed class WindowBoundsStore(AppPaths paths)
{
    private readonly Lock _gate = new();

    public WindowBounds? Load() =>
        JsonFile.Read<WindowBounds>(paths.WindowFile, _gate) is { Width: > 0, Height: > 0 } bounds ? bounds : null;

    public void Save(WindowBounds bounds) => JsonFile.Write(paths, paths.WindowFile, bounds, _gate);
}

/// <summary>A small state file: written whole through a temporary file, so a crash never leaves half of one.</summary>
internal static class JsonFile
{
    public static T? Read<T>(string path, Lock gate) where T : class
    {
        lock (gate)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public static void Write<T>(AppPaths paths, string path, T value, Lock gate)
    {
        lock (gate)
        {
            try
            {
                paths.EnsureCreated();
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(value));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
