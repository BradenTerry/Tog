using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Presentation;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class ChangeMarksTests
{
    private static DiffReader Reader() => new(new GitCli(TimeSpan.FromSeconds(30)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyList<ChangeMark>> MarksFor(TempRepo repo, string file) =>
        ChangeMarks.From(await Reader().ReadFileAsync(repo.Path, file, DiffBase.WorkingTree, null, Ct));

    [Fact]
    public async Task An_unchanged_file_has_no_marks()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\ntwo\n");
        repo.Commit("first");

        Assert.Empty(await MarksFor(repo, "a.txt"));
    }

    [Fact]
    public async Task New_lines_are_added()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\ntwo\n");
        repo.Commit("first");
        repo.Write("a.txt", "one\nnew a\nnew b\ntwo\n");

        Assert.Equal(
            [new ChangeMark(2, ChangeMarkKind.Added), new ChangeMark(3, ChangeMarkKind.Added)],
            await MarksFor(repo, "a.txt"));
    }

    [Fact]
    public async Task A_replaced_line_is_modified()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\ntwo\nthree\n");
        repo.Commit("first");
        repo.Write("a.txt", "one\nTWO\nthree\n");

        Assert.Equal([new ChangeMark(2, ChangeMarkKind.Modified)], await MarksFor(repo, "a.txt"));
    }

    [Fact]
    public async Task A_deletion_hangs_on_the_line_above_the_gap()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\ntwo\nthree\nfour\n");
        repo.Commit("first");
        repo.Write("a.txt", "one\nfour\n");

        Assert.Equal([new ChangeMark(1, ChangeMarkKind.Deleted)], await MarksFor(repo, "a.txt"));
    }

    [Fact]
    public async Task A_deletion_at_the_top_is_marked_on_the_first_line()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\ntwo\nthree\n");
        repo.Commit("first");
        repo.Write("a.txt", "three\n");

        Assert.Equal([new ChangeMark(1, ChangeMarkKind.Deleted)], await MarksFor(repo, "a.txt"));
    }

    [Fact]
    public async Task An_untracked_file_is_added_on_every_line()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first");
        repo.Write("new.txt", "x\ny\nz");

        var marks = await MarksFor(repo, "new.txt");

        Assert.Equal([1, 2, 3], marks.Select(m => m.Line));
        Assert.All(marks, m => Assert.Equal(ChangeMarkKind.Added, m.Kind));
    }

    [Fact]
    public async Task The_first_mark_is_the_first_changed_line()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n");
        repo.Commit("first");
        repo.Write("a.txt", "1\n2\n3\n4\n5\nsix\n7\n8\n9\nten\n");

        Assert.Equal(6, ChangeMarks.First(await MarksFor(repo, "a.txt")));
    }

    [Fact]
    public void No_diff_is_no_marks() => Assert.Empty(ChangeMarks.From(null));
}
