using System.Text;

namespace AgentsDashboard.Extensions.DotnetTests;

/// <summary>What the explorer's status filter lets through.</summary>
public enum OutcomeFilter
{
    All,
    Failed,
    Passed,
    Skipped,
}

/// <summary>What a row in the explorer stands for.</summary>
public enum TestNodeKind
{
    Namespace,
    Class,

    /// <summary>A test method. A data-driven one has its cases as children.</summary>
    Method,

    /// <summary>One row of a data-driven method.</summary>
    Case,
}

/// <summary>A test as the explorer knows it: its latest result, and the project that ran it.</summary>
public sealed record ExplorerTest(TestResultItem Result, string? ProjectName)
{
    public string Name => Result.Name;
    public TestOutcome Outcome => Result.Outcome;

    /// <summary>The fully qualified method, which is what a filter can name. Theory arguments stripped.</summary>
    public string Method
    {
        get
        {
            var bracket = Name.IndexOf('(');
            return bracket < 0 ? Name : Name[..bracket];
        }
    }
}

/// <summary>One node of the explorer tree.</summary>
public sealed class TestNode
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required TestNodeKind Kind { get; init; }

    /// <summary>The namespace, class or method this node names, fully qualified. A case uses its method's.</summary>
    public required string FullName { get; init; }

    public List<TestNode> Children { get; } = [];

    /// <summary>The test itself, on a leaf.</summary>
    public ExplorerTest? Test { get; init; }

    /// <summary>Every test at or under this node, filled in once the tree is built.</summary>
    public IReadOnlyList<ExplorerTest> Tests { get; internal set; } = [];

    public bool IsLeaf => Children.Count == 0;

    public int Failed => Tests.Count(t => t.Outcome == TestOutcome.Failed);
    public int Passed => Tests.Count(t => t.Outcome == TestOutcome.Passed);

    /// <summary>The outcome to draw: any failure wins, then any skip, then passed.</summary>
    public TestOutcome Outcome =>
        Tests.Any(t => t.Outcome == TestOutcome.Failed) ? TestOutcome.Failed
        : Tests.Count > 0 && Tests.All(t => t.Outcome == TestOutcome.Passed) ? TestOutcome.Passed
        : Tests.Any(t => t.Outcome is TestOutcome.Skipped or TestOutcome.NotExecuted) ? TestOutcome.Skipped
        : TestOutcome.Other;
}

/// <summary>One row as drawn: a node and how deep it sits.</summary>
public readonly record struct TestRow(TestNode Node, int Depth);

/// <summary>A run of one project, narrowed by a VSTest filter expression. A null filter runs everything.</summary>
public sealed record ProjectFilter(string ProjectName, string? Filter);

/// <summary>
/// Builds the Tests tab's explorer: every test the worktree's recent runs have
/// reported, grouped by namespace, class and method.
/// </summary>
/// <remarks>
/// There is no discovery step. The extension follows reports rather than
/// running anything of its own, so the tree is what the reports have said, with
/// each test's newest result winning. A test that has never been run is not in
/// it until something runs it.
/// </remarks>
public static class TestExplorer
{
    /// <summary>
    /// The newest result per test across <paramref name="runs"/>, which come most
    /// recent first. A partial run (one class, one failing test) only replaces
    /// the tests it touched, so the tree does not shrink to whatever ran last.
    /// </summary>
    public static IReadOnlyList<ExplorerTest> Latest(IReadOnlyList<TestRun> runs)
    {
        var seen = new Dictionary<string, ExplorerTest>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            foreach (var result in run.Results)
            {
                seen.TryAdd(result.Name, new ExplorerTest(result, run.ProjectName));
            }
        }

        return [.. seen.Values.OrderBy(t => t.Name, StringComparer.Ordinal)];
    }

    public static bool Matches(ExplorerTest test, OutcomeFilter outcome, string? search)
    {
        var outcomeOk = outcome switch
        {
            OutcomeFilter.Failed => test.Outcome == TestOutcome.Failed,
            OutcomeFilter.Passed => test.Outcome == TestOutcome.Passed,
            OutcomeFilter.Skipped => test.Outcome is TestOutcome.Skipped or TestOutcome.NotExecuted,
            _ => true,
        };

        return outcomeOk
               && (string.IsNullOrWhiteSpace(search)
                   || test.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static int Count(IReadOnlyList<ExplorerTest> tests, OutcomeFilter outcome) =>
        tests.Count(t => Matches(t, outcome, null));

    /// <summary>
    /// The tree for the tests that pass the filter. Namespaces that hold nothing
    /// but one other namespace are folded into it, so a project's
    /// <c>Company.Product.Tests</c> is one row rather than three nested ones.
    /// </summary>
    public static IReadOnlyList<TestNode> Build(IReadOnlyList<ExplorerTest> tests, OutcomeFilter outcome, string? search)
    {
        var root = new Draft("", "", TestNodeKind.Namespace, "");

        foreach (var test in tests.Where(t => Matches(t, outcome, search)))
        {
            var className = test.Result.ClassName;
            var parent = root;

            if (className is { Length: > 0 })
            {
                var dot = className.LastIndexOf('.');
                var ns = dot > 0 ? className[..dot] : "";
                if (ns.Length > 0)
                {
                    var path = "";
                    foreach (var segment in ns.Split('.'))
                    {
                        path = path.Length == 0 ? segment : path + "." + segment;
                        parent = parent.Child("ns:" + path, segment, TestNodeKind.Namespace, path);
                    }
                }

                parent = parent.Child("class:" + className, className[(dot + 1)..], TestNodeKind.Class, className);
            }

            var method = test.Method;
            var methodLabel = className is { Length: > 0 } && method.StartsWith(className + ".", StringComparison.Ordinal)
                ? method[(className.Length + 1)..]
                : method;
            var methodNode = parent.Child("method:" + method, methodLabel, TestNodeKind.Method, method);
            methodNode.Tests.Add(test);
        }

        return [.. root.Children.Values.Select(d => Freeze(Fold(d)))];
    }

    /// <summary>The rows to draw: the tree walked in order, into the nodes that are open.</summary>
    public static List<TestRow> Rows(IReadOnlyList<TestNode> roots, Func<TestNode, bool> isOpen)
    {
        var rows = new List<TestRow>();

        void Walk(TestNode node, int depth)
        {
            rows.Add(new TestRow(node, depth));
            if (!node.IsLeaf && isOpen(node))
            {
                foreach (var child in node.Children)
                {
                    Walk(child, depth + 1);
                }
            }
        }

        foreach (var root in roots)
        {
            Walk(root, 0);
        }

        return rows;
    }

    /// <summary>Every node in the tree, depth first.</summary>
    public static IEnumerable<TestNode> All(IReadOnlyList<TestNode> roots) =>
        roots.SelectMany(r => new[] { r }.Concat(All(r.Children)));

    /// <summary>
    /// Turns picked rows into one filtered run per project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A run is started per project rather than one across the solution, because a
    /// filter that matches nothing in a project still makes that project build,
    /// run, and write an empty report, which then shows as a run of its own.
    /// </para>
    /// <para>
    /// The filter is VSTest syntax, the one form every runner takes: VSTest,
    /// MSTest and NUnit on the testing platform, and xunit.v3, which also has its
    /// own simple filters but will not mix them with this one. A namespace or
    /// class matches by prefix, a method by exact name, which in xunit also takes
    /// in every case of a theory. A case cannot be named on its own, so picking
    /// one runs its method.
    /// </para>
    /// </remarks>
    /// <param name="picked">The rows picked.</param>
    /// <param name="narrowed">
    /// Whether the tree was built through a filter. A namespace or class then
    /// names the methods of it that are in the tree instead of matching by
    /// prefix, so it runs what is on screen rather than everything under it.
    /// </param>
    public static IReadOnlyList<ProjectFilter> FiltersFor(IEnumerable<TestNode> picked, bool narrowed = false)
    {
        var nodes = picked.ToList();

        // A node under another picked node is already covered by it.
        var covered = new HashSet<string>(nodes.SelectMany(n => All(n.Children)).Select(n => n.Id), StringComparer.Ordinal);
        var clauses = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var node in nodes.Where(n => !covered.Contains(n.Id)))
        {
            var byPrefix = node.Kind is TestNodeKind.Namespace or TestNodeKind.Class && !narrowed;

            foreach (var test in node.Tests)
            {
                var clause = byPrefix
                    ? "FullyQualifiedName~" + Escape(node.FullName + ".")
                    : "FullyQualifiedName=" + Escape(test.Method);

                var project = test.ProjectName ?? "";
                if (!clauses.TryGetValue(project, out var set))
                {
                    clauses[project] = set = new SortedSet<string>(StringComparer.Ordinal);
                }

                set.Add(clause);
            }
        }

        return [.. clauses
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ProjectFilter(kv.Key, string.Join('|', kv.Value)))];
    }

    /// <summary>The characters VSTest filter syntax treats as operators, backslash-escaped.</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static Draft Fold(Draft draft)
    {
        while (draft.Kind == TestNodeKind.Namespace
               && draft.Tests.Count == 0
               && draft.Children.Count == 1
               && draft.Children.Values.First() is { Kind: TestNodeKind.Namespace } only)
        {
            only.Label = draft.Label + "." + only.Label;
            draft = only;
        }

        foreach (var key in draft.Children.Keys.ToList())
        {
            draft.Children[key] = Fold(draft.Children[key]);
        }

        return draft;
    }

    private static TestNode Freeze(Draft draft)
    {
        // A method run once is a leaf; a theory run with several cases opens
        // onto them.
        if (draft.Kind == TestNodeKind.Method && draft.Tests.Count == 1)
        {
            return new TestNode
            {
                Id = draft.Id,
                Label = draft.Label,
                Kind = TestNodeKind.Method,
                FullName = draft.FullName,
                Test = draft.Tests[0],
                Tests = [draft.Tests[0]],
            };
        }

        var node = new TestNode
        {
            Id = draft.Id,
            Label = draft.Label,
            Kind = draft.Kind,
            FullName = draft.FullName,
        };

        if (draft.Kind == TestNodeKind.Method)
        {
            foreach (var test in draft.Tests)
            {
                var bracket = test.Name.IndexOf('(');
                node.Children.Add(new TestNode
                {
                    Id = "case:" + test.Name,
                    Label = bracket < 0 ? test.Name : test.Name[bracket..],
                    Kind = TestNodeKind.Case,
                    FullName = draft.FullName,
                    Test = test,
                    Tests = [test],
                });
            }

            node.Tests = draft.Tests;
            return node;
        }

        // Namespaces first, then classes, then loose methods, each by name.
        foreach (var child in draft.Children.Values
                     .OrderBy(c => c.Kind)
                     .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase))
        {
            node.Children.Add(Freeze(child));
        }

        node.Tests = [.. node.Children.SelectMany(c => c.Tests)];
        return node;
    }

    private sealed class Draft(string id, string label, TestNodeKind kind, string fullName)
    {
        public string Id { get; } = id;
        public string Label { get; set; } = label;
        public TestNodeKind Kind { get; } = kind;
        public string FullName { get; } = fullName;
        public Dictionary<string, Draft> Children { get; } = new(StringComparer.Ordinal);
        public List<ExplorerTest> Tests { get; } = [];

        public Draft Child(string id, string label, TestNodeKind kind, string fullName)
        {
            if (!Children.TryGetValue(id, out var child))
            {
                Children[id] = child = new Draft(id, label, kind, fullName);
            }

            return child;
        }
    }
}
