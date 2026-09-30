namespace Tog.Core.Git;

/// <summary>
/// Committing from Source control, and the few moves VS Code puts beside it:
/// amend, undo the last commit, push and pull.
/// </summary>
/// <remarks>
/// Only ever run when asked, from a button. Takes its own <see cref="IGitCli"/>
/// with a long timeout rather than the app's 30 seconds: a commit runs the
/// repository's hooks, which can lint or test, and a push or pull waits on the
/// network. Git never stops for an editor here: a message is always passed, or
/// an amend keeps the one it has.
/// </remarks>
public sealed class Commits(IGitCli git)
{
    /// <summary>
    /// Commits what is staged. A blank message is refused, except on an amend,
    /// which then keeps the last commit's message as VS Code does.
    /// </summary>
    public Task<GitResult> CommitAsync(
        string worktreePath,
        string message,
        bool amend = false,
        CancellationToken ct = default)
    {
        var blank = string.IsNullOrWhiteSpace(message);
        if (blank && !amend)
        {
            return Task.FromResult(new GitResult(1, "", "A commit needs a message."));
        }

        List<string> args = ["commit", "--quiet"];
        if (amend)
        {
            args.Add("--amend");
        }

        // One -m argument, whatever it holds: the list is passed as is, so a
        // message that starts with a dash or runs to several lines stays the
        // message.
        args.AddRange(blank ? ["--no-edit"] : ["-m", message]);
        return git.RunAsync(worktreePath, args, ct);
    }

    /// <summary>The last commit's full message, or null with no commits.</summary>
    public async Task<string?> LastMessageAsync(string worktreePath, CancellationToken ct = default)
    {
        var result = await git.RunAsync(worktreePath, ["log", "-1", "--format=%B"], ct).ConfigureAwait(false);
        return result.Ok ? result.StdOut.TrimEnd() : null;
    }

    /// <summary>
    /// Takes the last commit back, leaving its changes staged, as VS Code's Undo
    /// Last Commit does. Nothing in the working tree or the index moves.
    /// </summary>
    /// <remarks>
    /// The first commit has no parent to reset to, so there the branch ref is
    /// deleted instead, which leaves the same thing: no commits, everything
    /// staged.
    /// </remarks>
    public async Task<GitResult> UndoLastAsync(string worktreePath, CancellationToken ct = default)
    {
        var head = await git
            .RunAsync(worktreePath, ["rev-parse", "--verify", "--quiet", "HEAD"], ct)
            .ConfigureAwait(false);
        if (!head.Ok || head.StdOut.Length == 0)
        {
            return new GitResult(1, "", "There is no commit to undo.");
        }

        var parent = await git
            .RunAsync(worktreePath, ["rev-parse", "--verify", "--quiet", "HEAD~1"], ct)
            .ConfigureAwait(false);

        return parent.Ok && parent.StdOut.Length > 0
            ? await git.RunAsync(worktreePath, ["reset", "--soft", "HEAD~1"], ct).ConfigureAwait(false)
            : await git.RunAsync(worktreePath, ["update-ref", "-d", "HEAD"], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes the branch. One with no upstream yet is published to
    /// <c>origin</c>, or the only remote, and tracks it from then on, as VS
    /// Code's Publish Branch does.
    /// </summary>
    public async Task<GitResult> PushAsync(string worktreePath, CancellationToken ct = default)
    {
        var upstream = await git
            .RunAsync(worktreePath, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], ct)
            .ConfigureAwait(false);
        if (upstream.Ok)
        {
            return await git.RunAsync(worktreePath, ["push"], ct).ConfigureAwait(false);
        }

        var remotes = await git.RunAsync(worktreePath, ["remote"], ct).ConfigureAwait(false);
        var names = remotes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var remote = names.Contains("origin") ? "origin" : names.Length == 1 ? names[0] : null;

        return remote is null
            ? new GitResult(1, "", names.Length == 0
                ? "This repository has no remote to push to."
                : "The branch has no upstream, and there is more than one remote to pick from.")
            : await git.RunAsync(worktreePath, ["push", "--set-upstream", remote, "HEAD"], ct).ConfigureAwait(false);
    }

    /// <summary>Pulls the branch's upstream, the way the repository is configured to.</summary>
    public Task<GitResult> PullAsync(string worktreePath, CancellationToken ct = default) =>
        git.RunAsync(worktreePath, ["pull"], ct);
}
