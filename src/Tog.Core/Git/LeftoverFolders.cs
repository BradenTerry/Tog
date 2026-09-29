namespace Tog.Core.Git;

/// <summary>A folder where a worktree was, which git no longer lists as one.</summary>
/// <param name="RepoRoot">The repository whose worktree location it is in.</param>
/// <param name="Path">The folder, in native form.</param>
/// <param name="Files">How many files are left in it.</param>
/// <param name="Bytes">What they take on disk.</param>
/// <param name="Sample">A few of the files, relative to the folder, so a dialog can say what is in it.</param>
public sealed record LeftoverFolder(string RepoRoot, string Path, int Files, long Bytes, IReadOnlyList<string> Sample);

/// <summary>
/// Folders a worktree removal left behind, found and deleted.
/// </summary>
/// <remarks>
/// On Windows a folder cannot be deleted while a program has a file in it open
/// without sharing delete, or has it as its current directory, and a build
/// server left running by <c>dotnet build</c> commonly does both. Git's
/// <c>worktree remove</c> then fails half way: it deletes what it can, reports
/// the error, and unregisters the worktree anyway. The folder stays on disk, in
/// a place most repositories ignore, and nothing lists it, so it would sit
/// there unseen. A CI probe showed this on Windows only; macOS and Linux delete
/// a folder out from under whoever has it open.
/// <para>
/// So a removal that leaves one says so (<see cref="WorktreeCleanup.RemoveAsync"/>),
/// and the Worktrees view looks for them where worktrees live: the
/// <c>.claude/worktrees</c> folder the CLI and this app both create them in.
/// A worktree kept somewhere else is not looked for; its siblings are nobody's
/// business here.
/// </para>
/// </remarks>
public static class LeftoverFolders
{
    private const int SampleSize = 8;

    /// <summary>Where the CLI and this app put a repository's worktrees.</summary>
    public static string WorktreesFolder(string repoRoot) => System.IO.Path.Combine(repoRoot, ".claude", "worktrees");

    /// <summary>
    /// The folders in the repository's worktree location that are not among
    /// <paramref name="listed"/>, the worktree paths git reports, measured.
    /// </summary>
    public static IReadOnlyList<LeftoverFolder> Find(string repoRoot, IEnumerable<string> listed)
    {
        var folder = WorktreesFolder(repoRoot);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        // Compared through links: git reports real paths, and the repository may
        // have been reached through a link (macOS's /var is /private/var).
        var known = listed.Select(Key).ToHashSet(Repos.RepoDiscovery.PathComparer);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

        try
        {
            return Directory.EnumerateDirectories(folder, "*", options)
                .Where(d => !known.Contains(Key(d)))
                .Select(d => Measure(repoRoot, d))
                .OrderByDescending(l => l.Bytes)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>What is left in a folder.</summary>
    public static LeftoverFolder Measure(string repoRoot, string path)
    {
        var files = 0;
        long bytes = 0;
        var sample = new List<string>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = true,
        };

        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                files++;
                bytes += file.Length;
                if (sample.Count < SampleSize)
                {
                    sample.Add(System.IO.Path.GetRelativePath(path, file.FullName).Replace('\\', '/'));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Measured as far as it could be; the folder is still worth listing.
        }

        return new LeftoverFolder(repoRoot, Normalize(path), files, bytes, sample);
    }

    /// <summary>
    /// Deletes a folder and everything in it, trying again for a few seconds.
    /// True when it is gone.
    /// </summary>
    /// <remarks>
    /// The retries are for what lets go by itself: antivirus and the search
    /// indexer open a file for a moment, and a build server shuts down after a
    /// while idle. Git makes its object files read-only, which on Windows stops
    /// a delete on its own, so those are cleared first.
    /// </remarks>
    public static async Task<bool> DeleteAsync(string path, CancellationToken ct = default, int attempts = 5)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            try
            {
                ClearReadOnly(path);
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt + 1 >= attempts)
                {
                    return !Directory.Exists(path);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), ct).ConfigureAwait(false);
        }
    }

    private static void ClearReadOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = true,
        };

        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }

    private static string Key(string path) => Repos.RealPaths.Resolve(Normalize(path));

    private static string Normalize(string path) =>
        System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, '/');
}
