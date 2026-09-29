using System.Globalization;
using Togue.Core.Model;
using Togue.Core.Presentation;

namespace Togue.Core.Git;

/// <summary>The outcome of a cleanup action, in words for the UI.</summary>
/// <param name="Leftover">
/// Set when git removed the worktree but its folder could not be deleted: the
/// removal happened, and something is still on disk that the user has to hear about.
/// </param>
public sealed record CleanupResult(bool Ok, string Message, LeftoverFolder? Leftover = null);

/// <summary>
/// Finds out whether a worktree can go, and takes it away when asked.
/// </summary>
/// <remarks>
/// Nothing here runs on its own. Reading is safe to do at any time; every action
/// is a button someone pressed after a dialog showed them what it would do.
/// </remarks>
public sealed class WorktreeCleanup(IGitCli git, StatusReader status)
{
    /// <summary>
    /// The branches work lands on, best first: what <c>origin/HEAD</c> names,
    /// the local branch of the same name, and the primary worktree's branch.
    /// Merges are checked against each, since a merged pull request reaches
    /// <c>origin/main</c> before anyone pulls it into <c>main</c>.
    /// </summary>
    public async Task<IReadOnlyList<string>> BasesAsync(string repoRoot, string? primaryBranch, CancellationToken ct = default)
    {
        var bases = new List<string>();

        var remoteHead = await git
            .RunAsync(repoRoot, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], ct)
            .ConfigureAwait(false);
        if (remoteHead.Ok && remoteHead.StdOut.Length > 0)
        {
            var remote = remoteHead.StdOut.Trim();
            bases.Add(remote);
            bases.Add(remote[(remote.IndexOf('/') + 1)..]);
        }

        if (primaryBranch is not null)
        {
            bases.Add(primaryBranch);
        }

        bases.Add("main");
        bases.Add("master");

        var existing = new List<string>();
        foreach (var candidate in bases.Distinct(StringComparer.Ordinal))
        {
            var found = await git
                .RunAsync(repoRoot, ["rev-parse", "--verify", "--quiet", candidate + "^{commit}"], ct)
                .ConfigureAwait(false);
            if (found.Ok)
            {
                existing.Add(candidate);
            }

            // origin/HEAD, or else one of the fallbacks: two are plenty.
            if (existing.Count == 2)
            {
                break;
            }
        }

        return existing;
    }

    /// <summary>
    /// Everything the Worktrees view shows about one worktree: git state, how it
    /// stands against the bases, and what it takes up on disk.
    /// </summary>
    /// <param name="others">
    /// The repository's other worktrees. They are often inside the primary one
    /// (<c>.claude/worktrees</c>), and counting them there would count them twice.
    /// </param>
    public async Task<WorktreeFacts> ReadAsync(
        WorktreeInfo worktree,
        IReadOnlyList<string> bases,
        IReadOnlyList<string> others,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (worktree.Prunable || !Directory.Exists(worktree.Path))
        {
            return new WorktreeFacts { MeasuredAt = now };
        }

        var path = worktree.Path;
        var gitStatus = await status.ReadAsync(path, ct).ConfigureAwait(false);
        if (gitStatus is null)
        {
            return new WorktreeFacts { MeasuredAt = now, Error = "git status failed here." };
        }

        var (merge, against) = await MergeStateAsync(path, worktree.Branch, bases, ct).ConfigureAwait(false);
        var onlyHere = await OnlyHereAsync(path, bases, ct).ConfigureAwait(false);

        DateTimeOffset? lastCommit = null;
        var log = await git.RunAsync(path, ["log", "-1", "--format=%ct", "HEAD"], ct).ConfigureAwait(false);
        if (log.Ok && long.TryParse(log.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            lastCommit = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        var ignored = await IgnoredAsync(path, others, ct).ConfigureAwait(false);
        var changed = await ChangedAsync(path, ct).ConfigureAwait(false);
        var changedPaths = changed.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var usage = await Task.Run(() => DiskUsage.Measure(path, ignored, changedPaths, others), ct).ConfigureAwait(false);
        var changes = changed
            .Select(c => c with { Bytes = usage.Changed.GetValueOrDefault(c.Path) })
            .OrderByDescending(c => c.Bytes)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .ToList();

        return new WorktreeFacts
        {
            Status = gitStatus,
            OnlyHere = onlyHere,
            Merge = merge,
            Base = against,
            LastCommitAt = lastCommit,
            TotalBytes = usage.Total,
            OutputBytes = usage.Output.Sum(o => o.Bytes),
            ChangesBytes = changes.Sum(c => c.Bytes),
            Output = usage.Output,
            Changes = changes,
            Top = usage.Top,
            MeasuredAt = now,
        };
    }

    /// <summary>
    /// Merged, squash-merged or not, against the first base that took it. An
    /// ancestor check alone misses squash and rebase merges, which is how most
    /// pull requests land, so a branch whose tip is not in the base is also merged
    /// into it in memory (<c>git merge-tree</c>, nothing written to the worktree):
    /// when that changes nothing, the base already has the work.
    /// </summary>
    private async Task<(MergeState, string?)> MergeStateAsync(string path, string? branch, IReadOnlyList<string> bases, CancellationToken ct)
    {
        if (bases.Count == 0)
        {
            return (MergeState.Unknown, null);
        }

        var best = MergeState.NotMerged;
        string? bestBase = bases[0];

        foreach (var b in bases)
        {
            var own = await git.RunAsync(path, ["rev-list", "--count", b + "..HEAD"], ct).ConfigureAwait(false);
            if (own.Ok && own.StdOut.Trim() == "0")
            {
                // No commits the base lacks. Merged, or never committed to? The
                // topology cannot tell a fast-forwarded branch from an untouched
                // one; the branch's reflog can, since every commit adds a line.
                var never = false;
                if (branch is not null)
                {
                    var reflog = await git
                        .RunAsync(path, ["reflog", "show", "--format=%H", "refs/heads/" + branch], ct)
                        .ConfigureAwait(false);
                    never = reflog.Ok
                            && reflog.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct().Count() == 1;
                }

                return (never ? MergeState.NoOwnCommits : MergeState.Merged, b);
            }

            var merged = await git
                .RunAsync(path, ["merge-tree", "--write-tree", "--no-messages", b, "HEAD"], ct)
                .ConfigureAwait(false);
            var baseTree = await git.RunAsync(path, ["rev-parse", b + "^{tree}"], ct).ConfigureAwait(false);
            if (merged.Ok && baseTree.Ok
                && merged.StdOut.Split('\n')[0].Trim() == baseTree.StdOut.Trim())
            {
                best = MergeState.SquashMerged;
                bestBase = b;
                break;
            }
        }

        return (best, bestBase);
    }

    /// <summary>
    /// The files git status lists, one per file: untracked directories are
    /// listed file by file, so each one's size can be found.
    /// </summary>
    private async Task<IReadOnlyList<ChangedFile>> ChangedAsync(string path, CancellationToken ct)
    {
        var result = await git
            .RunAsync(path, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct)
            .ConfigureAwait(false);
        return result.Ok ? ParseChanged(result.StdOut) : [];
    }

    /// <summary>
    /// Parses <c>git status --porcelain=v1 -z</c>: <c>XY path</c> records
    /// separated by NUL, where a rename or copy is followed by one more record,
    /// the path it came from.
    /// </summary>
    public static IReadOnlyList<ChangedFile> ParseChanged(string stdout)
    {
        var list = new List<ChangedFile>();
        var records = stdout.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 4)
            {
                continue;
            }

            var (x, y) = (record[0], record[1]);
            if (x is 'R' or 'C')
            {
                i++;
            }

            var kind = (x, y) switch
            {
                ('?', '?') => 'U',
                ('U', _) or (_, 'U') or ('A', 'A') or ('D', 'D') => 'C',
                _ when x is 'R' or 'C' => 'R',
                _ when x is 'D' || y is 'D' => 'D',
                _ when x is 'A' => 'A',
                _ => 'M',
            };

            list.Add(new ChangedFile(record[3..], kind, 0));
        }

        return list;
    }

    private async Task<int> OnlyHereAsync(string path, IReadOnlyList<string> bases, CancellationToken ct)
    {
        List<string> args = ["rev-list", "--count", "HEAD", "--not", "--remotes"];
        args.AddRange(bases);
        var result = await git.RunAsync(path, args, ct).ConfigureAwait(false);
        return result.Ok && int.TryParse(result.StdOut.Trim(), out var n) ? n : 0;
    }

    /// <summary>
    /// The ignored entries, collapsed to directories. Entries that hold another
    /// worktree are left out: the space is that worktree's, and is counted there.
    /// </summary>
    private async Task<IReadOnlyList<string>> IgnoredAsync(string path, IReadOnlyList<string> others, CancellationToken ct)
    {
        var result = await git
            .RunAsync(path, ["ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory"], ct)
            .ConfigureAwait(false);
        if (!result.Ok)
        {
            return [];
        }

        // Worktree paths are native, and git answers relative to the worktree in
        // its own form, so the prefix is cut either way and the rest folded to '/'.
        var root = path.TrimEnd('/', '\\');
        var inside = others
            .Where(o => o.Length > root.Length + 1
                && o[root.Length] is '/' or '\\'
                && o.StartsWith(root, Repos.RealPaths.Comparison))
            .Select(o => o[(root.Length + 1)..].Replace('\\', '/').TrimEnd('/') + "/")
            .ToList();

        return result.StdOut
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => !inside.Any(i => i.StartsWith(e, StringComparison.Ordinal) || e.StartsWith(i, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// Removes a worktree, then its branch when asked. Run from the repository,
    /// never from inside the worktree going away.
    /// </summary>
    /// <param name="force">Remove even with changes. Only after a dialog has listed them.</param>
    /// <param name="deleteBranch">Delete the branch it had checked out.</param>
    /// <param name="forceBranch">
    /// Delete the branch even though git does not count it as merged. Right for a
    /// squash-merged branch, whose content the base has under other commits.
    /// </param>
    public async Task<CleanupResult> RemoveAsync(
        string repoRoot,
        WorktreeInfo worktree,
        bool force,
        bool deleteBranch,
        bool forceBranch,
        CancellationToken ct = default)
    {
        if (worktree.IsPrimary)
        {
            return new CleanupResult(false, "The main working tree cannot be removed.");
        }

        if (worktree.Locked)
        {
            var unlock = await git.RunAsync(repoRoot, ["worktree", "unlock", worktree.Path], ct).ConfigureAwait(false);
            if (!unlock.Ok)
            {
                return new CleanupResult(false, unlock.Message);
            }
        }

        List<string> args = ["worktree", "remove"];
        if (force)
        {
            args.Add("--force");
        }

        args.Add(worktree.Path);
        var removed = await git.RunAsync(repoRoot, args, ct).ConfigureAwait(false);
        LeftoverFolder? leftover = null;
        if (!removed.Ok && !await ListedAsync(repoRoot, worktree.Path, ct).ConfigureAwait(false))
        {
            // Git unregisters the worktree even when it cannot delete the folder,
            // which on Windows is what a program holding a file in it causes. The
            // removal happened; what is left is finished here, or reported.
            if (!await LeftoverFolders.DeleteAsync(worktree.Path, ct).ConfigureAwait(false))
            {
                leftover = LeftoverFolders.Measure(repoRoot, worktree.Path);
            }
        }
        else if (!removed.Ok)
        {
            // Put the lock back: it was not ours to take off for nothing.
            if (worktree.Locked)
            {
                List<string> relock = ["worktree", "lock"];
                if (worktree.LockReason is { Length: > 0 } reason)
                {
                    relock.AddRange(["--reason", reason]);
                }

                relock.Add(worktree.Path);
                await git.RunAsync(repoRoot, relock, ct).ConfigureAwait(false);
            }

            return new CleanupResult(false, removed.Message);
        }

        var left = leftover is null ? "" : "\n" + LeftBehind(worktree.Name, leftover, removed.Message);
        if (!deleteBranch || worktree.Branch is null)
        {
            return new CleanupResult(true, $"Removed {worktree.Name}.{left}", leftover);
        }

        var branch = await git
            .RunAsync(repoRoot, ["branch", forceBranch ? "-D" : "-d", worktree.Branch], ct)
            .ConfigureAwait(false);

        return branch.Ok
            ? new CleanupResult(true, $"Removed {worktree.Name} and branch {worktree.Branch}.{left}", leftover)
            : new CleanupResult(true, $"Removed {worktree.Name}. Kept branch {worktree.Branch}: {branch.Message}{left}", leftover);
    }

    /// <summary>
    /// Deletes a folder the Worktrees view found where a worktree used to be,
    /// after checking that git has not taken it back as a worktree since.
    /// </summary>
    public async Task<CleanupResult> DeleteLeftoverAsync(LeftoverFolder leftover, CancellationToken ct = default)
    {
        if (await ListedAsync(leftover.RepoRoot, leftover.Path, ct).ConfigureAwait(false))
        {
            return new CleanupResult(false, $"Git lists {leftover.Path} as a worktree again, so it was left alone.");
        }

        if (await LeftoverFolders.DeleteAsync(leftover.Path, ct).ConfigureAwait(false))
        {
            return new CleanupResult(true, $"Deleted {leftover.Path}.");
        }

        var now = LeftoverFolders.Measure(leftover.RepoRoot, leftover.Path);
        return new CleanupResult(
            false,
            $"Could not delete {now.Path}: another program still has something in it open, often a build server or an editor. "
                + $"{Fmt.Count(now.Files, "file")} ({Fmt.Bytes(now.Bytes)}) left. Close that program and try again.",
            now);
    }

    private static string LeftBehind(string name, LeftoverFolder leftover, string gitMessage) =>
        $"Its folder could not be deleted, because another program still has something in it open, often a build server "
            + $"or an editor ({gitMessage.Trim()}). Left on disk: {leftover.Path}, {Fmt.Count(leftover.Files, "file")} "
            + $"({Fmt.Bytes(leftover.Bytes)}). It is listed under Leftover folders, to delete once that program has closed.";

    /// <summary>Whether git still lists a path as one of the repository's worktrees.</summary>
    private async Task<bool> ListedAsync(string repoRoot, string path, CancellationToken ct)
    {
        var list = await git.RunAsync(repoRoot, ["worktree", "list", "--porcelain"], ct).ConfigureAwait(false);
        if (!list.Ok)
        {
            // Unsure means listed: nothing is deleted on a guess.
            return true;
        }

        var target = Repos.RealPaths.Resolve(Path.GetFullPath(path));
        return WorktreeLister.Parse(list.StdOut)
            .Any(w => string.Equals(Repos.RealPaths.Resolve(Path.GetFullPath(w.Path)), target, Repos.RealPaths.Comparison));
    }

    /// <summary>
    /// What removing a worktree with force would lose, for the dialog to list:
    /// the changed and untracked files as <c>git status --short</c> prints them,
    /// then the commits that are on no remote and not in the base.
    /// </summary>
    public async Task<(IReadOnlyList<string> Files, IReadOnlyList<string> Commits)> LossesAsync(
        string worktreePath,
        string? baseBranch,
        CancellationToken ct = default)
    {
        var status = await git
            .RunAsync(worktreePath, ["status", "--short", "--untracked-files=all"], ct)
            .ConfigureAwait(false);

        List<string> args = ["log", "--format=%h %s", "HEAD", "--not", "--remotes"];
        if (baseBranch is not null)
        {
            args.Add(baseBranch);
        }

        var log = await git.RunAsync(worktreePath, args, ct).ConfigureAwait(false);

        static IReadOnlyList<string> Lines(GitResult r) =>
            r.Ok ? r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];

        return (Lines(status), Lines(log));
    }

    /// <summary>Drops the records of worktrees whose directories are gone. Touches no files.</summary>
    public async Task<CleanupResult> PruneAsync(string repoRoot, CancellationToken ct = default)
    {
        var result = await git.RunAsync(repoRoot, ["worktree", "prune"], ct).ConfigureAwait(false);
        return new CleanupResult(result.Ok, result.Ok ? "Pruned." : result.Message);
    }

    /// <summary>
    /// Where a worktree stands, from what was measured. Protection wins: the
    /// primary, Togue's own directory and a running agent come before
    /// anything git says.
    /// </summary>
    public static CleanupState Classify(WorktreeInfo worktree, WorktreeFacts? facts, bool agentRunning, bool hostsTogue)
    {
        if (worktree.IsPrimary)
        {
            return CleanupState.Primary;
        }

        if (hostsTogue)
        {
            return CleanupState.Togue;
        }

        if (worktree.Prunable)
        {
            return CleanupState.Orphaned;
        }

        if (agentRunning)
        {
            return CleanupState.AgentRunning;
        }

        if (facts is null)
        {
            return CleanupState.Checking;
        }

        if (facts.Status is not { } status)
        {
            return CleanupState.Unknown;
        }

        // A squash-merged branch's commits are unique to it by definition, but the
        // base has their content, so they are not work that would be lost.
        var unsaved = !status.IsClean || (facts.OnlyHere > 0 && facts.Merge != MergeState.SquashMerged);
        if (unsaved)
        {
            return CleanupState.HasWork;
        }

        return facts.Merge switch
        {
            MergeState.Merged or MergeState.SquashMerged or MergeState.NoOwnCommits => CleanupState.SafeToRemove,
            _ => CleanupState.NotMerged,
        };
    }
}

/// <summary>Adds up what a worktree takes on disk, and how much of it is ignored.</summary>
public static class DiskUsage
{
    public sealed record Result(
        long Total,
        IReadOnlyList<DiskEntry> Output,
        IReadOnlyList<DiskEntry> Top,
        IReadOnlyDictionary<string, long> Changed);

    /// <summary>
    /// One walk of the tree. Symbolic links are not followed, since pnpm and
    /// friends link a package into many places and it is on disk once. The
    /// top-level <c>.git</c> is the repository (or, in a linked worktree, a file
    /// pointing at it), not the worktree, and is left out; so are the paths in <paramref name="skip"/>. Files in
    /// <paramref name="changed"/> are sized one by one and, like ignored ones,
    /// kept out of the top-level breakdown, which is the project's files alone.
    /// </summary>
    public static Result Measure(
        string root,
        IReadOnlyList<string> ignored,
        IReadOnlySet<string> changed,
        IReadOnlyList<string> skip)
    {
        var changedSizes = new Dictionary<string, long>(StringComparer.Ordinal);
        var dirs = new Dictionary<string, int>(StringComparer.Ordinal);
        var files = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ignored.Count; i++)
        {
            (ignored[i].EndsWith('/') ? dirs : files)[ignored[i]] = i;
        }

        var sizes = new long[ignored.Count];
        // Compared with DirectoryInfo.FullName, so in the same form: native
        // separators, and case folded where the filesystem folds it.
        var skipped = new HashSet<string>(
            skip.Select(s => Path.GetFullPath(s).TrimEnd(Path.DirectorySeparatorChar, '/')),
            Repos.RepoDiscovery.PathComparer);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = false,
        };

        long total = 0;
        var top = new Dictionary<string, long>(StringComparer.Ordinal);
        var stack = new Stack<(DirectoryInfo Dir, string Rel, int Entry, string? Top)>();
        stack.Push((new DirectoryInfo(root), "", -1, null));

        while (stack.Count > 0)
        {
            var (dir, rel, entry, under) = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = dir.EnumerateFileSystemInfos("*", options);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (child is DirectoryInfo sub)
                {
                    if ((rel.Length == 0 && sub.Name == ".git") || skipped.Contains(sub.FullName))
                    {
                        continue;
                    }

                    var subRel = rel + sub.Name + "/";
                    var subTop = under ?? subRel;
                    stack.Push((sub, subRel, entry >= 0 ? entry : dirs.GetValueOrDefault(subRel, -1), subTop));
                }
                else if (child is FileInfo file)
                {
                    // A linked worktree's .git is a file pointing at the repository.
                    if (rel.Length == 0 && file.Name == ".git")
                    {
                        continue;
                    }

                    long length;
                    try
                    {
                        length = file.Length;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    total += length;
                    var fileRel = rel + file.Name;
                    var at = entry >= 0 ? entry : files.GetValueOrDefault(fileRel, -1);
                    if (at >= 0)
                    {
                        sizes[at] += length;
                    }
                    else if (changed.Contains(fileRel))
                    {
                        changedSizes[fileRel] = length;
                    }
                    else
                    {
                        var fileTop = under ?? file.Name;
                        top[fileTop] = top.GetValueOrDefault(fileTop) + length;
                    }
                }
            }
        }

        var output = ignored
            .Select((path, i) => new DiskEntry(path, sizes[i]))
            .OrderByDescending(o => o.Bytes)
            .ToList();

        var topLevel = top
            .Where(t => t.Value > 0)
            .Select(t => new DiskEntry(t.Key, t.Value))
            .OrderByDescending(t => t.Bytes)
            .ToList();

        return new Result(total, output, topLevel, changedSizes);
    }
}
