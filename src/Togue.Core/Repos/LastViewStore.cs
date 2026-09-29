using System.Text.Json;

namespace Togue.Core.Repos;

/// <summary>What a window was showing when it was last used.</summary>
/// <param name="SessionId">The last agent that was open, kept after it is closed so a restart finds it.</param>
/// <param name="WorktreePath">The worktree in view.</param>
/// <param name="RepoRoot">The repository of <paramref name="WorktreePath"/>, for standing in when it is removed.</param>
/// <param name="Pinned">Whether the worktree was picked by hand rather than followed from the agent.</param>
public sealed record LastView(string? SessionId, string? WorktreePath, string? RepoRoot, bool Pinned);

/// <summary>Reads and writes <see cref="LastView"/>, so the app reopens where it was left.</summary>
/// <remarks>
/// A file rather than the browser's storage: the window is served from a port
/// picked afresh on every start, and the browser keeps its storage per origin,
/// so anything put there is gone at the next launch. Losing it costs nothing
/// but a click, so a file that fails to read is simply no file.
/// </remarks>
public sealed class LastViewStore(AppPaths paths)
{
    private readonly Lock _gate = new();

    public LastView? Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(paths.LastViewFile)
                    ? JsonSerializer.Deserialize<LastView>(File.ReadAllText(paths.LastViewFile))
                    : null;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public void Save(LastView view)
    {
        lock (_gate)
        {
            try
            {
                paths.EnsureCreated();
                var temp = paths.LastViewFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(view));
                File.Move(temp, paths.LastViewFile, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
