using Togue.Core.Git;

namespace Togue.Core.Tests;

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

        Assert.Equal(WorktreeLister.Native("/repo"), list[0].Path);
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

        var worktree = WorktreeLister.Parse(stdout)[0];
        Assert.True(worktree.Locked);
        Assert.Equal("claude session in progress", worktree.LockReason);
    }

    [Fact]
    public void Reads_a_worktree_whose_directory_is_gone_as_prunable()
    {
        const string stdout = """
            worktree /repo
            HEAD abc
            branch refs/heads/main

            worktree /repo/gone
            HEAD def
            branch refs/heads/old
            prunable gitdir file points to non-existent location

            """;

        var list = WorktreeLister.Parse(stdout);
        Assert.False(list[0].Prunable);
        Assert.True(list[1].Prunable);
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

