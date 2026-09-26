using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Git;

/// <summary>Where a file's changes are: in the index, in the working tree, or both.</summary>
/// <param name="Staged">The index differs from HEAD for this path.</param>
/// <param name="Unstaged">The working tree differs from the index for this path.</param>
/// <param name="Untracked">Git is not tracking this path yet.</param>
public readonly record struct FileStage(bool Staged, bool Unstaged, bool Untracked)
{
    public bool IsFullyStaged => Staged && !Unstaged;

    public bool HasAnything => Staged || Unstaged || Untracked;
}

/// <summary>
/// Staging and unstaging from the review screen.
/// </summary>
/// <remarks>
/// Reading a diff and deciding what to commit are the same sitting, so the
/// review does both. Paths are always passed after <c>--</c>, so a file
/// whose name looks like an option cannot be read as one.
/// </remarks>
public sealed class Staging(IGitCli git)
{
    /// <summary>Which files are staged, unstaged or untracked, keyed by repo-relative path.</summary>
    public async Task<IReadOnlyDictionary<string, FileStage>> ReadAsync(
        string worktreePath,
        CancellationToken ct = default)
    {
        var result = await git.RunAsync(
            worktreePath,
            ["-c", "core.quotePath=false", "status", "--porcelain=v2", "--untracked-files=all"],
            ct).ConfigureAwait(false);

        return result.Ok ? Parse(result.StdOut) : new Dictionary<string, FileStage>();
    }

    /// <summary>
    /// Reads the per-file half of porcelain v2.
    /// </summary>
    /// <remarks>
    /// The two-character field is the whole point: the first character is the
    /// index against HEAD and the second is the working tree against the index, so
    /// a file can be both staged and modified again since.
    /// </remarks>
    public static IReadOnlyDictionary<string, FileStage> Parse(string stdout)
    {
        var files = new Dictionary<string, FileStage>(StringComparer.Ordinal);

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 3)
            {
                continue;
            }

            switch (line[0])
            {
                case '1':
                case '2':
                {
                    var xy = Field(line, 1);
                    var path = PathOf(line, line[0] == '2' ? 9 : 8);
                    if (xy.Length == 2 && path is not null)
                    {
                        files[path] = new FileStage(xy[0] != '.', xy[1] != '.', Untracked: false);
                    }

                    break;
                }

                case 'u':
                {
                    // Unmerged. Both sides need attention, and staging it is how it
                    // gets resolved, so it reads as changed on both.
                    var path = PathOf(line, 10);
                    if (path is not null)
                    {
                        files[path] = new FileStage(Staged: true, Unstaged: true, Untracked: false);
                    }

                    break;
                }

                case '?':
                    files[line[2..]] = new FileStage(Staged: false, Unstaged: true, Untracked: true);
                    break;
            }
        }

        return files;
    }

    public Task<GitResult> StageAsync(
        string worktreePath,
        IEnumerable<string> paths,
        CancellationToken ct = default) =>
        Run(worktreePath, ["add", "--"], paths, ct);

    /// <summary>
    /// Takes paths back out of the index.
    /// </summary>
    /// <remarks>
    /// <c>git restore --staged</c> restores the index from HEAD, which a
    /// repository with no commits does not have. There, taking the path out of
    /// the index entirely is the same outcome, so that is what is done instead.
    /// </remarks>
    public async Task<GitResult> UnstageAsync(
        string worktreePath,
        IEnumerable<string> paths,
        CancellationToken ct = default)
    {
        var list = paths.ToList();
        if (list.Count == 0)
        {
            return new GitResult(0, "", "");
        }

        var head = await git
            .RunAsync(worktreePath, ["rev-parse", "--verify", "--quiet", "HEAD"], ct)
            .ConfigureAwait(false);

        return head.Ok && head.StdOut.Length > 0
            ? await Run(worktreePath, ["restore", "--staged", "--"], list, ct).ConfigureAwait(false)
            : await Run(worktreePath, ["rm", "--cached", "--quiet", "--"], list, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws away what the working tree has beyond the index: an edited file
    /// goes back to its staged version (or its committed one, when nothing is
    /// staged), and a new file is deleted. What is staged is kept, as VS Code's
    /// Discard Changes keeps it.
    /// </summary>
    /// <remarks>
    /// Each path is sorted by what <c>git status</c> says it is now, rather than
    /// by what the caller last saw, so a path that has since become clean or
    /// fully staged is left alone. <c>git clean</c> rather than a file delete for
    /// new files: git will not reach outside the worktree or delete a tracked or
    /// ignored file.
    /// </remarks>
    public async Task<GitResult> DiscardAsync(
        string worktreePath,
        IEnumerable<string> paths,
        CancellationToken ct = default)
    {
        var stages = await ReadAsync(worktreePath, ct).ConfigureAwait(false);
        var untracked = new List<string>();
        var edited = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (stages.TryGetValue(path, out var stage) && stage.Unstaged)
            {
                (stage.Untracked ? untracked : edited).Add(path);
            }
        }

        var restored = await Run(worktreePath, ["restore", "--worktree", "--"], edited, ct).ConfigureAwait(false);
        return restored.Ok
            ? await Run(worktreePath, ["clean", "--force", "--quiet", "--"], untracked, ct).ConfigureAwait(false)
            : restored;
    }

    /// <summary>Stages everything, new files included.</summary>
    public Task<GitResult> StageAllAsync(string worktreePath, CancellationToken ct = default) =>
        git.RunAsync(worktreePath, ["add", "--all", "--"], ct);

    public async Task<GitResult> UnstageAllAsync(string worktreePath, CancellationToken ct = default)
    {
        var head = await git
            .RunAsync(worktreePath, ["rev-parse", "--verify", "--quiet", "HEAD"], ct)
            .ConfigureAwait(false);

        return head.Ok && head.StdOut.Length > 0
            ? await git.RunAsync(worktreePath, ["restore", "--staged", "--", "."], ct).ConfigureAwait(false)
            : await git.RunAsync(worktreePath, ["rm", "--cached", "-r", "--quiet", "--", "."], ct).ConfigureAwait(false);
    }

    private Task<GitResult> Run(
        string worktreePath,
        IReadOnlyList<string> command,
        IEnumerable<string> paths,
        CancellationToken ct)
    {
        var args = new List<string>(command);
        args.AddRange(paths);
        return args.Count == command.Count
            ? Task.FromResult(new GitResult(0, "", ""))
            : git.RunAsync(worktreePath, args, ct);
    }

    /// <summary>The nth space-separated field of a porcelain line.</summary>
    private static string Field(string line, int index)
    {
        var start = 0;
        for (var i = 0; i < index; i++)
        {
            start = line.IndexOf(' ', start);
            if (start < 0)
            {
                return "";
            }

            start++;
        }

        var end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    /// <summary>
    /// The path at the end of a porcelain line, after a fixed number of fields.
    /// A rename line puts the old path after a tab, and the new one comes first.
    /// </summary>
    private static string? PathOf(string line, int fields)
    {
        var at = 0;
        for (var i = 0; i < fields; i++)
        {
            at = line.IndexOf(' ', at);
            if (at < 0)
            {
                return null;
            }

            at++;
        }

        var rest = line[at..];
        var tab = rest.IndexOf('\t');
        return tab < 0 ? rest : rest[..tab];
    }
}
