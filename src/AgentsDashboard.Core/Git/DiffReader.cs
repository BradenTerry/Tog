using System.Text;
using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Git;

/// <summary>Produces the diff the review screen renders.</summary>
public sealed class DiffReader(IGitCli git)
{
    /// <summary>Untracked files larger than this are listed but not expanded.</summary>
    private const int MaxUntrackedBytes = 512 * 1024;

    /// <summary>Git's empty tree object in a SHA-1 repository.</summary>
    private const string EmptyTreeSha1 = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    /// <summary>
    /// The diff for a worktree against the chosen base, with untracked files
    /// folded in as additions so a file the agent created but has not staged is
    /// reviewable like any other.
    /// </summary>
    public async Task<DiffSet> ReadAsync(
        string worktreePath,
        DiffBase diffBase,
        string? customRef = null,
        CancellationToken ct = default)
    {
        var (baseRef, error) = await ResolveBaseAsync(worktreePath, diffBase, customRef, ct).ConfigureAwait(false);
        if (baseRef is null)
        {
            return new DiffSet { WorktreePath = worktreePath, Base = diffBase, Error = error };
        }

        // `git diff <commit>` compares that commit to the working tree, so this
        // covers committed, staged and unstaged work in one pass. That matters:
        // an agent's changes are usually a mix of all three.
        var result = await git.RunAsync(
            worktreePath,
            [
                "-c", "core.quotePath=false",
                "diff", "--no-color", "--no-ext-diff", "--find-renames", "-U3",
                baseRef, "--",
            ],
            ct).ConfigureAwait(false);

        if (!result.Ok)
        {
            return new DiffSet
            {
                WorktreePath = worktreePath,
                Base = diffBase,
                BaseRef = baseRef,
                Error = result.Message,
            };
        }

        var files = UnifiedDiffParser.Parse(result.StdOut).ToList();
        files.AddRange(await ReadUntrackedAsync(worktreePath, ct).ConfigureAwait(false));
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new DiffSet
        {
            WorktreePath = worktreePath,
            Base = diffBase,
            BaseRef = baseRef,
            Files = files,
        };
    }

    /// <summary>
    /// One file's changes against the chosen base, with no context lines, for
    /// marking the lines of a file that is open in the editor.
    /// </summary>
    /// <remarks>
    /// Null when the file has no changes, or when the base cannot be resolved:
    /// the marks are a hint, and a file with none is shown as it is. An untracked
    /// file has no diff, so it comes back as one hunk adding every line.
    /// </remarks>
    public async Task<DiffFile?> ReadFileAsync(
        string worktreePath,
        string relativePath,
        DiffBase diffBase,
        string? customRef = null,
        CancellationToken ct = default)
    {
        var (baseRef, _) = await ResolveBaseAsync(worktreePath, diffBase, customRef, ct).ConfigureAwait(false);
        if (baseRef is null)
        {
            return null;
        }

        var untracked = await git.RunAsync(
            worktreePath,
            ["ls-files", "--others", "--exclude-standard", "--", relativePath],
            ct).ConfigureAwait(false);

        if (untracked.Ok && untracked.StdOut.Trim().Length > 0)
        {
            var full = Path.Combine(worktreePath, relativePath);
            var count = File.Exists(full) ? CountLines(await File.ReadAllTextAsync(full, ct).ConfigureAwait(false)) : 0;
            return new DiffFile
            {
                Path = relativePath,
                Kind = FileChangeKind.Untracked,
                Hunks =
                [
                    new DiffHunk
                    {
                        OldStart = 0,
                        NewStart = 1,
                        NewCount = count,
                        Lines = Enumerable.Range(1, count).Select(n => new DiffLine(DiffLineKind.Added, null, n, "")).ToList(),
                    },
                ],
            };
        }

        var result = await git.RunAsync(
            worktreePath,
            ["-c", "core.quotePath=false", "diff", "--no-color", "--no-ext-diff", "-U0", baseRef, "--", relativePath],
            ct).ConfigureAwait(false);

        return result.Ok ? UnifiedDiffParser.Parse(result.StdOut).FirstOrDefault() : null;
    }

    /// <summary>
    /// One file's changes against the chosen base with the whole file as
    /// context, so the diff reads as the file itself with its changes marked.
    /// </summary>
    /// <remarks>
    /// Null when git has nothing to say, which the caller answers with the
    /// ordinary diff. A renamed file is asked for under both names, or git sees
    /// the new name alone and reports every line as added. Untracked files are
    /// not handled here: their diff is already the whole file.
    /// </remarks>
    public async Task<DiffFile?> ReadWholeFileAsync(
        string worktreePath,
        string relativePath,
        string? oldPath,
        DiffBase diffBase,
        string? customRef = null,
        CancellationToken ct = default)
    {
        var (baseRef, _) = await ResolveBaseAsync(worktreePath, diffBase, customRef, ct).ConfigureAwait(false);
        if (baseRef is null)
        {
            return null;
        }

        List<string> args =
        [
            "-c", "core.quotePath=false",
            "diff", "--no-color", "--no-ext-diff", "--find-renames", $"-U{WholeFileContext}",
            baseRef, "--", relativePath,
        ];
        if (!string.IsNullOrEmpty(oldPath) && oldPath != relativePath)
        {
            args.Add(oldPath);
        }

        var result = await git.RunAsync(worktreePath, args, ct).ConfigureAwait(false);
        return result.Ok
            ? UnifiedDiffParser.Parse(result.StdOut).FirstOrDefault(f => f.Path == relativePath)
            : null;
    }

    /// <summary>
    /// Context lines that cover any file the editor will open, which stops at
    /// 2 MB long before this many lines.
    /// </summary>
    private const int WholeFileContext = 1_000_000;

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var lines = text.Count(c => c == '\n');
        return text.EndsWith('\n') ? lines : lines + 1;
    }

    /// <summary>The ref a base compares against, or null and the reason it has none.</summary>
    private async Task<(string? Ref, string? Error)> ResolveBaseAsync(
        string worktreePath,
        DiffBase diffBase,
        string? customRef,
        CancellationToken ct)
    {
        switch (diffBase)
        {
            case DiffBase.WorkingTree:
                return (await ResolveHeadAsync(worktreePath, ct).ConfigureAwait(false), null);

            case DiffBase.DefaultBranch:
                var mergeBase = await ResolveMergeBaseAsync(worktreePath, ct).ConfigureAwait(false);
                return mergeBase is null
                    ? (null, "No default branch to compare against. Set origin/HEAD, or pick a ref explicitly.")
                    : (mergeBase, null);

            case DiffBase.CustomRef:
                return string.IsNullOrWhiteSpace(customRef)
                    ? (null, "No ref given.")
                    : (customRef.Trim(), null);

            default:
                throw new ArgumentOutOfRangeException(nameof(diffBase));
        }
    }

    /// <summary>
    /// HEAD, or the empty tree when the repository has no commits yet.
    /// </summary>
    /// <remarks>
    /// A fresh repository has an unborn HEAD, and <c>git diff HEAD</c> against it
    /// fails outright. Diffing against the empty tree is the same question with
    /// an answer: every tracked file reads as added, which is what it is. The id
    /// is asked of git rather than hardcoded, because it differs between SHA-1
    /// and SHA-256 repositories.
    /// </remarks>
    private async Task<string> ResolveHeadAsync(string worktreePath, CancellationToken ct)
    {
        var head = await git
            .RunAsync(worktreePath, ["rev-parse", "--verify", "--quiet", "HEAD"], ct)
            .ConfigureAwait(false);

        if (head.Ok && head.StdOut.Length > 0)
        {
            return "HEAD";
        }

        var empty = await git
            .RunAsync(worktreePath, ["hash-object", "-t", "tree", "/dev/null"], ct)
            .ConfigureAwait(false);

        // Git for Windows maps /dev/null, but if that ever fails the SHA-1 empty
        // tree is a safe last resort: it is wrong only for a SHA-256 repository,
        // where the diff then reports the failure instead of a wrong answer.
        return empty.Ok && empty.StdOut.Length > 0
            ? empty.StdOut.Trim()
            : EmptyTreeSha1;
    }

    /// <summary>
    /// The merge base of HEAD and the repository's default branch, which is the
    /// commit a pull request would diff from.
    /// </summary>
    public async Task<string?> ResolveMergeBaseAsync(string worktreePath, CancellationToken ct = default)
    {
        var branch = await ResolveDefaultBranchAsync(worktreePath, ct).ConfigureAwait(false);
        if (branch is null)
        {
            return null;
        }

        var mergeBase = await git
            .RunAsync(worktreePath, ["merge-base", "HEAD", branch], ct)
            .ConfigureAwait(false);

        // No shared history (an orphan branch, or a shallow clone that does not
        // reach back far enough). Diffing against the branch tip is still useful
        // and is what the user asked for, so fall back to it rather than failing.
        return mergeBase.Ok && mergeBase.StdOut.Length > 0 ? mergeBase.StdOut.Trim() : branch;
    }

    /// <summary>
    /// The repository's default branch. Asks git what origin points at first;
    /// falls back to the conventional names when there is no remote HEAD.
    /// </summary>
    public async Task<string?> ResolveDefaultBranchAsync(string worktreePath, CancellationToken ct = default)
    {
        var head = await git
            .RunAsync(worktreePath, ["symbolic-ref", "--quiet", "refs/remotes/origin/HEAD"], ct)
            .ConfigureAwait(false);

        if (head.Ok && head.StdOut.Length > 0)
        {
            return head.StdOut.Trim();
        }

        foreach (var candidate in new[] { "origin/main", "origin/master", "main", "master" })
        {
            var verify = await git
                .RunAsync(worktreePath, ["rev-parse", "--verify", "--quiet", candidate + "^{commit}"], ct)
                .ConfigureAwait(false);

            if (verify.Ok && verify.StdOut.Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Untracked files, rendered as all-additions. Git will not diff them, but a
    /// file the agent just created is exactly the kind of change worth reviewing,
    /// so leaving it out would hide the most interesting part of the work.
    /// </summary>
    private async Task<IReadOnlyList<DiffFile>> ReadUntrackedAsync(
        string worktreePath,
        CancellationToken ct)
    {
        var status = await git.RunAsync(
            worktreePath,
            ["-c", "core.quotePath=false", "status", "--porcelain=v2", "--untracked-files=all"],
            ct).ConfigureAwait(false);

        if (!status.Ok)
        {
            return [];
        }

        var files = new List<DiffFile>();
        foreach (var raw in status.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("? ", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = line[2..];
            var full = Path.Combine(worktreePath, relative);
            files.Add(BuildUntracked(relative, full));
        }

        return files;
    }

    private static DiffFile BuildUntracked(string relative, string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return Placeholder(relative, binary: false);
            }

            if (info.Length > MaxUntrackedBytes)
            {
                return Placeholder(relative, binary: false);
            }

            var bytes = File.ReadAllBytes(fullPath);
            if (LooksBinary(bytes))
            {
                return Placeholder(relative, binary: true);
            }

            var text = Encoding.UTF8.GetString(bytes);
            var lines = text.Split('\n');

            // A trailing newline produces one empty element that is not a line.
            var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

            var diffLines = new List<DiffLine>(count);
            for (var i = 0; i < count; i++)
            {
                diffLines.Add(new DiffLine(DiffLineKind.Added, null, i + 1, lines[i].TrimEnd('\r')));
            }

            return new DiffFile
            {
                Path = relative,
                Kind = FileChangeKind.Untracked,
                Hunks = count == 0
                    ? []
                    :
                    [
                        new DiffHunk
                        {
                            OldStart = 0,
                            OldCount = 0,
                            NewStart = 1,
                            NewCount = count,
                            Lines = diffLines,
                        },
                    ],
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Placeholder(relative, binary: false);
        }
    }

    private static DiffFile Placeholder(string relative, bool binary) => new()
    {
        Path = relative,
        Kind = FileChangeKind.Untracked,
        IsBinary = binary,
    };

    /// <summary>A NUL byte early in the file is git's own binary heuristic.</summary>
    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var window = bytes.Length < 8000 ? bytes : bytes[..8000];
        return window.IndexOf((byte)0) >= 0;
    }
}
