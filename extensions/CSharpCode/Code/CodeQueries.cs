using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace AgentsDashboard.Extensions.CSharpCode;

/// <summary>
/// The four questions the editor asks about a position in a C# file: what is
/// this, where is it defined, where is it used, who calls it. And one about the
/// whole file: what kind of thing each name in it is, for colouring.
/// </summary>
/// <remarks>
/// <para>
/// Everything here works off a loaded <see cref="Solution"/> and touches no
/// MSBuild type, so the queries are testable against an <c>AdhocWorkspace</c>
/// and the process never loads MSBuild just to answer a hover.
/// </para>
/// <para>
/// Coordinates cross this boundary one-based, as Monaco uses them. There are
/// exactly three places that convert: <see cref="ToLinePosition"/> going in, and
/// <see cref="ToLocation"/> and <see cref="ToRun"/> coming out. Nothing else does
/// arithmetic on a line or a column.
/// </para>
/// </remarks>
public static class CodeQueries
{
    /// <summary>
    /// Hover text: the return type, the containing type, and the parameters with
    /// their names, which is what makes a signature readable at a glance.
    /// </summary>
    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeDefaultValue,
        localOptions: SymbolDisplayLocalOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.None, TimeSpan.FromMilliseconds(250));

    /// <summary>What the symbol under the caret is, and its doc summary.</summary>
    public static async Task<HoverInfo?> HoverAsync(
        Solution solution,
        string worktreePath,
        string path,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var symbol = await SymbolAtAsync(solution, path, line, column, cancellationToken).ConfigureAwait(false);

        return symbol is null
            ? null
            : new HoverInfo(symbol.ToDisplayString(SignatureFormat), SummaryOf(symbol, cancellationToken));
    }

    /// <summary>
    /// Where the symbol under the caret is declared in source. A symbol that only
    /// exists in metadata has nothing to open, so it returns nothing.
    /// </summary>
    public static async Task<IReadOnlyList<CodeLocation>> DefinitionAsync(
        Solution solution,
        string worktreePath,
        string path,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var symbol = await SymbolAtAsync(solution, path, line, column, cancellationToken).ConfigureAwait(false);
        if (symbol is null)
        {
            return [];
        }

        var definition = await SymbolFinder
            .FindSourceDefinitionAsync(symbol, solution, cancellationToken)
            .ConfigureAwait(false) ?? symbol;

        var locations = SourceLocations(definition, worktreePath, cancellationToken);

        // A symbol built by the compiler (a record's members, a primary
        // constructor) can have no Locations of its own but still point at the
        // syntax it was created from.
        if (locations.Count == 0)
        {
            locations = definition.DeclaringSyntaxReferences
                .Select(r => ToLocation(Location.Create(r.SyntaxTree, r.Span), worktreePath, cancellationToken))
                .OfType<CodeLocation>()
                .ToList();
        }

        return locations;
    }

    /// <summary>
    /// Every use of the symbol under the caret, its declarations first so the
    /// panel opens on the thing itself rather than on an arbitrary caller.
    /// </summary>
    public static async Task<IReadOnlyList<CodeLocation>> ReferencesAsync(
        Solution solution,
        string worktreePath,
        string path,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var symbol = await SymbolAtAsync(solution, path, line, column, cancellationToken).ConfigureAwait(false);
        if (symbol is null)
        {
            return [];
        }

        var found = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
        var definitions = new List<CodeLocation>();
        var uses = new List<CodeLocation>();

        foreach (var group in found)
        {
            definitions.AddRange(SourceLocations(group.Definition, worktreePath, cancellationToken));

            foreach (var reference in group.Locations)
            {
                var location = ToLocation(reference.Location, worktreePath, cancellationToken);
                if (location is not null)
                {
                    uses.Add(location);
                }
            }
        }

        return Distinct(definitions.Concat(Ordered(uses)));
    }

    /// <summary>
    /// Callers and callees of the method under the caret. Null when the caret is
    /// not on something callable, which is how the editor knows to say nothing
    /// rather than to show an empty panel.
    /// </summary>
    public static async Task<CallHierarchy?> CallHierarchyAsync(
        Solution solution,
        string worktreePath,
        string path,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var symbol = await SymbolAtAsync(solution, path, line, column, cancellationToken).ConfigureAwait(false);
        if (symbol is not IMethodSymbol method || !IsCallable(method))
        {
            return null;
        }

        var target = SourceLocations(method, worktreePath, cancellationToken).FirstOrDefault();
        if (target is null)
        {
            return null;
        }

        var callers = await CallersAsync(solution, method, worktreePath, cancellationToken).ConfigureAwait(false);
        var callees = await CalleesAsync(solution, method, worktreePath, cancellationToken).ConfigureAwait(false);

        return new CallHierarchy(target, callers, callees);
    }

    private static bool IsCallable(IMethodSymbol method) => method.MethodKind
        is MethodKind.Ordinary
        or MethodKind.Constructor
        or MethodKind.LocalFunction
        or MethodKind.PropertyGet
        or MethodKind.PropertySet
        or MethodKind.UserDefinedOperator
        or MethodKind.Conversion;

    private static async Task<IReadOnlyList<CallHierarchyItem>> CallersAsync(
        Solution solution,
        IMethodSymbol method,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var callers = await SymbolFinder.FindCallersAsync(method, solution, cancellationToken).ConfigureAwait(false);
        var items = new List<CallHierarchyItem>();

        foreach (var caller in callers)
        {
            var sites = Ordered(caller.Locations
                .Select(l => ToLocation(l, worktreePath, cancellationToken))
                .OfType<CodeLocation>());

            var declaration = SourceLocations(caller.CallingSymbol, worktreePath, cancellationToken).FirstOrDefault();
            if (declaration is null || sites.Count == 0)
            {
                continue;
            }

            items.Add(new CallHierarchyItem(
                caller.CallingSymbol.Name,
                ContainerOf(caller.CallingSymbol),
                declaration,
                Distinct(sites)));
        }

        return [.. items.OrderBy(i => i.Location.Path, StringComparer.Ordinal).ThenBy(i => i.Location.Line)];
    }

    /// <summary>
    /// Callees come from the body, not from an index: Roslyn answers "who calls
    /// this" but not "what does this call", so the declaring syntax is walked for
    /// invocations, object creations and property reads.
    /// </summary>
    private static async Task<IReadOnlyList<CallHierarchyItem>> CalleesAsync(
        Solution solution,
        IMethodSymbol method,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var grouped = new Dictionary<ISymbol, List<CodeLocation>>(SymbolEqualityComparer.Default);

        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var document = solution.GetDocument(reference.SyntaxTree);
            if (document is null)
            {
                continue;
            }

            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                continue;
            }

            var body = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);

            foreach (var node in body.DescendantNodesAndSelf())
            {
                var (callee, site) = CalleeOf(node, model, cancellationToken);
                if (callee is null || site is null)
                {
                    continue;
                }

                var target = callee.OriginalDefinition;
                if (!target.Locations.Any(l => l.IsInSource))
                {
                    continue;
                }

                var location = ToLocation(site, worktreePath, cancellationToken);
                if (location is null)
                {
                    continue;
                }

                if (!grouped.TryGetValue(target, out var sites))
                {
                    grouped[target] = sites = [];
                }

                sites.Add(location);
            }
        }

        var items = new List<CallHierarchyItem>();

        foreach (var (callee, sites) in grouped)
        {
            var declaration = SourceLocations(callee, worktreePath, cancellationToken).FirstOrDefault();
            if (declaration is null)
            {
                continue;
            }

            items.Add(new CallHierarchyItem(
                callee.Name,
                ContainerOf(callee),
                declaration,
                Distinct(Ordered(sites))));
        }

        return [.. items.OrderBy(i => i.Location.Path, StringComparer.Ordinal).ThenBy(i => i.Location.Line)];
    }

    /// <summary>The symbol a single node calls or reads, with the span to highlight.</summary>
    private static (ISymbol? Callee, Location? Site) CalleeOf(
        SyntaxNode node,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        switch (node)
        {
            case InvocationExpressionSyntax invocation:
                return (model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol,
                    NameOf(invocation.Expression).GetLocation());

            case ObjectCreationExpressionSyntax creation:
                return (model.GetSymbolInfo(creation, cancellationToken).Symbol as IMethodSymbol,
                    creation.Type.GetLocation());

            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                return (model.GetSymbolInfo(implicitCreation, cancellationToken).Symbol as IMethodSymbol,
                    implicitCreation.NewKeyword.GetLocation());

            // A property read is a call too, and it is the only one with no
            // invocation node. The member access that carries an invocation is
            // skipped so a call is not counted twice.
            case MemberAccessExpressionSyntax access when access.Parent is not InvocationExpressionSyntax:
                return (model.GetSymbolInfo(access, cancellationToken).Symbol as IPropertySymbol,
                    access.Name.GetLocation());

            default:
                return (null, null);
        }
    }

    /// <summary>The name part of a call target, so the site is the method name.</summary>
    private static SyntaxNode NameOf(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name,
        MemberBindingExpressionSyntax binding => binding.Name,
        _ => expression,
    };

    /// <summary>
    /// The symbol under the caret. <see cref="SymbolFinder"/> handles the common
    /// cases; the semantic model covers the rest, such as a caret inside a type
    /// name in a declaration, where there is no reference to find.
    /// </summary>
    private static async Task<ISymbol?> SymbolAtAsync(
        Solution solution,
        string path,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var document = DocumentFor(solution, path);
        if (document is null)
        {
            return null;
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var position = ToPosition(text, line, column);
        if (position is null)
        {
            return null;
        }

        var symbol = await SymbolFinder
            .FindSymbolAtPositionAsync(document, position.Value, cancellationToken)
            .ConfigureAwait(false);

        if (symbol is null)
        {
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var token = root?.FindToken(position.Value);

            // Only a position inside a token counts. FindToken answers with the
            // next token when the caret is in whitespace or a comment, and
            // without this a hover on a blank line reports the declaration below
            // it.
            var node = token is { } t && t.Span.Contains(position.Value) ? t.Parent : null;

            if (model is not null && node is not null)
            {
                var info = model.GetSymbolInfo(node, cancellationToken);
                symbol = info.Symbol
                    ?? info.CandidateSymbols.FirstOrDefault()
                    ?? model.GetDeclaredSymbol(node, cancellationToken);
            }
        }

        if (symbol is null)
        {
            return null;
        }

        // A reduced extension method and a constructed generic are not the symbol
        // anyone declared, and searching for them finds only the one call that
        // produced them.
        if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
        {
            symbol = reduced;
        }

        return symbol.OriginalDefinition;
    }

    private static Document? DocumentFor(Solution solution, string path)
    {
        var full = Path.GetFullPath(path);

        foreach (var id in solution.GetDocumentIdsWithFilePath(full))
        {
            var document = solution.GetDocument(id);
            if (document is not null)
            {
                return document;
            }
        }

        return null;
    }

    /// <summary>The XML doc summary as one line of prose.</summary>
    private static string? SummaryOf(ISymbol symbol, CancellationToken cancellationToken)
    {
        var xml = symbol.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XElement? summary;
        try
        {
            summary = XElement.Parse(xml, LoadOptions.PreserveWhitespace).Element("summary");
        }
        catch (System.Xml.XmlException)
        {
            // Malformed doc comments are a source error, not a reason to fail a hover.
            return null;
        }

        if (summary is null)
        {
            return null;
        }

        var text = new StringBuilder();
        Flatten(summary, text);

        var collapsed = Whitespace.Replace(text.ToString(), " ").Trim();

        return collapsed.Length == 0 ? null : collapsed;
    }

    /// <summary>
    /// Tags out, their content in. A cref or a paramref carries its meaning in an
    /// attribute rather than in text, so those become the name they point at.
    /// </summary>
    private static void Flatten(XElement element, StringBuilder text)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText content:
                    text.Append(content.Value);
                    break;

                case XElement child when child.Name.LocalName is "see" or "seealso":
                    if (string.IsNullOrWhiteSpace(child.Value))
                    {
                        text.Append(ShortName(child));
                    }
                    else
                    {
                        Flatten(child, text);
                    }

                    break;

                case XElement child when child.Name.LocalName is "paramref" or "typeparamref":
                    text.Append((string?)child.Attribute("name") ?? string.Empty);
                    break;

                case XElement child:
                    Flatten(child, text);
                    break;
            }
        }
    }

    /// <summary>"T:Repo.Calculator" is not prose. The last segment is.</summary>
    private static string ShortName(XElement element)
    {
        var cref = (string?)element.Attribute("cref") ?? (string?)element.Attribute("langword") ?? string.Empty;
        var colon = cref.IndexOf(':');
        if (colon >= 0)
        {
            cref = cref[(colon + 1)..];
        }

        var dot = cref.LastIndexOf('.');

        return dot >= 0 ? cref[(dot + 1)..] : cref;
    }

    private static List<CodeLocation> SourceLocations(
        ISymbol symbol,
        string worktreePath,
        CancellationToken cancellationToken) =>
        [.. symbol.Locations
            .Select(l => ToLocation(l, worktreePath, cancellationToken))
            .OfType<CodeLocation>()];

    private static List<CodeLocation> Ordered(IEnumerable<CodeLocation> locations) =>
        [.. locations
            .OrderBy(l => l.Path, StringComparer.Ordinal)
            .ThenBy(l => l.Line)
            .ThenBy(l => l.Column)];

    private static List<CodeLocation> Distinct(IEnumerable<CodeLocation> locations)
    {
        var seen = new HashSet<(string, int, int)>();

        return [.. locations.Where(l => seen.Add((l.Path, l.Line, l.Column)))];
    }

    /// <summary>
    /// One-based (line, column) to an offset in the document. The only place a
    /// position coming from the editor loses its one.
    /// </summary>
    private static int? ToPosition(SourceText text, int line, int column)
    {
        var position = ToLinePosition(line, column);
        if (position.Line < 0 || position.Line >= text.Lines.Count)
        {
            return null;
        }

        var textLine = text.Lines[position.Line];
        if (position.Character < 0 || position.Character > textLine.Span.Length)
        {
            return null;
        }

        return textLine.Start + position.Character;
    }

    private static LinePosition ToLinePosition(int line, int column) => new(line - 1, column - 1);

    /// <summary>
    /// A Roslyn location as the editor wants it: worktree-relative path, one-based
    /// positions, and the line itself so a results panel needs no second read.
    /// The only place a position going to the editor gains its one.
    /// </summary>
    private static CodeLocation? ToLocation(Location location, string worktreePath, CancellationToken cancellationToken)
    {
        if (!location.IsInSource || location.SourceTree is null)
        {
            return null;
        }

        var tree = location.SourceTree;
        var span = location.GetLineSpan().Span;
        var text = tree.GetText(cancellationToken);

        var preview = span.Start.Line < text.Lines.Count
            ? text.Lines[span.Start.Line].ToString().Trim()
            : string.Empty;

        return new CodeLocation(
            Relative(worktreePath, tree.FilePath),
            span.Start.Line + 1,
            span.Start.Character + 1,
            span.End.Line + 1,
            span.End.Character + 1,
            preview,
            ContainerOf(tree.GetRoot(cancellationToken), location.SourceSpan));
    }

    /// <summary>The type and member a span sits in, read off the syntax.</summary>
    private static string? ContainerOf(SyntaxNode root, TextSpan span)
    {
        string? member = null;
        string? type = null;

        for (var node = root.FindToken(span.Start).Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case MethodDeclarationSyntax m when member is null:
                    member = m.Identifier.Text;
                    break;
                case LocalFunctionStatementSyntax f when member is null:
                    member = f.Identifier.Text;
                    break;
                case ConstructorDeclarationSyntax c when member is null:
                    member = c.Identifier.Text;
                    break;
                case PropertyDeclarationSyntax p when member is null:
                    member = p.Identifier.Text;
                    break;
                case EventDeclarationSyntax e when member is null:
                    member = e.Identifier.Text;
                    break;
                case VariableDeclaratorSyntax v when member is null && v.Parent?.Parent is BaseFieldDeclarationSyntax:
                    member = v.Identifier.Text;
                    break;
                case BaseTypeDeclarationSyntax t:
                    type = t.Identifier.Text;
                    return Join(type, member);
            }
        }

        return Join(type, member);
    }

    /// <summary>The type and member of a symbol, for a caller the editor lists.</summary>
    private static string? ContainerOf(ISymbol symbol) => Join(symbol.ContainingType?.Name, symbol.Name);

    private static string? Join(string? type, string? member) => (type, member) switch
    {
        (null, null) => null,
        (null, _) => member,
        (_, null) => type,
        _ => $"{type}.{member}",
    };

    /// <summary>
    /// The Roslyn classifications that name a symbol, and the kind the editor
    /// colours each as.
    /// </summary>
    /// <remarks>
    /// Only names. Monaco's own C# grammar already colours keywords, strings,
    /// numbers and comments as you type, with nothing to wait for; what it cannot
    /// know is whether <c>Foo</c> is a type, a method or a local, which is the
    /// part that looks wrong next to VS Code. The kinds are Monaco's semantic token
    /// types, so the theme can colour them by name.
    /// </remarks>
    private static readonly Dictionary<string, string> NameKinds = new(StringComparer.Ordinal)
    {
        [ClassificationTypeNames.NamespaceName] = "namespace",
        [ClassificationTypeNames.ClassName] = "class",
        [ClassificationTypeNames.RecordClassName] = "class",
        [ClassificationTypeNames.DelegateName] = "class",
        [ClassificationTypeNames.ModuleName] = "class",
        [ClassificationTypeNames.StructName] = "struct",
        [ClassificationTypeNames.RecordStructName] = "struct",
        [ClassificationTypeNames.InterfaceName] = "interface",
        [ClassificationTypeNames.EnumName] = "enum",
        [ClassificationTypeNames.EnumMemberName] = "enumMember",
        [ClassificationTypeNames.TypeParameterName] = "typeParameter",
        [ClassificationTypeNames.MethodName] = "method",
        [ClassificationTypeNames.ExtensionMethodName] = "method",
        [ClassificationTypeNames.PropertyName] = "property",
        [ClassificationTypeNames.EventName] = "event",
        [ClassificationTypeNames.FieldName] = "field",
        [ClassificationTypeNames.ConstantName] = "field",
        [ClassificationTypeNames.ParameterName] = "parameter",
        [ClassificationTypeNames.LocalName] = "variable",
        [ClassificationTypeNames.LabelName] = "label",
    };

    /// <summary>
    /// Every name in a file with the kind of symbol it is, in file order. Empty
    /// when the file is not part of the solution.
    /// </summary>
    public static async Task<IReadOnlyList<ClassifiedRun>> ClassifyAsync(
        Solution solution,
        string path,
        CancellationToken cancellationToken)
    {
        if (solution.GetDocumentIdsWithFilePath(path).FirstOrDefault() is not { } id
            || solution.GetDocument(id) is not { } document)
        {
            return [];
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var spans = await Classifier
            .GetClassifiedSpansAsync(document, new TextSpan(0, text.Length), cancellationToken)
            .ConfigureAwait(false);

        var runs = new List<ClassifiedRun>();
        foreach (var span in spans)
        {
            if (NameKinds.TryGetValue(span.ClassificationType, out var kind)
                && ToRun(text, span.TextSpan, kind) is { } run)
            {
                runs.Add(run);
            }
        }

        // The classifier answers in file order already, but Monaco rejects tokens
        // that go backwards, so this is not left to chance.
        runs.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
        return runs;
    }

    /// <summary>A span as a one-based run, or null when it crosses a line, which no name does.</summary>
    private static ClassifiedRun? ToRun(SourceText text, TextSpan span, string kind)
    {
        var start = text.Lines.GetLinePosition(span.Start);
        var end = text.Lines.GetLinePosition(span.End);
        return start.Line == end.Line && span.Length > 0
            ? new ClassifiedRun(start.Line + 1, start.Character + 1, span.Length, kind)
            : null;
    }

    /// <summary>
    /// Worktree-relative with forward slashes, so a location addresses a file the
    /// same way every other link in the dashboard does. A file outside the
    /// worktree keeps its absolute path rather than growing a run of "..".
    /// </summary>
    private static string Relative(string worktreePath, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar);

        if (full.Length > root.Length
            && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && full[root.Length] == Path.DirectorySeparatorChar)
        {
            full = full[(root.Length + 1)..];
        }

        return full.Replace(Path.DirectorySeparatorChar, '/');
    }
}
