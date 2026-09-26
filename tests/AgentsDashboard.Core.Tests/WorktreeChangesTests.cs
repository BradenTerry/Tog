using AgentsDashboard.Core.Git;

namespace AgentsDashboard.Core.Tests;

public class WorktreeChangesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "wt");

    private static string At(params string[] parts) => Path.Combine([Root, .. parts]);

    [Fact]
    public void A_source_file_is_reported_by_its_worktree_path() =>
        Assert.Equal("src/App/Program.cs", WorktreeChanges.Classify(Root, At("src", "App", "Program.cs")));

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    public void Build_output_and_packages_are_not_reported(string folder) =>
        Assert.Null(WorktreeChanges.Classify(Root, At("src", folder, "x.dll")));

    [Theory]
    [InlineData("index", ".git")]
    [InlineData("HEAD", ".git")]
    [InlineData("ORIG_HEAD", null)]
    public void Inside_git_only_the_index_and_head_count(string name, string? expected) =>
        Assert.Equal(expected, WorktreeChanges.Classify(Root, At(".git", name)));

    [Fact]
    public void Git_objects_are_not_reported() =>
        Assert.Null(WorktreeChanges.Classify(Root, At(".git", "objects", "ab", "cdef")));

    [Fact]
    public void A_linked_worktree_s_index_outside_it_counts_as_git()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "main", ".git", "worktrees", "wt", "index");
        Assert.Equal(".git", WorktreeChanges.Classify(Root, elsewhere));
    }

    [Fact]
    public void A_linked_worktree_names_its_git_directory_in_its_dot_git_file()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var gitDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "main", ".git", "worktrees", "wt")).FullName;
            var worktree = Directory.CreateDirectory(Path.Combine(dir.FullName, "wt")).FullName;
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + gitDir + "\n");

            Assert.Equal(Path.GetFullPath(gitDir), WorktreeChanges.GitDirectoryOf(worktree));
            Assert.Null(WorktreeChanges.GitDirectoryOf(gitDir));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task An_edit_is_reported_once_after_the_worktree_goes_quiet()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var reports = new List<IReadOnlySet<string>>();
            var seen = new TaskCompletionSource();
            using var watcher = new WorktreeChanges(dir.FullName, changed =>
            {
                lock (reports)
                {
                    reports.Add(changed);
                }

                seen.TrySetResult();
            });

            // Several writes in a burst, as an agent's edit is.
            for (var i = 0; i < 5; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(dir.FullName, "a.txt"), "edit " + i, TestContext.Current.CancellationToken);
            }

            await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Task.Delay(WorktreeChanges.Quiet * 3, TestContext.Current.CancellationToken);

            lock (reports)
            {
                Assert.Single(reports);
                Assert.Contains("a.txt", reports[0]);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
