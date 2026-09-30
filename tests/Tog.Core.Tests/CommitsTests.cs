using Tog.Core.Git;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

public class CommitsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Commits Commits() => new(new GitCli());

    [Fact]
    public async Task Commits_what_is_staged_and_only_that()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first");
        repo.Write("a.txt", "two\n");
        repo.Write("b.txt", "new\n");
        repo.Git("add", "a.txt");

        var result = await Commits().CommitAsync(repo.Path, "Second\n\nWith a body", ct: Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("Second\n\nWith a body", repo.Git("log", "-1", "--format=%B").TrimEnd());
        Assert.Equal("?? b.txt", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task A_message_that_looks_like_an_option_is_still_the_message()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Git("add", "a.txt");

        var result = await Commits().CommitAsync(repo.Path, "--amend", ct: Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("--amend", repo.Git("log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task Refuses_a_blank_message()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Git("add", "a.txt");

        var result = await Commits().CommitAsync(repo.Path, "  \n", ct: Ct);

        Assert.False(result.Ok);
        Assert.Equal("A  a.txt", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task An_amend_with_no_message_keeps_the_last_one()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first");
        repo.Write("b.txt", "two\n");
        repo.Git("add", "b.txt");

        var result = await Commits().CommitAsync(repo.Path, "", amend: true, ct: Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("1", repo.Git("rev-list", "--count", "HEAD"));
        Assert.Equal("first", repo.Git("log", "-1", "--format=%s"));
        Assert.Equal("", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Undo_leaves_the_commit_staged()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first");
        repo.Write("a.txt", "two\n");
        repo.Commit("second");

        var commits = Commits();
        Assert.Equal("second", await commits.LastMessageAsync(repo.Path, Ct));
        var result = await commits.UndoLastAsync(repo.Path, Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("first", repo.Git("log", "-1", "--format=%s"));
        Assert.Equal("M  a.txt", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Undoing_the_first_commit_leaves_no_commits_and_everything_staged()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first");

        var result = await Commits().UndoLastAsync(repo.Path, Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Throws<InvalidOperationException>(() => repo.Git("rev-parse", "--verify", "HEAD"));
        Assert.Equal("A  a.txt", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Undo_with_no_commits_fails_without_touching_anything()
    {
        using var repo = new TempRepo();

        Assert.False((await Commits().UndoLastAsync(repo.Path, Ct)).Ok);
    }

    [Fact]
    public async Task Push_publishes_a_branch_with_no_upstream_and_tracks_it()
    {
        using var remote = new TempRepo();
        remote.Git("config", "receive.denyCurrentBranch", "ignore");
        using var repo = new TempRepo();
        repo.Git("remote", "add", "origin", remote.Path);
        repo.Write("a.txt", "one\n");
        repo.Commit("first");

        var result = await Commits().PushAsync(repo.Path, Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("origin/main", repo.Git("rev-parse", "--abbrev-ref", "@{u}"));
        Assert.Equal(repo.Git("rev-parse", "HEAD"), remote.Git("rev-parse", "main"));
    }

    [Fact]
    public async Task Push_with_no_remote_says_so()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first");

        var result = await Commits().PushAsync(repo.Path, Ct);

        Assert.False(result.Ok);
        Assert.Contains("no remote", result.Message);
    }
}
