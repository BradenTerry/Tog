using Togue.Core.Git;
using Togue.Core.Tests.Support;

namespace Togue.Core.Tests;

public class StagingParseTests
{
    [Fact]
    public void Tells_staged_from_unstaged_from_both()
    {
        const string stdout = """
            # branch.head main
            1 M. N... 100644 100644 100644 abc def staged-only.cs
            1 .M N... 100644 100644 100644 abc def unstaged-only.cs
            1 MM N... 100644 100644 100644 abc def both.cs
            ? brand-new.cs
            """;

        var files = Staging.Parse(stdout);

        // The two characters are two different questions: the index against HEAD,
        // and the working tree against the index.
        Assert.Equal(new FileStage(true, false, false), files["staged-only.cs"]);
        Assert.Equal(new FileStage(false, true, false), files["unstaged-only.cs"]);
        Assert.Equal(new FileStage(true, true, false), files["both.cs"]);
        Assert.Equal(new FileStage(false, true, true), files["brand-new.cs"]);
    }

    [Fact]
    public void Reads_the_new_path_of_a_rename()
    {
        const string stdout = "2 R. N... 100644 100644 100644 abc def R100 new/Path.cs\told/Path.cs";

        var file = Assert.Single(Staging.Parse(stdout));

        Assert.Equal("new/Path.cs", file.Key);
        Assert.True(file.Value.Staged);
    }

    [Fact]
    public void An_unmerged_path_counts_as_changed_on_both_sides()
    {
        const string stdout = "u UU N... 100644 100644 100644 100644 a b c src/Conflict.cs";

        Assert.Equal(new FileStage(true, true, false), Staging.Parse(stdout)["src/Conflict.cs"]);
    }

    [Fact]
    public void Handles_a_path_with_a_space_in_it()
    {
        const string stdout = "1 .M N... 100644 100644 100644 abc def my folder/My File.cs";

        Assert.True(Staging.Parse(stdout).ContainsKey("my folder/My File.cs"));
    }

    [Fact]
    public void A_clean_worktree_has_no_files() => Assert.Empty(Staging.Parse("# branch.head main"));

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void Fully_staged_means_staged_with_nothing_left_over(bool staged, bool unstaged, bool expected) =>
        Assert.Equal(expected, new FileStage(staged, unstaged, false).IsFullyStaged);
}

public class StagingIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Staging Stage() => new(new GitCli());

    [Fact]
    public async Task Stages_and_unstages_one_file()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Write("b.txt", "one\n");
        repo.Commit("first");
        repo.Write("a.txt", "two\n");
        repo.Write("b.txt", "two\n");

        var staging = Stage();
        await staging.StageAsync(repo.Path, ["a.txt"], Ct);

        var afterStage = await staging.ReadAsync(repo.Path, Ct);
        Assert.True(afterStage["a.txt"].IsFullyStaged);
        Assert.False(afterStage["b.txt"].Staged);

        await staging.UnstageAsync(repo.Path, ["a.txt"], Ct);

        var afterUnstage = await staging.ReadAsync(repo.Path, Ct);
        Assert.False(afterUnstage["a.txt"].Staged);
        Assert.True(afterUnstage["a.txt"].Unstaged);
    }

    [Fact]
    public async Task Stages_a_new_file()
    {
        using var repo = new TempRepo();
        repo.Write("committed.txt", "x\n");
        repo.Commit("first");
        repo.Write("brand-new.cs", "hello\n");

        var staging = Stage();
        Assert.True((await staging.ReadAsync(repo.Path, Ct))["brand-new.cs"].Untracked);

        await staging.StageAsync(repo.Path, ["brand-new.cs"], Ct);

        var after = await staging.ReadAsync(repo.Path, Ct);
        Assert.True(after["brand-new.cs"].Staged);
        Assert.False(after["brand-new.cs"].Untracked);
    }

    [Fact]
    public async Task Unstages_in_a_repository_with_no_commits()
    {
        using var repo = new TempRepo();
        repo.Write("first.cs", "hello\n");
        repo.Git("add", "-A");

        // There is no HEAD to restore the index from, so unstaging has to take the
        // path out of the index instead.
        var staging = Stage();
        Assert.True((await staging.ReadAsync(repo.Path, Ct))["first.cs"].Staged);

        await staging.UnstageAsync(repo.Path, ["first.cs"], Ct);

        Assert.True((await staging.ReadAsync(repo.Path, Ct))["first.cs"].Untracked);
    }

    [Fact]
    public async Task Stages_and_unstages_everything()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first");
        repo.Write("a.txt", "two\n");
        repo.Write("b.txt", "new\n");

        var staging = Stage();
        await staging.StageAllAsync(repo.Path, Ct);

        var staged = await staging.ReadAsync(repo.Path, Ct);
        Assert.All(staged.Values, f => Assert.True(f.Staged));

        await staging.UnstageAllAsync(repo.Path, Ct);

        var unstaged = await staging.ReadAsync(repo.Path, Ct);
        Assert.All(unstaged.Values, f => Assert.False(f.Staged));
    }

    [Fact]
    public async Task Handles_a_path_with_a_space_and_a_non_ascii_name()
    {
        using var repo = new TempRepo();
        repo.Write("base.txt", "x\n");
        repo.Commit("first");
        repo.Write("my folder/café.txt", "one\n");

        var staging = Stage();
        await staging.StageAsync(repo.Path, ["my folder/café.txt"], Ct);

        Assert.True((await staging.ReadAsync(repo.Path, Ct))["my folder/café.txt"].Staged);
    }

    [Fact]
    public async Task Discarding_keeps_what_is_staged_and_drops_the_rest()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first");
        repo.Write("a.txt", "two\n");
        var staging = Stage();
        await staging.StageAsync(repo.Path, ["a.txt"], Ct);
        repo.Write("a.txt", "three\n");

        var result = await staging.DiscardAsync(repo.Path, ["a.txt"], Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
        Assert.True((await staging.ReadAsync(repo.Path, Ct))["a.txt"].IsFullyStaged);
    }

    [Fact]
    public async Task Discarding_a_new_file_deletes_it_and_leaves_the_others()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first");
        repo.Write("new/made.cs", "hello\n");
        repo.Write("new/kept.cs", "hello\n");
        repo.Write("a.txt", "two\n");

        var result = await Stage().DiscardAsync(repo.Path, ["new/made.cs"], Ct);

        Assert.True(result.Ok, result.Message);
        Assert.False(File.Exists(Path.Combine(repo.Path, "new", "made.cs")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "new", "kept.cs")));
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
    }

    [Fact]
    public async Task Discarding_a_deletion_brings_the_file_back()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first");
        File.Delete(Path.Combine(repo.Path, "a.txt"));

        var result = await Stage().DiscardAsync(repo.Path, ["a.txt"], Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
    }

    [Fact]
    public async Task Discarding_in_a_repository_with_no_commits_restores_from_the_index()
    {
        using var repo = new TempRepo();
        repo.Write("first.cs", "hello\n");
        repo.Git("add", "-A");
        repo.Write("first.cs", "changed\n");

        var result = await Stage().DiscardAsync(repo.Path, ["first.cs"], Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(repo.Path, "first.cs")));
    }

    [Fact]
    public async Task Staging_nothing_does_nothing()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first");

        Assert.True((await Stage().StageAsync(repo.Path, [], Ct)).Ok);
    }
}
