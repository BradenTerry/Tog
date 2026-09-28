using AgentsDashboard.Extensions.DotnetTests;

namespace AgentsDashboard.Extensions.DotnetTests.Tests;

public class TestExplorerTests
{
    private static TestResultItem Result(string name, TestOutcome outcome = TestOutcome.Passed) => new()
    {
        TestId = name,
        Name = name,
        ClassName = TrxParser.SplitClassName(name),
        Outcome = outcome,
    };

    private static TestRun Run(string project, params TestResultItem[] results) => new()
    {
        Id = project + Guid.NewGuid(),
        WorktreePath = "/w",
        ProjectName = project,
        Results = results,
    };

    private static IReadOnlyList<ExplorerTest> Tests(params TestResultItem[] results) =>
        TestExplorer.Latest([Run("App.Tests", results)]);

    private static TestNode Find(IReadOnlyList<TestNode> roots, string id) =>
        TestExplorer.All(roots).Single(n => n.Id == id);

    [Fact]
    public void The_newest_run_wins_and_older_runs_fill_in_what_it_did_not_touch()
    {
        var newer = Run("App.Tests", Result("Ns.A.One", TestOutcome.Failed));
        var older = Run("App.Tests", Result("Ns.A.One"), Result("Ns.A.Two"));

        var tests = TestExplorer.Latest([newer, older]);

        Assert.Equal(["Ns.A.One", "Ns.A.Two"], tests.Select(t => t.Name));
        Assert.Equal(TestOutcome.Failed, tests[0].Outcome);
    }

    [Fact]
    public void Groups_by_namespace_class_and_method_folding_single_namespaces()
    {
        var roots = TestExplorer.Build(
            Tests(Result("Co.Product.Tests.A.One"), Result("Co.Product.Tests.B.Two"), Result("Co.Product.Tests.Sub.C.Three")),
            OutcomeFilter.All,
            null);

        var root = Assert.Single(roots);
        Assert.Equal("Co.Product.Tests", root.Label);
        Assert.Equal(TestNodeKind.Namespace, root.Kind);
        Assert.Equal("Co.Product.Tests", root.FullName);

        // Namespaces before classes.
        Assert.Equal(["Sub", "A", "B"], root.Children.Select(c => c.Label));
        Assert.Equal(3, root.Tests.Count);

        var one = Find(roots, "method:Co.Product.Tests.A.One");
        Assert.True(one.IsLeaf);
        Assert.Equal("One", one.Label);
    }

    [Fact]
    public void A_theory_opens_onto_its_cases()
    {
        var roots = TestExplorer.Build(
            Tests(Result("Ns.A.Maps(x: 1)"), Result("Ns.A.Maps(x: 2)", TestOutcome.Failed)),
            OutcomeFilter.All,
            null);

        var method = Find(roots, "method:Ns.A.Maps");
        Assert.Equal(["(x: 1)", "(x: 2)"], method.Children.Select(c => c.Label));
        Assert.All(method.Children, c => Assert.Equal(TestNodeKind.Case, c.Kind));
        Assert.Equal(TestOutcome.Failed, method.Outcome);
        Assert.Equal(1, method.Failed);
    }

    [Fact]
    public void Filters_by_outcome_and_name_case_insensitively()
    {
        var tests = Tests(
            Result("Ns.A.Parses_input"),
            Result("Ns.A.Parses_output", TestOutcome.Failed),
            Result("Ns.B.Writes", TestOutcome.Failed),
            Result("Ns.B.Skips", TestOutcome.Skipped));

        var failed = TestExplorer.Build(tests, OutcomeFilter.Failed, null);
        Assert.Equal(2, failed.Sum(r => r.Tests.Count));

        var searched = TestExplorer.Build(tests, OutcomeFilter.Failed, "  PARSES ");
        Assert.Equal(["Ns.A.Parses_output"], TestExplorer.All(searched).Where(n => n.IsLeaf).Select(n => n.Test!.Name));

        Assert.Equal(1, TestExplorer.Count(tests, OutcomeFilter.Skipped));
        Assert.Empty(TestExplorer.Build(tests, OutcomeFilter.Passed, "Writes"));
    }

    [Fact]
    public void Rows_walk_only_into_open_nodes()
    {
        var roots = TestExplorer.Build(Tests(Result("Ns.A.One"), Result("Ns.B.Two")), OutcomeFilter.All, null);

        var closed = TestExplorer.Rows(roots, _ => false);
        Assert.Equal(["ns:Ns"], closed.Select(r => r.Node.Id));

        var open = TestExplorer.Rows(roots, _ => true);
        Assert.Equal(
            ["ns:Ns", "class:Ns.A", "method:Ns.A.One", "class:Ns.B", "method:Ns.B.Two"],
            open.Select(r => r.Node.Id));
        Assert.Equal([0, 1, 2, 1, 2], open.Select(r => r.Depth));
    }

    [Fact]
    public void Picked_groups_match_by_prefix_and_tests_by_exact_method()
    {
        var roots = TestExplorer.Build(
            Tests(Result("Ns.A.One"), Result("Ns.A.Maps(x: 1)"), Result("Ns.A.Maps(x: 2)"), Result("Ns.B.Two")),
            OutcomeFilter.All,
            null);

        var filters = TestExplorer.FiltersFor([Find(roots, "class:Ns.B"), Find(roots, "case:Ns.A.Maps(x: 1)")]);

        var only = Assert.Single(filters);
        Assert.Equal("App.Tests", only.ProjectName);
        Assert.Equal("FullyQualifiedName=Ns.A.Maps|FullyQualifiedName~Ns.B.", only.Filter);
    }

    [Fact]
    public void A_row_under_a_picked_group_adds_nothing()
    {
        var roots = TestExplorer.Build(Tests(Result("Ns.A.One"), Result("Ns.A.Two")), OutcomeFilter.All, null);

        var filters = TestExplorer.FiltersFor([Find(roots, "class:Ns.A"), Find(roots, "method:Ns.A.One")]);

        Assert.Equal("FullyQualifiedName~Ns.A.", Assert.Single(filters).Filter);
    }

    [Fact]
    public void A_narrowed_group_names_only_the_methods_in_view()
    {
        var tests = Tests(Result("Ns.A.One", TestOutcome.Failed), Result("Ns.A.Two"), Result("Ns.A.Three", TestOutcome.Failed));
        var roots = TestExplorer.Build(tests, OutcomeFilter.Failed, null);

        var filters = TestExplorer.FiltersFor([Find(roots, "class:Ns.A")], narrowed: true);

        Assert.Equal("FullyQualifiedName=Ns.A.One|FullyQualifiedName=Ns.A.Three", Assert.Single(filters).Filter);
    }

    [Fact]
    public void A_namespace_shared_by_two_projects_runs_in_each()
    {
        var tests = TestExplorer.Latest([
            Run("Core.Tests", Result("Ns.Core.One")),
            Run("App.Tests", Result("Ns.App.Two")),
        ]);
        var roots = TestExplorer.Build(tests, OutcomeFilter.All, null);

        var filters = TestExplorer.FiltersFor([Find(roots, "ns:Ns")]);

        Assert.Equal(["App.Tests", "Core.Tests"], filters.Select(f => f.ProjectName));
        Assert.All(filters, f => Assert.Equal("FullyQualifiedName~Ns.", f.Filter));
    }

    [Fact]
    public void Escapes_filter_operators()
    {
        Assert.Equal(@"A\(b\)\|c\&d\=e\!f\~g\\h", TestExplorer.Escape(@"A(b)|c&d=e!f~g\h"));
    }
}
