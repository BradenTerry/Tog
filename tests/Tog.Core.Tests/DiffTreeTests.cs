using Tog.Core.Git;
using Tog.Core.Model;

namespace Tog.Core.Tests;

public class DiffTreeTests
{
    private static DiffFile File(string path, int added = 1, int removed = 0) => new()
    {
        Path = path,
        Hunks =
        [
            new DiffHunk
            {
                Lines =
                [
                    .. Enumerable.Range(1, added).Select(i => new DiffLine(DiffLineKind.Added, null, i, "+")),
                    .. Enumerable.Range(1, removed).Select(i => new DiffLine(DiffLineKind.Removed, i, null, "-")),
                ],
            },
        ],
    };

    private static PathTreeNode<DiffFile> Child(PathTreeNode<DiffFile> node, string name) =>
        node.Children.Single(c => c.Name == name);

    [Fact]
    public void Puts_files_under_the_directories_they_are_in()
    {
        var root = DiffTree.Build([File("src/A.cs"), File("src/B.cs"), File("README.md")]);

        Assert.Equal(["src", "README.md"], root.Children.Select(c => c.Name));

        var src = Child(root, "src");
        Assert.False(src.IsFile);
        Assert.Equal(["A.cs", "B.cs"], src.Children.Select(c => c.Name));
        Assert.True(src.Children[0].IsFile);
    }

    [Fact]
    public void Joins_a_chain_of_directories_that_has_nothing_to_branch_on()
    {
        var root = DiffTree.Build([File("src/Project/Feature/Thing.cs")]);

        // Three rows each holding one child would spend the width on indentation
        // and say nothing.
        var only = Assert.Single(root.Children);
        Assert.Equal("src/Project/Feature", only.Name);
        Assert.Equal("src/Project/Feature", only.Path);
        Assert.Equal("Thing.cs", Assert.Single(only.Children).Name);
    }

    [Fact]
    public void Stops_joining_where_the_tree_branches()
    {
        var root = DiffTree.Build([File("src/App/Program.cs"), File("src/Core/Thing.cs")]);

        var src = Assert.Single(root.Children);
        Assert.Equal("src", src.Name);
        Assert.Equal(["App", "Core"], src.Children.Select(c => c.Name));
    }

    [Fact]
    public void Does_not_join_past_a_directory_that_also_holds_a_file()
    {
        var root = DiffTree.Build([File("src/Directory.Build.props"), File("src/App/Program.cs")]);

        var src = Assert.Single(root.Children);
        Assert.Equal("src", src.Name);
        Assert.Equal(["App", "Directory.Build.props"], src.Children.Select(c => c.Name));
    }

    [Fact]
    public void Puts_directories_before_files_and_sorts_each_by_name()
    {
        var root = DiffTree.Build([File("zeta.txt"), File("alpha.txt"), File("b/x.cs"), File("a/y.cs")]);

        Assert.Equal(["a", "b", "alpha.txt", "zeta.txt"], root.Children.Select(c => c.Name));
    }

    [Fact]
    public void Totals_roll_up_through_the_tree()
    {
        var root = DiffTree.Build(
        [
            File("src/App/A.cs", added: 3, removed: 1),
            File("src/App/B.cs", added: 2, removed: 4),
            File("README.md", added: 1),
        ]);

        Assert.Equal(3, root.FileCount);
        Assert.Equal(6, root.Additions());
        Assert.Equal(5, root.Deletions());

        var app = Child(root, "src/App");
        Assert.Equal(2, app.FileCount);
        Assert.Equal(5, app.Additions());
        Assert.Equal(5, app.Deletions());
    }

    [Fact]
    public void A_file_node_carries_its_diff_and_its_full_path()
    {
        var file = File("src/Deep/Thing.cs");
        var root = DiffTree.Build([file]);

        var node = Assert.Single(Child(root, "src/Deep").Children);
        Assert.Same(file, node.Value);
        Assert.Equal("src/Deep/Thing.cs", node.Path);
    }

    [Fact]
    public void Handles_windows_separators()
    {
        var root = DiffTree.Build([File("src\\App\\Program.cs")]);

        Assert.Equal("src/App", Assert.Single(root.Children).Name);
    }

    [Fact]
    public void An_empty_diff_gives_an_empty_tree() =>
        Assert.Empty(DiffTree.Build([]).Children);
}

public class WorktreeFilesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_tracked_and_untracked_files_but_not_ignored_ones()
    {
        using var repo = new Support.TempRepo();
        repo.Write(".gitignore", "bin/\n*.log\n");
        repo.Write("src/App.cs", "class App;\n");
        repo.Commit("first");
        repo.Write("src/New.cs", "class New;\n");
        repo.Write("bin/Debug/App.dll", "binary-ish\n");
        repo.Write("noise.log", "noise\n");

        var files = await new WorktreeFiles(new GitCli()).ListAsync(repo.Path, Ct);
        var paths = files.Select(f => f.Path).ToList();

        // Listing through git means .gitignore is honoured for free. A worktree's
        // build output is not something you want to browse.
        Assert.Contains("src/App.cs", paths);
        Assert.Contains("src/New.cs", paths);
        Assert.Contains(".gitignore", paths);
        Assert.DoesNotContain("bin/Debug/App.dll", paths);
        Assert.DoesNotContain("noise.log", paths);

        Assert.True(files.Single(f => f.Path == "src/App.cs").Tracked);
        Assert.False(files.Single(f => f.Path == "src/New.cs").Tracked);
    }

    [Fact]
    public async Task Handles_a_path_with_a_space_and_a_non_ascii_name()
    {
        using var repo = new Support.TempRepo();
        repo.Write("my folder/café.txt", "one\n");
        repo.Commit("first");

        var files = await new WorktreeFiles(new GitCli()).ListAsync(repo.Path, Ct);

        Assert.Contains("my folder/café.txt", files.Select(f => f.Path));
    }

    [Fact]
    public void Reads_a_file_as_lines()
    {
        using var repo = new Support.TempRepo();
        repo.Write("a.txt", "one\ntwo\nthree\n");

        var content = new WorktreeFiles(new GitCli()).Read(repo.Path, "a.txt");

        Assert.Null(content.Error);
        Assert.Equal(["one", "two", "three"], content.Lines);
        Assert.False(content.Truncated);
    }

    [Fact]
    public void Marks_a_binary_file_rather_than_showing_it()
    {
        using var repo = new Support.TempRepo();
        File.WriteAllBytes(Path.Combine(repo.Path, "logo.png"), [0x89, 0x50, 0x00, 0x01, 0x02]);

        var content = new WorktreeFiles(new GitCli()).Read(repo.Path, "logo.png");

        Assert.True(content.IsBinary);
        Assert.Empty(content.Lines);
    }

    [Fact]
    public void Says_so_when_the_file_is_gone()
    {
        using var repo = new Support.TempRepo();

        Assert.NotNull(new WorktreeFiles(new GitCli()).Read(repo.Path, "nope.txt").Error);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("src/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public void Refuses_a_path_that_leaves_the_worktree(string path)
    {
        // The path comes from the UI, so it is checked rather than trusted.
        Assert.Null(WorktreeFiles.Resolve("/repo/wt", path));
    }

    [Theory]
    [InlineData("src/App.cs")]
    [InlineData("a/b/c/d.txt")]
    [InlineData("my folder/café.txt")]
    public void Accepts_a_path_inside_the_worktree(string path) =>
        Assert.NotNull(WorktreeFiles.Resolve("/repo/wt", path));

    [Fact]
    public void Arranges_the_files_as_a_tree()
    {
        var tree = WorktreeFiles.Tree(
        [
            new WorktreeFile("src/App/Program.cs", true),
            new WorktreeFile("src/Core/Thing.cs", true),
            new WorktreeFile("README.md", true),
        ]);

        Assert.Equal(["src", "README.md"], tree.Children.Select(c => c.Name));
        Assert.Equal(3, tree.FileCount);
    }
}
