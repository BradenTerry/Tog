using Tog.Core.Presentation;

namespace Tog.Core.Tests;

public class FileIconsTests
{
    [Theory]
    [InlineData("src/App/Program.cs", "C#", "fi-csharp")]
    [InlineData("Components/Page.RAZOR", "@", "fi-razor")]
    [InlineData("wwwroot/app.js", "JS", "fi-js")]
    [InlineData("package.json", "{}", "fi-json")]
    public void An_extension_picks_the_language(string path, string glyph, string css) =>
        Assert.Equal(new FileIcon(glyph, css), FileIcons.For(path));

    [Fact]
    public void A_whole_name_wins_over_having_no_extension() =>
        Assert.Equal("fi-docker", FileIcons.For("deploy/Dockerfile").Css);

    [Fact]
    public void A_dotfile_is_matched_by_its_name() =>
        Assert.Equal("fi-git", FileIcons.For(".gitignore").Css);

    [Theory]
    [InlineData("Makefile")]
    [InlineData("notes.unknownext")]
    public void Anything_else_is_a_plain_page(string path) =>
        Assert.Equal(new FileIcon("", "fi-plain"), FileIcons.For(path));
}
