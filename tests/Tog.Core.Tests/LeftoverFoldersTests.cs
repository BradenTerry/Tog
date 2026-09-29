using Tog.Core.Git;
using Tog.Core.Model;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

public class LeftoverFoldersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WorktreeCleanup Cleanup() => new(new GitCli(), new StatusReader(new GitCli()));

    private static IReadOnlyList<WorktreeInfo> List(TempRepo repo) => WorktreeLister.Parse(repo.Git("worktree", "list", "--porcelain"));

    /// <summary>A repo with one linked worktree in the CLI's place for them, holding an ignored build output file.</summary>
    private static (TempRepo Repo, WorktreeInfo Worktree) WithWorktree()
    {
        var repo = new TempRepo();
        repo.Write(".gitignore", "bin/\n.claude/\n");
        repo.Write("a.txt", "a\n");
        repo.Commit("first");
        repo.Git("worktree", "add", "-q", "-b", "feature", Path.Combine(LeftoverFolders.WorktreesFolder(repo.Path), "feature"));
        var worktree = List(repo).Single(w => !w.IsPrimary);
        Directory.CreateDirectory(Path.Combine(worktree.Path, "bin"));
        File.WriteAllText(Path.Combine(worktree.Path, "bin", "out.dll"), "12345");
        return (repo, worktree);
    }

    [Fact]
    public void Finds_a_folder_git_does_not_list_and_says_what_is_in_it()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;
        var stray = Path.Combine(LeftoverFolders.WorktreesFolder(repo.Path), "gone");
        Directory.CreateDirectory(Path.Combine(stray, "src"));
        File.WriteAllText(Path.Combine(stray, "src", "left.txt"), "abc");

        var found = Assert.Single(LeftoverFolders.Find(repo.Path, List(repo).Select(w => w.Path)));

        Assert.Equal("gone", Path.GetFileName(found.Path));
        Assert.Equal(1, found.Files);
        Assert.Equal(3, found.Bytes);
        Assert.Equal(["src/left.txt"], found.Sample);
        Assert.True(Directory.Exists(worktree.Path));
    }

    [Fact]
    public void A_repository_with_no_worktree_folder_has_no_leftovers()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("first");

        Assert.Empty(LeftoverFolders.Find(repo.Path, List(repo).Select(w => w.Path)));
    }

    [Fact]
    public async Task Deletes_a_leftover_folder()
    {
        var (repo, _) = WithWorktree();
        using var __ = repo;
        var stray = Path.Combine(LeftoverFolders.WorktreesFolder(repo.Path), "gone");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "left.txt"), "abc");
        var leftover = Assert.Single(LeftoverFolders.Find(repo.Path, List(repo).Select(w => w.Path)));

        var result = await Cleanup().DeleteLeftoverAsync(leftover, Ct);

        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(stray));
    }

    [Fact]
    public async Task Never_deletes_a_folder_git_lists_as_a_worktree()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;

        var result = await Cleanup().DeleteLeftoverAsync(LeftoverFolders.Measure(repo.Path, worktree.Path), Ct);

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(worktree.Path));
    }

    [Fact]
    public async Task A_clean_removal_leaves_nothing_to_report()
    {
        var (repo, worktree) = WithWorktree();
        using var _ = repo;

        var result = await Cleanup().RemoveAsync(repo.Path, worktree, force: false, deleteBranch: false, forceBranch: false, Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Null(result.Leftover);
        Assert.Empty(LeftoverFolders.Find(repo.Path, List(repo).Select(w => w.Path)));
    }

    // Only Windows refuses to delete a folder with a file in it held open; macOS
    // and Linux delete it out from under the holder. See LeftoverFolders.

    [Fact]
    public async Task A_removal_blocked_by_an_open_file_reports_the_folder_left_behind()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows keeps a folder whose file is held open.");
        var (repo, worktree) = WithWorktree();
        using var _ = repo;

        CleanupResult result;
        using (new FileStream(Path.Combine(worktree.Path, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            result = await Cleanup().RemoveAsync(repo.Path, worktree, force: false, deleteBranch: true, forceBranch: false, Ct);
        }

        // Git unregistered it, so the removal happened, and the branch with it.
        Assert.True(result.Ok, result.Message);
        Assert.DoesNotContain(List(repo), w => w.Branch == "feature");
        Assert.Equal("", repo.Git("branch", "--list", "feature"));

        // What is left is reported, not hidden.
        var leftover = Assert.IsType<LeftoverFolder>(result.Leftover);
        Assert.Contains(leftover.Path, result.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));
        Assert.Single(LeftoverFolders.Find(repo.Path, List(repo).Select(w => w.Path)));

        // Once the program lets go, it can be deleted from the list.
        var deleted = await Cleanup().DeleteLeftoverAsync(leftover, Ct);
        Assert.True(deleted.Ok, deleted.Message);
        Assert.False(Directory.Exists(worktree.Path));
    }

    [Fact]
    public async Task A_removal_blocked_for_a_moment_finishes_on_its_own()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows keeps a folder whose file is held open.");
        var (repo, worktree) = WithWorktree();
        using var _ = repo;

        // Held for a moment, as antivirus or the indexer would, and let go while
        // the removal is retrying the delete.
        var held = new FileStream(Path.Combine(worktree.Path, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var release = Task.Run(async () =>
        {
            await Task.Delay(700, Ct);
            await held.DisposeAsync();
        }, Ct);

        var result = await Cleanup().RemoveAsync(repo.Path, worktree, force: false, deleteBranch: false, forceBranch: false, Ct);
        await release;

        Assert.True(result.Ok, result.Message);
        Assert.Null(result.Leftover);
        Assert.False(Directory.Exists(worktree.Path));
    }

    [Fact]
    public async Task A_leftover_still_held_open_is_not_reported_as_deleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows keeps a folder whose file is held open.");
        var (repo, _) = WithWorktree();
        using var __ = repo;
        var stray = Path.Combine(LeftoverFolders.WorktreesFolder(repo.Path), "gone");
        Directory.CreateDirectory(stray);
        var file = Path.Combine(stray, "left.txt");
        File.WriteAllText(file, "abc");
        var leftover = Assert.Single(LeftoverFolders.Find(repo.Path, List(repo).Select(w => w.Path)));

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var result = await Cleanup().DeleteLeftoverAsync(leftover, Ct);

            Assert.False(result.Ok);
            Assert.Contains("another program", result.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(stray));
        }
    }
}
