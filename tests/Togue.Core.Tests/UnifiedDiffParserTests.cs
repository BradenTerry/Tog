using Togue.Core.Git;
using Togue.Core.Model;

namespace Togue.Core.Tests;

public class UnifiedDiffParserTests
{
    [Fact]
    public void Numbers_both_sides_of_a_hunk()
    {
        const string diff = """
            diff --git a/src/A.cs b/src/A.cs
            index 111..222 100644
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -10,4 +10,5 @@ public class A
             unchanged one
            -gone
            +new one
            +new two
             unchanged two
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));
        Assert.Equal("src/A.cs", file.Path);
        Assert.Equal(FileChangeKind.Modified, file.Kind);

        var hunk = Assert.Single(file.Hunks);
        Assert.Equal("public class A", hunk.Section);

        var lines = hunk.Lines;
        Assert.Equal(5, lines.Count);

        Assert.Equal((DiffLineKind.Context, 10, 10), (lines[0].Kind, lines[0].OldLine, lines[0].NewLine));
        Assert.Equal((DiffLineKind.Removed, 11, null), (lines[1].Kind, lines[1].OldLine, lines[1].NewLine));
        Assert.Equal((DiffLineKind.Added, null, 11), (lines[2].Kind, lines[2].OldLine, lines[2].NewLine));
        Assert.Equal((DiffLineKind.Added, null, 12), (lines[3].Kind, lines[3].OldLine, lines[3].NewLine));
        Assert.Equal((DiffLineKind.Context, 12, 13), (lines[4].Kind, lines[4].OldLine, lines[4].NewLine));

        Assert.Equal(2, file.Additions);
        Assert.Equal(1, file.Deletions);
    }

    [Fact]
    public void Reads_an_added_file()
    {
        const string diff = """
            diff --git a/new.txt b/new.txt
            new file mode 100644
            index 000..111
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,2 @@
            +one
            +two
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));
        Assert.Equal("new.txt", file.Path);
        Assert.Equal(FileChangeKind.Added, file.Kind);
        Assert.Equal(2, file.Additions);
    }

    [Fact]
    public void Reads_a_deleted_file()
    {
        const string diff = """
            diff --git a/old.txt b/old.txt
            deleted file mode 100644
            index 111..000
            --- a/old.txt
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -one
            -two
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));
        Assert.Equal("old.txt", file.Path);
        Assert.Equal(FileChangeKind.Deleted, file.Kind);
        Assert.Equal(2, file.Deletions);
    }

    [Fact]
    public void Reads_a_rename_and_keeps_the_old_path()
    {
        const string diff = """
            diff --git a/src/Old.cs b/src/New.cs
            similarity index 92%
            rename from src/Old.cs
            rename to src/New.cs
            index 111..222 100644
            --- a/src/Old.cs
            +++ b/src/New.cs
            @@ -1 +1 @@
            -old line
            +new line
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));
        Assert.Equal("src/New.cs", file.Path);
        Assert.Equal("src/Old.cs", file.OldPath);
        Assert.Equal(FileChangeKind.Renamed, file.Kind);
    }

    [Fact]
    public void Marks_a_binary_file_and_gives_it_no_hunks()
    {
        const string diff = """
            diff --git a/logo.png b/logo.png
            index 111..222 100644
            Binary files a/logo.png and b/logo.png differ
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));
        Assert.Equal("logo.png", file.Path);
        Assert.True(file.IsBinary);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void Separates_several_files_and_several_hunks()
    {
        const string diff = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -a
            +A
            @@ -10,2 +10,2 @@
             ctx
            -b
            diff --git a/b.txt b/b.txt
            --- a/b.txt
            +++ b/b.txt
            @@ -5 +5 @@
            -c
            +C
            """;

        var files = UnifiedDiffParser.Parse(diff);

        Assert.Equal(2, files.Count);
        Assert.Equal(2, files[0].Hunks.Count);
        Assert.Equal(10, files[0].Hunks[1].OldStart);
        Assert.Single(files[1].Hunks);
    }

    [Fact]
    public void Handles_a_path_with_a_space_in_it()
    {
        const string diff = """
            diff --git a/dir name/My File.cs b/dir name/My File.cs
            --- a/dir name/My File.cs
            +++ b/dir name/My File.cs
            @@ -1 +1 @@
            -a
            +b
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));
        Assert.Equal("dir name/My File.cs", file.Path);
    }

    [Fact]
    public void Drops_the_tab_separated_field_git_adds_after_a_path_with_a_space()
    {
        // Git appends a tab and a trailing field whenever the path contains a
        // space, so that the path itself stays unambiguous.
        const string diff = "diff --git a/dir name/My File.cs b/dir name/My File.cs\n"
                            + "--- a/dir name/My File.cs\t\n"
                            + "+++ b/dir name/My File.cs\t\n"
                            + "@@ -1 +1 @@\n"
                            + "-a\n"
                            + "+b\n";

        Assert.Equal("dir name/My File.cs", Assert.Single(UnifiedDiffParser.Parse(diff)).Path);
    }

    [Fact]
    public void Ignores_the_no_newline_marker()
    {
        const string diff = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -a
            \ No newline at end of file
            +b
            \ No newline at end of file
            """;

        var hunk = Assert.Single(Assert.Single(UnifiedDiffParser.Parse(diff)).Hunks);
        Assert.Equal(2, hunk.Lines.Count);
    }

    [Fact]
    public void An_empty_diff_yields_no_files() => Assert.Empty(UnifiedDiffParser.Parse(""));
}
