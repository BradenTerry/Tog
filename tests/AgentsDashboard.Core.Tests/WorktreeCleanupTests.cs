using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class WorktreeCleanupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static WorktreeCleanup Cleanup() => new(new GitCli(), new StatusReader(new GitCli()));

    /// <summary>A repo with a commit on main, an ignore file for build output, and one linked worktree on <c>feature</c>.</summary>
    private static (TempRepo Repo, WorktreeInfo Worktree) WithWorktree()
    {
        var repo = new TempRepo();
        repo.Write(".gitignore", "bin/\n.env\n.claude/\n");
        repo.Write("a.txt", "a\n");
        repo.Commit("first");
        repo.Git("worktree", "add", "-q", "-b", "feature", Path.Combine(repo.Path, ".claude", "worktrees", "feature"));
        return (repo, List(repo).Single(w => !w.IsPrimary));
    }

    private static IReadOnlyList<WorktreeInfo> List(TempRepo repo) => WorktreeLister.Parse(repo.Git("worktree", "list", "--porcelain"));

    private static string In(WorktreeInfo worktree, string relative, string content)
    {
        var full = Path.Combine(worktree.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static async Task<WorktreeFacts> Read(TempRepo repo, WorktreeInfo worktree)
    {
        var cleanup = Cleanup();
        var bases = await cleanup.BasesAsync(repo.Path, "main", Ct);
        var others = List(repo).Select(w => w.Path).Where(p => p != worktree.Path).ToList();
        return await cleanup.ReadAsync(worktree, bases, others, Now, Ct);
    }

    [Fact]
    public async Task A_fresh_worktree_has_no_commits_of_its_own_and_is_safe_to_remove()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;

        var facts = await Read(repo, worktree);

        Assert.Equal(MergeState.NoOwnCommits, facts.Merge);
        Assert.Equal(CleanupState.SafeToRemove, WorktreeCleanup.Classify(worktree, facts, agentRunning: false, hostsDashboard: false));
    }

    [Fact]
    public async Task A_commit_nowhere_else_is_work_that_would_be_lost()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        In(worktree, "b.txt", "b\n");
        repo.Git("-C", worktree.Path, "add", "-A");
        repo.Git("-C", worktree.Path, "commit", "-q", "-m", "b");

        var facts = await Read(repo, worktree);

        Assert.Equal(1, facts.OnlyHere);
        Assert.Equal(MergeState.NotMerged, facts.Merge);
        Assert.Equal(CleanupState.HasWork, WorktreeCleanup.Classify(worktree, facts, false, false));
    }

    [Fact]
    public async Task An_untracked_file_is_work_but_an_ignored_one_is_output()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        In(worktree, "bin/app.dll", new string('x', 1000));

        var clean = await Read(repo, worktree);
        Assert.Equal(CleanupState.SafeToRemove, WorktreeCleanup.Classify(worktree, clean, false, false));

        In(worktree, "notes.md", "draft\n");
        var dirty = await Read(repo, worktree);
        Assert.Equal(CleanupState.HasWork, WorktreeCleanup.Classify(worktree, dirty, false, false));
    }

    [Fact]
    public async Task A_fast_forwarded_branch_is_merged()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        In(worktree, "b.txt", "b\n");
        repo.Git("-C", worktree.Path, "add", "-A");
        repo.Git("-C", worktree.Path, "commit", "-q", "-m", "b");
        repo.Git("merge", "-q", "--ff-only", "feature");

        var facts = await Read(repo, worktree);

        Assert.Equal(MergeState.Merged, facts.Merge);
        Assert.Equal(0, facts.OnlyHere);
        Assert.Equal(CleanupState.SafeToRemove, WorktreeCleanup.Classify(worktree, facts, false, false));
    }

    [Fact]
    public async Task A_squash_merged_branch_is_merged_even_though_its_commits_are_its_own()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        In(worktree, "b.txt", "b\n");
        repo.Git("-C", worktree.Path, "add", "-A");
        repo.Git("-C", worktree.Path, "commit", "-q", "-m", "b");
        In(worktree, "c.txt", "c\n");
        repo.Git("-C", worktree.Path, "add", "-A");
        repo.Git("-C", worktree.Path, "commit", "-q", "-m", "c");

        repo.Git("merge", "-q", "--squash", "feature");
        repo.Git("commit", "-q", "-m", "squashed");
        repo.Write("later.txt", "main moved on\n");
        repo.Commit("later");

        var facts = await Read(repo, worktree);

        Assert.Equal(MergeState.SquashMerged, facts.Merge);
        Assert.Equal(CleanupState.SafeToRemove, WorktreeCleanup.Classify(worktree, facts, false, false));
    }

    [Fact]
    public async Task Measures_ignored_output_apart_from_source()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        In(worktree, "bin/app.dll", new string('x', 5000));
        In(worktree, ".env", "SECRET=1\n");

        var facts = await Read(repo, worktree);

        var bin = Assert.Single(facts.Output, o => o.Path == "bin/");
        Assert.Equal(5000, bin.Bytes);
        var env = Assert.Single(facts.Output, o => o.Path == ".env");
        Assert.Equal(5000 + env.Bytes, facts.OutputBytes);
        Assert.True(facts.ProjectBytes > 0);
    }

    [Fact]
    public async Task Splits_the_size_into_project_files_changes_and_output()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        In(worktree, "src/deep/big.txt", new string('x', 3000));
        repo.Git("-C", worktree.Path, "add", "-A");
        repo.Git("-C", worktree.Path, "commit", "-q", "-m", "src");
        In(worktree, "src/deep/new.txt", new string('y', 700));
        In(worktree, "a.txt", new string('z', 200));
        In(worktree, "bin/app.dll", new string('x', 1000));

        var facts = await Read(repo, worktree);

        Assert.Equal(1000, facts.OutputBytes);
        Assert.Equal(900, facts.ChangesBytes);
        Assert.Equal(
            [("src/deep/new.txt", 'U', 700L), ("a.txt", 'M', 200L)],
            facts.Changes.Select(c => (c.Path, c.Kind, c.Bytes)));

        // The top level is the project's files alone: bin/ is output, and the
        // changed a.txt and new.txt are counted as changes, not here.
        Assert.Equal([("src/", 3000L), (".gitignore", facts.Top[1].Bytes)], facts.Top.Select(t => (t.Path, t.Bytes)));
        Assert.Equal(facts.ProjectBytes, facts.Top.Sum(t => t.Bytes));
        Assert.Equal(facts.TotalBytes, facts.ProjectBytes + facts.ChangesBytes + facts.OutputBytes);
    }

    [Fact]
    public void Reads_each_kind_of_change_and_skips_the_old_name_of_a_rename()
    {
        var changes = WorktreeCleanup.ParseChanged(
            " M mod.txt\0A  added.txt\0 D gone.txt\0R  new.txt\0old.txt\0?? fresh.txt\0UU both.txt\0");

        Assert.Equal(
            [("mod.txt", 'M'), ("added.txt", 'A'), ("gone.txt", 'D'), ("new.txt", 'R'), ("fresh.txt", 'U'), ("both.txt", 'C')],
            changes.Select(c => (c.Path, c.Kind)));
    }

    [Fact]
    public async Task The_primary_does_not_count_worktrees_inside_it()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        In(worktree, "bin/big.dll", new string('x', 200_000));
        var primary = List(repo).Single(w => w.IsPrimary);

        var facts = await Read(repo, primary);

        Assert.True(facts.TotalBytes < 200_000, $"counted {facts.TotalBytes} bytes");
        Assert.DoesNotContain(facts.Output, o => o.Path.StartsWith(".claude", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removes_a_worktree_and_its_merged_branch()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        In(worktree, "bin/app.dll", "x");

        var result = await Cleanup().RemoveAsync(repo.Path, worktree, force: false, deleteBranch: true, forceBranch: false, Ct);

        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Equal("", repo.Git("branch", "--list", "feature"));
    }

    [Fact]
    public async Task Refuses_a_dirty_worktree_unless_forced()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        In(worktree, "a.txt", "changed\n");

        var refused = await Cleanup().RemoveAsync(repo.Path, worktree, false, false, false, Ct);
        Assert.False(refused.Ok);
        Assert.True(Directory.Exists(worktree.Path));

        var forced = await Cleanup().RemoveAsync(repo.Path, worktree, true, false, false, Ct);
        Assert.True(forced.Ok, forced.Message);
        Assert.False(Directory.Exists(worktree.Path));
    }

    [Fact]
    public async Task Unlocks_a_locked_worktree_to_remove_it_and_locks_it_again_when_that_fails()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        repo.Git("worktree", "lock", "--reason", "busy", worktree.Path);
        In(worktree, "a.txt", "changed\n");
        var locked = List(repo).Single(w => !w.IsPrimary);

        var refused = await Cleanup().RemoveAsync(repo.Path, locked, false, false, false, Ct);
        Assert.False(refused.Ok);
        Assert.True(List(repo).Single(w => !w.IsPrimary).Locked);

        var forced = await Cleanup().RemoveAsync(repo.Path, locked, true, false, false, Ct);
        Assert.True(forced.Ok, forced.Message);
    }

    [Fact]
    public async Task Keeps_an_unmerged_branch_unless_told_otherwise()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        In(worktree, "b.txt", "b\n");
        repo.Git("-C", worktree.Path, "add", "-A");
        repo.Git("-C", worktree.Path, "commit", "-q", "-m", "b");

        var result = await Cleanup().RemoveAsync(repo.Path, worktree, false, deleteBranch: true, forceBranch: false, Ct);

        Assert.True(result.Ok);
        Assert.Contains("Kept branch feature", result.Message);
        Assert.NotEqual("", repo.Git("branch", "--list", "feature"));
    }

    [Fact]
    public async Task Never_removes_the_primary()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "a");
        repo.Commit("first");

        var result = await Cleanup().RemoveAsync(repo.Path, List(repo)[0], true, false, false, Ct);

        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(repo.Path, "a.txt")));
    }

    [Fact]
    public async Task Prunes_a_worktree_whose_directory_was_deleted()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        Directory.Delete(worktree.Path, recursive: true);
        Assert.True(List(repo).Single(w => !w.IsPrimary).Prunable);

        var result = await Cleanup().PruneAsync(repo.Path, Ct);

        Assert.True(result.Ok);
        Assert.Single(List(repo));
    }

    [Theory]
    [InlineData(true, false, false, CleanupState.Primary)]
    [InlineData(false, true, false, CleanupState.Dashboard)]
    [InlineData(false, false, true, CleanupState.AgentRunning)]
    public void Protection_comes_before_anything_git_says(bool primary, bool dashboard, bool agent, CleanupState expected)
    {
        var worktree = new WorktreeInfo { Path = "/r/w", Name = "w", IsPrimary = primary };
        var safe = new WorktreeFacts { Status = new GitStatusInfo(), Merge = MergeState.Merged };

        Assert.Equal(expected, WorktreeCleanup.Classify(worktree, safe, agent, dashboard));
    }

    [Fact]
    public void Clean_pushed_and_unmerged_is_its_own_state()
    {
        var worktree = new WorktreeInfo { Path = "/r/w", Name = "w" };
        var facts = new WorktreeFacts { Status = new GitStatusInfo(), Merge = MergeState.NotMerged };

        Assert.Equal(CleanupState.NotMerged, WorktreeCleanup.Classify(worktree, facts, false, false));
    }
}
