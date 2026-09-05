using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Git;

/// <summary>Reads a repository's worktrees.</summary>
public sealed class WorktreeLister(IGitCli git)
{
    /// <summary>
    /// Every worktree of the repository containing <paramref name="repoRoot"/>,
    /// primary first.
    /// </summary>
    public async Task<IReadOnlyList<WorktreeInfo>> ListAsync(string repoRoot, CancellationToken ct = default)
    {
        var result = await git.RunAsync(repoRoot, ["worktree", "list", "--porcelain"], ct).ConfigureAwait(false);
        return result.Ok ? Parse(result.StdOut) : [];
    }

    /// <summary>
    /// The repository root for a directory, or null when it is not in one.
    /// </summary>
    public async Task<string?> FindRepoRootAsync(string directory, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var result = await git.RunAsync(directory, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false);
        return result.Ok && result.StdOut.Length > 0 ? result.StdOut.Trim() : null;
    }

    /// <summary>
    /// The main working tree of the repository a directory belongs to. Every
    /// worktree of one repo shares this, which is what lets worktrees of the same
    /// repo group together instead of appearing as separate repositories.
    /// </summary>
    public async Task<string?> FindPrimaryRootAsync(string directory, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        // --path-format=absolute keeps this from returning a path relative to the
        // worktree, which is what git does by default for a linked worktree.
        var result = await git
            .RunAsync(directory, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct)
            .ConfigureAwait(false);

        if (!result.Ok || result.StdOut.Length == 0)
        {
            return null;
        }

        var commonDir = result.StdOut.Trim();

        // The common dir is <primary>/.git for a normal repo, and the repo itself
        // for a bare one.
        var name = Path.GetFileName(commonDir.TrimEnd(Path.DirectorySeparatorChar, '/'));
        return name == ".git"
            ? Path.GetDirectoryName(commonDir.TrimEnd(Path.DirectorySeparatorChar, '/'))
            : commonDir;
    }

    /// <summary>
    /// Parses <c>git worktree list --porcelain</c>: blank-line separated records
    /// of <c>key value</c> lines, the first of which is always <c>worktree</c>.
    /// </summary>
    public static IReadOnlyList<WorktreeInfo> Parse(string stdout)
    {
        var list = new List<WorktreeInfo>();
        string? path = null;
        string? branch = null;
        var detached = false;
        var locked = false;
        var first = true;

        void Flush()
        {
            if (path is null)
            {
                return;
            }

            list.Add(new WorktreeInfo
            {
                Path = path,
                Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, '/')) is { Length: > 0 } n
                    ? n
                    : path,
                Branch = branch,
                IsPrimary = first,
                Detached = detached,
                Locked = locked,
            });

            first = false;
            path = null;
            branch = null;
            detached = false;
            locked = false;
        }

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                // Reported as a full ref: refs/heads/<name>.
                var value = line["branch ".Length..];
                branch = value.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? value["refs/heads/".Length..]
                    : value;
            }
            else if (line == "detached")
            {
                detached = true;
            }
            else if (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal))
            {
                locked = true;
            }
        }

        Flush();
        return list;
    }
}
