using System.Text;
using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Presentation;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class WorktreeFileEditingTests
{
    private static WorktreeFiles Files() => new(new GitCli());

    [Fact]
    public void Reads_a_file_back_as_one_piece_of_text()
    {
        using var dir = new TempDir();
        dir.File("a.txt", "one\ntwo\n");

        var file = Files().ReadText(dir.Path, "a.txt");

        Assert.Equal("one\ntwo\n", file.Text);
        Assert.Equal("\n", file.Eol);
        Assert.False(file.HasBom);
        Assert.True(file.Editable);
    }

    [Fact]
    public void Reports_the_line_ending_a_windows_file_uses_and_hands_back_newlines()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "a.txt"), "one\r\ntwo\r\n");

        var file = Files().ReadText(dir.Path, "a.txt");

        // The editor only ever sees "\n"; the file's own ending comes back beside it
        // so saving does not rewrite every line.
        Assert.Equal("one\ntwo\n", file.Text);
        Assert.Equal("\r\n", file.Eol);
    }

    [Fact]
    public void Strips_a_byte_order_mark_and_remembers_it_was_there()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(
            Path.Combine(dir.Path, "a.txt"),
            [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("hi\n")]);

        var file = Files().ReadText(dir.Path, "a.txt");

        Assert.Equal("hi\n", file.Text);
        Assert.True(file.HasBom);
    }

    [Fact]
    public void The_stamp_changes_when_the_file_changes()
    {
        using var dir = new TempDir();
        dir.File("a.txt", "one\n");
        var files = Files();

        var before = files.ReadText(dir.Path, "a.txt").Stamp;
        dir.File("a.txt", "two\n");
        var after = files.ReadText(dir.Path, "a.txt").Stamp;

        Assert.NotEqual(before, after);
        Assert.Equal(64, before.Length);
    }

    [Fact]
    public void Refuses_to_save_over_a_change_made_since_the_file_was_opened()
    {
        using var dir = new TempDir();
        var full = dir.File("a.txt", "one\n");
        var files = Files();
        var stamp = files.ReadText(dir.Path, "a.txt").Stamp;

        // The agent working in the worktree got there first.
        dir.File("a.txt", "agent wrote this\n");

        var result = files.Write(dir.Path, "a.txt", "mine\n", stamp);

        Assert.False(result.Ok);
        Assert.True(result.Conflict);
        Assert.Equal("The file changed on disk since you opened it.", result.Message);
        Assert.Equal("agent wrote this\n", File.ReadAllText(full));
    }

    [Fact]
    public void Saving_an_existing_file_with_no_stamp_at_all_is_a_conflict()
    {
        using var dir = new TempDir();
        dir.File("a.txt", "one\n");

        Assert.True(Files().Write(dir.Path, "a.txt", "mine\n", expectedStamp: null).Conflict);
    }

    [Fact]
    public void Forcing_the_save_overwrites_the_change()
    {
        using var dir = new TempDir();
        var full = dir.File("a.txt", "one\n");

        var result = Files().Write(dir.Path, "a.txt", "mine\n", "stale", force: true);

        Assert.True(result.Ok);
        Assert.Equal("Saved.", result.Message);
        Assert.Equal("mine\n", File.ReadAllText(full));
        Assert.NotNull(result.Stamp);
    }

    [Fact]
    public void Saving_recreates_the_line_endings_and_the_byte_order_mark()
    {
        using var dir = new TempDir();
        var full = Path.Combine(dir.Path, "a.txt");
        File.WriteAllBytes(
            full,
            [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("one\r\n")]);

        var files = Files();
        var file = files.ReadText(dir.Path, "a.txt");

        Assert.True(files.Write(dir.Path, "a.txt", "one\ntwo\n", file.Stamp, file.Eol, file.HasBom).Ok);

        var bytes = File.ReadAllBytes(full);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Equal("one\r\ntwo\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public void Saving_creates_a_file_that_is_not_there_yet()
    {
        using var dir = new TempDir();

        var result = Files().Write(dir.Path, "new/deep.txt", "hello\n", expectedStamp: null);

        Assert.True(result.Ok);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(dir.Path, "new", "deep.txt")));
    }

    [Fact]
    public void The_new_stamp_matches_what_a_read_would_give()
    {
        using var dir = new TempDir();
        var files = Files();

        var written = files.Write(dir.Path, "a.txt", "hello\n", expectedStamp: null);

        Assert.Equal(files.ReadText(dir.Path, "a.txt").Stamp, written.Stamp);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("nested/../../escape.txt")]
    public void Refuses_a_path_that_climbs_out_of_the_worktree(string relative)
    {
        using var dir = new TempDir();

        var result = Files().Write(dir.Path, relative, "x\n", expectedStamp: null);

        Assert.False(result.Ok);
        Assert.Equal("That path is not inside this worktree.", result.Message);
        Assert.False(File.Exists(Path.Combine(dir.Path, "..", "escape.txt")));
    }

    [Fact]
    public void Refuses_an_absolute_path()
    {
        using var dir = new TempDir();
        var outside = Path.Combine(Path.GetTempPath(), "agents-dashboard-tests", "outside.txt");

        Assert.False(Files().Write(dir.Path, outside, "x\n", expectedStamp: null).Ok);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void A_binary_file_is_not_editable()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "a.bin"), [1, 2, 0, 3]);

        var file = Files().ReadText(dir.Path, "a.bin");

        Assert.True(file.IsBinary);
        Assert.False(file.Editable);
    }

    [Fact]
    public void A_file_outside_any_worktree_reads_and_saves_by_its_absolute_path()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json", "{}\n");
        var files = Files();

        var file = files.ReadTextOutside(path);
        Assert.True(file.Editable);
        Assert.Equal(path, file.Path);

        var saved = files.WriteOutside(path, "{ \"a\": 1 }\n", file.Stamp);

        Assert.True(saved.Ok);
        Assert.Equal("{ \"a\": 1 }\n", File.ReadAllText(path));
        Assert.Equal(files.ReadTextOutside(path).Stamp, saved.Stamp);
    }

    [Fact]
    public void A_file_outside_a_worktree_still_refuses_a_save_over_a_change_made_since()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json", "{}\n");
        var files = Files();
        var stamp = files.ReadTextOutside(path).Stamp;

        File.WriteAllText(path, "{ \"changed\": true }\n");
        var result = files.WriteOutside(path, "{ \"mine\": true }\n", stamp);

        Assert.False(result.Ok);
        Assert.True(result.Conflict);
        Assert.Equal("{ \"changed\": true }\n", File.ReadAllText(path));
    }

    [Fact]
    public void A_relative_path_is_not_taken_as_outside_the_worktree()
    {
        Assert.NotNull(Files().ReadTextOutside("settings.json").Error);
        Assert.False(Files().WriteOutside("settings.json", "x", null).Ok);
    }

    [Fact]
    public void Saving_through_a_link_writes_the_file_it_points_at_and_keeps_the_link()
    {
        using var dir = new TempDir();
        var target = dir.File("dotfiles/CLAUDE.md", "one\n");
        var link = Path.Combine(dir.Path, "CLAUDE.md");
        Links.File(link, target);
        var files = Files();

        var result = files.WriteOutside(link, "two\n", files.ReadTextOutside(link).Stamp);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("two\n", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void A_linked_file_is_read_whole_rather_than_as_long_as_the_link()
    {
        using var dir = new TempDir();
        var target = dir.File("dotfiles/CLAUDE.md", "a line well past the length of the link's own path\n");
        Links.File(Path.Combine(dir.Path, "CLAUDE.md"), target);

        Assert.Equal("a line well past the length of the link's own path\n", Files().ReadText(dir.Path, "CLAUDE.md").Text);
        Assert.Equal("a line well past the length of the link's own path", Assert.Single(Files().Read(dir.Path, "CLAUDE.md").Lines, l => l.Length > 0));
    }
}

public class FileReferenceTests
{
    // Native, so on Windows the absolute paths below carry a drive and backslashes.
    private static readonly string Worktree = Path.GetFullPath("/repo/wt");

    private static readonly string[] Known =
    [
        "src/Foo/Bar.cs",
        "Program.cs",
        "docs/readme.md",
    ];

    private static IReadOnlyList<FileReference> Find(string text) =>
        FileReferences.Find(text, Worktree, p => Known.Contains(p));

    [Fact]
    public void Finds_an_editor_style_reference_with_a_line()
    {
        var r = Assert.Single(Find("see src/Foo/Bar.cs:42 for the fix"));

        Assert.Equal("src/Foo/Bar.cs", r.Path);
        Assert.Equal(42, r.Line);
        Assert.Null(r.Column);
    }

    [Fact]
    public void Finds_a_line_and_column()
    {
        var r = Assert.Single(Find("src/Foo/Bar.cs:42:7"));

        Assert.Equal(42, r.Line);
        Assert.Equal(7, r.Column);
    }

    [Fact]
    public void Finds_the_msbuild_form()
    {
        var r = Assert.Single(Find("src/Foo/Bar.cs(42,7): error CS0103"));

        Assert.Equal("src/Foo/Bar.cs", r.Path);
        Assert.Equal(42, r.Line);
        Assert.Equal(7, r.Column);
    }

    [Fact]
    public void Finds_the_msbuild_form_with_only_a_line()
    {
        var r = Assert.Single(Find("src/Foo/Bar.cs(42): warning"));

        Assert.Equal(42, r.Line);
        Assert.Null(r.Column);
    }

    [Fact]
    public void Finds_a_dotnet_stack_frame()
    {
        var r = Assert.Single(Find($"   at Foo.Bar() in {Path.Combine(Worktree, "src", "Foo", "Bar.cs")}:line 42"));

        Assert.Equal("src/Foo/Bar.cs", r.Path);
        Assert.Equal(42, r.Line);
    }

    [Fact]
    public void Finds_a_bare_path()
    {
        var r = Assert.Single(Find("I changed src/Foo/Bar.cs today"));

        Assert.Equal("src/Foo/Bar.cs", r.Path);
        Assert.Null(r.Line);
    }

    [Fact]
    public void A_windows_drive_letter_is_part_of_the_path_not_a_line_separator()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A drive path is only rooted on Windows.");

        var r = Assert.Single(FileReferences.Find(@"C:\repo\wt\Program.cs(12,5): error", @"C:\repo\wt", p => Known.Contains(p)));

        Assert.Equal("Program.cs", r.Path);
        Assert.Equal(12, r.Line);
        Assert.Equal(5, r.Column);
    }

    [Fact]
    public void Drops_a_path_that_is_not_a_file_in_the_worktree() =>
        Assert.Empty(Find("have a look at src/Nope/Missing.cs"));

    [Fact]
    public void Ordinary_prose_with_a_colon_is_not_a_link() =>
        Assert.Empty(Find("note: the build is green, version 1.2 shipped"));

    [Fact]
    public void Makes_an_absolute_path_inside_the_worktree_relative()
    {
        var r = Assert.Single(Find($"edit {Path.Combine(Worktree, "Program.cs")} now"));

        Assert.Equal("Program.cs", r.Path);
    }

    [Fact]
    public void Drops_an_absolute_path_outside_the_worktree() =>
        Assert.Empty(Find("edit /elsewhere/Program.cs now"));

    [Fact]
    public void Normalises_backslashes_and_a_leading_dot_slash()
    {
        Assert.Equal("src/Foo/Bar.cs", Assert.Single(Find(@"src\Foo\Bar.cs")).Path);
        Assert.Equal("docs/readme.md", Assert.Single(Find("./docs/readme.md")).Path);
    }

    [Fact]
    public void Strips_trailing_punctuation()
    {
        var r = Assert.Single(Find("it lives in `src/Foo/Bar.cs`."));

        Assert.Equal("src/Foo/Bar.cs", r.Path);
        Assert.Equal("src/Foo/Bar.cs", "it lives in `src/Foo/Bar.cs`.".Substring(r.Start, r.Length));
    }

    [Fact]
    public void References_never_overlap_and_come_out_in_order()
    {
        const string text = "src/Foo/Bar.cs:42 and Program.cs(7,2) and docs/readme.md";

        var found = Find(text);

        Assert.Equal(["src/Foo/Bar.cs", "Program.cs", "docs/readme.md"], found.Select(r => r.Path));

        for (var i = 1; i < found.Count; i++)
        {
            Assert.True(found[i].Start >= found[i - 1].Start + found[i - 1].Length);
        }
    }

    [Fact]
    public void Segments_put_the_original_text_back_together()
    {
        const string text = "fix src/Foo/Bar.cs:42, then Program.cs, then stop.";

        var segments = FileReferences.Segments(text, Find(text));

        Assert.Equal(text, string.Concat(segments.Select(s => s.Text)));
        Assert.Equal(2, segments.Count(s => s.Reference is not null));
    }

    [Fact]
    public void Text_with_no_references_is_one_plain_segment()
    {
        var segment = Assert.Single(FileReferences.Segments("nothing here", []));

        Assert.Null(segment.Reference);
        Assert.Equal("nothing here", segment.Text);
    }
}

public class EditorLinkTests
{
    [Fact]
    public void Escapes_a_space_and_keeps_the_separators() =>
        Assert.Equal(
            "vscode://file/repo/my%20wt/src/a.cs",
            EditorLinks.VsCode("/repo/my wt/src/a.cs"));

    [Fact]
    public void Appends_the_line_and_column()
    {
        Assert.Equal("vscode://file/repo/a.cs:42", EditorLinks.VsCode("/repo/a.cs", 42));
        Assert.Equal("vscode://file/repo/a.cs:42:7", EditorLinks.VsCode("/repo/a.cs", 42, 7));
    }

    [Fact]
    public void Keeps_a_windows_drive_as_the_first_segment() =>
        Assert.Equal("vscode://file/C:/my%20wt/a.cs:12", EditorLinks.VsCode(@"C:\my wt\a.cs", 12));
}
