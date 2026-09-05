using AgentsDashboard.Core.Testing;

namespace AgentsDashboard.Core.Tests;

public class XmlElementScannerTests
{
    [Fact]
    public void Finds_a_self_closing_element()
    {
        var (spans, _) = XmlElementScanner.Scan("<a><x id=\"1\" /></a>", "x", 0);
        var span = Assert.Single(spans);
        Assert.Equal("<x id=\"1\" />", "<a><x id=\"1\" /></a>"[span.Start..span.End]);
    }

    [Fact]
    public void Does_not_match_an_element_whose_name_merely_starts_the_same()
    {
        Assert.Empty(XmlElementScanner.Scan("<UnitTestResult a=\"1\" />", "UnitTest", 0).Spans);
    }

    [Fact]
    public void Ignores_an_angle_bracket_inside_an_attribute_value()
    {
        const string xml = """<x msg="a > b" /><x msg="c" />""";
        Assert.Equal(2, XmlElementScanner.Scan(xml, "x", 0).Spans.Count);
    }

    [Fact]
    public void Steps_over_comments_and_cdata()
    {
        const string xml = "<x><!-- </x> --><![CDATA[ </x> ]]></x>";
        var span = Assert.Single(XmlElementScanner.Scan(xml, "x", 0).Spans);
        Assert.Equal(xml.Length, span.End);
    }

    [Fact]
    public void Returns_nothing_for_an_element_that_has_not_closed()
    {
        Assert.Empty(XmlElementScanner.Scan("<x a=\"1\"><y/>", "x", 0).Spans);
    }

    [Fact]
    public void Counts_only_complete_elements()
    {
        Assert.Equal(2, XmlElementScanner.Count("<x/><x/><x", "x"));
    }
}

public class TrxLocatorTests
{
    [Theory]
    [InlineData("My.Project.Tests_net10.0_arm64.trx", "My.Project.Tests")]
    [InlineData("My.Tests_net8.0_x64.trx", "My.Tests")]
    [InlineData("My.Tests_net9.0.trx", "My.Tests")]
    [InlineData("results.trx", "results")]
    [InlineData("braden_MACHINE_2026-09-05_10_11_12.trx", "braden_MACHINE_2026-09-05_10_11_12")]
    public void Reads_the_project_name_off_the_default_file_name(string file, string expected) =>
        Assert.Equal(expected, TrxLocator.ProjectNameFrom("/a/b/" + file));
}
