namespace AgentsDashboard.Core.Git;

/// <summary>A branch a new worktree can be put on.</summary>
/// <param name="Name">The local branch name, the same for a remote one: what it is checked out as.</param>
/// <param name="Remote">The remote it only exists on so far, or null for a local branch.</param>
public sealed record BranchChoice(string Name, string? Remote)
{
    /// <summary>The ref git is handed: <c>origin/feature</c> for a remote branch, the name for a local one.</summary>
    public string Ref => Remote is null ? Name : $"{Remote}/{Name}";
}

/// <summary>
/// Creates a worktree for a new agent, the way <c>claude --worktree</c> does.
/// </summary>
/// <remarks>
/// The same layout the CLI uses, so worktrees made either way sit side by side
/// and look alike: <c>.claude/worktrees/&lt;name&gt;</c> inside the repository, on
/// a new branch <c>worktree-&lt;name&gt;</c> from the current HEAD. Only ever run
/// because you asked for a new worktree.
///
/// A worktree can instead be put on a branch that already exists, as long as no
/// other worktree has it checked out (git refuses a branch in two worktrees). A
/// branch that is only on a remote gets a local branch of the same name tracking
/// it, which is what <c>git switch</c> would do; checking out the remote ref
/// itself would leave the agent on a detached HEAD with nowhere to commit.
/// </remarks>
public sealed class WorktreeCreator(IGitCli git)
{
    private static readonly string[] Adjectives = ["brisk", "calm", "clever", "eager", "gentle", "keen", "lively", "mighty", "nimble", "quiet", "steady", "swift"];
    private static readonly string[] Nouns = ["badger", "falcon", "heron", "lynx", "marten", "otter", "puffin", "raven", "sparrow", "stoat", "tern", "wren"];

    public async Task<(string? Path, string? Error)> CreateAsync(
        string repoRoot,
        string? name,
        BranchChoice? branch = null,
        CancellationToken ct = default)
    {
        // An existing branch names its worktree when you did not, so the folder
        // says which branch it holds.
        var slug = Slug(name);
        if (slug.Length == 0 && branch is not null)
        {
            slug = Slug(branch.Name);
        }

        if (slug.Length == 0)
        {
            slug = $"{Adjectives[Random.Shared.Next(Adjectives.Length)]}-{Nouns[Random.Shared.Next(Nouns.Length)]}-{Random.Shared.Next(100, 999)}";
        }

        var path = System.IO.Path.Combine(repoRoot, ".claude", "worktrees", slug);
        if (Directory.Exists(path))
        {
            return (null, $"A worktree named {slug} already exists.");
        }

        string[] args = branch switch
        {
            null => ["worktree", "add", "-b", "worktree-" + slug, path],
            { Remote: null } => ["worktree", "add", path, branch.Name],
            _ => ["worktree", "add", "--track", "-b", branch.Name, path, branch.Ref],
        };

        var result = await git.RunAsync(repoRoot, args, ct).ConfigureAwait(false);
        return result.Ok ? (path, null) : (null, result.Message.Trim());
    }

    /// <summary>
    /// The branches a new worktree could be put on: local ones no worktree has
    /// checked out, then remote ones with no local branch of the same name, each
    /// group by name.
    /// </summary>
    /// <remarks>
    /// Remote branches are as fresh as the last fetch; <see cref="FetchAsync"/>
    /// brings them up to date. A remote branch whose local namesake exists is left
    /// out even when that local branch is checked out elsewhere: it would have to
    /// be created under a second name, and the local one is what you want anyway.
    /// </remarks>
    public async Task<IReadOnlyList<BranchChoice>> ListBranchesAsync(string repoRoot, CancellationToken ct = default)
    {
        var remotes = await git.RunAsync(repoRoot, ["remote"], ct).ConfigureAwait(false);
        var refs = await git
            .RunAsync(repoRoot, ["for-each-ref", "--format=%(refname)%00%(worktreepath)", "refs/heads", "refs/remotes"], ct)
            .ConfigureAwait(false);

        return refs.Ok ? ParseBranches(refs.StdOut, remotes.Ok ? remotes.StdOut : "") : [];
    }

    /// <summary>
    /// Parses <c>for-each-ref</c> output of <c>refname NUL worktreepath</c> lines,
    /// given the output of <c>git remote</c> to split a remote ref at the right slash.
    /// </summary>
    public static IReadOnlyList<BranchChoice> ParseBranches(string refs, string remotes)
    {
        // Longest first: a remote may itself contain a slash, and "team/origin"
        // has to win over "team" for refs/remotes/team/origin/main.
        var remoteNames = remotes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderByDescending(r => r.Length)
            .ToList();

        var local = new List<string>();
        var free = new List<string>();
        var remote = new List<BranchChoice>();

        foreach (var line in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\0');
            var refName = parts[0];
            var worktree = parts.Length > 1 ? parts[1] : "";

            if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                var name = refName["refs/heads/".Length..];
                local.Add(name);
                if (worktree.Length == 0)
                {
                    free.Add(name);
                }
            }
            else if (refName.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var rest = refName["refs/remotes/".Length..];
                var owner = remoteNames.FirstOrDefault(r => rest.StartsWith(r + "/", StringComparison.Ordinal));
                if (owner is null)
                {
                    continue;
                }

                var name = rest[(owner.Length + 1)..];
                if (name != "HEAD")
                {
                    remote.Add(new BranchChoice(name, owner));
                }
            }
        }

        // git lists refs in byte order, which puts every capitalised name before
        // any lower-case one; a person reading the list expects case not to matter.
        return
        [
            .. free.Order(StringComparer.OrdinalIgnoreCase).Select(n => new BranchChoice(n, null)),
            .. remote
                .Where(r => !local.Contains(r.Name, StringComparer.Ordinal))
                .OrderBy(r => r.Ref, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Brings the remote branches up to date. Only run when you ask: it talks to
    /// the network and can take a while, and it moves remote-tracking refs.
    /// </summary>
    public async Task<string?> FetchAsync(string repoRoot, CancellationToken ct = default)
    {
        var result = await git.RunAsync(repoRoot, ["fetch", "--all", "--prune"], ct).ConfigureAwait(false);
        return result.Ok ? null : result.Message.Trim();
    }

    /// <summary>A name git and every file system will take: lower case letters, digits and dashes.</summary>
    public static string Slug(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }
}
