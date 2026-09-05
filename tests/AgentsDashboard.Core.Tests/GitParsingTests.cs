using AgentsDashboard.Core.Git;

namespace AgentsDashboard.Core.Tests;

public class WorktreeListerTests
{
    [Fact]
    public void Parses_a_primary_and_a_linked_worktree()
    {
        const string stdout = """
            worktree /repo
            HEAD abc123
            branch refs/heads/main

            worktree /repo/.claude/worktrees/lively-badger
            HEAD def456
            branch refs/heads/feature-x
            locked

            """;

        var list = WorktreeLister.Parse(stdout);

        Assert.Equal(2, list.Count);

        Assert.Equal("/repo", list[0].Path);
        Assert.Equal("repo", list[0].Name);
        Assert.Equal("main", list[0].Branch);
        Assert.True(list[0].IsPrimary);
        Assert.False(list[0].Locked);

        Assert.Equal("lively-badger", list[1].Name);
        Assert.Equal("feature-x", list[1].Branch);
        Assert.False(list[1].IsPrimary);
        Assert.True(list[1].Locked);
    }

    [Fact]
    public void Marks_a_detached_worktree_and_gives_it_no_branch()
    {
        const string stdout = """
            worktree /repo
            HEAD abc123
            branch refs/heads/main

            worktree /repo/wt
            HEAD def456
            detached

            """;

        var list = WorktreeLister.Parse(stdout);

        Assert.True(list[1].Detached);
        Assert.Null(list[1].Branch);
    }

    [Fact]
    public void Reads_a_lock_that_carries_a_reason()
    {
        const string stdout = """
            worktree /repo/wt
            HEAD abc
            branch refs/heads/x
            locked claude session in progress

            """;

        Assert.True(WorktreeLister.Parse(stdout)[0].Locked);
    }

    [Fact]
    public void Handles_a_final_record_with_no_trailing_blank_line()
    {
        const string stdout = "worktree /repo\nHEAD abc\nbranch refs/heads/main";
        Assert.Single(WorktreeLister.Parse(stdout));
    }
}

public class StatusReaderTests
{
    [Fact]
    public void Counts_staged_unstaged_and_untracked_separately()
    {
        const string stdout = """
            # branch.oid abc123
            # branch.head feature
            # branch.upstream origin/feature
            # branch.ab +3 -1
            1 .M N... 100644 100644 100644 abc def src/A.cs
            1 M. N... 100644 100644 100644 abc def src/B.cs
            1 MM N... 100644 100644 100644 abc def src/C.cs
            ? src/New.cs
            ? src/Other.cs
            """;

        var status = StatusReader.Parse(stdout);

        // A.cs and C.cs are modified in the working tree; B.cs and C.cs are staged.
        Assert.Equal(2, status.Changed);
        Assert.Equal(2, status.Staged);
        Assert.Equal(2, status.Untracked);
        Assert.Equal(3, status.Ahead);
        Assert.Equal(1, status.Behind);
        Assert.Equal("origin/feature", status.Upstream);
        Assert.False(status.IsClean);
    }

    [Fact]
    public void Reads_a_clean_worktree()
    {
        const string stdout = """
            # branch.oid abc123
            # branch.head main
            """;

        var status = StatusReader.Parse(stdout);

        Assert.True(status.IsClean);
        Assert.Equal(0, status.Ahead);
        Assert.Null(status.Upstream);
    }

    [Fact]
    public void Counts_a_rename_and_an_unmerged_path()
    {
        const string stdout = """
            2 R. N... 100644 100644 100644 abc def R100 new/Path.cs	old/Path.cs
            u UU N... 100644 100644 100644 100644 a b c src/Conflict.cs
            """;

        var status = StatusReader.Parse(stdout);

        Assert.Equal(1, status.Staged);
        Assert.Equal(1, status.Changed);
    }
}

public class WorktreeRouteTests
{
    private const string Primary = "/Users/b/Projects/Soar";
    private const string Linked = "/Users/b/Projects/Soar/.claude/worktrees/lively-badger";

    [Fact]
    public void Round_trips_a_path()
    {
        Assert.Equal(Primary, AgentsDashboard.Core.Model.WorktreeRoute.PathOf(
            AgentsDashboard.Core.Model.WorktreeRoute.For(Primary)));
    }

    [Fact]
    public void Round_trips_a_path_with_a_tab()
    {
        Assert.Equal(Linked, AgentsDashboard.Core.Model.WorktreeRoute.PathOf(
            AgentsDashboard.Core.Model.WorktreeRoute.For(Linked, "changes")));
    }

    [Fact]
    public void A_linked_worktree_does_not_also_select_the_primary_it_lives_inside()
    {
        var url = AgentsDashboard.Core.Model.WorktreeRoute.For(Linked, "tests");

        Assert.True(AgentsDashboard.Core.Model.WorktreeRoute.Shows(url, Linked));
        Assert.False(AgentsDashboard.Core.Model.WorktreeRoute.Shows(url, Primary));
    }

    [Fact]
    public void The_primary_selects_only_itself()
    {
        var url = AgentsDashboard.Core.Model.WorktreeRoute.For(Primary);

        Assert.True(AgentsDashboard.Core.Model.WorktreeRoute.Shows(url, Primary));
        Assert.False(AgentsDashboard.Core.Model.WorktreeRoute.Shows(url, Linked));
    }

    [Theory]
    [InlineData("")]
    [InlineData("repos")]
    [InlineData("worktree/")]
    public void Reads_nothing_out_of_a_url_that_is_not_a_worktree_page(string url) =>
        Assert.Null(AgentsDashboard.Core.Model.WorktreeRoute.PathOf(url));

    [Fact]
    public void Ignores_a_query_string() =>
        Assert.Equal(Primary, AgentsDashboard.Core.Model.WorktreeRoute.PathOf(
            AgentsDashboard.Core.Model.WorktreeRoute.For(Primary) + "?x=1"));

    [Fact]
    public void Handles_a_path_with_a_space_in_it()
    {
        const string spaced = "/Users/b/My Projects/repo";
        Assert.Equal(spaced, AgentsDashboard.Core.Model.WorktreeRoute.PathOf(
            AgentsDashboard.Core.Model.WorktreeRoute.For(spaced)));
    }
}
