using AgentsDashboard.Extensions.DotnetTests;

namespace AgentsDashboard.Extensions.DotnetTests.Tests;

public class TrxParserTests
{
    private const string Header = """
        <?xml version="1.0" encoding="UTF-8"?>
        <TestRun id="a" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
        """;

    private static string Result(string name, string outcome, string duration = "00:00:00.0100000") =>
        $"""
            <UnitTestResult executionId="{name}" testId="{name}" testName="{name}" outcome="{outcome}" duration="{duration}" />
        """;

    [Fact]
    public void Reads_results_from_a_complete_report()
    {
        var trx = Header
                  + Result("Ns.A.One", "Passed")
                  + Result("Ns.A.Two", "Failed")
                  + "</Results><ResultSummary outcome=\"Completed\"><Counters total=\"2\" passed=\"1\" failed=\"1\" /></ResultSummary></TestRun>";

        var scan = TrxParser.Scan(trx);

        Assert.Equal(2, scan.Results.Count);
        Assert.Equal(TestOutcome.Passed, scan.Results[0].Outcome);
        Assert.Equal(TestOutcome.Failed, scan.Results[1].Outcome);
        Assert.Equal(2, scan.DeclaredTotal);
        Assert.True(scan.Completed);
    }

    [Fact]
    public void Ignores_a_result_that_is_still_being_written()
    {
        // What the file looks like mid-run: the writer is partway through an element.
        var trx = Header + Result("Ns.A.One", "Passed") + "    <UnitTestResult testName=\"Ns.A.Two\" outc";

        var scan = TrxParser.Scan(trx);

        Assert.Single(scan.Results);
        Assert.Equal("Ns.A.One", scan.Results[0].Name);
        Assert.False(scan.Completed);
    }

    [Fact]
    public void Resumes_from_the_offset_and_reports_only_what_is_new()
    {
        var first = Header + Result("Ns.A.One", "Passed");
        var scan1 = TrxParser.Scan(first);
        Assert.Single(scan1.Results);

        var second = first + Result("Ns.A.Two", "Failed");
        var scan2 = TrxParser.Scan(second, scan1.NextOffset);

        Assert.Single(scan2.Results);
        Assert.Equal("Ns.A.Two", scan2.Results[0].Name);
    }

    [Fact]
    public void A_partial_element_is_picked_up_once_it_completes()
    {
        var partial = Header + "    <UnitTestResult testName=\"Ns.A.One\" outcome=\"Pas";
        var scan1 = TrxParser.Scan(partial);
        Assert.Empty(scan1.Results);

        var whole = Header + Result("Ns.A.One", "Passed");
        var scan2 = TrxParser.Scan(whole, scan1.NextOffset);

        Assert.Single(scan2.Results);
        Assert.Equal("Ns.A.One", scan2.Results[0].Name);
    }

    [Fact]
    public void Keeps_a_data_driven_result_whole_rather_than_stopping_at_its_first_inner_result()
    {
        var trx = Header + """
                <UnitTestResult testId="outer" testName="Ns.A.Theory" outcome="Failed">
                  <InnerResults>
                    <UnitTestResult testId="i1" testName="Ns.A.Theory(x: 1)" outcome="Passed" />
                    <UnitTestResult testId="i2" testName="Ns.A.Theory(x: 2)" outcome="Failed" />
                  </InnerResults>
                </UnitTestResult>
            """;

        var scan = TrxParser.Scan(trx);

        // The outer element is one result. Its inner results live inside it and are
        // not scanned again at the top level.
        Assert.Single(scan.Results);
        Assert.Equal("Ns.A.Theory", scan.Results[0].Name);
        Assert.Equal(TestOutcome.Failed, scan.Results[0].Outcome);
    }

    [Fact]
    public void Reads_the_failure_message_and_stack()
    {
        var trx = Header + """
                <UnitTestResult testId="a" testName="Ns.A.One" outcome="Failed" duration="00:00:00.5000000">
                  <Output>
                    <ErrorInfo>
                      <Message>Assert.Equal() Failure</Message>
                      <StackTrace>   at Ns.A.One() in /repo/tests/A.cs:line 42</StackTrace>
                    </ErrorInfo>
                  </Output>
                </UnitTestResult>
            """;

        var result = Assert.Single(TrxParser.Scan(trx).Results);

        Assert.Equal("Assert.Equal() Failure", result.Message);
        Assert.Contains("A.cs:line 42", result.StackTrace);
        Assert.Equal(TimeSpan.FromMilliseconds(500), result.Duration);
    }

    [Fact]
    public void Falls_back_to_counting_test_definitions_when_there_are_no_counters()
    {
        var trx = Header + Result("Ns.A.One", "Passed") + """
            </Results>
            <TestDefinitions>
              <UnitTest name="One" storage="a.dll" id="1"><TestMethod className="Ns.A" name="One" /></UnitTest>
              <UnitTest name="Two" storage="a.dll" id="2"><TestMethod className="Ns.A" name="Two" /></UnitTest>
              <UnitTest name="Three" storage="a.dll" id="3"><TestMethod className="Ns.A" name="Three" /></UnitTest>
            </TestDefinitions>
            """;

        Assert.Equal(3, TrxParser.Scan(trx).DeclaredTotal);
    }

    [Fact]
    public void Total_is_unknown_before_the_run_says_so()
    {
        Assert.Null(TrxParser.Scan(Header + Result("Ns.A.One", "Passed")).DeclaredTotal);
    }

    [Theory]
    [InlineData("Passed", TestOutcome.Passed)]
    [InlineData("Failed", TestOutcome.Failed)]
    [InlineData("Error", TestOutcome.Failed)]
    [InlineData("Timeout", TestOutcome.Failed)]
    [InlineData("NotExecuted", TestOutcome.NotExecuted)]
    [InlineData("Inconclusive", TestOutcome.Skipped)]
    [InlineData("Warning", TestOutcome.Other)]
    public void Maps_outcomes(string raw, TestOutcome expected) =>
        Assert.Equal(expected, TrxParser.MapOutcome(raw));

    [Theory]
    [InlineData("Ns.Sub.Class.Method", "Ns.Sub.Class")]
    [InlineData("Ns.Class.Method(x: 1.5)", "Ns.Class")]
    [InlineData("Method", null)]
    public void Splits_the_declaring_type_off_a_test_name(string name, string? expected) =>
        Assert.Equal(expected, TrxParser.SplitClassName(name));
}
