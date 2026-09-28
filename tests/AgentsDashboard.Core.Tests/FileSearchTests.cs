using AgentsDashboard.Core.Search;

namespace AgentsDashboard.Core.Tests;

public class FileSearchTests
{
    private static readonly string[] Paths =
    [
        "src/AgentsDashboard.App/Components/Panels/FileDocument.razor",
        "src/AgentsDashboard.App/Components/Panels/DiffDocument.razor",
        "src/AgentsDashboard.App/Components/Panels/FileTreePanel.razor",
        "src/fixtures/docs/other.md",
        "docs/editor.md",
        "README.md",
        "src/AgentsDashboard.Core/Git/WorktreeFiles.cs",
    ];

    private static IReadOnlyList<string> Find(string query, params string[] recent) =>
        FileSearch.Find(Paths, query, recent).Select(m => m.Path).ToList();

    [Fact]
    public void Matches_characters_in_order_but_not_together()
    {
        Assert.Equal("src/AgentsDashboard.App/Components/Panels/FileDocument.razor", Find("fdoc")[0]);
    }

    [Fact]
    public void Leaves_out_paths_missing_a_character()
    {
        Assert.Empty(Find("zzz"));
        Assert.DoesNotContain("README.md", Find("fdoc"));
    }

    [Fact]
    public void Is_case_insensitive()
    {
        Assert.Equal(Find("readme"), Find("README"));
        Assert.Equal("README.md", Find("readme")[0]);
    }

    [Fact]
    public void A_match_in_the_name_beats_one_spread_over_the_directories()
    {
        var results = Find("editor");

        Assert.Equal("docs/editor.md", results[0]);
    }

    [Fact]
    public void Word_starts_beat_letters_in_the_middle()
    {
        // Both names hold d-d; DiffDocument has them at the start of each word.
        var results = Find("dd");

        Assert.Equal("src/AgentsDashboard.App/Components/Panels/DiffDocument.razor", results[0]);
    }

    [Fact]
    public void A_piece_with_a_slash_matches_the_whole_path()
    {
        var results = Find("panels/ftp");

        Assert.Equal(["src/AgentsDashboard.App/Components/Panels/FileTreePanel.razor"], results);
    }

    [Fact]
    public void Every_space_separated_piece_must_match()
    {
        var results = Find("core wtf");

        Assert.Equal(["src/AgentsDashboard.Core/Git/WorktreeFiles.cs"], results);
    }

    [Fact]
    public void Recent_matches_come_first_even_when_others_score_higher()
    {
        var results = Find("doc", "src/AgentsDashboard.App/Components/Panels/DiffDocument.razor");

        Assert.Equal("src/AgentsDashboard.App/Components/Panels/DiffDocument.razor", results[0]);
        Assert.True(FileSearch.Find(Paths, "doc", ["src/AgentsDashboard.App/Components/Panels/DiffDocument.razor"])[0].Recent);
    }

    [Fact]
    public void Positions_point_at_the_matched_characters()
    {
        var match = FileSearch.Match("docs/editor.md", "edmd")!;

        Assert.Equal("edmd", string.Concat(match.Positions.Select(i => "docs/editor.md"[i])));
        Assert.Equal(match.Positions.Order(), match.Positions);
    }

    [Fact]
    public void Prefers_the_word_start_over_the_first_occurrence()
    {
        // Greedy would take the d, o and c of "docs"; the name's word start is better.
        var match = FileSearch.Match("docs/FileDocument.cs", "doc")!;

        Assert.Equal(9, match.Positions[0]);
    }

    [Fact]
    public void Caps_the_results()
    {
        var many = Enumerable.Range(0, 200).Select(i => $"src/file{i}.cs").ToList();

        Assert.Equal(10, FileSearch.Find(many, "file", [], limit: 10).Count);
    }

    [Fact]
    public void An_empty_query_finds_nothing()
    {
        Assert.Empty(Find("   "));
    }

    [Theory]
    [InlineData("foo.cs", "foo.cs", null, false)]
    [InlineData("foo.cs:42", "foo.cs", 42, false)]
    [InlineData("foo.cs:42:7", "foo.cs", 42, false)]
    [InlineData(":17", "", 17, true)]
    [InlineData(":", "", null, true)]
    [InlineData("foo.cs:", "foo.cs:", null, false)]
    [InlineData("foo.cs:abc", "foo.cs:abc", null, false)]
    [InlineData("  bar  ", "bar", null, false)]
    public void Parses_line_suffixes(string raw, string text, int? line, bool goToLine)
    {
        var query = FileQuery.Parse(raw);

        Assert.Equal(new FileQuery(text, line, goToLine), query);
    }
}
