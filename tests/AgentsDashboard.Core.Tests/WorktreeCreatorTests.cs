using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class WorktreeCreatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TempRepo Seeded()
    {
        var repo = new TempRepo();
        repo.Write("a.txt", "a");
        repo.Commit("first");
        return repo;
    }

    [Fact]
    public async Task Lists_local_branches_no_worktree_has_and_remote_ones_with_no_local_namesake()
    {
        using var origin = Seeded();
        origin.Git("branch", "shared");
        origin.Git("branch", "feature/remote-only");

        using var repo = Seeded();
        repo.Git("remote", "add", "origin", origin.Path);
        repo.Git("fetch", "-q", "origin");
        repo.Git("branch", "free");
        repo.Git("branch", "shared");
        repo.Git("branch", "busy");
        repo.Git("worktree", "add", "-q", Path.Combine(repo.Path, ".claude", "worktrees", "busy"), "busy");

        var branches = await new WorktreeCreator(new GitCli()).ListBranchesAsync(repo.Path, Ct);

        Assert.Equal(
            ["free", "origin/feature/remote-only", "shared"],
            branches.Select(b => b.Ref).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Lists_each_group_by_name_whatever_the_case()
    {
        var branches = WorktreeCreator.ParseBranches(
            "refs/heads/Zeta\0\nrefs/heads/alpha\0\nrefs/heads/Beta\0\nrefs/remotes/origin/Remote\0\nrefs/remotes/origin/omega\0\n",
            "origin\n");

        Assert.Equal(
            ["alpha", "Beta", "Zeta", "origin/omega", "origin/Remote"],
            branches.Select(b => b.Ref));
    }

    [Fact]
    public void Splits_a_remote_ref_at_the_longest_remote_name()
    {
        var branches = WorktreeCreator.ParseBranches(
            "refs/remotes/team/origin/main\0\nrefs/remotes/team/HEAD\0\n",
            "team\nteam/origin\n");

        var branch = Assert.Single(branches);
        Assert.Equal(new BranchChoice("main", "team/origin"), branch);
    }

    [Fact]
    public async Task Puts_a_new_worktree_on_an_existing_local_branch_named_after_it()
    {
        using var repo = Seeded();
        repo.Git("branch", "feature/parser");

        var (path, error) = await new WorktreeCreator(new GitCli())
            .CreateAsync(repo.Path, null, new BranchChoice("feature/parser", null), Ct);

        Assert.Null(error);
        Assert.Equal(Path.Combine(repo.Path, ".claude", "worktrees", "feature-parser"), path);
        Assert.Equal("feature/parser", new TempRepoAt(path!).Git("branch", "--show-current"));
    }

    [Fact]
    public async Task A_remote_branch_gets_a_local_branch_that_tracks_it()
    {
        using var origin = Seeded();
        origin.Git("branch", "remote-work");
        using var repo = Seeded();
        repo.Git("remote", "add", "origin", origin.Path);
        repo.Git("fetch", "-q", "origin");

        var (path, error) = await new WorktreeCreator(new GitCli())
            .CreateAsync(repo.Path, "mine", new BranchChoice("remote-work", "origin"), Ct);

        Assert.Null(error);
        var worktree = new TempRepoAt(path!);
        Assert.Equal("remote-work", worktree.Git("branch", "--show-current"));
        Assert.Equal("origin/remote-work", worktree.Git("rev-parse", "--abbrev-ref", "@{upstream}"));
    }

    [Fact]
    public async Task With_no_branch_it_still_makes_a_new_one()
    {
        using var repo = Seeded();

        var (path, error) = await new WorktreeCreator(new GitCli()).CreateAsync(repo.Path, "tidy", ct: Ct);

        Assert.Null(error);
        Assert.Equal("worktree-tidy", new TempRepoAt(path!).Git("branch", "--show-current"));
    }

    /// <summary>Runs git in a worktree the test did not create itself.</summary>
    private sealed class TempRepoAt(string path)
    {
        public string Git(params string[] args)
        {
            var result = new GitCli().RunAsync(path, args).GetAwaiter().GetResult();
            Assert.True(result.Ok, result.Message);
            return result.StdOut.Trim();
        }
    }
}
