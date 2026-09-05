using AgentsDashboard.Core.Presentation;

namespace AgentsDashboard.Core.Tests;

public class SyntaxTests
{
    /// <summary>The runs of a single line, as (text, class) pairs.</summary>
    private static List<(string Text, string? Css)> Runs(string path, params string[] lines)
    {
        var highlighter = new LineHighlighter(path);
        return lines
            .SelectMany(l => highlighter.Next(l))
            .Select(s => (s.Text, s.Css))
            .ToList();
    }

    private static string? ClassOf(List<(string Text, string? Css)> runs, string text) =>
        runs.FirstOrDefault(r => r.Text == text).Css;

    [Theory]
    [InlineData("a.cs", SyntaxLanguage.CLike)]
    [InlineData("a.ts", SyntaxLanguage.CLike)]
    [InlineData("a.json", SyntaxLanguage.CLike)]
    [InlineData("a.csproj", SyntaxLanguage.Xml)]
    [InlineData("a.razor", SyntaxLanguage.Xml)]
    [InlineData("a.md", SyntaxLanguage.Markdown)]
    [InlineData("LICENSE", SyntaxLanguage.None)]
    [InlineData("a.bin", SyntaxLanguage.None)]
    public void Picks_a_language_from_the_extension(string path, SyntaxLanguage expected) =>
        Assert.Equal(expected, Syntax.LanguageOf(path));

    [Fact]
    public void Colours_a_line_of_csharp()
    {
        var runs = Runs("a.cs", "public string Name = \"hi\"; // note");

        Assert.Equal("t-kw", ClassOf(runs, "public"));
        Assert.Equal("t-kw", ClassOf(runs, "string"));
        Assert.Equal("t-typ", ClassOf(runs, "Name"));
        Assert.Equal("t-str", ClassOf(runs, "\"hi\""));
        Assert.Equal("t-com", ClassOf(runs, "// note"));
    }

    [Fact]
    public void The_runs_put_the_line_back_together_exactly()
    {
        const string line = "  var x = Foo(1, \"two\"); // three";

        Assert.Equal(line, string.Concat(Runs("a.cs", line).Select(r => r.Text)));
    }

    [Fact]
    public void A_line_with_nothing_to_colour_produces_no_runs() =>
        Assert.Empty(Runs("a.cs", "        "));

    [Fact]
    public void A_block_comment_carries_across_lines()
    {
        var runs = Runs("a.cs", "int a = 1; /* opens", "still comment", "closes */ int b = 2;");

        Assert.Equal("t-com", ClassOf(runs, "/* opens"));
        Assert.Equal("t-com", ClassOf(runs, "still comment"));
        Assert.Equal("t-com", ClassOf(runs, "closes */"));

        // Code after the close is code again, not more comment.
        Assert.Equal("t-kw", ClassOf(runs, "int"));
    }

    [Fact]
    public void Resetting_forgets_an_unterminated_comment()
    {
        var highlighter = new LineHighlighter("a.cs");
        highlighter.Next("/* opens");
        highlighter.Reset();

        // A diff hunk starts after a gap in the file, so state from before it is
        // not a continuation of anything.
        var runs = highlighter.Next("public void M()").Select(s => (s.Text, s.Css)).ToList();
        Assert.Equal("t-kw", ClassOf(runs, "public"));
    }

    [Fact]
    public void A_verbatim_string_may_run_past_the_end_of_the_line()
    {
        var runs = Runs("a.cs", "var sql = @\"SELECT", "FROM T\"; var next = 1;");

        Assert.Equal("t-str", ClassOf(runs, "\"SELECT"));
        Assert.Equal("t-str", ClassOf(runs, "FROM T\""));
        Assert.Equal("t-kw", ClassOf(runs, "var"));
    }

    [Fact]
    public void An_ordinary_string_does_not_run_past_the_end_of_the_line()
    {
        // An unterminated quote is far more often a quote inside prose than a
        // string that continues, so it must not swallow the rest of the file.
        var runs = Runs("a.cs", "var a = \"unterminated", "public void M()");

        Assert.Equal("t-kw", ClassOf(runs, "public"));
    }

    [Fact]
    public void Reads_an_escaped_quote_inside_a_string()
    {
        var runs = Runs("a.cs", "var a = \"say \\\"hi\\\" now\"; var b = 1;");

        Assert.Equal("t-str", ClassOf(runs, "\"say \\\"hi\\\" now\""));
    }

    [Fact]
    public void Colours_a_preprocessor_directive()
    {
        Assert.Equal("t-meta", ClassOf(Runs("a.cs", "#nullable enable"), "#nullable enable"));
    }

    [Fact]
    public void Colours_markup()
    {
        var runs = Runs("a.csproj", "  <PackageVersion Include=\"xunit.v3\" Version=\"4.0.0\" />");

        Assert.Equal("t-typ", ClassOf(runs, "PackageVersion"));
        Assert.Equal("t-meta", ClassOf(runs, "Include"));
        Assert.Equal("t-str", ClassOf(runs, "\"xunit.v3\""));
    }

    [Fact]
    public void A_markup_comment_carries_across_lines()
    {
        var runs = Runs("a.xml", "<!-- opens", "middle", "closes --><Tag />");

        Assert.Equal("t-com", ClassOf(runs, "<!-- opens"));
        Assert.Equal("t-com", ClassOf(runs, "middle"));
        Assert.Equal("t-typ", ClassOf(runs, "Tag"));
    }

    [Fact]
    public void Does_not_take_a_greater_than_inside_an_attribute_as_the_end_of_the_tag()
    {
        var runs = Runs("a.xml", "<Item Condition=\"'$(X)' != '>'\" Name=\"a\" />");

        Assert.Equal("t-meta", ClassOf(runs, "Name"));
    }

    [Fact]
    public void Colours_json_keys_apart_from_values()
    {
        var runs = Runs("a.json", "  \"version\": \"10.0.300\",");

        Assert.Equal("t-meta", ClassOf(runs, "\"version\""));
        Assert.Equal("t-str", ClassOf(runs, "\"10.0.300\""));
    }

    [Fact]
    public void Colours_markdown_structure()
    {
        var runs = Runs("a.md", "## Heading", "some `code` here");

        Assert.Equal("t-kw", ClassOf(runs, "## Heading"));
        Assert.Equal("t-str", ClassOf(runs, "`code`"));
    }

    [Fact]
    public void A_markdown_fence_holds_until_it_closes()
    {
        var runs = Runs("a.md", "```bash", "dotnet test", "```", "after");

        Assert.Equal("t-meta", ClassOf(runs, "```bash"));
        Assert.Equal("t-str", ClassOf(runs, "dotnet test"));
        Assert.DoesNotContain(runs, r => r.Text == "after" && r.Css is not null);
    }

    [Fact]
    public void Colours_a_shell_comment()
    {
        Assert.Equal("t-com", ClassOf(Runs("a.sh", "cd /tmp # go there"), "# go there"));
    }

    [Fact]
    public void Colours_a_yaml_key()
    {
        var runs = Runs("a.yml", "  runs-on: ubuntu-latest");

        Assert.Equal("t-meta", ClassOf(runs, "runs-on"));
    }

    [Fact]
    public void Leaves_a_file_it_does_not_know_alone() =>
        Assert.False(new LineHighlighter("LICENSE").Enabled);

    [Theory]
    [InlineData("a.cs", "public class C { }")]
    [InlineData("a.cs", "\"unclosed")]
    [InlineData("a.cs", "/*")]
    [InlineData("a.xml", "<a b=\"c\">text</a>")]
    [InlineData("a.xml", "<unclosed")]
    [InlineData("a.md", "# h `x` *y*")]
    [InlineData("a.json", "{ \"a\": [1, 2] }")]
    [InlineData("a.yml", "a: b # c")]
    public void Whatever_the_line_the_runs_reproduce_it(string path, string line) =>
        Assert.Equal(line, string.Concat(new LineHighlighter(path).Next(line).Select(s => s.Text)));
}
