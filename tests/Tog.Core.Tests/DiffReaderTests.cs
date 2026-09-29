using Tog.Core.Git;
using Tog.Core.Model;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

public class DiffReaderTests
{
    private static DiffReader Reader() => new(new GitCli(TimeSpan.FromSeconds(30)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_committed_staged_and_unstaged_work_in_one_pass()
    {
        using var repo = new TempRepo();
        repo.Write("kept.txt", "one\ntwo\nthree\n");
        repo.Write("staged.txt", "a\n");
        repo.Commit("first");

        repo.Write("kept.txt", "one\nCHANGED\nthree\n");
        repo.Write("staged.txt", "a\nb\n");
        repo.Git("add", "staged.txt");

        var set = await Reader().ReadAsync(repo.Path, DiffBase.WorkingTree, ct: Ct);

        Assert.Null(set.Error);
        Assert.Equal(2, set.Files.Count);

        var kept = set.Files.Single(f => f.Path == "kept.txt");
        Assert.Equal(1, kept.Additions);
        Assert.Equal(1, kept.Deletions);

        var staged = set.Files.Single(f => f.Path == "staged.txt");
        Assert.Equal(1, staged.Additions);
    }

    [Fact]
    public async Task Reads_one_file_whole_with_its_changes_in_place()
    {
        using var repo = new TempRepo();
        var lines = Enumerable.Range(1, 20).Select(n => $"line {n}").ToList();
        repo.Write("long.txt", string.Join('\n', lines) + "\n");
        repo.Commit("first");

        lines[9] = "CHANGED";
        repo.Write("long.txt", string.Join('\n', lines) + "\n");

        var file = await Reader().ReadWholeFileAsync(repo.Path, "long.txt", null, DiffBase.WorkingTree, ct: Ct);

        Assert.NotNull(file);
        var all = Assert.Single(file.Hunks).Lines;
        Assert.Equal(Enumerable.Range(1, 20).Select(n => (int?)n), all.Where(l => l.Kind != DiffLineKind.Removed).Select(l => l.NewLine));
        Assert.Equal("line 10", all.Single(l => l.Kind == DiffLineKind.Removed).Text);
        Assert.Equal("CHANGED", all.Single(l => l.Kind == DiffLineKind.Added).Text);
    }

    [Fact]
    public async Task Reads_a_renamed_file_whole_against_its_old_name()
    {
        using var repo = new TempRepo();
        repo.Write("old.txt", "one\ntwo\nthree\nfour\nfive\n");
        repo.Commit("first");
        repo.Git("mv", "old.txt", "new.txt");
        repo.Write("new.txt", "one\ntwo\nTHREE\nfour\nfive\n");
        repo.Git("add", "new.txt");

        var file = await Reader().ReadWholeFileAsync(repo.Path, "new.txt", "old.txt", DiffBase.WorkingTree, ct: Ct);

        Assert.NotNull(file);
        Assert.Equal(1, file.Additions);
        Assert.Equal(1, file.Deletions);
    }

    [Fact]
    public async Task Includes_untracked_files_as_additions()
    {
        using var repo = new TempRepo();
        repo.Write("committed.txt", "x\n");
        repo.Commit("first");
        repo.Write("brand-new.cs", "line one\nline two\n");

        var set = await Reader().ReadAsync(repo.Path, DiffBase.WorkingTree, ct: Ct);

        var file = set.Files.Single(f => f.Path == "brand-new.cs");
        Assert.Equal(FileChangeKind.Untracked, file.Kind);
        Assert.Equal(2, file.Additions);

        // A file the agent just created is exactly the kind of change worth
        // reviewing, and it must be anchorable line by line like any other.
        var lines = file.Hunks.Single().Lines;
        Assert.Equal([1, 2], lines.Select(l => l.NewLine));
    }

    [Fact]
    public async Task Works_in_a_repository_with_no_commits_yet()
    {
        using var repo = new TempRepo();
        repo.Write("first.cs", "hello\n");
        repo.Git("add", "-A");

        var set = await Reader().ReadAsync(repo.Path, DiffBase.WorkingTree, ct: Ct);

        Assert.Null(set.Error);
        var file = Assert.Single(set.Files);
        Assert.Equal("first.cs", file.Path);
        Assert.Equal(1, file.Additions);
    }

    [Fact]
    public async Task Diffs_a_branch_against_the_default_branch()
    {
        using var repo = new TempRepo();
        repo.Write("base.txt", "base\n");
        repo.Commit("first");

        repo.Git("switch", "-q", "-c", "feature");
        repo.Write("on-branch.txt", "branch work\n");
        repo.Commit("branch commit");

        // main moves on after the branch point; the diff must be against the merge
        // base, not against main's tip, or unrelated work shows up as changes.
        repo.Git("switch", "-q", "main");
        repo.Write("elsewhere.txt", "unrelated\n");
        repo.Commit("main moves on");
        repo.Git("switch", "-q", "feature");

        var set = await Reader().ReadAsync(repo.Path, DiffBase.DefaultBranch, ct: Ct);

        Assert.Null(set.Error);
        var file = Assert.Single(set.Files);
        Assert.Equal("on-branch.txt", file.Path);
    }

    [Fact]
    public async Task Reports_git_s_own_message_when_a_ref_does_not_exist()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("first");

        var set = await Reader().ReadAsync(repo.Path, DiffBase.CustomRef, "no-such-ref", Ct);

        Assert.NotNull(set.Error);
        Assert.Empty(set.Files);
    }

    [Fact]
    public async Task Says_so_when_asked_for_a_custom_ref_with_no_ref()
    {
        using var repo = new TempRepo();
        var set = await Reader().ReadAsync(repo.Path, DiffBase.CustomRef, "  ", Ct);

        Assert.Equal("No ref given.", set.Error);
    }

    [Fact]
    public async Task Reads_a_deleted_file()
    {
        using var repo = new TempRepo();
        repo.Write("gone.txt", "one\ntwo\n");
        repo.Commit("first");
        File.Delete(Path.Combine(repo.Path, "gone.txt"));

        var set = await Reader().ReadAsync(repo.Path, DiffBase.WorkingTree, ct: Ct);

        var file = Assert.Single(set.Files);
        Assert.Equal(FileChangeKind.Deleted, file.Kind);
        Assert.Equal(2, file.Deletions);
    }

    [Fact]
    public async Task Handles_a_path_with_a_space_and_a_non_ascii_name()
    {
        using var repo = new TempRepo();
        repo.Write("my folder/café.txt", "one\n");
        repo.Commit("first");
        repo.Write("my folder/café.txt", "one\ntwo\n");

        var set = await Reader().ReadAsync(repo.Path, DiffBase.WorkingTree, ct: Ct);

        // core.quotePath=false is what keeps this from arriving octal-escaped.
        Assert.Equal("my folder/café.txt", Assert.Single(set.Files).Path);
    }
}

public class WorktreeListerIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_a_repository_and_the_worktrees_added_to_it()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("first");
        repo.Git("worktree", "add", "-q", "-b", "side", ".worktrees/side");

        var lister = new WorktreeLister(new GitCli());
        var list = await lister.ListAsync(repo.Path, Ct);

        Assert.Equal(2, list.Count);
        Assert.True(list[0].IsPrimary);
        Assert.Equal("main", list[0].Branch);
        Assert.Equal("side", list[1].Branch);
        Assert.False(list[1].IsPrimary);
    }

    [Fact]
    public async Task Resolves_a_linked_worktree_back_to_the_repository_it_belongs_to()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("first");
        repo.Git("worktree", "add", "-q", "-b", "side", ".worktrees/side");

        var lister = new WorktreeLister(new GitCli());
        var linked = Path.Combine(repo.Path, ".worktrees", "side");

        // This is what groups every worktree of one repo under one heading rather
        // than showing each as a repository of its own. Compared against git's own
        // idea of the toplevel, because macOS resolves /var to /private/var and
        // the two spellings are the same directory.
        var root = await lister.FindPrimaryRootAsync(linked, Ct);
        var expected = WorktreeLister.Native(repo.Git("rev-parse", "--show-toplevel"));

        Assert.Equal(
            expected.TrimEnd(Path.DirectorySeparatorChar),
            root?.TrimEnd(Path.DirectorySeparatorChar));
    }
}
