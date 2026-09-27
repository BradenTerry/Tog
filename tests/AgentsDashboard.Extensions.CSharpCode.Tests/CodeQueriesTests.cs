using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace AgentsDashboard.Extensions.CSharpCode.Tests;

public class CodeQueriesTests
{
    /// <summary>
    /// A fake worktree root, so relative paths are exercised as well. Full, as a
    /// loaded solution's paths are: on Windows "/repo" is only rooted, and gains
    /// a drive when the queries look a document up.
    /// </summary>
    private static readonly string Worktree = Path.GetFullPath("/repo");

    private const string ACs = """
        namespace Repo;

        public class Calculator
        {
            /// <summary>
            /// Adds <paramref name="a"/> to <paramref name="b"/>.
            /// </summary>
            public int Add(int a, int b) => a + b;

            public int Twice(int n) => Add(n, n);
        }
        """;

    private const string BCs = """
        namespace Repo;

        public class Runner
        {
            public int Run()
            {
                var c = new Calculator();
                return c.Twice(3) + c.Add(1, 2);
            }
        }
        """;

    private static Solution Sample()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Repo", "Repo", LanguageNames.CSharp)
            .WithProjectCompilationOptions(
                projectId,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(projectId, Framework());

        solution = Add(solution, projectId, "A.cs", ACs);
        solution = Add(solution, projectId, "B.cs", BCs);

        return solution;
    }

    private static Solution Add(Solution solution, ProjectId projectId, string name, string text) =>
        solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            name,
            SourceText.From(text),
            filePath: Absolute(name));

    /// <summary>Enough of the framework for the compiler to bind int and object.</summary>
    private static IEnumerable<MetadataReference> Framework() =>
        AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string assemblies
            ? assemblies
                .Split(Path.PathSeparator)
                .Where(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a))
            : [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

    /// <summary>The one-based line and column of a snippet, as the editor would send it.</summary>
    private static (int Line, int Column) At(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{snippet}' is not in the source.");

        var before = source[..index];
        var line = before.Count(c => c == '\n') + 1;
        var lastBreak = before.LastIndexOf('\n');

        return (line, index - lastBreak);
    }

    private static string Absolute(string name) => Path.Combine(Worktree, "src", name);

    [Fact]
    public void The_position_helper_agrees_with_hand_counting()
    {
        // Add is declared on the eighth line of A.cs, sixteen characters in.
        Assert.Equal((8, 16), At(ACs, "Add(int a, int b)"));
    }

    [Fact]
    public async Task Classify_tells_types_methods_parameters_and_locals_apart()
    {
        var runs = await CodeQueries.ClassifyAsync(Sample(), Absolute("B.cs"), TestContext.Current.CancellationToken);

        string KindAt(string snippet)
        {
            var (line, column) = At(BCs, snippet);
            return Assert.Single(runs, r => r.Line == line && r.Column == column).Kind;
        }

        Assert.Equal("class", KindAt("Runner"));
        Assert.Equal("method", KindAt("Run()"));
        Assert.Equal("class", KindAt("Calculator()"));
        Assert.Equal("variable", KindAt("c.Twice"));
        Assert.Equal("method", KindAt("Twice"));
        Assert.Equal("namespace", KindAt("Repo;"));
    }

    [Fact]
    public async Task Classify_leaves_keywords_to_the_editor_and_answers_in_order()
    {
        var runs = await CodeQueries.ClassifyAsync(Sample(), Absolute("A.cs"), TestContext.Current.CancellationToken);

        var (line, column) = At(ACs, "public int Add");
        Assert.DoesNotContain(runs, r => r.Line == line && r.Column == column);
        Assert.Equal(runs.OrderBy(r => r.Line).ThenBy(r => r.Column), runs);
        Assert.Contains(runs, r => r.Kind == "parameter");
    }

    [Fact]
    public async Task Classify_a_file_outside_the_solution_is_empty() =>
        Assert.Empty(await CodeQueries.ClassifyAsync(Sample(), Absolute("Missing.cs"), TestContext.Current.CancellationToken));

    [Fact]
    public async Task Hover_shows_a_signature_and_the_doc_summary()
    {
        var (line, column) = At(ACs, "Add(int a, int b)");

        var hover = await CodeQueries.HoverAsync(
            Sample(), Worktree, Absolute("A.cs"), line, column, TestContext.Current.CancellationToken);

        Assert.NotNull(hover);
        Assert.Equal("int Calculator.Add(int a, int b)", hover.Signature);
        Assert.Equal("Adds a to b.", hover.Summary);
    }

    [Fact]
    public async Task Definition_of_a_call_lands_on_the_declaration_in_the_other_file()
    {
        var (line, column) = At(BCs, "Add(1, 2)");

        var definitions = await CodeQueries.DefinitionAsync(
            Sample(), Worktree, Absolute("B.cs"), line, column, TestContext.Current.CancellationToken);

        var definition = Assert.Single(definitions);
        Assert.Equal("src/A.cs", definition.Path);
        Assert.Equal(8, definition.Line);
        Assert.Equal(16, definition.Column);
        Assert.Equal(19, definition.EndColumn);
        Assert.Equal("public int Add(int a, int b) => a + b;", definition.Preview);
        Assert.Equal("Calculator.Add", definition.Container);
    }

    [Fact]
    public async Task References_list_the_definition_first_and_then_every_use()
    {
        var (line, column) = At(ACs, "Add(int a, int b)");

        var references = await CodeQueries.ReferencesAsync(
            Sample(), Worktree, Absolute("A.cs"), line, column, TestContext.Current.CancellationToken);

        Assert.Equal(3, references.Count);
        Assert.Equal(("src/A.cs", 8), (references[0].Path, references[0].Line));
        Assert.Equal(("src/A.cs", 10), (references[1].Path, references[1].Line));
        Assert.Equal("Calculator.Twice", references[1].Container);
        Assert.Equal(("src/B.cs", 8), (references[2].Path, references[2].Line));
        Assert.Equal("Runner.Run", references[2].Container);
    }

    [Fact]
    public async Task Call_hierarchy_lists_callers_with_their_call_sites_and_callees()
    {
        var (line, column) = At(ACs, "Twice(int n)");

        var hierarchy = await CodeQueries.CallHierarchyAsync(
            Sample(), Worktree, Absolute("A.cs"), line, column, TestContext.Current.CancellationToken);

        Assert.NotNull(hierarchy);
        Assert.Equal(("src/A.cs", 10), (hierarchy.Target.Path, hierarchy.Target.Line));

        var caller = Assert.Single(hierarchy.Callers);
        Assert.Equal("Run", caller.Name);
        Assert.Equal("Runner.Run", caller.Container);
        var site = Assert.Single(caller.CallSites);
        Assert.Equal(("src/B.cs", 8), (site.Path, site.Line));

        var callee = Assert.Single(hierarchy.Callees);
        Assert.Equal("Add", callee.Name);
        Assert.Equal("Calculator.Add", callee.Container);
        Assert.Equal(8, callee.Location.Line);
        Assert.Equal(10, Assert.Single(callee.CallSites).Line);
    }

    [Fact]
    public async Task A_symbol_that_is_not_callable_has_no_call_hierarchy()
    {
        var (line, column) = At(ACs, "Calculator");

        var hierarchy = await CodeQueries.CallHierarchyAsync(
            Sample(), Worktree, Absolute("A.cs"), line, column, TestContext.Current.CancellationToken);

        Assert.Null(hierarchy);
    }

    [Fact]
    public async Task A_position_with_no_symbol_answers_with_nothing()
    {
        var solution = Sample();

        // Line 2 of A.cs is blank, so there is nothing under the caret at all.
        var hover = await CodeQueries.HoverAsync(
            solution, Worktree, Absolute("A.cs"), 2, 1, TestContext.Current.CancellationToken);
        var definitions = await CodeQueries.DefinitionAsync(
            solution, Worktree, Absolute("A.cs"), 2, 1, TestContext.Current.CancellationToken);

        Assert.Null(hover);
        Assert.Empty(definitions);
    }

    [Fact]
    public async Task A_position_past_the_end_of_the_file_answers_with_nothing()
    {
        var hover = await CodeQueries.HoverAsync(
            Sample(), Worktree, Absolute("A.cs"), 9999, 1, TestContext.Current.CancellationToken);

        Assert.Null(hover);
    }

    [Fact]
    public async Task A_document_that_is_not_in_the_solution_answers_with_nothing()
    {
        var solution = Sample();

        var hover = await CodeQueries.HoverAsync(
            solution, Worktree, Absolute("Z.cs"), 1, 1, TestContext.Current.CancellationToken);
        var references = await CodeQueries.ReferencesAsync(
            solution, Worktree, Absolute("Z.cs"), 1, 1, TestContext.Current.CancellationToken);

        Assert.Null(hover);
        Assert.Empty(references);
    }

    [Fact]
    public void Code_intelligence_starts_not_loaded_and_ignores_edits_until_it_is()
    {
        var intelligence = new RoslynCodeIntelligence(new SolutionLoader());

        // No MSBuild is touched here: nothing loads until EnsureLoadedAsync runs.
        intelligence.UpdateDocument(Worktree, "src/A.cs", "class X;");

        var status = intelligence.Status(Worktree);
        Assert.Equal(CodeLoadState.NotLoaded, status.State);
        Assert.Equal(0, status.Projects);
        Assert.Equal(0, status.Documents);
    }

    [Fact]
    public async Task Queries_never_start_a_load()
    {
        var intelligence = new RoslynCodeIntelligence(new SolutionLoader());
        var token = TestContext.Current.CancellationToken;

        var hover = await intelligence.HoverAsync(Worktree, "src/A.cs", 1, 1, token);
        var references = await intelligence.ReferencesAsync(Worktree, "src/A.cs", 1, 1, token);
        var runs = await intelligence.ClassifyAsync(Worktree, "src/A.cs", token);

        Assert.Null(hover);
        Assert.Empty(references);
        Assert.Empty(runs);
        Assert.Equal(CodeLoadState.NotLoaded, intelligence.Status(Worktree).State);
    }

    [Fact]
    public async Task A_failed_load_is_reported_and_unload_turns_it_off_again()
    {
        var intelligence = new RoslynCodeIntelligence(new SolutionLoader());
        var missing = Path.Combine(Path.GetTempPath(), "no-such-worktree-" + Guid.NewGuid().ToString("N"));

        // A missing directory fails before MSBuild is touched.
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => intelligence.LoadAsync(missing, TestContext.Current.CancellationToken));
        Assert.Equal(CodeLoadState.Failed, intelligence.Status(missing).State);

        intelligence.Unload(missing);

        Assert.Equal(CodeLoadState.NotLoaded, intelligence.Status(missing).State);
    }

    [Fact(Skip = "Loads the real SDK; run by hand")]
    public async Task Loads_this_repository_from_its_slnx()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var started = Stopwatch.StartNew();

        var solution = await new SolutionLoader()
            .LoadAsync(root, null, TestContext.Current.CancellationToken);

        started.Stop();
        Assert.True(solution.Projects.Count() >= 3, $"Loaded {solution.Projects.Count()} projects.");
        Assert.NotNull(SolutionLoader.FindSolutionFile(root));
        TestContext.Current.TestOutputHelper?.WriteLine($"Loaded in {started.Elapsed.TotalSeconds:F1}s.");
    }
}
