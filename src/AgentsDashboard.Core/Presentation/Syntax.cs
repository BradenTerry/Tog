namespace AgentsDashboard.Core.Presentation;

/// <summary>What a run of characters is, for colouring.</summary>
public enum TokenKind
{
    Text,
    Comment,
    String,
    Number,
    Keyword,

    /// <summary>A type name, or an element name in markup.</summary>
    Type,

    /// <summary>A preprocessor directive, an attribute name, an object key.</summary>
    Meta,

    Operator,
}

/// <summary>A run of characters within one line.</summary>
public readonly record struct Token(TokenKind Kind, int Start, int Length);

/// <summary>
/// What the tokenizer is part way through when a line ends.
/// </summary>
/// <remarks>
/// Highlighting is done a line at a time, because that is how both the diff and
/// the file browser render. Block comments and multi-line strings do not respect
/// line boundaries, so the little that carries over is threaded through
/// explicitly rather than by re-reading the file each time.
/// </remarks>
public readonly record struct SyntaxState(SyntaxMode Mode = SyntaxMode.Normal, char Delimiter = '\0');

public enum SyntaxMode
{
    Normal,
    BlockComment,

    /// <summary>Inside a string that is allowed to span lines.</summary>
    MultilineString,

    /// <summary>Inside a fenced code block in Markdown.</summary>
    Fence,
}

/// <summary>
/// Which language a file is in, as far as colouring cares.
/// </summary>
public enum SyntaxLanguage
{
    None,
    CLike,
    Xml,
    Markdown,
}

/// <summary>
/// A small, line-oriented syntax highlighter.
/// </summary>
/// <remarks>
/// <para>
/// Done on the server rather than in the browser, because Blazor owns the DOM: a
/// client-side highlighter that rewrites the rendered lines would be undone by
/// the next render, and re-running it after every render turns every comment you
/// add into a full re-highlight of the page. Emitting the runs as part of the
/// render is both simpler and correct by construction.
/// </para>
/// <para>
/// It is deliberately approximate. The aim is that code reads as code -- strings,
/// comments and keywords set apart from the rest -- not that every language is
/// parsed properly. One configurable tokenizer covers the C-family and the
/// languages shaped like it; markup and Markdown get their own, because they are
/// not shaped like it at all.
/// </para>
/// </remarks>
public static class Syntax
{
    /// <summary>How a language differs from the generic shape.</summary>
    private sealed record Spec(
        IReadOnlySet<string> Keywords,
        string? LineComment,
        bool BlockComments,
        bool BackTickStrings = false,
        bool SingleQuoteStrings = true,
        bool TypeHeuristic = true,
        bool KeyHeuristic = false,
        char DirectivePrefix = '\0');

    public static SyntaxLanguage LanguageOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".markdown" => SyntaxLanguage.Markdown,
            ".xml" or ".html" or ".htm" or ".xhtml" or ".svg" or ".razor" or ".cshtml" or ".vbhtml"
                or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" or ".slnx"
                or ".config" or ".runsettings" or ".trx" or ".plist" or ".resx" or ".axaml" or ".xaml"
                => SyntaxLanguage.Xml,
            "" => SyntaxLanguage.None,
            _ => SpecFor(ext) is null ? SyntaxLanguage.None : SyntaxLanguage.CLike,
        };
    }

    /// <summary>
    /// The tokens in one line, and the state the next line starts in.
    /// </summary>
    /// <remarks>
    /// Adjacent plain runs are merged, and a line with nothing worth colouring
    /// returns no tokens at all, so the common case costs the renderer nothing.
    /// </remarks>
    public static (IReadOnlyList<Token> Tokens, SyntaxState Next) Tokenize(
        string line,
        string path,
        SyntaxState state)
    {
        var language = LanguageOf(path);
        var tokens = new List<Token>();

        var next = language switch
        {
            SyntaxLanguage.Xml => Markup(line, state, tokens),
            SyntaxLanguage.Markdown => Markdown(line, state, tokens),
            SyntaxLanguage.CLike => Generic(line, SpecFor(Path.GetExtension(path).ToLowerInvariant())!, state, tokens),
            _ => state,
        };

        return (Simplify(tokens), next);
    }

    /// <summary>Drops plain runs and merges what is left, since Text is the default.</summary>
    private static IReadOnlyList<Token> Simplify(List<Token> tokens)
    {
        tokens.RemoveAll(t => t.Kind == TokenKind.Text || t.Length <= 0);
        return tokens.Count == 0 ? [] : tokens;
    }

    private static readonly IReadOnlySet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double",
        "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "get", "global", "goto", "if", "implicit", "in", "init", "int", "interface",
        "internal", "is", "lock", "long", "nameof", "namespace", "new", "not", "null", "object",
        "operator", "out", "override", "params", "partial", "private", "protected", "public",
        "readonly", "record", "ref", "required", "return", "sbyte", "sealed", "set", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var", "virtual",
        "void", "volatile", "when", "where", "while", "with", "yield",
    };

    private static readonly IReadOnlySet<string> JsKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "as", "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger",
        "default", "delete", "do", "else", "enum", "export", "extends", "false", "finally", "for",
        "from", "function", "get", "if", "implements", "import", "in", "instanceof", "interface",
        "let", "new", "null", "of", "package", "private", "protected", "public", "readonly",
        "return", "set", "static", "super", "switch", "this", "throw", "true", "try", "type",
        "typeof", "undefined", "var", "void", "while", "with", "yield",
    };

    private static readonly IReadOnlySet<string> PythonKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif",
        "else", "except", "False", "finally", "for", "from", "global", "if", "import", "in", "is",
        "lambda", "None", "nonlocal", "not", "or", "pass", "raise", "return", "True", "try",
        "while", "with", "yield",
    };

    private static readonly IReadOnlySet<string> ShellKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "case", "do", "done", "elif", "else", "esac", "export", "fi", "for", "function", "if", "in",
        "local", "return", "then", "until", "while",
    };

    private static readonly IReadOnlySet<string> SqlKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "join", "inner", "left", "right", "outer", "on", "group", "by",
        "order", "having", "insert", "into", "values", "update", "set", "delete", "create", "table",
        "alter", "drop", "index", "view", "as", "and", "or", "not", "null", "is", "in", "exists",
        "case", "when", "then", "else", "end", "distinct", "union", "all", "limit", "offset",
    };

    private static readonly IReadOnlySet<string> JsonKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "true", "false", "null",
    };

    private static readonly IReadOnlySet<string> CssKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "important", "inherit", "initial", "unset", "auto", "none", "flex", "grid", "block",
        "inline", "absolute", "relative", "fixed", "sticky", "hidden", "visible",
    };

    private static readonly IReadOnlySet<string> FSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "let", "mutable", "module", "namespace", "open", "type", "member", "match", "with", "when",
        "if", "then", "else", "elif", "for", "in", "do", "while", "rec", "and", "or", "not", "new",
        "of", "fun", "function", "try", "finally", "true", "false", "null", "override", "abstract",
        "static", "member", "inherit", "interface", "yield", "return", "use", "async", "task",
    };

    private static Spec? SpecFor(string ext) => ext switch
    {
        ".cs" or ".csx" => new Spec(CSharpKeywords, "//", true, DirectivePrefix: '#'),
        ".fs" or ".fsx" or ".fsi" => new Spec(FSharpKeywords, "//", true),
        ".js" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".jsx" or ".java" or ".kt" or ".go"
            or ".rs" or ".c" or ".h" or ".cpp" or ".hpp" or ".swift" or ".scala" or ".dart"
            => new Spec(JsKeywords, "//", true, BackTickStrings: true),
        ".json" or ".jsonc" => new Spec(JsonKeywords, "//", true, SingleQuoteStrings: false,
            TypeHeuristic: false, KeyHeuristic: true),
        ".css" or ".scss" or ".less" => new Spec(CssKeywords, "//", true, TypeHeuristic: false),
        ".py" or ".pyi" => new Spec(PythonKeywords, "#", false),
        ".sh" or ".bash" or ".zsh" or ".fish" => new Spec(ShellKeywords, "#", false, TypeHeuristic: false),
        ".yml" or ".yaml" or ".toml" or ".ini" or ".conf" or ".editorconfig" or ".gitignore"
            or ".gitattributes" or ".dockerignore"
            => new Spec(new HashSet<string>(StringComparer.Ordinal) { "true", "false", "null" },
                "#", false, TypeHeuristic: false, KeyHeuristic: true),
        ".sql" => new Spec(SqlKeywords, "--", true, TypeHeuristic: false),
        ".ps1" or ".psm1" => new Spec(ShellKeywords, "#", false, TypeHeuristic: false),
        _ => null,
    };

    /// <summary>
    /// The tokenizer for everything shaped like C: comments, strings, numbers,
    /// keywords and operators, with a couple of switchable heuristics.
    /// </summary>
    private static SyntaxState Generic(string line, Spec spec, SyntaxState state, List<Token> tokens)
    {
        var i = 0;

        if (state.Mode == SyntaxMode.BlockComment)
        {
            var close = line.IndexOf("*/", StringComparison.Ordinal);
            if (close < 0)
            {
                tokens.Add(new Token(TokenKind.Comment, 0, line.Length));
                return state;
            }

            tokens.Add(new Token(TokenKind.Comment, 0, close + 2));
            i = close + 2;
        }
        else if (state.Mode == SyntaxMode.MultilineString)
        {
            var close = line.IndexOf(state.Delimiter);
            if (close < 0)
            {
                tokens.Add(new Token(TokenKind.String, 0, line.Length));
                return state;
            }

            tokens.Add(new Token(TokenKind.String, 0, close + 1));
            i = close + 1;
        }

        while (i < line.Length)
        {
            var c = line[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (spec.LineComment is { } lc && Matches(line, i, lc))
            {
                tokens.Add(new Token(TokenKind.Comment, i, line.Length - i));
                return state with { Mode = SyntaxMode.Normal };
            }

            if (spec.BlockComments && Matches(line, i, "/*"))
            {
                var close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    tokens.Add(new Token(TokenKind.Comment, i, line.Length - i));
                    return new SyntaxState(SyntaxMode.BlockComment);
                }

                tokens.Add(new Token(TokenKind.Comment, i, close + 2 - i));
                i = close + 2;
                continue;
            }

            if (c == '"' || (spec.SingleQuoteStrings && c == '\'') || (spec.BackTickStrings && c == '`'))
            {
                // A verbatim or raw string can run past the end of the line, and a
                // backtick template literal always can.
                var verbatim = c == '"' && i > 0 && (line[i - 1] == '@' || line[i - 1] == '$');
                var multiline = c == '`' || verbatim;
                i = ReadString(line, i, c, multiline, tokens, out var unterminated);

                if (unterminated && multiline)
                {
                    return new SyntaxState(SyntaxMode.MultilineString, c);
                }

                // A quoted string before a colon is an object key, not a value.
                // Telling the two apart is most of what makes JSON readable.
                if (spec.KeyHeuristic && tokens.Count > 0 && NextNonSpace(line, i) == ':')
                {
                    tokens[^1] = tokens[^1] with { Kind = TokenKind.Meta };
                }

                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Number, start, i - start));
                continue;
            }

            if (spec.DirectivePrefix != '\0' && c == spec.DirectivePrefix && IsFirstOnLine(line, i))
            {
                tokens.Add(new Token(TokenKind.Meta, i, line.Length - i));
                return state with { Mode = SyntaxMode.Normal };
            }

            if (char.IsLetter(c) || c == '_' || c == '$' || c == '@' || c == '-')
            {
                var start = i;
                while (i < line.Length
                       && (char.IsLetterOrDigit(line[i]) || line[i] is '_' or '$' or '@' or '-'))
                {
                    i++;
                }

                var word = line[start..i];
                tokens.Add(new Token(Classify(word, spec, line, i), start, i - start));
                continue;
            }

            // A key is a bare word before a colon, which is what YAML, TOML and an
            // ini file are made of.
            if (spec.KeyHeuristic && c == ':')
            {
                tokens.Add(new Token(TokenKind.Operator, i, 1));
                i++;
                continue;
            }

            var opStart = i;
            while (i < line.Length && IsOperator(line[i]))
            {
                i++;
            }

            if (i == opStart)
            {
                i++;
                continue;
            }

            tokens.Add(new Token(TokenKind.Operator, opStart, i - opStart));
        }

        return state with { Mode = SyntaxMode.Normal };
    }

    private static TokenKind Classify(string word, Spec spec, string line, int after)
    {
        if (spec.Keywords.Contains(word))
        {
            return TokenKind.Keyword;
        }

        if (word.Length > 1 && word[0] == '@' && spec.DirectivePrefix == '\0')
        {
            // A decorator, an annotation, or Razor's escape into code.
            return TokenKind.Meta;
        }

        if (spec.KeyHeuristic && NextNonSpace(line, after) == ':')
        {
            return TokenKind.Meta;
        }

        if (spec.TypeHeuristic && char.IsUpper(word[0]))
        {
            return TokenKind.Type;
        }

        return TokenKind.Text;
    }

    /// <summary>Reads a quoted string, honouring backslash escapes and doubled quotes.</summary>
    private static int ReadString(
        string line,
        int start,
        char quote,
        bool multiline,
        List<Token> tokens,
        out bool unterminated)
    {
        var i = start + 1;
        while (i < line.Length)
        {
            if (line[i] == '\\' && !multiline)
            {
                i += 2;
                continue;
            }

            if (line[i] == quote)
            {
                // "" inside a verbatim string is one escaped quote, not the end.
                if (multiline && i + 1 < line.Length && line[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                tokens.Add(new Token(TokenKind.String, start, i + 1 - start));
                unterminated = false;
                return i + 1;
            }

            i++;
        }

        tokens.Add(new Token(TokenKind.String, start, line.Length - start));
        unterminated = true;
        return line.Length;
    }

    /// <summary>
    /// Markup: tags, attribute names and their values, comments, and the text
    /// between them. Also covers Razor well enough to read, where most of a file
    /// is markup and the code is left plain.
    /// </summary>
    private static SyntaxState Markup(string line, SyntaxState state, List<Token> tokens)
    {
        var i = 0;

        if (state.Mode == SyntaxMode.BlockComment)
        {
            var close = line.IndexOf("-->", StringComparison.Ordinal);
            if (close < 0)
            {
                tokens.Add(new Token(TokenKind.Comment, 0, line.Length));
                return state;
            }

            tokens.Add(new Token(TokenKind.Comment, 0, close + 3));
            i = close + 3;
        }

        while (i < line.Length)
        {
            var open = line.IndexOf('<', i);
            if (open < 0)
            {
                break;
            }

            if (Matches(line, open, "<!--"))
            {
                var close = line.IndexOf("-->", open + 4, StringComparison.Ordinal);
                if (close < 0)
                {
                    tokens.Add(new Token(TokenKind.Comment, open, line.Length - open));
                    return new SyntaxState(SyntaxMode.BlockComment);
                }

                tokens.Add(new Token(TokenKind.Comment, open, close + 3 - open));
                i = close + 3;
                continue;
            }

            var limit = TagEnd(line, open);

            tokens.Add(new Token(TokenKind.Operator, open, 1));
            i = open + 1;

            if (i < limit && (line[i] == '/' || line[i] == '?' || line[i] == '!'))
            {
                tokens.Add(new Token(TokenKind.Operator, i, 1));
                i++;
            }

            var nameStart = i;
            while (i < limit && (char.IsLetterOrDigit(line[i]) || line[i] is '_' or '-' or ':' or '.'))
            {
                i++;
            }

            if (i > nameStart)
            {
                tokens.Add(new Token(TokenKind.Type, nameStart, i - nameStart));
            }

            i = Attributes(line, i, limit, tokens);
        }

        return state with { Mode = SyntaxMode.Normal };
    }

    /// <summary>
    /// Index just past the tag's closing angle bracket, or the end of the line.
    /// Quoted attribute values are skipped, because an MSBuild condition is full
    /// of them and every one would otherwise end the tag early.
    /// </summary>
    private static int TagEnd(string line, int open)
    {
        var quote = '\0';
        for (var i = open + 1; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i + 1;
            }
        }

        return line.Length;
    }

    /// <summary>Attribute names and quoted values, up to the end of the tag.</summary>
    private static int Attributes(string line, int i, int limit, List<Token> tokens)
    {
        while (i < limit)
        {
            var c = line[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                i = ReadString(line, i, c, multiline: false, tokens, out _);
                continue;
            }

            if (c is '>' or '/' or '=' or '?')
            {
                tokens.Add(new Token(TokenKind.Operator, i, 1));
                i++;
                continue;
            }

            var start = i;
            while (i < limit && !char.IsWhiteSpace(line[i]) && line[i] is not ('=' or '>' or '/' or '"' or '\''))
            {
                i++;
            }

            if (i == start)
            {
                i++;
                continue;
            }

            tokens.Add(new Token(TokenKind.Meta, start, i - start));
        }

        return i;
    }

    /// <summary>
    /// Markdown: headings, fenced code, inline code, links and emphasis. Enough
    /// structure to skim a document by, which is what a reader wants from it.
    /// </summary>
    private static SyntaxState Markdown(string line, SyntaxState state, List<Token> tokens)
    {
        var fence = line.TrimStart().StartsWith("```", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal);

        if (state.Mode == SyntaxMode.Fence)
        {
            tokens.Add(new Token(fence ? TokenKind.Meta : TokenKind.String, 0, line.Length));
            return fence ? state with { Mode = SyntaxMode.Normal } : state;
        }

        if (fence)
        {
            tokens.Add(new Token(TokenKind.Meta, 0, line.Length));
            return new SyntaxState(SyntaxMode.Fence);
        }

        var trimmed = line.TrimStart();
        var indent = line.Length - trimmed.Length;

        if (trimmed.StartsWith('#'))
        {
            tokens.Add(new Token(TokenKind.Keyword, 0, line.Length));
            return state;
        }

        if (trimmed.StartsWith("> ", StringComparison.Ordinal))
        {
            tokens.Add(new Token(TokenKind.Comment, 0, line.Length));
            return state;
        }

        if (trimmed.StartsWith("- ", StringComparison.Ordinal)
            || trimmed.StartsWith("* ", StringComparison.Ordinal)
            || trimmed.StartsWith("+ ", StringComparison.Ordinal))
        {
            tokens.Add(new Token(TokenKind.Operator, indent, 1));
        }

        Inline(line, '`', TokenKind.String, tokens);
        Inline(line, '*', TokenKind.Type, tokens);
        return state;
    }

    /// <summary>Marks runs delimited by a repeated character, such as `code`.</summary>
    private static void Inline(string line, char delimiter, TokenKind kind, List<Token> tokens)
    {
        var i = 0;
        while (i < line.Length)
        {
            var open = line.IndexOf(delimiter, i);
            if (open < 0)
            {
                return;
            }

            var close = line.IndexOf(delimiter, open + 1);
            if (close < 0)
            {
                return;
            }

            tokens.Add(new Token(kind, open, close + 1 - open));
            i = close + 1;
        }
    }

    private static bool Matches(string line, int at, string token) =>
        at + token.Length <= line.Length
        && string.CompareOrdinal(line, at, token, 0, token.Length) == 0;

    private static bool IsFirstOnLine(string line, int at) =>
        line.AsSpan(0, at).IsWhiteSpace();

    private static char NextNonSpace(string line, int from)
    {
        for (var i = from; i < line.Length; i++)
        {
            if (!char.IsWhiteSpace(line[i]))
            {
                return line[i];
            }
        }

        return '\0';
    }

    private static bool IsOperator(char c) =>
        !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c is not ('_' or '"' or '\'' or '`');

    /// <summary>The CSS class for a kind, or null for plain text.</summary>
    public static string? CssClass(TokenKind kind) => kind switch
    {
        TokenKind.Comment => "t-com",
        TokenKind.String => "t-str",
        TokenKind.Number => "t-num",
        TokenKind.Keyword => "t-kw",
        TokenKind.Type => "t-typ",
        TokenKind.Meta => "t-meta",
        TokenKind.Operator => "t-op",
        _ => null,
    };
}
