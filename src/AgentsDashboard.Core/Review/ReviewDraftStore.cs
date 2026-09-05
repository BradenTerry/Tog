using System.Text.Json;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Repos;

namespace AgentsDashboard.Core.Review;

/// <summary>
/// Holds the review you are part way through writing.
/// </summary>
/// <remarks>
/// Persisted outside the worktree, one file per worktree, so a draft survives
/// navigating away, restarting the app, and the agent committing underneath you.
/// It is not put in the worktree because an unsubmitted review is not part of
/// the work: it would show up as an untracked file in the very diff it is about.
/// </remarks>
public sealed class ReviewDraftStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Lock _gate = new();

    public ReviewDraft Load(string worktreePath)
    {
        lock (_gate)
        {
            var file = FileFor(worktreePath);
            try
            {
                if (File.Exists(file))
                {
                    var loaded = JsonSerializer.Deserialize<ReviewDraft>(File.ReadAllText(file), Options);
                    if (loaded is not null)
                    {
                        return loaded with { WorktreePath = worktreePath };
                    }
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // A draft we cannot read is a draft that is gone. Start a new one
                // rather than blocking the review screen on it.
            }

            return new ReviewDraft { WorktreePath = worktreePath };
        }
    }

    public void Save(ReviewDraft draft)
    {
        lock (_gate)
        {
            paths.EnsureCreated();
            var file = FileFor(draft.WorktreePath);

            if (draft.IsEmpty)
            {
                TryDelete(file);
                return;
            }

            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(draft with { UpdatedAt = DateTimeOffset.Now }, Options));
            File.Move(temp, file, overwrite: true);
        }
    }

    public void Clear(string worktreePath)
    {
        lock (_gate)
        {
            TryDelete(FileFor(worktreePath));
        }
    }

    private string FileFor(string worktreePath) =>
        Path.Combine(paths.DraftsDir, AppPaths.KeyFor(worktreePath) + ".json");

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // It will be overwritten next time.
        }
    }
}
