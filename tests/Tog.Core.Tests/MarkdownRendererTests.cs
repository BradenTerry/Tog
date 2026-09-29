using Tog.Core.Presentation;

namespace Tog.Core.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void Renders_headings_tables_and_task_lists()
    {
        var html = MarkdownRenderer.Render("""
            # Title

            | A | B |
            | --- | --- |
            | 1 | 2 |

            - [x] done
            - [ ] not yet
            """);

        Assert.Contains("<h1", html, StringComparison.Ordinal);
        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("type=\"checkbox\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_html_in_the_file_is_not_passed_through()
    {
        var html = MarkdownRenderer.Render("hello <script>alert(1)</script> <img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mermaid_block_is_left_for_the_page_to_draw()
    {
        var html = MarkdownRenderer.Render("""
            ```mermaid
            flowchart LR
              A --> B
            ```
            """);

        Assert.Contains("class=\"mermaid\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_code_block_keeps_its_language_for_colouring()
    {
        var html = MarkdownRenderer.Render("""
            ```csharp
            var x = 1;
            ```
            """);

        Assert.Contains("language-csharp", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_to_another_file_opens_it_in_the_files_tab()
    {
        var html = MarkdownRenderer.Render("See [the review doc](../review.md#submit).", "docs/design/extensions.md");

        Assert.Contains("data-file=\"docs/review.md\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"#\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_web_link_opens_outside_the_app()
    {
        var html = MarkdownRenderer.Render("[ACP](https://agentclientprotocol.com)");

        Assert.Contains("target=\"_blank\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-file", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("docs", "../README.md", "README.md")]
    [InlineData("docs/design", "./x.md", "docs/design/x.md")]
    [InlineData("", "/src/a.cs", "src/a.cs")]
    [InlineData("docs", "a%20b.md", "docs/a b.md")]
    [InlineData("", "../outside.md", null)]
    public void Resolves_relative_links_from_the_file(string directory, string url, string? expected) =>
        Assert.Equal(expected, MarkdownRenderer.Resolve(directory, url));
}
